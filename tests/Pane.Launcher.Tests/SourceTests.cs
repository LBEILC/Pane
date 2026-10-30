using Pane.Contracts;
using Pane.Services.Launcher;
using Pane.Settings;

static partial class Program
{
    static async Task SourceTests(){
        var snapshot=IndexBuilder.Build(new FakeCatalog(20),[new(@"C:\fixture\银行.txt",false)],default).Snapshot;
        var context=new SourceContext("wx",snapshot,Defaults().Launcher with{PinnedApps=["app:2"]},new Dictionary<string,AppUse>());
        var executor=new FakeExecutor();
        await ApplicationSourceTests(context,executor);
        await FavoriteSourceTests(context,executor);
        await FileSourceTests(context,executor);
        await CalculationSourceTests(context,executor);
        await CommandSourceTests(context,executor);
        await AiSourceTests(context);
        await WebSourceTests(context,executor);
    }
    static async Task ApplicationSourceTests(SourceContext c,FakeExecutor executor){
        int before=checks;string? used=null;var source=new ApplicationSource(executor,id=>used=id);
        var rows=await source.SearchAsync(c,default);Check(rows.Count==2&&rows.All(r=>r.Group==source.Group&&r.Highlights!.Count>0),"application source matches/highlights");
        Check((await source.SearchAsync(c with{Query="app"},default)).Count==source.Limit(c),"application source limit");
        Check((await source.SearchAsync(c with{Query=""},default)).Single().Id=="app:2","application source home");
        await source.ExecuteAsync(rows[0],c,default);Check(used==rows[0].Id&&executor.Calls.Last()=="open:launch:1:False","application source execution/usage");
        Console.WriteLine($"source application assertions={checks-before}");
    }
    static async Task FavoriteSourceTests(SourceContext c,FakeExecutor executor){
        int before=checks;var source=new FavoriteSource(executor);var rows=await source.SearchAsync(c with{Query="yh"},default);
        Check(rows.Single().Group==source.Group&&rows[0].Highlights!.Count>0,"favorite source pinyin");
        await source.ExecuteAsync(rows[0],c,default);Check(executor.Calls.Last()==@"open:C:\fixture\银行.txt:False","favorite source execution");
        Check((await source.SearchAsync(c with{Query=""},default)).Count==0,"favorite empty home");
        Console.WriteLine($"source favorite assertions={checks-before}");
    }
    static async Task FileSourceTests(SourceContext c,FakeExecutor executor){
        int before=checks;var files=new FakeFiles{ReturnRow=true};var source=new FileSource(files,executor);
        Check((await source.SearchAsync(c with{Query="x"},default)).Count==0&&files.Calls==0,"file short query");
        var rows=await source.SearchAsync(c,default);Check(rows.Single().Group==source.Group&&files.LastLimit==source.Limit(c),"file source forwarding");
        await source.ExecuteAsync(rows[0],c,default);Check(executor.Calls.Last().StartsWith("open:C:"),"file source execution");
        files.Delay=500;Check((await source.SearchAsync(c,default)).Count==0,"file source own timeout");
        Console.WriteLine($"source file assertions={checks-before}");
    }
    static async Task CalculationSourceTests(SourceContext c,FakeExecutor executor){
        int before=checks;var source=new CalculationSource(executor);var rows=await source.SearchAsync(c with{Query="1+2*3"},default);
        Check(rows.Single().Id=="calc:7"&&rows[0].Group==source.Group,"calculation source row");
        await source.ExecuteAsync(rows[0],c,default);Check(executor.Calls.Last()=="copy:7","calculation source execution");
        Check((await source.SearchAsync(c with{Query="1/0"},default)).Count==0,"calculation invalid");
        Console.WriteLine($"source calculation assertions={checks-before}");
    }
    static async Task CommandSourceTests(SourceContext c,FakeExecutor executor){
        int before=checks;var source=new CommandSource(executor);var rows=await source.SearchAsync(c with{Query="chongqi"},default);
        Check(rows.Single().Id=="command:restart"&&rows[0].Group==source.Group,"command source pinyin");
        await source.ExecuteAsync(rows[0],c,default);Check(executor.Calls.Last()=="command:restart","command fake execution");
        Check((await source.SearchAsync(c with{Query=""},default)).Count==0,"command empty home");
        Console.WriteLine($"source command assertions={checks-before}");
    }
    static async Task AiSourceTests(SourceContext c){
        int before=checks;var source=new AiSource();var rows=await source.SearchAsync(c,default);
        Check(rows.Select(r=>r.Id).SequenceEqual(["ai:translate","ai:ask"])&&rows.All(r=>r.Group==source.Group),"AI stable IDs");
        Check(rows[1].Hotkey=="Tab"&&(await source.SearchAsync(c with{Query=""},default)).Count==0,"AI shortcut/home");
        try{await source.ExecuteAsync(rows[0],c,default);Check(false,"AI unsupported");}catch(NotSupportedException){Check(true,"AI remains unsupported");}
        Console.WriteLine($"source AI assertions={checks-before}");
    }
    static async Task WebSourceTests(SourceContext c,FakeExecutor executor){
        int before=checks;var source=new WebSource(executor);var rows=await source.SearchAsync(c with{Query="gh a& 中"},default);
        Check(rows.Count==2&&rows[0].Id=="web:https://github.com/search?q=a%26%20%E4%B8%AD"&&rows[1].Subtitle=="必应","web keyword/default");
        await source.ExecuteAsync(rows[0],c,default);Check(executor.Calls.Last()=="open:"+rows[0].Id[4..]+":False","web fake execution");
        Check((await source.SearchAsync(c with{Query=""},default)).Count==0,"web empty home");
        Console.WriteLine($"source web assertions={checks-before}");
    }
}
