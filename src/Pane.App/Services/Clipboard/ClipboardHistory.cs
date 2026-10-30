using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using Pane.Contracts;
using Pane.Settings;
namespace Pane.Services.Clipboard;

internal sealed record ClipboardContent(string? Text=null, string[]? Files=null, byte[]? Png=null,
    string Program="", string Source="", string? Icon=null, bool Exclude=false,
    int? History=null, int? Cloud=null, bool Own=false);
internal interface IClipboardSource : IDisposable
{
    event Action<ClipboardContent>? Captured;
    void Start();
    void Stop();
}
internal interface IClipboardWriter
{
    Task PasteAsync(ClipEntry entry,bool plainText,nint target,CancellationToken token);
}
internal interface IClipboardImages
{
    (int Width,int Height) Save(byte[] png,string full,string thumbnail);
}
internal sealed record StoredClip(ClipEntry Entry,string Fingerprint);
[JsonSerializable(typeof(StoredClip[]))]
internal partial class ClipboardJson:JsonSerializerContext { }

internal static partial class ClipboardClassifier
{
    [GeneratedRegex(@"^#(?:[0-9a-fA-F]{3}|[0-9a-fA-F]{6}|[0-9a-fA-F]{8})$")]
    private static partial Regex Hex();
    [GeneratedRegex(@"^(rgb|rgba)\(\s*(\d{1,3})\s*,\s*(\d{1,3})\s*,\s*(\d{1,3})(?:\s*,\s*(0|1|0?\.\d+|1\.0+))?\s*\)$",RegexOptions.IgnoreCase)]
    private static partial Regex Rgb();
    [GeneratedRegex(@"\b(class|public|private|return|function|const|let|var|if|for|while|def|import)\b")]
    private static partial Regex Keyword();
    public static ClipKind Classify(string value)
    {
        string s=value.Trim();
        if(!s.Any(char.IsWhiteSpace)&&Uri.TryCreate(s,UriKind.Absolute,out var uri)&&uri.Scheme is "http" or "https"&&uri.Host.Length>0)return ClipKind.Link;
        var m=Rgb().Match(s);
        if(Hex().IsMatch(s)||m.Success&&Enumerable.Range(2,3).All(i=>int.Parse(m.Groups[i].Value)<=255)&&m.Groups[5].Success==m.Groups[1].Value.Equals("rgba",StringComparison.OrdinalIgnoreCase))return ClipKind.Color;
        // 宁可少判：多行、缩进、关键字，再加成对花括号和分号，或 Python 冒号块。
        if(s.Contains('\n')&&value.Split('\n').Any(l=>l.StartsWith("    ")||l.StartsWith('\t'))&&Keyword().IsMatch(s)&&
            (s.Contains('{')&&s.Contains('}')&&s.Contains(';')||s.Split('\n')[0].TrimEnd().EndsWith(':')))return ClipKind.Code;
        return ClipKind.Text;
    }
    public static bool Allowed(ClipboardContent c,ClipboardSettings s)=>!c.Own&&
        !(s.HonorExcludeMarker&&(c.Exclude||c.History==0||c.Cloud==0))&&
        !s.ExcludedPrograms.Contains(c.Program,StringComparer.OrdinalIgnoreCase);
}

internal sealed class ClipboardHistory:IClipboardHistory,IDisposable
{
    readonly object sync=new();
    readonly SettingsStore settings;
    readonly IClipboardSource source;
    readonly IClipboardWriter writer;
    readonly IClipboardImages images;
    readonly string directory,index;
    readonly List<StoredClip> entries=[];
    readonly SemaphoreSlim signal=new(0,1);
    readonly Task persistence;
    bool stopping,dirty;
    readonly object lifecycle=new();
    bool listening;
    public int StorageFailures {get;private set;}
    public Task Ready {get;}
    public event EventHandler? Changed;
    public ClipboardHistory(SettingsStore settings,IClipboardSource source,IClipboardWriter writer,IClipboardImages images)
    {
        this.settings=settings;this.source=source;this.writer=writer;this.images=images;
        directory=Path.Combine(settings.DirectoryPath,"clipboard");index=Path.Combine(directory,"history.json");
        persistence=Task.Run(PersistLoop);
        Ready=Task.Run(()=>{
            Directory.CreateDirectory(directory);
            if(File.Exists(index)){
                var loaded=JsonSerializer.Deserialize(File.ReadAllBytes(index),ClipboardJson.Default.StoredClipArray)??[];
                lock(sync)entries.AddRange(loaded);
            }
            source.Captured+=Capture;settings.Changed+=SettingsChanged;SettingsChanged(this,EventArgs.Empty);
        });
    }
    void SettingsChanged(object? sender,EventArgs e)
    {
        // 停止监听会等待回调退出，不可持有回调所需的历史锁。
        lock(lifecycle){
            bool enabled=settings.Current.Clipboard.Enabled;
            if(enabled!=listening){if(enabled)source.Start();else source.Stop();listening=enabled;}
            if(enabled)lock(sync){int count=entries.Count;Trim();if(entries.Count!=count)Schedule();}
        }
        Changed?.Invoke(this,EventArgs.Empty);
    }
    public IReadOnlyList<ClipEntry> Snapshot(){lock(sync)return entries.Select(e=>e.Entry).OrderByDescending(e=>e.Pinned).ThenByDescending(e=>e.CopiedAt).ToArray();}
    internal void Capture(ClipboardContent content)
    {
        var s=settings.Current.Clipboard;if(!s.Enabled||!ClipboardClassifier.Allowed(content,s))return;
        ClipKind kind;string fingerprint;
        if(content.Files is {Length:>0}){if(!s.RecordFiles)return;kind=ClipKind.Files;fingerprint="f:"+Hash(Encoding.UTF8.GetBytes(string.Join('\0',content.Files)));}
        else if(content.Png is {Length:>0}){if(!s.RecordImages)return;kind=ClipKind.Image;fingerprint="i:"+Hash(content.Png);}
        else if(!string.IsNullOrEmpty(content.Text)){if(!s.RecordText)return;kind=ClipboardClassifier.Classify(content.Text);fingerprint="t:"+Hash(Encoding.UTF8.GetBytes(content.Text));}
        else return;
        lock(sync){
            if(stopping||!settings.Current.Clipboard.Enabled)return;
            var now=DateTimeOffset.UtcNow;var latest=entries.MaxBy(e=>e.Entry.CopiedAt);
            if(s.Dedupe&&latest?.Fingerprint==fingerprint){entries[entries.IndexOf(latest)]=latest with{Entry=latest.Entry with{CopiedAt=now}};}
            else {
                string id=Guid.NewGuid().ToString("N");string? full=null,thumb=null;int? width=null,height=null;
                if(kind==ClipKind.Image){full=Path.Combine(directory,id+".png");thumb=Path.Combine(directory,id+".thumb.png");var size=images.Save(content.Png!,full,thumb);width=size.Width;height=size.Height;}
                entries.Add(new(new(id,kind,kind==ClipKind.Image?null:content.Text,kind==ClipKind.Files?content.Files?.ToArray():null,
                    thumb==null?null:new(thumb),width,height,content.Source,content.Icon==null?null:new("app",content.Icon),now,false,full),fingerprint));
                Trim();
            }
            Schedule();
        }
        Changed?.Invoke(this,EventArgs.Empty);
    }
    static string Hash(byte[] bytes)=>Convert.ToHexString(SHA256.HashData(bytes));
    void Trim(){var remove=entries.Where(e=>!e.Entry.Pinned).OrderByDescending(e=>e.Entry.CopiedAt).Skip(settings.Current.Clipboard.Limit).ToHashSet();entries.RemoveAll(remove.Contains);}
    void Schedule(){dirty=true;if(signal.CurrentCount==0)signal.Release();}
    public void SetPinned(string id,bool pinned)=>Change(id,e=>e with{Pinned=pinned});
    void Change(string id,Func<ClipEntry,ClipEntry> update){lock(sync){int i=entries.FindIndex(e=>e.Entry.Id==id);if(i<0)return;entries[i]=entries[i] with{Entry=update(entries[i].Entry)};Trim();Schedule();}Changed?.Invoke(this,EventArgs.Empty);}
    public void Delete(string id){lock(sync){entries.RemoveAll(e=>e.Entry.Id==id);Schedule();}Changed?.Invoke(this,EventArgs.Empty);}
    public void ClearUnpinned(){lock(sync){entries.RemoveAll(e=>!e.Entry.Pinned);Schedule();}Changed?.Invoke(this,EventArgs.Empty);}
    public async Task PasteAsync(string id,bool plainText,nint targetWindow,CancellationToken cancellationToken)
    {
        await Ready.ConfigureAwait(false);ClipEntry entry;lock(sync)entry=entries.First(e=>e.Entry.Id==id).Entry;
        await writer.PasteAsync(entry,plainText,targetWindow,cancellationToken).ConfigureAwait(false);
        if(settings.Current.Clipboard.MoveToTopAfterPaste)Change(id,e=>e with{CopiedAt=DateTimeOffset.UtcNow});
    }
    async Task PersistLoop()
    {
        while(true){
            await signal.WaitAsync().ConfigureAwait(false);await Task.Delay(200).ConfigureAwait(false);
            StoredClip[]? snapshot;bool failed=false;
            lock(sync){snapshot=dirty?entries.ToArray():null;dirty=false;}
            if(snapshot!=null){try{
                    Directory.CreateDirectory(directory);string temp=index+".tmp";
                    using(var file=new FileStream(temp,FileMode.Create,FileAccess.Write,FileShare.None)){JsonSerializer.Serialize(file,snapshot,ClipboardJson.Default.StoredClipArray);file.Flush(true);}
                    File.Move(temp,index,true);
                    // 先持久化新索引，再清理不再引用的图像；中途崩溃只会留下孤立文件。
                    lock(sync){
                        var keep=entries.Concat(snapshot).SelectMany(e=>new[]{e.Entry.ContentPath,e.Entry.Thumbnail?.Path}).Where(p=>p!=null).ToHashSet(StringComparer.OrdinalIgnoreCase);
                        foreach(var path in Directory.EnumerateFiles(directory,"*.png"))if(!keep.Contains(path))File.Delete(path);
                    }
                }catch(IOException){lock(sync){StorageFailures++;dirty=true;failed=true;}}catch(UnauthorizedAccessException){lock(sync){StorageFailures++;dirty=true;failed=true;}}}
            lock(sync){
                // 关闭期间若还有新变更，再提交一次；持久化失败时保留上一个完整索引。
                if(stopping&&dirty&&!failed){if(signal.CurrentCount==0)signal.Release();continue;}
                if(stopping)return;
                if(dirty&&signal.CurrentCount==0)signal.Release();
            }
        }
    }
    public void Dispose()
    {
        try{Ready.GetAwaiter().GetResult();}finally{
            source.Captured-=Capture;settings.Changed-=SettingsChanged;source.Dispose();
            lock(sync){stopping=true;if(signal.CurrentCount==0)signal.Release();}persistence.GetAwaiter().GetResult();signal.Dispose();
        }
    }
}
