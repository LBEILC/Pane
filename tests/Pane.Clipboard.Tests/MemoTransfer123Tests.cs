using Pane.Contracts;
using Pane.Services.Clipboard;
using Pane.Services.Memo;
using Pane.Settings;
using System.IO.Compression;

static partial class Program
{
    static async Task MemoTransfer123Tests()
    {
        string root=TestResults.NewDirectory("123-transfer-");
        DataStorage Service(string name)
        {
            var store=new SettingsStore(Path.Combine(root,name),Defaults());
            return new DataStorage(store,()=>null,()=>{});
        }
        var source=Service("source"); var dest=Service("dest");
        var stamp=DateTimeOffset.UtcNow;
        string a=NoteDisk.NewId(), b=NoteDisk.NewId(), c=NoteDisk.NewId(), d=NoteDisk.NewId();
        void Seed(DataStorage storage,params (string Id,string Text,int Age)[] entries)
        {
            string folder=storage.CurrentMemoFolder;
            Directory.CreateDirectory(folder);
            var rows=new List<NoteFile>();
            foreach(var entry in entries)
            {
                string name=entry.Text+" "+entry.Id[..8]+".md";
                string path=Path.Combine(folder,name); File.WriteAllText(path,entry.Text);
                var file=new FileInfo(path);
                rows.Add(new(entry.Id,name,true,stamp.AddDays(-2),stamp.AddHours(-entry.Age),"测试电脑",file.Length,file.LastWriteTimeUtc));
            }
            NoteDisk.Atomic(Path.Combine(folder,NoteDisk.Index),NoteDisk.Encode(rows),true);
        }
        Seed(source,(a,"导入更新",0),(b,"导入较旧",2),(c,"仅在备份",1));
        Seed(dest,(a,"本机较旧",2),(b,"本机更新",0),(d,"仅在本机",1));
        string zip=Path.Combine(root,"备份.zip");
        await source.ExportAsync(zip,new(false,false,ClipboardExport.None),null,default);
        using(var archive=ZipFile.OpenRead(zip))
            Check(archive.GetEntry("memo/"+NoteDisk.Index)!=null && archive.Entries.Count(e=>e.FullName.EndsWith(".md"))==3,"导出正文和索引");
        await dest.StageImportAsync(zip,new(false,false,false,DataImportMode.Merge),null,default);
        DataStorage.ApplyPending(Path.Combine(root,"dest"));
        Check(dest.LastImport is {Error:null},"导入完成");
        string folder=Path.Combine(root,"dest","备忘录");
        var rows=NoteDisk.Scan(folder);
        Check(rows.Length==4 && rows.All(r=>r.Pinned && r.From=="测试电脑") &&
            rows.ToDictionary(r=>r.Id,r=>File.ReadAllText(Path.Combine(folder,r.FileName))) is var merged &&
            merged[a]=="导入更新" && merged[b]=="本机更新" && merged[c]=="仅在备份" && merged[d]=="仅在本机" &&
            rows.Single(r=>r.Id==a).Modified==stamp,"按 id 合并，两边各取更新的记录");
    }
}
