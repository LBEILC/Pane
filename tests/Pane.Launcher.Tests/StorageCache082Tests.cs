using Pane.Contracts;
using Pane.Services.Icons;
using Pane.Services.Launcher;
using Pane.Settings;

static partial class Program
{
    sealed class StorageIndex082:ILauncherIndex
    {
        internal int Builds;
        internal readonly TaskCompletionSource Started=new(TaskCreationOptions.RunContinuationsAsynchronously),Finish=new(TaskCreationOptions.RunContinuationsAsynchronously);
        public PersistedIndex? Load()=>null;
        public async Task<PersistedIndex> BuildAsync(FavoriteEntry[] favorites,CancellationToken token)
        {
            if(Interlocked.Increment(ref Builds)>1){Started.TrySetResult();await Finish.Task.WaitAsync(token);}
            return IndexBuilder.Build(new FakeCatalog(),favorites,token);
        }
    }
    static async Task StorageCache082Tests()
    {
        int before=checks;string root=TestResults.NewDirectory("storage082-");
        var index=new StorageIndex082();var store=new SettingsStore(root,Defaults());
        using var search=new LauncherSearch(store,index,new FakeFiles(),new FakeExecutor());await search.Indexing;
        var original=await search.SearchAsync("wx",default);search.RebuildStorageCache();
        await index.Started.Task.WaitAsync(TimeSpan.FromSeconds(10));
        var during=await search.SearchAsync("wx",default);
        Check(index.Builds==2&&!search.Indexing.IsCompleted,"清理后不受两分钟节流限制，后台重建");
        Check(during.Items.Select(i=>i.Id).SequenceEqual(original.Items.Select(i=>i.Id)),"重建中仍能呼出并搜索旧快照");
        index.Finish.SetResult();await search.Indexing;Check(search.IndexError==null&&search.IndexReady,"重建后索引可用");
        var extractor=new FileIconExtractor();using var icons=new FileIcons(root,extractor);
        string fixture=Path.Combine(root,"fake.txt"),path=await WaitIcon(icons,fixture);File.Delete(path);icons.RebuildStorageCache();
        string restored=await WaitIcon(icons,fixture);
        Check(File.Exists(restored)&&extractor.Calls[fixture]==2,"清理后文件图标重新提取");
        icons.Dispose();await icons.Completion;
        Console.WriteLine($"PASS launcher storage082 assertions={checks-before};realExecutions=0");
    }
}
