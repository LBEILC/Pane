using System.Buffers.Binary;
using System.IO.Compression;
using System.Text.Json;
using Pane.Contracts;
using Pane.Services.Clipboard;
using Pane.Settings;
using System.Text.Json.Nodes;
using Pane.Localization;

static partial class Program
{
    sealed class TransferProgress: IProgress<double>{internal List<double> Values=[];public void Report(double value)=>Values.Add(value);}
    static async Task Transfer083Tests()
    {
        int begin=checks;
        string root=TestResults.NewDirectory("083-");Directory.CreateDirectory(root);
        SettingsStore Store(string name){var s=new SettingsStore(Path.Combine(root,name),Defaults());s.Save(Defaults());return s;}
        DataStorage Transfer(SettingsStore s)=>new(s,()=>null,()=>{});
        void Put(string path,string value){Directory.CreateDirectory(Path.GetDirectoryName(path)!);File.WriteAllText(path,value);}
        StoredClip Text(string text,bool pinned=false,int age=0)=>new(new(Guid.NewGuid().ToString("N"),ClipKind.Text,text,null,null,null,null,"fixture",null,DateTimeOffset.UtcNow.AddMinutes(-age),pinned),"ignored");
        StoredClip Image(SettingsStore s,byte value,bool pinned=false,int age=0){string id=Guid.NewGuid().ToString("N"),p=Path.Combine(s.Current.Storage.LargeFilesDirectory??Path.Combine(s.DirectoryPath,"clipboard"),id+".png");Directory.CreateDirectory(Path.GetDirectoryName(p)!);File.WriteAllBytes(p,[value,2,3]);File.WriteAllBytes(p.Replace(".png",".thumb.png"),[value]);return new(new(id,ClipKind.Image,null,null,new(p.Replace(".png",".thumb.png")),1,1,"fixture",null,DateTimeOffset.UtcNow.AddMinutes(-age),pinned,p),"untrusted");}
        void Clips(SettingsStore s,params StoredClip[] clips)=>new ClipboardStorage().Save(Path.Combine(s.DirectoryPath,"clipboard/history.json"),clips,_=>{});
        StoredClip[] ReadClips(SettingsStore s)=>new ClipboardStorage().Load(Path.Combine(s.DirectoryPath,"clipboard/history.json"));
        string ZipText(string zip,string name){using var archive=ZipFile.OpenRead(zip);using var reader=new StreamReader(archive.GetEntry(name)!.Open());return reader.ReadToEnd();}
        async Task Reject(Func<Task> action,string message){try{await action();Check(false,"expected rejection");}catch(DataStorageException e){Check(e.Message==message,"error: "+e.Message);}}
        var source=Store("source");source.Save(source.Current with{General=source.Current.General with{StartWithWindows=true,PanelRadius=30},Clipboard=source.Current.Clipboard with{KeepHotkeyWithWinV=false}});
        var originalImage=Image(source,11,true);var text=Text("hello",true,1);
        Clips(source,text,Text("code"),originalImage,new(new("file",ClipKind.Files,null,["D:\\fixture.txt"],null,null,null,"fixture",null,DateTimeOffset.UtcNow,false),"file"));
        Put(Path.Combine(source.DirectoryPath,"gestures.json"),"{}");Put(Path.Combine(source.DirectoryPath,"launcher-usage.json"),"{\"app\":{\"Count\":3,\"LastUsed\":\"2026-11-01T00:00:00Z\"}}");
        Put(Path.Combine(source.DirectoryPath,"terminal-history.json"),"[\"new\",\"shared\"]");
        foreach(string name in new[]{"ai-key","chatgpt-token","winv-original.json","launcher-index.json","logs/test.log"})Put(Path.Combine(source.DirectoryPath,name),"secret");
        string full="";
        foreach(var option in Enum.GetValues<ClipboardExport>()){
            string zip=Path.Combine(root,option+".zip");var progress=new TransferProgress();var result=await Transfer(source).ExportAsync(zip,new(true,true,option),progress,default);
            Check(result.Bytes>0&&progress.Values.First()==0&&progress.Values.Last()==1,"export progress");
            using var archive=ZipFile.OpenRead(zip);var names=archive.Entries.Select(e=>e.FullName).ToArray();
            Check(names.All(n=>n=="manifest.json"||n.StartsWith("settings/")||n.StartsWith("usage/")||n.StartsWith("clipboard/")),"export allowlist");
            var settingsNode=JsonNode.Parse(ZipText(zip,"settings/settings.json"))!;
            Check(settingsNode["Storage"]==null&&settingsNode["General"]!["StartWithWindows"]==null&&settingsNode["Launcher"]!["Position"]==null,"local fields absent");
            Check(settingsNode["Clipboard"]!["KeepHotkeyWithWinV"]!.GetValue<bool>()==false,"084 exported preference");
            var preview=await Transfer(source).InspectAsync(zip,default);Check(preview.HasSettings&&preview.HasUsage&&preview.ClipboardEntries==(option==ClipboardExport.None?0:option==ClipboardExport.TextOnly?2:3)&&preview.ClipboardImages==(option==ClipboardExport.WithImages?1:0),"preview counts");
            if(option!=ClipboardExport.None){var clips=JsonSerializer.Deserialize(ZipText(zip,"clipboard/history.json"),ClipboardJson.Default.StoredClipArray)!;Check(clips.All(c=>c.Entry.Kind!=ClipKind.Files),"no files");Check(clips[0].Entry.Pinned&&clips[0].Entry.CopiedAt==text.Entry.CopiedAt&&clips[0].Entry.Source=="fixture","metadata");}
            Check(names.Count(n=>n.StartsWith("clipboard/images/"))==(option==ClipboardExport.WithImages?2:0),"image entries");if(option==ClipboardExport.WithImages)full=zip;
        }
        string destination=Path.Combine(root,"failed.zip");Put(destination,"previous");
        await Reject(()=>Transfer(source).ExportAsync(destination,new(true,true,ClipboardExport.WithImages),new ThrowProgress(),default),string.Format(Strings.Data_ExportFailed,"IOException (0x80070020)"));
        Check(File.ReadAllText(destination)=="previous"&&!Directory.GetFiles(root,"failed.zip.*.tmp").Any(),"atomic failure");
        string Bad(string name,Action<ZipArchive> create){string path=Path.Combine(root,name+".zip");using var zip=ZipFile.Open(path,ZipArchiveMode.Create);create(zip);return path;}
        void Entry(ZipArchive zip,string name,string content){using var writer=new StreamWriter(zip.CreateEntry(name).Open());writer.Write(content);}
        var manifest=JsonNode.Parse(ZipText(full,"manifest.json"))!.AsObject();manifest["settings"]=false;manifest["usage"]=false;manifest["clipboard"]=false;manifest["clipboardEntries"]=0;manifest["clipboardImages"]=0;
        string none=Bad("empty",z=>Entry(z,"manifest.json",manifest.ToJsonString()));
        await Reject(()=>Transfer(source).StageImportAsync(none,new(true,true,true,DataImportMode.Replace),null,default),Strings.Data_ImportNothing);
        string plain=Path.Combine(root,"plain");Put(plain,"plain");
        foreach(string bad in new[]{plain,Bad("no-manifest",_=>{}),Bad("broken",z=>Entry(z,"manifest.json","{"))})await Reject(()=>Transfer(source).InspectAsync(bad,default),Strings.Data_ImportInvalid);
        var newer=manifest.DeepClone();newer["format"]=2;await Reject(()=>Transfer(source).InspectAsync(Bad("newer",z=>Entry(z,"manifest.json",newer.ToJsonString())),default),Strings.Data_ImportNewerFormat);
        foreach(string name in new[]{"../escape.txt","/absolute.txt","C:/absolute.txt","clipboard/../../escape.txt"}){
            string bad=Bad("slip"+Guid.NewGuid().ToString("N"),z=>{Entry(z,"manifest.json",manifest.ToJsonString());Entry(z,name,"bad");});
            await Reject(()=>Transfer(source).StageImportAsync(bad,new(true,true,true,DataImportMode.Replace),null,default),Strings.Data_ImportInvalid);
            Check(!Directory.Exists(Path.Combine(source.DirectoryPath,".import-pending"))&&!File.Exists(Path.Combine(root,"escape.txt")),"slip leaves no output");
        }
        string many=Bad("many",z=>{for(int i=0;i<=DataStorage.ImportEntryLimit;i++)z.CreateEntry("n"+i);});
        await Reject(()=>Transfer(source).StageImportAsync(many,new(true,true,true,DataImportMode.Replace),null,default),Strings.Data_ImportTooLarge);
        // 改中央目录中的解压尺寸，无需生成数 GB 夹具。
        string huge=Bad("huge",z=>Entry(z,"manifest.json",manifest.ToJsonString()));byte[] bytes=File.ReadAllBytes(huge);
        for(int i=0;i<bytes.Length-46;i++)if(BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(i,4))==0x02014b50){BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(i+24,4),(uint)(DataStorage.ImportFileLimit+1));break;}File.WriteAllBytes(huge,bytes);
        await Reject(()=>Transfer(source).StageImportAsync(huge,new(true,true,true,DataImportMode.Replace),null,default),Strings.Data_ImportTooLarge);
        Check(!Directory.Exists(Path.Combine(source.DirectoryPath,".import-pending")),"size rejection cleared");
        string totalZip=Bad("total",z=>{Entry(z,"manifest.json",manifest.ToJsonString());for(int i=0;i<5;i++)Entry(z,"large"+i,"");});
        bytes=File.ReadAllBytes(totalZip);
        for(int i=0;i<bytes.Length-46;i++)if(BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(i,4))==0x02014b50)BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(i+24,4),(uint)DataStorage.ImportFileLimit);
        File.WriteAllBytes(totalZip,bytes);
        await Reject(()=>Transfer(source).StageImportAsync(totalZip,new(true,true,true,DataImportMode.Replace),null,default),Strings.Data_ImportTooLarge);
        var target=Store("target");string large=Path.Combine(root,"large");Directory.CreateDirectory(large);target.Save(target.Current with{Storage=new(large),General=target.Current.General with{StartWithWindows=false},Launcher=target.Current.Launcher with{Position=new("fixture",.2,.3)}});
        var oldImage=Image(target,22);Clips(target,Text("old"),oldImage);
        Put(Path.Combine(target.DirectoryPath,"launcher-usage.json"),"{\"app\":{\"Count\":5,\"LastUsed\":\"2026-11-02T00:00:00Z\"}}");Put(Path.Combine(target.DirectoryPath,"terminal-history.json"),"[\"local\",\"shared\"]");
        async Task Apply(SettingsStore s,DataImportOptions options,string? zip=null){await Transfer(s).StageImportAsync(zip??full,options,null,default);StorageRecovery.RecoverAll(s.DirectoryPath);s.Load();Check(Transfer(s).LastImport?.Error==null,"apply success");}
        await Apply(target,new(true,false,false,DataImportMode.Replace));
        Check(!target.Current.Clipboard.KeepHotkeyWithWinV,"084 imported preference");
        Check(target.Current.General.PanelRadius==30&&!target.Current.General.StartWithWindows&&target.Current.Storage.LargeFilesDirectory==large&&target.Current.Launcher.Position?.MonitorId=="fixture","local settings preserved");
        Check(ReadClips(target).Length==2&&File.ReadAllText(Path.Combine(target.DirectoryPath,"launcher-usage.json")).Contains("5"),"settings only");
        await Apply(target,new(false,true,false,DataImportMode.Merge));
        var usage=JsonNode.Parse(File.ReadAllText(Path.Combine(target.DirectoryPath,"launcher-usage.json")))!;
        Check(usage["app"]!["Count"]!.GetValue<int>()==8&&usage["app"]!["LastUsed"]!.GetValue<DateTimeOffset>().Day==2,"usage merge");
        Check(JsonNode.Parse(File.ReadAllText(Path.Combine(target.DirectoryPath,"terminal-history.json")))!.AsArray().Select(n=>n!.GetValue<string>()).SequenceEqual(["local","shared","new"]),"terminal merge");
        await Apply(target,new(false,true,false,DataImportMode.Replace));Check(JsonNode.Parse(File.ReadAllText(Path.Combine(target.DirectoryPath,"launcher-usage.json")))!["app"]!["Count"]!.GetValue<int>()==3,"usage replace");
        await Apply(target,new(false,false,true,DataImportMode.Replace));
        var imported=ReadClips(target);Check(imported.Length==3&&!File.Exists(oldImage.Entry.ContentPath)&&imported.Single(c=>c.Entry.Kind==ClipKind.Image).Entry.ContentPath!.StartsWith(large)&&File.Exists(imported.Single(c=>c.Entry.Kind==ClipKind.Image).Entry.ContentPath),"clipboard replace images");
        Check(Directory.GetFiles(Path.Combine(target.DirectoryPath,"backups"),"*.zip").Length==3,"backup retention");
        string backup=Transfer(target).LastImport!.Backup;await Apply(target,new(false,false,true,DataImportMode.Replace),backup);Check(ReadClips(target).Any(c=>c.Entry.Text=="old"),"backup restore");
        target.Save(target.Current with{Clipboard=target.Current.Clipboard with{Limit=200,ImageLimitMegabytes=1}});
        var pin=Text("keep pin",true,999);var duplicate=Image(target,11);var oversized=Image(target,33,false,500);File.WriteAllBytes(oversized.Entry.ContentPath!,new byte[2*1024*1024]);
        Clips(target,Enumerable.Range(0,205).Select(i=>Text("item"+i,false,i)).Concat([pin,Text("hello"),duplicate,oversized]).ToArray());
        await Apply(target,new(false,false,true,DataImportMode.Merge));
        var merged=ReadClips(target);Check(merged.Count(c=>c.Entry.Text=="hello")==1&&merged.Count(c=>c.Entry.Kind==ClipKind.Image)==1,"dedupe text image");Check(merged.Count(c=>!c.Entry.Pinned)<=200&&merged.Any(c=>c.Entry.Text=="keep pin")&&!File.Exists(oversized.Entry.ContentPath),"limits pinned");
        // 单独让图片容量触发，避免仅由条数上限淘汰。
        var capacity=Image(target,44,false,10);File.WriteAllBytes(capacity.Entry.ContentPath!,new byte[2*1024*1024]);Clips(target,pin,capacity);
        await Apply(target,new(false,false,true,DataImportMode.Merge));Check(!ReadClips(target).Any(c=>c.Entry.Id==capacity.Entry.Id)&&!File.Exists(capacity.Entry.ContentPath)&&ReadClips(target).Any(c=>c.Entry.Pinned&&c.Entry.Kind==ClipKind.Image),"image capacity");
        var before=Directory.GetFiles(target.DirectoryPath,"*.json").Concat(Directory.GetFiles(Path.Combine(target.DirectoryPath,"clipboard"),"*.json")).Concat(Directory.GetFiles(large,"*.png")).Where(p=>!p.EndsWith(".import-result.json")).ToDictionary(p=>p,File.ReadAllBytes);
        await Transfer(target).StageImportAsync(full,new(true,true,true,DataImportMode.Replace),null,default);
        DataStorage.ImportStep=step=>{if(step=="applied-file")throw new IOException();};try{StorageRecovery.RecoverAll(target.DirectoryPath);}finally{DataStorage.ImportStep=null;}
        Check(Transfer(target).LastImport?.Error!=null&&before.All(p=>File.Exists(p.Key)&&p.Value.SequenceEqual(File.ReadAllBytes(p.Key))),"failure exact rollback");
        // 在首次写入后保留事务与部分状态，模拟进程被终止的磁盘现场。
        string crashCopy=Path.Combine(root,"crash-copy");
        await Transfer(target).StageImportAsync(full,new(true,true,true,DataImportMode.Replace),null,default);
        DataStorage.ImportStep=step=>{if(step!="applied-file"||Directory.Exists(crashCopy))return;CopyTree083(Path.Combine(target.DirectoryPath,".import-pending"),crashCopy);};
        try{StorageRecovery.RecoverAll(target.DirectoryPath);}finally{DataStorage.ImportStep=null;}
        CopyTree083(crashCopy,Path.Combine(target.DirectoryPath,".import-pending"));StorageRecovery.RecoverAll(target.DirectoryPath);
        Check(Transfer(target).LastImport?.Error!=null&&before.All(p=>File.Exists(p.Key)&&p.Value.SequenceEqual(File.ReadAllBytes(p.Key))),"interrupted restart rollback");
        Transfer(target).AcknowledgeImport();Check(Transfer(target).LastImport==null,"ack durable");
        var commands=Store("commands");Put(Path.Combine(commands.DirectoryPath,"terminal-history.json"),new JsonArray(Enumerable.Range(0,25).Select(i=>(JsonNode?)JsonValue.Create("cmd"+i)).ToArray()).ToJsonString());
        await Apply(commands,new(false,true,false,DataImportMode.Merge));Check(JsonNode.Parse(File.ReadAllText(Path.Combine(commands.DirectoryPath,"terminal-history.json")))!.AsArray().Count==20,"terminal limit");
        // 同时存在搬移恢复和导入任务时，导入备份必须看到搬移之前的数据。
        var both=Store("both");var bothImage=Image(both,55);Clips(both,bothImage);string moved=Path.Combine(root,"moved");Directory.CreateDirectory(moved);string movedImage=Path.Combine(moved,Path.GetFileName(bothImage.Entry.ContentPath)!);
        await Transfer(both).StageImportAsync(full,new(true,false,false,DataImportMode.Replace),null,default);
        StorageRecovery.Begin(both.DirectoryPath,[new(bothImage.Entry.ContentPath!,movedImage)]);File.Copy(bothImage.Entry.ContentPath!,movedImage);File.Delete(bothImage.Entry.ContentPath!);both.Save(both.Current with{Storage=new(moved)});
        both.Load();Check(Transfer(both).LastImport?.Error==null&&both.Current.Storage.LargeFilesDirectory==null&&File.Exists(bothImage.Entry.ContentPath)&&!File.Exists(movedImage),"082 before 083");
        var empty=new SettingsStore(Path.Combine(root,"roundtrip"),Defaults());await Apply(empty,new(true,true,true,DataImportMode.Replace));string again=Path.Combine(root,"again.zip");await Transfer(empty).ExportAsync(again,new(true,true,ClipboardExport.WithImages),null,default);
        foreach(string name in new[]{"settings/settings.json","usage/launcher-usage.json","usage/terminal-history.json"})Check(JsonNode.DeepEquals(JsonNode.Parse(ZipText(full,name)),JsonNode.Parse(ZipText(again,name))),"roundtrip "+name);
        var round=JsonSerializer.Deserialize(ZipText(again,"clipboard/history.json"),ClipboardJson.Default.StoredClipArray)!;
        Check(round.Length==3&&round.Where(c=>c.Entry.Kind!=ClipKind.Image).Select(c=>c.Entry.Text).ToHashSet().SetEquals(["hello","code"]),"roundtrip clips");
        // 活跃服务尚未落盘的条目也必须出现在导出快照中。
        var live=Store("snapshot");var fake=new FakeSource();using(var active=new ClipboardHistory(live,fake,new FakeWriter(),new WindowsClipboardImages())){await active.Ready;fake.Emit(new(Text:"live"));string liveZip=Path.Combine(root,"live.zip");await new DataStorage(live,()=>active,()=>{}).ExportAsync(liveZip,new(false,false,ClipboardExport.TextOnly),null,default);Check((await Transfer(live).InspectAsync(liveZip,default)).ClipboardEntries==1,"live snapshot");}
        Console.WriteLine($"PASS transfer083 assertions={checks-begin};systemClipboard=0");
    }
    sealed class ThrowProgress:IProgress<double>{public void Report(double value){if(value>0)throw new IOException("fixture",unchecked((int)0x80070020));}}
    static void CopyTree083(string source,string target){Directory.CreateDirectory(target);foreach(string file in Directory.GetFiles(source,"*",SearchOption.AllDirectories)){string dest=Path.Combine(target,Path.GetRelativePath(source,file));Directory.CreateDirectory(Path.GetDirectoryName(dest)!);File.Copy(file,dest,true);}}
}
