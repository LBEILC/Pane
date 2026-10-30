using System.Diagnostics;
using System.Text;
using Pane.Contracts;
using Pane.Services.Launcher;
static partial class Program
{
    sealed class FakeIpc:IEverythingIpc{
        internal List<(string Text,int Offset,int Count,int Sort)> Calls=[];
        internal bool Unavailable;
        public Task<EverythingPage> QueryAsync(string query,int offset,int count,int sort,CancellationToken token){
            token.ThrowIfCancellationRequested();Calls.Add((query,offset,count,sort));
            if(Unavailable)throw new FileSearchUnavailableException("fixture unavailable");
            return Task.FromResult(new EverythingPage(count==0?[]:[new(@"C:\fixture\Test.TXT","Test.TXT",FileCategory.Text,12,DateTimeOffset.UnixEpoch)],123));
        }
    }
    static async Task FileWindowTests(){
        int before=checks;var ipc=new FakeIpc();var executor=new FakeExecutor();var service=new FileSearch(ipc,executor);
        foreach(var pair in FileSearch.Extensions)foreach(var ext in pair.Value.Split(' '))Check(FileSearch.Category("x."+ext.ToUpperInvariant(),false)==pair.Key,"extension "+ext);
        Check(FileSearch.Category("x.txt",true)==FileCategory.Folder&&FileSearch.Category("x.unknown",false)==FileCategory.All,"folder/unknown");
        var page=await service.SearchAsync(new("test",FileCategory.Text,FileSort.Modified),50,50,default);
        Check(ipc.Calls.Single()==("<test> file: ext:"+FileSearch.Extensions[FileCategory.Text].Replace(' ',';'),50,50,14),"filter paging sort");
        Check(page.Total==123&&page.Items[0].Size==12&&page.Items[0].Modified==DateTimeOffset.UnixEpoch&&page.Items[0].Highlights!.SequenceEqual([new TextSpan(0,4)]),"metadata and highlight");
        ipc.Calls.Clear();page=await service.SearchAsync(new("test",FileCategory.All,FileSort.Relevance),0,50,default);
        Check(page.Counts?.Count==12&&page.Counts.Values.All(v=>v==123)&&ipc.Calls.Count==12,"all counts");
        Check(ipc.Calls[0]==("test",0,50,1)&&ipc.Calls.Skip(1).All(c=>c.Count==0),"counts no rows");
        Check((await service.SearchAsync(new("",FileCategory.All,FileSort.Relevance),0,50,default)).Total==0,"empty");
        ipc.Unavailable=true;try{await service.SearchAsync(new("test",FileCategory.All,FileSort.Relevance),0,50,default);Check(false,"unavailable");}catch(FileSearchUnavailableException){Check(true,"unavailable");}
        using(var cancelled=new CancellationTokenSource()){cancelled.Cancel();try{await service.SearchAsync(new("test",FileCategory.All,FileSort.Relevance),0,50,cancelled.Token);Check(false,"cancel");}catch(OperationCanceledException){Check(true,"cancel");}}
        await service.OpenAsync(@"C:\fixture.txt",default);await service.RevealAsync(@"C:\fixture.txt",default);Check(executor.Calls.Count==2,"file executor");
        string dir=Path.Combine(Path.GetTempPath(),"pane021-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(dir);
        try{
            Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
            foreach(var (name,encoding) in new[]{("utf8",Encoding.UTF8),("utf16",Encoding.Unicode),("utf16be",Encoding.BigEndianUnicode),("gbk",Encoding.GetEncoding(936))}){
                string path=Path.Combine(dir,name+".txt");await File.WriteAllTextAsync(path,"中文测试\nsecond",encoding);
                var sw=Stopwatch.StartNew();var preview=await service.PreviewAsync(new(path,name,FileCategory.Text,null,null),default);
                Console.WriteLine($"preview {name} ms={sw.Elapsed.TotalMilliseconds:F3}");Check(preview.TextHead=="中文测试\nsecond","preview "+name);
            }
            string binary=Path.Combine(dir,"binary.txt");await File.WriteAllBytesAsync(binary,[0,1,2,3,255]);
            var timer=Stopwatch.StartNew();Check((await service.PreviewAsync(new(binary,"binary",FileCategory.Text,null,null),default)).TextHead==null,"binary");Console.WriteLine($"preview binary ms={timer.Elapsed.TotalMilliseconds:F3}");
            string longFile=Path.Combine(dir,"long.txt");await File.WriteAllTextAsync(longFile,new string('中',1000000));
            timer.Restart();Check((await service.PreviewAsync(new(longFile,"long",FileCategory.Text,null,null),default)).TextHead?.Length==4000,"long bounded");Console.WriteLine($"preview long ms={timer.Elapsed.TotalMilliseconds:F3}");
            await File.WriteAllLinesAsync(longFile,Enumerable.Range(0,100).Select(i=>i.ToString()));
            Check((await service.PreviewAsync(new(longFile,"long",FileCategory.Text,null,null),default)).TextHead!.Split('\n').Length==40,"40 lines");
            string png=Path.Combine(dir,"image.png");byte[] header=[137,80,78,71,13,10,26,10,0,0,0,13,73,72,68,82,0,0,2,128,0,0,1,224];await File.WriteAllBytesAsync(png,header);
            timer.Restart();var image=await service.PreviewAsync(new(png,"image",FileCategory.Image,null,null),default);Check(image.ImageWidth==640&&image.ImageHeight==480,"header only PNG");Console.WriteLine($"preview image ms={timer.Elapsed.TotalMilliseconds:F3}");
            foreach(var (format,data,w,h) in new (string,byte[],int,int)[]{
                ("gif",[71,73,70,56,57,97,10,0,20,0],10,20),
                ("ico",[0,0,1,0,1,0,0,32],256,32),
                ("svg",Encoding.UTF8.GetBytes("<svg width='640' height='480'/>"),640,480),
                ("tiff",[73,73,42,0,8,0,0,0,2,0,0,1,3,0,1,0,0,0,10,0,0,0,1,1,4,0,1,0,0,0,20,0,0,0],10,20),
                ("jpeg",[255,216,255,192,0,7,8,0,20,0,10],10,20),
                ("avif",[0,0,0,12,102,116,121,112,97,118,105,102,0,0,0,20,105,115,112,101,0,0,0,0,0,0,0,10,0,0,0,20],10,20)
            }){
                await File.WriteAllBytesAsync(png,data);var result=await service.PreviewAsync(new(png,format,FileCategory.Image,null,null),default);
                Check(result.ImageWidth==w&&result.ImageHeight==h,"image header "+format);
            }

            timer.Restart();Check((await service.PreviewAsync(new(dir,"folder",FileCategory.Folder,null,null),default)).FolderItems==7,"folder");Console.WriteLine($"preview folder ms={timer.Elapsed.TotalMilliseconds:F3}");
            Console.WriteLine($"file-window assertions={checks-before}");
        }finally{Directory.Delete(dir,true);}
    }
    static async Task FilePerformance(){
        string dir=Path.Combine(Path.GetTempPath(),"pane021-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(dir);
        try{
            for(int i=0;i<70;i++)await File.WriteAllTextAsync(Path.Combine(dir,$"{Path.GetFileName(dir)}-fixture-{i:D3}.txt"),"fixture");
            var ipc=new EverythingIpc();var service=new FileSearch(ipc,new FakeExecutor());string query=Path.GetFileName(dir)+"-fixture-";
            await Task.Delay(1000);
            var direct=new List<double>();var counts=new List<double>();var launcher=new List<double>();var cached=new List<double>();var files=new EverythingSearch();
            for(int i=0;i<21;i++){
                var sw=Stopwatch.StartNew();var raw=await ipc.QueryAsync(query,0,50,1,default);direct.Add(sw.Elapsed.TotalMilliseconds);
                if(raw.Total!=70||raw.Items.Count!=50)throw new Exception($"real fixture total={raw.Total}, rows={raw.Items.Count}");
                service=new FileSearch(ipc,new FakeExecutor());sw.Restart();var page=await service.SearchAsync(new(query,FileCategory.All,FileSort.Relevance),0,50,default);
                var allCounts=page.Counts??await service.CountsAsync(query,FileCategory.All,page.Total);
                if(allCounts?[FileCategory.Text]!=70)throw new Exception("real counts unavailable");
                counts.Add(sw.Elapsed.TotalMilliseconds);
                sw.Restart();await service.SearchAsync(new(query,FileCategory.All,FileSort.Relevance),0,50,default);cached.Add(sw.Elapsed.TotalMilliseconds);
                sw.Restart();var rows=await files.SearchAsync(query,3,default);launcher.Add(sw.Elapsed.TotalMilliseconds);
                if(rows is not LauncherFileRows{Total:70,Count:3})throw new Exception("launcher total");
            }
            var second=await service.SearchAsync(new(query,FileCategory.Text,FileSort.Modified),50,50,default);
            if(second.Items.Count!=20||second.Total!=70||second.Items.Any(h=>h.Size!=7||h.Modified==null)||!second.Items.Select(h=>h.Modified).SequenceEqual(second.Items.Select(h=>h.Modified).OrderDescending()))throw new Exception("real paging metadata");
            Console.WriteLine($"real Everything fixture=70; firstMedianMs={direct.Order().ElementAt(10):F3}; coldCountsMedianMs={counts.Order().ElementAt(10):F3}; cachedCountsMedianMs={cached.Order().ElementAt(10):F3}; launcherFilesMedianMs={launcher.Order().ElementAt(10):F3}");
        }finally{Directory.Delete(dir,true);}
    }
}
