using Pane.Contracts;
using Pane.Services;
using Pane.Services.Launcher;
using Pane.Settings;

static partial class Program
{
    sealed class RankingIpc(FileHit[] rows) : IEverythingIpc
    {
        internal int HeadCalls, ExactCalls;
        public Task<EverythingPage> QueryAsync(string query,int offset,int count,int sort,CancellationToken token,int timeoutMs)
        {
            token.ThrowIfCancellationRequested();
            bool Matches(FileHit hit,string expr)
            {
                if (expr.StartsWith('<'))
                {
                    int level=0, end=0;
                    for (;end<expr.Length;end++)
                    {
                        if(expr[end]=='<') level++;
                        if(expr[end]=='>' && --level==0) break;
                    }
                    return Matches(hit,expr[1..end]) && Matches(hit,expr[(end+1)..].Trim());
                }
                if(expr.StartsWith("folder:|")) return hit.Category==FileCategory.Folder || Matches(hit,expr[8..]);
                if(expr=="folder:") return hit.Category==FileCategory.Folder;
                if(expr.StartsWith("file:")) return hit.Category!=FileCategory.Folder && Matches(hit,expr[5..].Trim());
                if(expr.StartsWith("ext:")) return expr[4..].Split(';').Contains(Path.GetExtension(hit.Name).TrimStart('.'));
                if(expr.StartsWith("wfn:")) return hit.Name.Equals(expr[4..].Trim('"'),StringComparison.OrdinalIgnoreCase);
                if(expr.StartsWith("startwith:")) return hit.Name.StartsWith(expr[10..].Trim('"'),StringComparison.OrdinalIgnoreCase);
                return hit.Name.Contains(expr,StringComparison.OrdinalIgnoreCase);
            }
            if(query.Contains("startwith:")) HeadCalls++;
            if(query.Contains("wfn:")) ExactCalls++;
            var filtered=rows.Where(h=>Matches(h,query));
            var ordered=sort==14?filtered.OrderByDescending(h=>h.Modified):filtered.OrderBy(h=>h.Name);
            return Task.FromResult(new EverythingPage(ordered.Skip(offset).Take(count).ToArray(),ordered.Count()));
        }
    }

    sealed class SlowExtras125(FileHit[] recent) : IEverythingIpc
    {
        public async Task<EverythingPage> QueryAsync(string query,int offset,int count,int sort,CancellationToken token,int timeoutMs)
        {
            if (query.Contains(':')) await Task.Delay(Timeout.Infinite,token);
            return new(recent,1000);
        }
    }

    static async Task RankingTests125()
    {
        await DialogSearchTests();
        await FolderListing120Tests();
        static FileHit Hit(string path) => new(path,Path.GetFileName(path),FileSearch.Category(path,!Path.HasExtension(path)),null,null);
        var expected=Hit(@"D:\AIPrototype");
        var sessions=Enumerable.Range(1,5).Select(i=>Hit(@"C:\Users\你\.dsh\sessions\--C-Users-你-AppData-Local-Temp-worker-D--AIPrototype-sample-"+i))
            .Append(Hit(@"C:\Users\你\.finch\sessions\--D--AIPrototype-sample--")).ToArray();
        string root=TestResults.NewDirectory("125-");
        var picked=Hit(Path.Combine(root,".dsh","--AIPrototype-test"));
        Directory.CreateDirectory(picked.Path);
        var ipc=new RankingIpc([..sessions,picked,expected]);
        var store=new SettingsStore(root,Defaults());
        SearchMemory? memory=null;
        var files=new EverythingSearch(ipc,()=>memory);
        using var search=new LauncherSearch(store,new Index076([]),files,new FakeExecutor(),new Environment037(),new Process037(),actionPlatform:new ActionPlatform());
        await search.Indexing;
        memory=search.SearchMemory;
        var window=new FileSearch(ipc,new FakeExecutor(),memory:()=>memory);
        var dialog=new DialogSearchService(ipc,memory:()=>memory);
        async Task First(string q,string path)
        {
            var page=await window.SearchAsync(new(q,FileCategory.All,FileSort.Relevance),0,50,default);
            var bar=await dialog.SearchAsync(q,null,8,default);
            var launch=await files.SearchAsync(q,8,default);
            Check(page.Items.Count==8 && bar.Items.Count==8 && launch.Count==8 && ((LauncherFileRows)launch).Total==8,"三处完整候选");
            Check(page.Items[0].Path==path && bar.Items[0].Path==path && launch[0].Id=="file:"+path,"三处第一条 "+q);
        }
        await First("AIPro",expected.Path);
        var slow=new EverythingSearch(new SlowExtras125([sessions[0],expected]));
        var fallback=await slow.SearchAsync("AIPro",8,default);
        Check(fallback[0].Id=="file:"+expected.Path && ((LauncherFileRows)fallback).Total==1000,"补充批次超时返回已打分的最近结果");
        foreach (string excluded in new[]{" ","bd 天气",">dir","1+2","https://example.com",@"D:\fixture"})
            memory.RecordFile(excluded,picked.Path);
        Check(memory.Count==0,"特殊输入不记");
        memory.RecordFile(" AIPro ",picked.Path);
        await First("aipro",picked.Path);
        await First("aip",picked.Path);
        Check((await search.SearchAsync("aipro",default)).Items.First(i=>i.Group==LauncherGroups.Best).Id=="file:"+picked.Path,"121 最佳");
        store.Update(s=>s with{Launcher=s.Launcher with{LearnSelections=false}});
        memory.RecordFile("other",picked.Path);
        Check(memory.Count==1,"关闭时不记");
        await First("aipro",expected.Path);

        string[] byTier=[@"D:\AIPro",@"D:\AIPrototype",@"D:\a-AIPro",@"D:\MyAIPro",@"D:\Temp\AIPrototype",@"D:\xaiprox"];
        var ranked=FileRanking.Rank(byTier.Reverse().Select(Hit),"AIPro",[]);
        Check(ranked.Select(h=>h.Path).SequenceEqual(byTier) || ranked.Select(h=>h.Path).SequenceEqual(
            new[]{byTier[0],byTier[1],byTier[3],byTier[2],byTier[4],byTier[5]}),"分档与杂项降权");
        var multi=FileRanking.Rank(new[]{@"D:\xaipro-tag",@"D:\tag-AIPro",@"D:\AIPro tag",@"D:\tag"}.Select(Hit),"AIPro tag",[]);
        Check(multi.Select(h=>h.Name).SequenceEqual(["AIPro tag","tag-AIPro","xaipro-tag","tag"]) &&
            multi[1].Highlights!.SequenceEqual([new TextSpan(4,5),new TextSpan(0,3)]),"整串优先、分词取最低档和高亮");
        Check(FileRanking.Highlight(Hit(@"D:\xpro-Pro"),"pro").Highlights!.Single()==new TextSpan(5,3),"高亮第二处词首");
        var newer=Hit(@"D:\q12") with{Modified=DateTimeOffset.UtcNow};
        var older=Hit(@"D:\q11") with{Modified=DateTimeOffset.UnixEpoch};
        Check(FileRanking.Rank([older,newer],"q",[])[0].Path==newer.Path,"最近修改");
        Check(FileRanking.Rank(new[]{@"D:\深\q123",@"D:\q123",@"D:\q1"}.Select(Hit),"q",[]).Select(h=>h.Path)
            .SequenceEqual([@"D:\q1",@"D:\q123",@"D:\深\q123"]),"浅路径和短名字");
        var rows=Enumerable.Range(0,1000).Select(i=>Hit(@"D:\"+(i<500?"x":"")+"proj"+i+(i%3==0?".txt":i%3==1?".pdf":""))
            with{Modified=DateTimeOffset.UnixEpoch.AddMinutes(-i)}).ToArray();
        ipc=new RankingIpc(rows);
        window=new FileSearch(ipc,new FakeExecutor());
        FileQuery all=new("proj",FileCategory.All,FileSort.Relevance);
        async Task<string[]> Page(int offset)
        {
            var page=await window.SearchAsync(all,offset,50,default);
            Check(page.Total==1000 && page.Items.Count==50,"页长和总数");
            return page.Items.Select(h=>h.Path).ToArray();
        }
        var later=await Page(300);
        var early=await Page(50);
        var paths=new List<string>();
        for(int i=0;i<1000;i+=50) paths.AddRange(await Page(i));
        Check(paths.ToHashSet().SetEquals(rows.Select(h=>h.Path)) && paths.Count==1000 && ipc.HeadCalls==1,"缓存头部，完整去重分页");
        Check(early.SequenceEqual(paths.Skip(50).Take(50)) && later.SequenceEqual(paths.Skip(300).Take(50))
            && (await Page(800)).SequenceEqual(paths.Skip(800).Take(50)),"乱序取页");
        foreach(bool foldersOnly in new[]{true,false})
        {
            var page=await window.SearchAsync(all with{Extensions=["txt"],FoldersOnly=foldersOnly},0,50,default);
            var bar=await new DialogSearchService(ipc).SearchAsync("proj",new(1,0,0,0,0,true,false,false,["txt"],null,foldersOnly),3,default);
            Check(page.Total==(foldersOnly?333:667) && page.Counts![FileCategory.All]==page.Total &&
                page.Counts[FileCategory.Folder]==333 && page.Counts[FileCategory.Pdf]==0 && page.Counts[FileCategory.Text]==(foldersOnly?0:334),"筛选后的分类计数");
            Check(bar.Total==page.Total && bar.Items.Count==(foldersOnly?8:11) && bar.Items.Take(8).All(h=>h.Category==FileCategory.Folder),"对话框总数和文件夹条件");
        }
        var legacy=new FakeIpc();
        window=new FileSearch(legacy,new FakeExecutor());
        foreach(string syntax in new[]{@"x\y","*.txt","ext:png"})
        {
            await window.SearchAsync(all with{Text=syntax},1,5,default);
            Check(legacy.Calls.Last()==(syntax,1,5,1),"语法保留原排序");
        }
        Console.WriteLine($"125 assertions={checks}");
        int before = checks;
        var noise = Enumerable.Range(0,520).Select(i=>Hit(@"D:\.cache\--AIPro-"+i)).ToArray();
        var mixed = new RankingIpc([..noise, expected, Hit(@"D:\AIPrototype.txt"), Hit(@"D:\AIPro")]);
        var head = await FileRanking.HeadAsync(mixed,"AIPro",s=>s,[],default,3000,false);
        Check(mixed.HeadCalls==1 && mixed.ExactCalls==0 && head.Items[0].Name=="AIPro",
            "包含批次截断时，完整前缀覆盖全名");
        var dialogIpc = new RankingIpc([..noise, ..noise.Select(h=>Hit(h.Path+".txt")),expected,Hit(@"D:\AIPro.txt")]);
        var grouped = await new DialogSearchService(dialogIpc).SearchAsync("AIPro",null,8,default);
        Check(dialogIpc.HeadCalls==1 && dialogIpc.ExactCalls==0 && grouped.Total==1042 &&
            grouped.Items[0].Path==expected.Path && grouped.Items[8].Name=="AIPro.txt","两组共用一次完整前缀查询");
        var broad = new RankingIpc([..Enumerable.Range(0,4100).Select(i=>Hit(@"D:\AIPro-"+i)),expected]);
        await FileRanking.HeadAsync(broad,"AIPro",s=>s,[],default,3000,false);
        Check(broad.HeadCalls==1 && broad.ExactCalls==1,"前缀截断时单查全名");
        Console.WriteLine($"140 assertions={checks-before}");
    }
}
