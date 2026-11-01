using Pane.Contracts;
using Pane.Services.Launcher;
using Pane.Settings;

static partial class Program
{

    static void ApplicationNameTests(){
        var table=new PinyinTable();
        foreach(var (name,query) in new[]{("微信","wx"),("微信","weixin"),("记事本","jsb"),("重启","chongqi"),("重启","zhongqi"),("银行","yinhang"),("银行","yinxing"),("VS 代码","vsdaima"),("VS 代码","vsdm"),("音乐","yinyue"),("音乐","yinle")})Check(table.Create(name).Match(query)!=null,"pinyin "+name+" "+query);
        Check(table.Create("VS 代码").Match("daima")!.Spans.SequenceEqual([new TextSpan(3,2)]),"mixed UTF16 highlights");
        Check(table.Create("😀微信").Match("weixin")!.Spans.SequenceEqual([new TextSpan(2,2)]),"surrogate UTF16 highlights");
        Check(table.Create("微信").Match("eix")!.Spans.SequenceEqual([new TextSpan(0,2)]),"partial syllable highlight");
        foreach(var (name,q,score) in new[]{("微信","微",100),("微信","wx",90),("微信","信",70),("微信","weixin",60),("记事本","sb",45),("微信","ixin",35),("Visual Code","vc",90),("abcdef","ace",10)})Check(table.Create(name).Match(q)?.Score==score,"rank "+score);
    }

    static void CalculatorBehaviorTests(){
        foreach(var (input,expected) in new (string,string?)[]{("1+2*3","7"),("(1+2)*3","9"),("2^3^2","512"),("5%2","1"),("2×3","6"),("6÷2","3"),("-1+2","1"),("1/0",null),("process()",null),("1+",null),("42",null)})Check(Calculator.Calc(input)==expected,"calculator "+input);
    }

    static async Task OriginalApplicationTests(SettingsStore store,LauncherSearch service,FakeFiles files,FakeExecutor fake){
        var home=await service.SearchAsync("",default);Check(home.IsHome&&home.Items.Select(i=>i.Id).SequenceEqual(["app:2","app:1"]),"pinned ordering");
        var result=await service.SearchAsync("wx",default);var app=result.Items.First(i=>i.Kind==LauncherItemKind.App);await service.ExecuteAsync(app,default);
        Check(fake.Calls.Last()=="open:launch:1:False","fake app executor");
        home=await service.SearchAsync("",default);Check(home.Items[0].Group=="最近使用"&&home.Items[0].Id=="app:1","recents usage");
        result=await service.SearchAsync("微",default);Check(result.Items.First(i=>i.Kind==LauncherItemKind.App).Id=="app:1","frequency tie break");
        using(var restored=new LauncherSearch(store,new FixtureIndex(new FakeCatalog()),files,fake)){await restored.Indexing;Check((await restored.SearchAsync("",default)).Items[0].Group=="最近使用","usage persisted");}
    }

    static async Task OriginalFavoriteTests(LauncherSearch service){
        LauncherResult result;
        result=await service.SearchAsync("yh",default);Check(result.Items.Any(i=>i.Group=="常用"&&i.Highlights!.Count>0),"favorite pinyin");
    }

    static async Task OriginalWebTests(LauncherSearch service){
        LauncherResult result;
        result=await service.SearchAsync("bing a& 中",default);var web=result.Items.Where(i=>i.Kind==LauncherItemKind.Web).ToArray();
        Check(web.Length==0,"web group removed");
        await service.ExecuteAsync(new("web:https://example.invalid",LauncherItemKind.Web,"网页","test","",new("search")),default);
    }

    static async Task OriginalCalculationTests(LauncherSearch service,FakeExecutor fake){
        LauncherResult result;
        result=await service.SearchAsync("1+2",default);Check(result.Items[0].Kind==LauncherItemKind.Calculation,"calculation first");await service.ExecuteAsync(result.Items[0],default);Check(fake.Calls.Last()=="copy:3","calculation fake copy");
    }

    static async Task OriginalAiTests(LauncherSearch service){
        var result=await service.SearchAsync("1+2",default);
        Check(!result.Items.Any(i=>i.Kind==LauncherItemKind.Ai),"AI group removed");
    }

    static async Task OriginalCommandTests(LauncherSearch service,FakeExecutor fake){
        LauncherResult result;
        foreach(var command in new[]{"锁屏","睡眠","重启","关机","清空回收站","打开剪贴板历史","Pane 设置"}){result=await service.SearchAsync(command,default);var item=result.Items.First(i=>i.Id.StartsWith("command:",StringComparison.Ordinal));await service.ExecuteAsync(item,default);Check(fake.Calls.Last().StartsWith("command:"),"fake command "+command);}
    }
}
