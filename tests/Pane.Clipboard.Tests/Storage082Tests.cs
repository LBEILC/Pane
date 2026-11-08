using System.Diagnostics;
using System.Text.Json;
using Pane.Contracts;
using Pane.Localization;
using Pane.Services.Clipboard;
using Pane.Settings;

static partial class Program
{
    sealed class StorageImages:IClipboardImages
    {
        public (int Width,int Height) Save(byte[] png,string full,string thumb){File.WriteAllBytes(full,png);File.WriteAllBytes(thumb,new byte[32]);return (1,1);}
    }
    sealed class InlineProgress(Action<double> report):IProgress<double>{public void Report(double value)=>report(value);}
    static DataStorage StorageService(SettingsStore store,Func<ClipboardHistory?>? history=null,Action? rebuild=null,Action<string>? notify=null,Action<string,string>? copy=null,Func<string,long>? space=null)
        =>new(store,history??(()=>null),rebuild??(()=>{}),notify){InstallDirectory=Path.Combine(Path.GetDirectoryName(store.DirectoryPath)!,"install"),SystemDirectories=[Path.Combine(Path.GetDirectoryName(store.DirectoryPath)!,"system")],Copy=copy??((a,b)=>File.Copy(a,b)),FreeSpace=space??(_=>long.MaxValue)};
    static void Put(string path,int length){Directory.CreateDirectory(Path.GetDirectoryName(path)!);File.WriteAllBytes(path,new byte[length]);}
    static async Task<DataStorageException> StorageFails(Func<Task> work,string label)
    {
        try{await work();throw new Exception("未拒绝："+label);}catch(DataStorageException e){Check(true,label);return e;}
    }
    static async Task Storage082Tests()
    {
        int before=checks;
        await StorageMeasure082();await StorageMove082();await StorageFallback082();await StorageLimit082();await StorageTransaction082();
        Console.WriteLine($"PASS storage082 assertions={checks-before};systemClipboardRead=0;systemClipboardWrite=0");
    }
    static async Task StorageMeasure082()
    {
        string root=TestRoot(),data=Path.Combine(root,"data"),external=Path.Combine(root,"external","Pane");
        var store=new SettingsStore(data,Defaults() with{Storage=new(external)});
        Put(Path.Combine(data,"settings.json"),11);Put(Path.Combine(data,"gestures.json"),12);
        Put(Path.Combine(data,"launcher-usage.json"),13);Put(Path.Combine(data,"terminal-history.json"),14);
        Put(Path.Combine(data,"clipboard","history.json"),15);Put(Path.Combine(data,"clipboard","icons","app.png"),16);Put(Path.Combine(external,"image.png"),17);
        Put(Path.Combine(data,"launcher-index.json"),18);Put(Path.Combine(data,"launcher-icons","one.png"),19);
        Put(Path.Combine(data,"masked-icons","one.png"),20);Put(Path.Combine(data,"file-icons","one.png"),21);Put(Path.Combine(data,"updates","setup.exe"),22);
        Put(Path.Combine(data,"logs","old.log"),23);Put(Path.Combine(data,"ai-secrets.json"),500);Put(Path.Combine(data,"winv-original.json"),500);
        int rebuilt=0,changed=0;var service=StorageService(store,rebuild:()=>rebuilt++);service.Changed+=(_,_)=>changed++;
        var report=await service.MeasureAsync(default);
        foreach(var expected in new[]{(StorageCategory.Settings,23L,2),(StorageCategory.Usage,27L,2),(StorageCategory.Clipboard,48L,3),(StorageCategory.Cache,100L,5),(StorageCategory.Logs,23L,1)}){
            var actual=report.Items.Single(i=>i.Category==expected.Item1);Check(actual.Bytes==expected.Item2&&actual.Files==expected.Item3,"分类统计 "+expected.Item1);
        }
        Check(report.Total==221&&report.Items.Single(i=>i.Category==StorageCategory.Clipboard).Folder==Path.Combine(data,"clipboard"),"总量和打开文件夹");
        string busy=Path.Combine(data,"logs","busy.log");Put(busy,30);
        using(var held=new FileStream(busy,FileMode.Open,FileAccess.ReadWrite,FileShare.None)){
            Check((await service.MeasureAsync(default)).Items.Single(i=>i.Category==StorageCategory.Logs).Bytes==23,"读不了的文件跳过");
            Check(await service.ClearAsync(StorageCategory.Logs,default)==23&&File.Exists(busy),"占用日志跳过");
        }
        Check(await service.ClearAsync(StorageCategory.Logs,default)==30,"释放后的日志能清理");
        new ClipboardLog(data).Write("fixture");Check(File.Exists(Path.Combine(data,"logs","clipboard.log")),"清空后日志继续写");
        Check(await service.ClearAsync(StorageCategory.Cache,default)==100&&rebuilt==1,"清缓存并触发假索引重建");
        Check((await service.MeasureAsync(default)).Items.Single(i=>i.Category==StorageCategory.Cache).Files==0,"缓存清空");
        foreach(var category in new[]{StorageCategory.Settings,StorageCategory.Usage,StorageCategory.Clipboard}){
            try{await service.ClearAsync(category,default);throw new Exception("非法清理类别");}catch(ArgumentException){Check(true,"拒绝 "+category);}
        }
        Check(File.Exists(Path.Combine(data,"settings.json"))&&File.Exists(Path.Combine(external,"image.png"))&&changed==3,"清理不影响设置和图片并通知");
        for(int i=0;i<10000;i++)Put(Path.Combine(data,"file-icons",$"{i}.png"),1);
        var watch=Stopwatch.StartNew();var measured=await service.MeasureAsync(default);watch.Stop();
        Check(measured.Items.Single(i=>i.Category==StorageCategory.Cache) is {Files:10000,Bytes:10000},"一万文件统计");
        Console.WriteLine($"storage082 measure files=10000 elapsedMs={watch.Elapsed.TotalMilliseconds:F3}");
    }
    static async Task StorageMove082()
    {
        string root=TestRoot(),data=Path.Combine(root,"data"),target=Path.Combine(root,"large");
        Check(DataStorage.InstallationRoot(Path.Combine(root,"install","app"))==Path.Combine(root,"install"),"安装目录包含 app 的父目录");
        var store=new SettingsStore(data,Defaults());store.Save(Defaults());var source=new FakeSource();
        using var history=new ClipboardHistory(store,source,new FakeWriter(),new StorageImages());await history.Ready;
        source.Emit(new(Png:[1,2,3]));source.Emit(new(Text:"fixture"));
        var original=history.Snapshot().Single(e=>e.Kind==ClipKind.Image);var service=StorageService(store,()=>history);
        int changed=0;service.Changed+=(_,_)=>changed++;var progress=new List<double>();
        await service.MoveLargeFilesAsync(target,new InlineProgress(progress.Add),default);
        string destination=Path.Combine(target,"Pane");var moved=history.Snapshot().Single(e=>e.Kind==ClipKind.Image);
        Check(Path.GetDirectoryName(moved.ContentPath)==destination&&File.ReadAllBytes(moved.ContentPath!).SequenceEqual(new byte[]{1,2,3}),"搬到 Pane 子目录并保持内容");
        Check(!File.Exists(original.ContentPath)&&!File.Exists(original.Thumbnail!.Path)&&File.Exists(moved.Thumbnail!.Path),"原图和缩略图搬移");
        Check(store.Current.Storage.LargeFilesDirectory==destination&&service.LargeFiles==new LargeFilesLocation(destination,false,destination,false),"设置和状态切换");
        var loaded=new SettingsStore(data,Defaults());loaded.Load();var disk=new ClipboardStorage().Load(Path.Combine(data,"clipboard","history.json"));
        Check(loaded.Current.Storage.LargeFilesDirectory==destination&&disk.Where(e=>e.Entry.Kind==ClipKind.Image).All(e=>File.Exists(e.Entry.ContentPath)&&File.Exists(e.Entry.Thumbnail!.Path)),"重新加载路径可读");
        Check(progress.First()==0&&progress.Last()==1&&changed==1,"进度和 Changed");
        await service.MoveLargeFilesAsync(null,null,default);
        Check(history.Snapshot().Single(e=>e.Kind==ClipKind.Image).ContentPath==original.ContentPath&&File.Exists(original.ContentPath)&&!File.Exists(moved.ContentPath)&&store.Current.Storage.LargeFilesDirectory==null,"搬回默认位置");
        string json=File.ReadAllText(Path.Combine(data,"settings.json")),index=File.ReadAllText(Path.Combine(data,"clipboard","history.json"));
        string absent=Enumerable.Range('D',23).Select(c=>$"{(char)c}:\\").First(p=>!Directory.Exists(p));
        foreach(string invalid in new[]{Path.Combine(absent,"pane-nonexistent-disk-082"),data,Path.Combine(root,"install"),Path.Combine(root,"system")}){
            var error=await StorageFails(()=>service.MoveLargeFilesAsync(invalid,null,default),"目标拒绝 "+Path.GetFileName(invalid));
            Check(error.Message==Strings.Data_MoveTargetInvalid&&File.ReadAllText(Path.Combine(data,"settings.json"))==json&&File.ReadAllText(Path.Combine(data,"clipboard","history.json"))==index&&File.Exists(original.ContentPath),"无效目标不改变数据");
        }
        int copies=0;var failed=StorageService(store,()=>history,copy:(a,b)=>{File.Copy(a,b);if(++copies==2)throw new IOException();});
        await StorageFails(()=>failed.MoveLargeFilesAsync(target,null,default),"第二个副本失败");
        Check(File.Exists(original.ContentPath)&&File.Exists(original.Thumbnail!.Path)&&File.ReadAllText(Path.Combine(data,"settings.json"))==json&&File.ReadAllText(Path.Combine(data,"clipboard","history.json"))==index&&!Directory.EnumerateFiles(destination).Any(),"复制失败完整回滚无残留");
        var lowSpace=StorageService(store,()=>history,space:_=>0);
        var noSpace=await StorageFails(()=>lowSpace.MoveLargeFilesAsync(target,null,default),"空间不足");Check(noSpace.Message==string.Format(Strings.Data_MoveNoSpace,"1 MB"),"空间不足报告差额");
        var corrupt=StorageService(store,()=>history,copy:(a,b)=>{File.Copy(a,b);using var stream=new FileStream(b,FileMode.Open,FileAccess.Write);stream.WriteByte(255);});
        await StorageFails(()=>corrupt.MoveLargeFilesAsync(target,null,default),"哈希核对拒绝损坏副本");
        Check(File.Exists(original.ContentPath)&&!Directory.EnumerateFiles(destination).Any()&&File.ReadAllText(Path.Combine(data,"clipboard","history.json"))==index,"哈希失败回滚");
        using(var held=new FileStream(original.Thumbnail!.Path!,FileMode.Open,FileAccess.Read,FileShare.Read)){
            await StorageFails(()=>service.MoveLargeFilesAsync(target,null,default),"删除旧图失败");
            Check(File.Exists(original.ContentPath)&&File.Exists(original.Thumbnail.Path)&&!Directory.EnumerateFiles(destination).Any()&&File.ReadAllText(Path.Combine(data,"settings.json"))==json&&File.ReadAllText(Path.Combine(data,"clipboard","history.json"))==index,"删除失败恢复已删文件和两个 JSON");
        }
        using var copying=new ManualResetEventSlim();using var resume=new ManualResetEventSlim();using var capturing=new ManualResetEventSlim();int copyCount=0;
        var slow=StorageService(store,()=>history,copy:(a,b)=>{if(Interlocked.Increment(ref copyCount)==1){copying.Set();if(!resume.Wait(10000))throw new TimeoutException();}File.Copy(a,b);});
        var moving=slow.MoveLargeFilesAsync(target,null,default);Check(copying.Wait(10000),"搬移开始");
        var capture=Task.Run(()=>{capturing.Set();source.Emit(new(Png:[9,8,7]));});Check(capturing.Wait(10000),"搬移中收到新图片");
        resume.Set();await moving;await capture;
        Check(history.Snapshot().Where(e=>e.Kind==ClipKind.Image).Count()==2&&history.Snapshot().Where(e=>e.Kind==ClipKind.Image).All(e=>Path.GetDirectoryName(e.ContentPath)==destination&&File.Exists(e.ContentPath)),"搬移期间图片等候后写入新位置");
        // 已存在且名为 Pane 的目录直接复用，不再追加子目录。
        string direct=Path.Combine(root,"direct","Pane");Directory.CreateDirectory(direct);await slow.MoveLargeFilesAsync(direct,null,default);
        Check(slow.LargeFiles.Path==direct&&!Directory.Exists(Path.Combine(direct,"Pane")),"直接使用 Pane 目录");
    }
    static async Task StorageFallback082()
    {
        string root=TestRoot(),data=Path.Combine(root,"data"),configured=Path.Combine(root,"large","Pane"),offline=Path.Combine(root,"offline");Directory.CreateDirectory(configured);
        var store=new SettingsStore(data,Defaults() with{Storage=new(configured)});store.Save(store.Current);
        string oldId;
        using(var history=new ClipboardHistory(store,new FakeSource(),new FakeWriter(),new StorageImages())){await history.Ready;history.Capture(new(Png:[1,2]));oldId=history.Snapshot().Single().Id;}
        Directory.Move(configured,offline);
        store=new SettingsStore(data,Defaults());store.Load();
        var source=new FakeSource();using var restored=new ClipboardHistory(store,source,new FakeWriter(),new StorageImages());await restored.Ready;
        int notifications=0;var service=StorageService(store,()=>restored,notify:text=>{notifications++;Check(text==string.Format(Strings.Data_LargeFilesUnavailable,configured),"不可用通知文案");});service.NotifyUnavailable();
        Check(service.LargeFiles.Unavailable&&service.LargeFiles.IsDefault&&notifications==1,"回退且同路径仅通知一次");
        Check(restored.Snapshot().Any(e=>e.Id==oldId)&&!File.Exists(restored.Snapshot().Single().ContentPath),"丢失的旧图片保留条目");
        source.Emit(new(Png:[3,4]));Check(restored.Snapshot().Any(e=>Path.GetDirectoryName(e.ContentPath)==Path.Combine(data,"clipboard")),"新图片写入默认位置");
        var failure=await StorageFails(()=>service.ReuseConfiguredAsync(null,default),"仍不可用拒绝复用");Check(failure.Message==Strings.Data_ReuseUnavailable,"不可用复用文案");
        Directory.Move(offline,configured);int changed=0;service.Changed+=(_,_)=>changed++;
        await service.ReuseConfiguredAsync(null,default);
        Check(!service.LargeFiles.Unavailable&&!service.LargeFiles.IsDefault&&changed==1&&restored.Snapshot().Count==2&&restored.Snapshot().All(e=>Path.GetDirectoryName(e.ContentPath)==configured&&File.Exists(e.ContentPath)),"恢复后合并并切回");
        Check(!Directory.EnumerateFiles(Path.Combine(data,"clipboard"),"*.png").Any(),"回退图片已搬走");
    }
    static async Task StorageLimit082()
    {
        string root=TestRoot(),data=Path.Combine(root,"data");var store=new SettingsStore(data,Defaults());store.Save(Defaults());
        // 旧 JSON 没有新增字段，Storage:null 也按默认值读取。
        string json=JsonSerializer.Serialize(Defaults(),SettingsJson.Default.PaneSettings);
        using(var document=JsonDocument.Parse(json))File.WriteAllText(Path.Combine(data,"settings.json"),"{"+string.Join(',',document.RootElement.EnumerateObject().Where(p=>p.Name!="Storage").Select(p=>"\""+p.Name+"\":"+p.Value.GetRawText()))+"}");
        store.Load();Check(store.Current.Storage.LargeFilesDirectory==null&&store.Current.Clipboard.ImageLimitMegabytes==0,"旧设置兼容");
        store.Save(store.Current with{Storage=null!});Check(store.Current.Storage.LargeFilesDirectory==null,"Storage null 默认");
        string pinned,old,newest;var source=new FakeSource();
        using(var history=new ClipboardHistory(store,source,new FakeWriter(),new StorageImages())){
            await history.Ready;
            byte[] bytes=new byte[600*1024];bytes[0]=1;source.Emit(new(Png:bytes));pinned=history.Snapshot()[0].Id;history.SetPinned(pinned,true);
            bytes[0]=2;source.Emit(new(Png:bytes));old=history.Snapshot().First(e=>!e.Pinned).Id;
            source.Emit(new(Text:"fixture retained"));source.Emit(new(Files:[Path.Combine(root,"fake.txt")]));
            bytes[0]=3;source.Emit(new(Png:bytes));newest=history.Snapshot().First(e=>!e.Pinned&&e.Kind==ClipKind.Image).Id;
            Check(history.Snapshot().Count(e=>e.Kind==ClipKind.Image)==3,"上限零不删");
        }
        store.Save(store.Current with{Clipboard=store.Current.Clipboard with{ImageLimitMegabytes=1}});
        using(var history=new ClipboardHistory(store,new FakeSource(),new FakeWriter(),new StorageImages())){
            await history.Ready;
            Check(history.Snapshot().Any(e=>e.Id==pinned)&&!history.Snapshot().Any(e=>e.Id==old)&&history.Snapshot().Any(e=>e.Id==newest),"启动时删最旧且固定不计入");
            Check(history.Snapshot().Any(e=>e.Kind==ClipKind.Text)&&history.Snapshot().Any(e=>e.Kind==ClipKind.Files),"文字文件不受影响");
            byte[] bytes=new byte[600*1024];bytes[0]=4;history.Capture(new(Png:bytes));
            Check(!history.Snapshot().Any(e=>e.Id==newest)&&history.Snapshot().Any(e=>e.Id==pinned)&&history.Snapshot().Count(e=>e.Kind==ClipKind.Image)==2,"存新图触发上限");
            Check(history.Snapshot().Where(e=>e.Kind==ClipKind.Image).All(e=>Path.IsPathFullyQualified(e.ContentPath!)&&File.Exists(e.ContentPath)),"旧完整路径仍可读");
        }
    }
    static async Task StorageTransaction082()
    {
        string root=TestRoot(),data=Path.Combine(root,"data"),target=Path.Combine(root,"large","Pane");
        var store=new SettingsStore(data,Defaults());store.Save(Defaults());
        using(var history=new ClipboardHistory(store,new FakeSource(),new FakeWriter(),new StorageImages())){await history.Ready;history.Capture(new(Png:[11,22]));}
        string index=Path.Combine(data,"clipboard","history.json"),previous=File.ReadAllText(index),settings=File.ReadAllText(Path.Combine(data,"settings.json"));
        var clips=new ClipboardStorage().Load(index);string source=clips[0].Entry.ContentPath!,destination=Path.Combine(target,Path.GetFileName(source));
        Directory.CreateDirectory(target);StorageRecovery.Begin(data,[new(source,destination)]);File.Copy(source,destination);
        store.Save(store.Current with{Storage=new(target)});new ClipboardStorage().Save(index,[clips[0] with{Entry=clips[0].Entry with{ContentPath=destination}}],_=>{});File.Delete(source);
        // 模拟已提交两个 JSON、删除了一部分旧文件后进程中断。
        var restarted=new SettingsStore(data,Defaults());restarted.Load();
        Check(restarted.Current.Storage.LargeFilesDirectory==null&&File.ReadAllText(index)==previous&&File.ReadAllText(Path.Combine(data,"settings.json"))==settings,"启动时原子恢复设置历史");
        Check(File.Exists(source)&&!File.Exists(destination)&&!Directory.Exists(Path.Combine(data,".storage-move")),"启动恢复旧图片并清掉事务副本");
    }
}
