using System.Diagnostics;
using Pane.Contracts;
using Pane.Services.Launcher;

static partial class Program
{
    sealed class FolderListingIpc(string folder,IReadOnlyList<FileHit> index):IEverythingIpc
    {
        public Task<EverythingPage> QueryAsync(string query,int offset,int count,int sort,CancellationToken token,int timeoutMs=3000)
        {
            string parent=$"parent:\"{folder}\"";
            IEnumerable<FileHit> rows=index.Where(hit=>Path.GetDirectoryName(hit.Path)==folder);
            if(query==$"<{parent}> folder:")rows=rows.Where(hit=>hit.Category==FileCategory.Folder);
            else if(query.StartsWith($"<{parent}> file: ext:",StringComparison.Ordinal)){
                var extensions=query[(query.IndexOf("ext:",StringComparison.Ordinal)+4)..].Split(';');
                rows=rows.Where(hit=>hit.Category!=FileCategory.Folder&&extensions.Contains(Path.GetExtension(hit.Name).TrimStart('.')));
            }else Check(query==parent,"查询限定到指定的父目录");
            var ordered=sort switch{
                1=>rows.OrderBy(hit=>hit.Name,StringComparer.Ordinal),
                14=>rows.OrderByDescending(hit=>hit.Modified),
                _=>throw new Exception("排序参数不正确")
            };
            return Task.FromResult(new EverythingPage(ordered.Skip(offset).Take(count).ToArray(),rows.Count()));
        }
    }

    static async Task FolderListing120Tests()
    {
        int before=checks;
        var directory=Directory.CreateTempSubdirectory("pane 120-");
        string folder=directory.FullName;
        try{
            var children=Enumerable.Range(0,114).Select(i=>{
                string name=i==0?"000":$"{i:D3}.txt";
                return new FileHit(Path.Combine(folder,name),name,i==0?FileCategory.Folder:FileCategory.Text,
                    i==0?null:i,DateTimeOffset.UnixEpoch.AddMinutes(i));
            }).ToArray();
            var nested=children[1] with{Path=Path.Combine(children[0].Path,children[1].Name)};
            var service=new FileSearch(new FolderListingIpc(folder,[nested,..children]),new FakeExecutor());
            FileQuery all=new(folder,FileCategory.All,FileSort.Relevance);
            var combined=new List<string>();
            foreach(int offset in new[]{0,50,100}){
                var page=await service.SearchAsync(all,offset,50,default);
                Check(page.Folder==folder&&page.Total==115&&page.Items.Count==Math.Min(50,115-offset),"连续分页的范围");
                combined.AddRange(page.Items.Select(hit=>hit.Path));
                if(offset==0){
                    var first=page.Items[0];
                    Check(first==new FileHit(folder,directory.Name,FileCategory.Folder,null,directory.LastWriteTimeUtc),"首行路径、名称、时间及空字段");
                    Check(page.Counts?[FileCategory.All]==114&&page.Counts[FileCategory.Folder]==1&&page.Counts[FileCategory.Text]==113,"计数只含直接子项");
                }
            }
            Check(combined.SequenceEqual(children.Select(hit=>hit.Path).Prepend(folder)),"三页拼接顺序及完整性");

            foreach(var category in new[]{FileCategory.All,FileCategory.Folder,FileCategory.Text})
            foreach(var sort in Enum.GetValues<FileSort>()){
                var page=await service.SearchAsync(new(folder,category,sort),0,50,default);
                var matches=children.Where(hit=>category==FileCategory.All||category==hit.Category);
                var ordered=sort==FileSort.Modified?matches.OrderByDescending(hit=>hit.Modified):matches.OrderBy(hit=>hit.Name,StringComparer.Ordinal);
                Check(page.Total==matches.Count()+1&&page.Items.Select(hit=>hit.Path).SequenceEqual(ordered.Select(hit=>hit.Path).Prepend(folder).Take(50)),"分类与排序 "+category+" "+sort);
            }
            foreach(var (input,path) in new[]{
                ("\""+folder+"\"",folder),(folder+"\\",folder),(folder.Replace('\\','/'),folder),
                ("%USERPROFILE%",Environment.ExpandEnvironmentVariables("%USERPROFILE%")),
                (directory.Root.FullName,directory.Root.FullName)
            }){
                var empty=new FileSearch(new FolderListingIpc(path,[]),new FakeExecutor());
                var page=await empty.SearchAsync(all with{Text=input},0,1,default);
                string name=Path.GetFileName(path);
                Check(page.Folder==path&&page.Items.Single().Name==(name.Length==0?path:name),"识别路径并保留根目录名称");
            }
            string file=Path.Combine(folder,"fixture.txt");
            File.WriteAllText(file,"120");
            var ipc=new FakeIpc();
            var normal=new FileSearch(ipc,new FakeExecutor());
            async Task CheckOrdinary(string input){
                ipc.Calls.Clear();
                var page=await normal.SearchAsync(all with{Text=input},0,50,default);
                Check(page.Folder==null&&page.Total==123&&page.Items.Single().Name=="Test.TXT"&&ipc.Calls[0]==(input,0,50,1),"回到原关键词与分页");
            }
            foreach(string input in new[]{file,Path.Combine(folder,"missing"),"test"})await CheckOrdinary(input);

            var released=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            normal.FolderProbe=path=>{released.Task.Wait(TimeSpan.FromSeconds(5));return children[0];};
            var watch=Stopwatch.StartNew();
            try{await CheckOrdinary(folder);}
            finally{released.SetResult();}
            Check(watch.ElapsedMilliseconds<2000,"目录探测超时后及时返回");
            Console.WriteLine($"120 assertions={checks-before} pages=50/50/15 total=115 timeoutMs={watch.ElapsedMilliseconds}");
        }finally{directory.Delete(true);}
    }
}
