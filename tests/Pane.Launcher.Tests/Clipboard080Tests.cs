using Pane.Contracts;
using Pane.Services.Launcher;
using Pane.Settings;
static partial class Program
{
    static async Task Clipboard080Tests()
    {
        var writes=new List<(string Text,int Mode)>();
        var executor=new WindowsLauncherExecutor(_=>{},(text,mode)=>{writes.Add((text,mode));return 1;});
        var store=new SettingsStore(TestResults.NewDirectory(),Defaults());
        using var search=new LauncherSearch(store,new FixtureIndex(new FakeCatalog()),new FakeFiles(),executor);
        await search.Indexing;
        var result=await search.SearchAsync("1+2",default);
        var calculation=result.Items.First(i=>i.Group==LauncherGroups.Calculation);
        await search.ExecuteAsync(calculation,default);
        Check(writes.Count==1&&writes[0]==("3",0),"计算结果复制不带自身标记");
        var clip=new ClipEntry("fixture",ClipKind.Files,null,[@"D:\图片\fixture.png"],null,null,null,"fixture",null,DateTimeOffset.Now,false);
        await search.RunAsync("copy-path",clip,default);
        Check(writes.Count==2&&writes[1]==(clip.Files![0],0),"复制路径不带自身标记");
        await executor.CopyAsync(@"D:\图片\file-window.png",default);
        Check(writes.Last()==(@"D:\图片\file-window.png",0),"文件窗口共用复制执行器不带自身标记");
        Console.WriteLine($"PASS launcher080 assertions={checks};systemClipboard=0");
    }
}
