using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Pane.Contracts;
using Pane.Localization;
using Pane.Settings;

namespace Pane.Services.Clipboard;

internal sealed partial class DataStorage
{
    internal const int ImportEntryLimit=100000;
    internal const long ImportFileLimit=1024L*1024*1024,ImportTotalLimit=4L*1024*1024*1024;
    internal const long ImportJsonLimit=64L*1024*1024;
    static string Pending(string data)=>Path.Combine(data,".import-pending");
    static string Outcome(string data)=>Path.Combine(data,".import-result.json");
    static readonly string[] TransferFiles=["settings/settings.json","settings/gestures.json","usage/launcher-usage.json","usage/terminal-history.json","usage/web-search-history.json","clipboard/history.json"];
    static string LocalFile(string data,string name)=>Path.Combine(data,name.StartsWith("clipboard/",StringComparison.Ordinal)?name.Replace('/',Path.DirectorySeparatorChar):name[(name.IndexOf('/')+1)..]);
    static JsonObject Object(string text)=>JsonNode.Parse(text) as JsonObject??throw new InvalidDataException();
    static JsonObject ReadObject(string path)=>Object(File.ReadAllText(path));
    static string Reason(Exception e)=>$"{e.GetType().Name} (0x{e.HResult:X8})";
    static DataStorageException Invalid()=>new(Strings.Data_ImportInvalid);
    static void WriteAtomic(string path,string text)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        using(var file=new FileStream(path+".tmp",FileMode.Create,FileAccess.Write,FileShare.None)){
            file.Write(Encoding.UTF8.GetBytes(text));file.Flush(true);
        }
        File.Move(path+".tmp",path,true);
    }
    static void DeletePending(string data)
    {
        string root=Path.GetFullPath(Pending(data));
        if(!StorageLocation.Within(root,Path.GetFullPath(data)))throw new IOException();
        if(Directory.Exists(root))Directory.Delete(root,true);
    }
    public DataImportOutcome? LastImport {
        get {string path=Outcome(settings.DirectoryPath);if(!File.Exists(path))return null;var obj=ReadObject(path);return new(obj["backup"]!.GetValue<string>(),obj["error"]?.GetValue<string>());}
    }
    public void AcknowledgeImport()=>File.Delete(Outcome(settings.DirectoryPath));
    public Task<DataExportResult> ExportAsync(string path,DataExportOptions options,IProgress<double>? progress,CancellationToken cancellationToken)=>Task.Run(async()=>{
        await operation.WaitAsync(cancellationToken).ConfigureAwait(false);
        try{
            if(options.Usage)await flushUsage().WaitAsync(cancellationToken).ConfigureAwait(false);
            if(history() is {} loading)await loading.Ready.ConfigureAwait(false);
            lock(settings.StorageSync){
                var active=history();if(active?.Ready.IsCompletedSuccessfully!=true)active=null;
                lock(active?.StorageGate??settings.StorageSync)
                    return ExportCore(settings.DirectoryPath,path,options,active?.StorageSnapshot(),progress,cancellationToken);
            }
        }finally{operation.Release();}
    },cancellationToken);
    static DataExportResult ExportCore(string data,string path,DataExportOptions options,StoredClip[]? snapshot,IProgress<double>? progress,CancellationToken token)
    {
        string temp=path+"."+Guid.NewGuid().ToString("N")+".tmp";
        try{
            var contents=new Dictionary<string,byte[]>(StringComparer.Ordinal);
            var images=new Dictionary<string,string>(StringComparer.Ordinal);
            if(options.Settings){
                string source=Path.Combine(data,"settings.json");
                if(File.Exists(source)){var obj=ReadObject(source);LocalSettingsFields.Apply(obj,null);contents.Add("settings/settings.json",Encoding.UTF8.GetBytes(obj.ToJsonString()));}
                string gestures=Path.Combine(data,"gestures.json");if(File.Exists(gestures))contents.Add("settings/gestures.json",File.ReadAllBytes(gestures));
            }
            if(options.Usage)foreach(string name in TransferFiles.Where(n=>n.StartsWith("usage/",StringComparison.Ordinal))){string source=LocalFile(data,name);if(File.Exists(source))contents.Add(name,File.ReadAllBytes(source));}
            StoredClip[] clips=[];
            if(options.Clipboard!=ClipboardExport.None){
                clips=(snapshot??new ClipboardStorage().Load(LocalFile(data,"clipboard/history.json"))).Where(c=>c.Entry.Kind is ClipKind.Text or ClipKind.Code or ClipKind.Link or ClipKind.Color||options.Clipboard==ClipboardExport.WithImages&&c.Entry.Kind==ClipKind.Image).Select(c=>{
                    if(c.Entry.Kind!=ClipKind.Image)return c;
                    string id=Guid.NewGuid().ToString("N"),full="clipboard/images/"+id+".png",thumb="clipboard/images/"+id+".thumb.png";
                    images.Add(full,c.Entry.ContentPath??throw new InvalidDataException());
                    if(c.Entry.Thumbnail?.Path is {} p)images.Add(thumb,p);
                    return c with{Entry=c.Entry with{ContentPath=full,Thumbnail=c.Entry.Thumbnail?.Path==null?null:new(thumb)}};
                }).ToArray();
                contents.Add("clipboard/history.json",JsonSerializer.SerializeToUtf8Bytes(clips,ClipboardJson.Default.StoredClipArray));
            }
            var manifest=new JsonObject{["format"]=1,["app"]=SettingsStore.CurrentVersion(),["exportedAt"]=DateTimeOffset.UtcNow.ToString("O"),["settings"]=contents.ContainsKey("settings/settings.json"),["usage"]=contents.Keys.Any(n=>n.StartsWith("usage/",StringComparison.Ordinal)),["clipboard"]=options.Clipboard!=ClipboardExport.None,["clipboardEntries"]=clips.Length,["clipboardImages"]=clips.Count(c=>c.Entry.Kind==ClipKind.Image)};
            contents.Add("manifest.json",Encoding.UTF8.GetBytes(manifest.ToJsonString()));
            int done=0,total=contents.Count+images.Count;progress?.Report(0);
            using(var output=new FileStream(temp,FileMode.CreateNew,FileAccess.Write,FileShare.None)){
                using(var zip=new ZipArchive(output,ZipArchiveMode.Create,true)){
                    foreach(var pair in contents){token.ThrowIfCancellationRequested();using var entry=zip.CreateEntry(pair.Key).Open();entry.Write(pair.Value);progress?.Report(++done/(double)total);}
                    foreach(var pair in images){token.ThrowIfCancellationRequested();using var entry=zip.CreateEntry(pair.Key).Open();using var source=File.OpenRead(pair.Value);CopyBounded(source,entry,ImportFileLimit,token);progress?.Report(++done/(double)total);}
                }
                output.Flush(true);
            }
            token.ThrowIfCancellationRequested();File.Move(temp,path,true);return new(path,new FileInfo(path).Length);
        }catch(OperationCanceledException){throw;}
        catch(Exception e){throw new DataStorageException(string.Format(Strings.Data_ExportFailed,Reason(e)),e);}
        finally{File.Delete(temp);}
    }
    static void CopyBounded(Stream source,Stream target,long limit,CancellationToken token)
    {
        byte[] buffer=new byte[81920];long size=0;int read;
        while((read=source.Read(buffer))>0){token.ThrowIfCancellationRequested();size+=read;if(size>limit)throw new DataStorageException(Strings.Data_ImportTooLarge);target.Write(buffer,0,read);}
    }
    static bool ImageName(string name)
    {
        if(!name.StartsWith("clipboard/images/",StringComparison.Ordinal))return false;
        string leaf=name[17..];return leaf.EndsWith(".png",StringComparison.Ordinal)&&Guid.TryParseExact(leaf.EndsWith(".thumb.png",StringComparison.Ordinal)?leaf[..^10]:leaf[..^4],"N",out _);
    }
    static bool Allowed(string name)=>TransferFiles.Contains(name)||ImageName(name);
    static JsonObject ValidateArchive(ZipArchive zip)
    {
        if(zip.Entries.Count>ImportEntryLimit)throw new DataStorageException(Strings.Data_ImportTooLarge);
        long total=0;var names=new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach(var entry in zip.Entries){
            string name=entry.FullName;
            if(name.Length==0||name.Contains('\\')||name.Contains(':')||name.StartsWith('/')||name.Split('/').Any(p=>p is ".." or ".")||!names.Add(name))throw Invalid();
            if(entry.Length>ImportFileLimit||(total+=entry.Length)>ImportTotalLimit)throw new DataStorageException(Strings.Data_ImportTooLarge);
        }
        var manifest=zip.GetEntry("manifest.json")??throw Invalid();if(manifest.Length>1024*1024)throw new DataStorageException(Strings.Data_ImportTooLarge);
        using var source=manifest.Open();using var buffer=new MemoryStream();CopyBounded(source,buffer,1024*1024,default);
        var obj=Object(Encoding.UTF8.GetString(buffer.ToArray()));
        int format=obj["format"]!.GetValue<int>();if(format>1)throw new DataStorageException(Strings.Data_ImportNewerFormat);if(format!=1)throw Invalid();
        if(string.IsNullOrWhiteSpace(obj["app"]!.GetValue<string>())||!DateTimeOffset.TryParse(obj["exportedAt"]!.GetValue<string>(),out _))throw Invalid();
        bool s=obj["settings"]!.GetValue<bool>(),u=obj["usage"]!.GetValue<bool>(),c=obj["clipboard"]!.GetValue<bool>();
        int count=obj["clipboardEntries"]!.GetValue<int>(),pictures=obj["clipboardImages"]!.GetValue<int>();
        if(count<0||pictures<0||pictures>count||!c&&count!=0||s!=(zip.GetEntry("settings/settings.json")!=null)||u!=zip.Entries.Any(e=>e.FullName is "usage/launcher-usage.json" or "usage/terminal-history.json" or "usage/web-search-history.json")||c!=(zip.GetEntry("clipboard/history.json")!=null))throw Invalid();
        return obj;
    }
    public Task<DataImportPreview> InspectAsync(string path,CancellationToken cancellationToken)=>Task.Run(()=>{
        try{cancellationToken.ThrowIfCancellationRequested();using var zip=ZipFile.OpenRead(path);var m=ValidateArchive(zip);return new DataImportPreview(m["app"]!.GetValue<string>(),DateTimeOffset.Parse(m["exportedAt"]!.GetValue<string>()),m["settings"]!.GetValue<bool>(),m["usage"]!.GetValue<bool>(),m["clipboardEntries"]!.GetValue<int>(),m["clipboardImages"]!.GetValue<int>());}
        catch(DataStorageException){throw;}catch(OperationCanceledException){throw;}catch(Exception e){throw new DataStorageException(Strings.Data_ImportInvalid,e);}
    },cancellationToken);
    public Task StageImportAsync(string path,DataImportOptions options,IProgress<double>? progress,CancellationToken cancellationToken)=>Task.Run(async()=>{
        await operation.WaitAsync(cancellationToken).ConfigureAwait(false);
        string data=settings.DirectoryPath,root=Pending(data);
        try{
            DeletePending(data);using var zip=ZipFile.OpenRead(path);var manifest=ValidateArchive(zip);
            if(!Enum.IsDefined(options.Mode))throw Invalid();
            var entries=zip.Entries.Where(e=>Allowed(e.FullName)&&(e.FullName.StartsWith("settings/",StringComparison.Ordinal)?options.Settings:e.FullName.StartsWith("usage/",StringComparison.Ordinal)?options.Usage:options.Clipboard)).ToArray();
            if(!entries.Any(e=>TransferFiles.Contains(e.FullName)))throw new DataStorageException(Strings.Data_ImportNothing);
            int done=0;progress?.Report(0);
            foreach(var entry in entries){
                cancellationToken.ThrowIfCancellationRequested();string target=Path.Combine(root,entry.FullName.Replace('/',Path.DirectorySeparatorChar));Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                using var source=entry.Open();using var output=new FileStream(target,FileMode.CreateNew,FileAccess.Write,FileShare.None);
                CopyBounded(source,output,Math.Min(entry.Length,entry.FullName.EndsWith(".json",StringComparison.Ordinal)?ImportJsonLimit:ImportFileLimit),cancellationToken);
                if(output.Length!=entry.Length)throw Invalid();output.Flush(true);progress?.Report(++done/(double)entries.Length);
            }
            ValidatePayload(root);
            if(options.Clipboard&&File.Exists(Path.Combine(root,"clipboard/history.json"))){
                var clips=new ClipboardStorage().Load(Path.Combine(root,"clipboard/history.json"));
                if(clips.Length!=manifest["clipboardEntries"]!.GetValue<int>()||clips.Count(c=>c.Entry.Kind==ClipKind.Image)!=manifest["clipboardImages"]!.GetValue<int>())throw Invalid();
            }
            WriteAtomic(Path.Combine(root,"pending.json"),new JsonObject{["settings"]=options.Settings,["usage"]=options.Usage,["clipboard"]=options.Clipboard,["merge"]=options.Mode==DataImportMode.Merge}.ToJsonString());
        }catch(OperationCanceledException){DeletePending(data);throw;}
        catch(DataStorageException){DeletePending(data);throw;}
        catch(Exception e){DeletePending(data);throw new DataStorageException(Strings.Data_ImportInvalid,e);}
        finally{operation.Release();}
    },cancellationToken);
    static void ValidatePayload(string root)
    {
        foreach(string name in TransferFiles){string path=Path.Combine(root,name);if(!File.Exists(path))continue;using var doc=JsonDocument.Parse(File.ReadAllText(path));if(doc.RootElement.ValueKind is not(JsonValueKind.Object or JsonValueKind.Array))throw Invalid();}
        string usage=Path.Combine(root,"usage/launcher-usage.json");
        if(File.Exists(usage))foreach(var pair in ReadObject(usage)){
            if(string.IsNullOrEmpty(pair.Key)||pair.Value?["Count"]?.GetValue<int>() is not >=0||pair.Value?["LastUsed"]?.GetValue<DateTimeOffset>()==null)throw Invalid();
        }
        string web=Path.Combine(root,"usage/web-search-history.json");
        if(File.Exists(web)){var records=JsonSerializer.Deserialize(File.ReadAllText(web),Pane.Services.Launcher.WebHistoryJson.Default.DictionaryStringListWebSearchRecord)??throw Invalid();foreach(var pair in records)if(string.IsNullOrEmpty(pair.Key)||pair.Value==null||pair.Value.Any(r=>r==null||!Pane.Services.Launcher.WebSearchHistory.Valid(r.Text)))throw Invalid();}
        string terminal=Path.Combine(root,"usage/terminal-history.json");
        if(File.Exists(terminal))foreach(var value in JsonNode.Parse(File.ReadAllText(terminal))!.AsArray())if(value?.GetValue<string>()==null)throw Invalid();
        string index=Path.Combine(root,"clipboard/history.json");if(!File.Exists(index))return;
        foreach(var clip in new ClipboardStorage().Load(index)){
            if(clip?.Entry is not {} e||!Enum.IsDefined(e.Kind)||e.Kind==ClipKind.Files||e.Kind!=ClipKind.Image&&e.Text==null)throw Invalid();
            if(e.Kind==ClipKind.Image){
                foreach(string? image in new[]{e.ContentPath,e.Thumbnail?.Path}.Where((_,i)=>i==0||e.Thumbnail!=null))
                    if(image==null||!ImageName(image)||!File.Exists(Path.Combine(root,image)))throw Invalid();
            }
        }
    }
}
