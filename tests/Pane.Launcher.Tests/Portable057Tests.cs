using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json;
using Pane.Contracts;
using Pane.Services.Launcher;
using Pane.Settings;

static partial class Program
{
    [DllImport("Pane.Native.dll",CharSet=CharSet.Unicode)]
    static extern int CreatePaneShortcut(string path,string executable,string directory);
    [DllImport("kernel32.dll",CharSet=CharSet.Unicode,SetLastError=true)]
    static extern Microsoft.Win32.SafeHandles.SafeFileHandle CreateFileW(string path,uint access,uint share,nint security,uint disposition,uint flags,nint template);
    [DllImport("kernel32.dll",SetLastError=true)]
    [return:MarshalAs(UnmanagedType.Bool)]
    static extern bool DeviceIoControl(Microsoft.Win32.SafeHandles.SafeFileHandle file,uint code,byte[] input,int size,nint output,int outputSize,out int returned,nint overlapped);
    static void Junction057(string path,string target)
    {
        Directory.CreateDirectory(path);
        byte[] substitute=System.Text.Encoding.Unicode.GetBytes(@"\??\"+Path.GetFullPath(target));
        byte[] print=System.Text.Encoding.Unicode.GetBytes(Path.GetFullPath(target));
        using var bytes=new MemoryStream();using(var writer=new BinaryWriter(bytes,System.Text.Encoding.Unicode,true)){
            writer.Write(0xA0000003u);writer.Write((ushort)(8+substitute.Length+2+print.Length+2));writer.Write((ushort)0);
            writer.Write((ushort)0);writer.Write((ushort)substitute.Length);writer.Write((ushort)(substitute.Length+2));writer.Write((ushort)print.Length);
            writer.Write(substitute);writer.Write((ushort)0);writer.Write(print);writer.Write((ushort)0);
        }
        using var handle=CreateFileW(path,0x40000000,0,0,3,0x02200000,0);
        if(handle.IsInvalid||!DeviceIoControl(handle,0x900a4,bytes.ToArray(),(int)bytes.Length,0,0,out _,0))throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
    }
    static string Empty057(string root,string relative)
    {
        string path=Path.GetFullPath(Path.Combine(root,relative));Directory.CreateDirectory(Path.GetDirectoryName(path)!);File.WriteAllText(path,"");return path;
    }
    static PortableEntry Portable057(string path,PortableKind kind=PortableKind.Program)=>new(path,kind,DateTimeOffset.UtcNow);
    static LauncherItem File057(string path)=>new("file:"+path,LauncherItemKind.File,LauncherGroups.Files,Path.GetFileName(path),"",new("doc"));
    static LauncherItem App057(IndexedApp app)=>new(app.Id,LauncherItemKind.App,LauncherGroups.Applications,app.Name.Name,"",new("app"));
    sealed class Executor057:ILauncherExecutor
    {
        internal Exception? Failure;internal int Opens;
        public Task OpenAsync(string path,bool packaged,CancellationToken token){token.ThrowIfCancellationRequested();if(Failure!=null)throw Failure;Opens++;return Task.CompletedTask;}
        public Task RevealAsync(string path,CancellationToken token)=>Task.CompletedTask;
        public Task CopyAsync(string path,CancellationToken token)=>Task.CompletedTask;
        public Task CommandAsync(string id,CancellationToken token)=>Task.CompletedTask;
    }
    sealed class Index057(SettingsStore store,IndexedApp[]? installed=null):ILauncherIndex
    {
        internal TaskCompletionSource? Hold;internal int Builds;
        public PersistedIndex? Load()=>null;
        public async Task<PersistedIndex> BuildAsync(FavoriteEntry[] favorites,CancellationToken token)
        {
            Builds++;var entries=store.Current.Launcher.Portable.ToArray();
            var scan=PortableCatalog.Scan(entries,token);var table=new PinyinTable();
            var apps=scan.Candidates.Select(c=>new IndexedApp(c.Row.LegacyId,table.Create(c.Row.Name),c.Row.Parsing,false,null){Target=c.Row.Target,PortablePath=c.Owner,PortableKind=c.Kind}).DistinctBy(a=>a.Id).ToArray();
            var value=new PersistedIndex(1,new([..installed??[],..apps],[],[]),new(0,0,0,0,apps.Length,0,0){PortableStatus=scan.Status},DateTimeOffset.UtcNow);
            if(Hold is {} hold)await hold.Task.WaitAsync(token);
            return value;
        }
    }
    static async Task Settle057(LauncherSearch search)
    {
        using var deadline=new CancellationTokenSource(TimeSpan.FromSeconds(15));
        do {await search.Indexing.WaitAsync(deadline.Token);await Task.Delay(5,deadline.Token);}while(search.Rebuilding);
    }
    static async Task Portable057Tests()
    {
        int before=checks;string root=TestResults.NewDirectory("057-");Directory.CreateDirectory(root);
        var now=DateTimeOffset.UtcNow;
        string folder=Path.Combine(root,"folder"),first=Empty057(folder,"Alpha.exe"),second=Empty057(folder,"child/Beta.exe");
        Empty057(folder,"child/deep/TooDeep.exe");Empty057(folder,"notes.txt");
        string[] auxiliary=["uninstall","unins000","卸载","卸載","帮助","幫助","說明","readme","app help","help","setup","install","update","updater","crash","crashpad","helper","reporter"];
        foreach(string name in auxiliary)Empty057(folder,name+".exe");
        string outside=Path.Combine(root,"outside");Empty057(outside,"Linked.exe");
        string linkDir=Path.Combine(folder,"linked");
        try{
            Directory.CreateSymbolicLink(linkDir,outside);
            File.CreateSymbolicLink(Path.Combine(folder,"LinkedFile.exe"),Path.Combine(outside,"Linked.exe"));
            Console.WriteLine("057 links=directory-and-file-symlinks");
        }catch(IOException e) when((e.HResult&0xffff)==1314){Junction057(linkDir,outside);Console.WriteLine("057 links=junction;symlinkPrivilege=False");}
        var scan=PortableCatalog.Scan([Portable057(folder,PortableKind.Folder),Portable057(folder.ToUpperInvariant(),PortableKind.Folder)],default);
        Check(scan.Candidates.Select(c=>c.Row.Target).SequenceEqual([first,second]),"057 两层扫描、辅助程序与符号链接跳过、大小写去重");
        Check(scan.Status[folder]==new PortableStatus(PortableScanState.Ready,2),"057 文件夹状态与数量");
        Check(PortableCatalog.Scan([Portable057(linkDir,PortableKind.Folder)],default).Candidates.Length==0,"057 链接根目录不跟随");
        string huge=Path.Combine(root,"huge");for(int i=304;i>=0;i--)Empty057(huge,$"App{i:D3}.exe");
        var capped=PortableCatalog.Scan([Portable057(huge,PortableKind.Folder)],default);
        Check(capped.Candidates.Length==300&&Path.GetFileName(capped.Candidates[^1].Row.Target)=="App299.exe","057 按文件名排序截取300个");
        Check(capped.Status[huge].Programs==300&&capped.Truncated.SequenceEqual([huge]),"057 截断诊断与实际数量");
        string shortcut=Path.Combine(root,"Alpha.lnk"),text=Empty057(root,"plain.txt"),nonExe=Path.Combine(root,"Text.lnk");
        Marshal.ThrowExceptionForHR(CreatePaneShortcut(shortcut,first,root));Marshal.ThrowExceptionForHR(CreatePaneShortcut(nonExe,text,root));
        var links=PortableCatalog.Scan([Portable057(shortcut),Portable057(nonExe),Portable057(Path.Combine(root,"gone.exe"))],default);
        Check(links.Candidates.Length==1&&links.Candidates[0].Row.Target==first&&links.Candidates[0].Row.Parsing==shortcut,"057 真实快捷方式仅接纳exe且保留启动入口");
        Check(links.Status[shortcut].Programs==1&&links.Status[Path.Combine(root,"gone.exe")].State==PortableScanState.Missing,"057 单程序存在和缺失状态");
        Check(PortableCatalog.Scan([Portable057(Path.Combine(root,"gone"),PortableKind.Folder)],default).Status.Values.Single().State==PortableScanState.Missing,"057 缺失目录状态");
        var row=scan.Candidates[0].Row;var installed=row with{Source=0,Name="Installed",LegacyId="app:installed"};
        var groups=AppDeduplication.Resolve([row,installed]);
        Check(groups.Length==1&&groups[0].StableId==installed.LegacyId&&groups[0].MergedIds.Contains(row.LegacyId),"057 与安装应用合并并保留安装Id及迁移别名");
        Check(PortableCatalog.Counts(scan,AppDeduplication.Resolve([..scan.Candidates.Select(c=>c.Row),installed]))[folder].Programs==1,"057 文件夹数量扣除安装应用重复项");
        Check(AppDeduplication.Resolve([row,installed with{Args="--profile=A"}]).Length==2,"057 参数不同不合并");
        Check(PortableCatalog.Id(first)==PortableCatalog.Id(first.ToUpperInvariant()),"057 稳定Id不受大小写影响");
        Check(PortableCatalog.DisplayName(first,"Application")=="Alpha"&&PortableCatalog.DisplayName(first,"  ")=="Alpha"&&PortableCatalog.DisplayName(first,"Alpha Editor")=="Alpha Editor","057 显示名回退与版本说明");
        var store=new SettingsStore(Path.Combine(root,"settings"),Defaults());
        store.Save(Defaults() with{Launcher=Defaults().Launcher with{Portable=[Portable057(first),Portable057(first.ToUpperInvariant())],RememberOpenedPrograms=false}});store.Load();
        Check(store.Current.Launcher.Portable.Length==1&&!store.Current.Launcher.RememberOpenedPrograms&&store.Current.Launcher.Portable[0].Kind==PortableKind.Program,"057 设置读写与路径去重");
        string json=File.ReadAllText(Path.Combine(store.DirectoryPath,"settings.json"));
        Check(json.Contains("\"Program\""),"057 枚举沿用字符串存储");
        var old=System.Text.Json.Nodes.JsonNode.Parse(json)!;old["Launcher"]!.AsObject().Remove("Portable");old["Launcher"]!.AsObject().Remove("RememberOpenedPrograms");
        File.WriteAllText(Path.Combine(store.DirectoryPath,"settings.json"),old.ToJsonString());store.Load();
        Check(store.Current.Launcher.Portable.Length==0&&store.Current.Launcher.RememberOpenedPrograms,"057 旧设置默认值");
        var executor=new Executor057();var index=new Index057(store);
        using(var search=new LauncherSearch(store,index,new FakeFiles(),executor)){
            await Settle057(search);int changed=0;((IPortableAppsStatus)search).Changed+=(_,_)=>Interlocked.Increment(ref changed);
            await search.ExecuteAsync(File057(first),default);await Settle057(search);
            Check(store.Current.Launcher.Portable.Single().Kind==PortableKind.Learned,"057 文件组成功打开后记忆");
            Check((await search.SearchAsync("Alpha",default)).Items.Any(i=>i.Id==PortableCatalog.Id(first))&&changed>0,"057 记忆触发重建及状态事件");
            await search.ExecuteAsync(File057(first),default);
            Check(store.Current.Launcher.Portable.Length==1,"057 已索引程序不重复记忆");
            var fileService=new FileSearch(new FakeIpc(),executor,search.OpenFileAsync);
            await fileService.OpenAsync(shortcut,default);
            Check(store.Current.Launcher.Portable.Length==1,"057 文件窗口快捷方式与已索引exe去重");
            string another=Empty057(root,"Another.exe");await fileService.OpenAsync(another,default);await Settle057(search);
            Check(store.Current.Launcher.Portable.Any(e=>e.Path==another),"057 文件窗口及f直达共用打开后记忆");
            store.Save(store.Current with{Launcher=store.Current.Launcher with{RememberOpenedPrograms=false}});
            await search.ExecuteAsync(File057(second),default);
            Check(store.Current.Launcher.Portable.Length==2,"057 关闭开关不新增也不删除");
            store.Save(store.Current with{Launcher=store.Current.Launcher with{RememberOpenedPrograms=true}});
            foreach(var error in new Exception[]{new IOException("fixture"),new System.ComponentModel.Win32Exception(1223)}){
                executor.Failure=error;try{await search.ExecuteAsync(File057(second),default);}catch(Exception e) when(ReferenceEquals(e,error)){}
                Check(store.Current.Launcher.Portable.Length==2,"057 失败或UAC取消不记忆");
            }
            executor.Failure=null;await search.ExecuteAsync(File057(Path.Combine(folder,"setup.exe")),default);
            Check(store.Current.Launcher.Portable.Length==2,"057 辅助程序不记忆");
            index.Hold=new(TaskCreationOptions.RunContinuationsAsynchronously);
            var item=(await search.SearchAsync("Alpha",default)).Items.Single(i=>i.Id==PortableCatalog.Id(first));
            Check((await search.GetActionsAsync(item,default)).SequenceEqual([LauncherItemAction.RunAsAdmin,LauncherItemAction.OpenLocation,LauncherItemAction.CopyPath,LauncherItemAction.Properties,LauncherItemAction.RemoveFromApps,LauncherItemAction.EditAliases,LauncherItemAction.RemoveFromRecent,LauncherItemAction.HideFromSearch]),"057 Learned动作无卸载且可移除、编辑别名及移除最近使用");
            await search.RunAsync(item,LauncherItemAction.RemoveFromApps,default);
            Check(store.Current.Launcher.Portable.All(e=>e.Path!=first)&&!(await search.SearchAsync("Alpha",default)).Items.Any(i=>i.Id==item.Id)&&!search.Indexing.IsCompleted,"057 移除返回前设置与快照消失无需等待重建");
            Check(search.Get(another).State==PortableScanState.Scanning,"057 重建中状态为Scanning");
            index.Hold.TrySetResult();index.Hold=null;await Settle057(search);
            Check(search.Get(another)==new PortableStatus(PortableScanState.Ready,1),"057 重建完成发布Ready数量");
            store.Save(store.Current with{Launcher=store.Current.Launcher with{Portable=[Portable057(first),Portable057(folder,PortableKind.Folder)]}});await Settle057(search);
            item=(await search.SearchAsync("Alpha",default)).Items.Single(i=>i.Kind==LauncherItemKind.App);
            Check((await search.GetActionsAsync(item,default)).Contains(LauncherItemAction.RemoveFromApps),"057 Program有移除动作");
            var child=(await search.SearchAsync("Beta",default)).Items.Single(i=>i.Kind==LauncherItemKind.App);
            Check(!(await search.GetActionsAsync(child,default)).Contains(LauncherItemAction.RemoveFromApps),"057 Folder无移除动作");
            await search.RunAsync(item,LauncherItemAction.RemoveFromApps,default);await Settle057(search);
            Check(store.Current.Launcher.Portable.All(e=>e.Kind!=PortableKind.Program),"057 Program从设置移除");
            store.Save(store.Current with{Launcher=store.Current.Launcher with{Portable=[]}});await Settle057(search);
            Check(!(await search.SearchAsync("Beta",default)).Items.Any(i=>i.Kind==LauncherItemKind.App),"057 设置清空触发索引重建");
        }
        var installedApp=new IndexedApp("app:installed",new PinyinTable().Create("Installed"),first,false,null){Target=first};
        var installedStore=new SettingsStore(Path.Combine(root,"installed"),Defaults());
        using(var search=new LauncherSearch(installedStore,new Index057(installedStore,[installedApp]),new FakeFiles(),executor)){
            await Settle057(search);await search.ExecuteAsync(File057(first),default);
            Check(installedStore.Current.Launcher.Portable.Length==0,"057 安装应用不记忆");
            string freshLink=Path.Combine(root,"Beta.lnk");Marshal.ThrowExceptionForHR(CreatePaneShortcut(freshLink,second,root));
            await search.ExecuteAsync(File057(freshLink),default);await Settle057(search);
            Check(installedStore.Current.Launcher.Portable.Single().Path==freshLink,"057 新快捷方式打开后记忆原入口");
            await search.ExecuteAsync(File057(nonExe),default);
            Check(installedStore.Current.Launcher.Portable.Length==1,"057 指向非exe的快捷方式不记忆");
        }
        var evictionStore=new SettingsStore(Path.Combine(root,"eviction"),Defaults());
        var entries=Enumerable.Range(0,100).Select(i=>new PortableEntry(Empty057(root,$"learned/L{i:D3}.exe"),PortableKind.Learned,now.AddDays(-200+i))).ToArray();
        evictionStore.Save(Defaults() with{Launcher=Defaults().Launcher with{Portable=entries}});
        File.WriteAllText(Path.Combine(evictionStore.DirectoryPath,"launcher-usage.json"),JsonSerializer.Serialize(new Dictionary<string,AppUse>{[PortableCatalog.Id(entries[0].Path)]=new(1,now)},LauncherJson.Default.DictionaryStringAppUse));
        using(var search=new LauncherSearch(evictionStore,new Index057(evictionStore),new FakeFiles(),executor)){
            await Settle057(search);await search.ExecuteAsync(File057(first),default);await Settle057(search);
            var saved=evictionStore.Current.Launcher.Portable;
            Check(saved.Length==100&&saved.Any(e=>e.Path==entries[0].Path)&&saved.All(e=>e.Path!=entries[1].Path)&&saved.Any(e=>e.Path==first),"057 第101条按使用时间及AddedAt淘汰");
        }
        await PortableWorker057(huge);
        Console.WriteLine($"057 assertions={checks-before};realExecutions=0;clipboardWrites=0");
    }
    static async Task PortableWorker057(string folder)
    {
        string dir=TestResults.NewDirectory("057-worker-");var index=new ProcessLauncherIndex(dir);
        await index.BuildAsync([],default);
        var baseline=await index.BuildAsync([],default);double baseMs=index.LastMilliseconds;
        index.PortableProvider=()=>[Portable057(folder,PortableKind.Folder)];
        var result=await index.BuildAsync([],default);double withMs=index.LastMilliseconds;
        Check(result.Snapshot.Apps.Count(a=>a.PortablePath==folder)==300&&result.Metrics.PortableStatus[folder].Programs==300,"057 真索引子进程请求、扫描、持久化300条");
        Check(result.Metrics.TruncatedPortableFolders.Contains(folder)&&index.Outcome=="completed"&&Exited(index.LastProcessId),"057 真索引子进程记录截断且退出");
        Console.WriteLine($"057 index baselineMs={baseMs:F2};portable300Ms={withMs:F2};extraMs={withMs-baseMs:F2};catalogExtraMs={result.Metrics.Milliseconds-baseline.Metrics.Milliseconds:F2};warmInstalledCache=True");
    }
}
