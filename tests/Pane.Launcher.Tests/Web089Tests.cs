using System.Buffers.Binary;
using System.Net;
using System.Net.Http;
using System.Text;
using Pane.Contracts;
using Pane.Services.Launcher;
using Pane.Settings;

static partial class Program
{
    sealed class Http089(Func<HttpRequestMessage,CancellationToken,Task<HttpResponseMessage>> send):HttpMessageHandler
    {
        internal int Calls;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,CancellationToken token){Interlocked.Increment(ref Calls);return send(request,token);}
    }
    static HttpResponseMessage Reply089(string body)=>new(HttpStatusCode.OK){Content=new StringContent(body)};
    static HttpResponseMessage Bytes089(byte[] body)=>new(HttpStatusCode.OK){Content=new ByteArrayContent(body)};
    static SearchEngine Engine089(string host)=>new(host,"x","https://"+host+"/search?q={query}");
    static async Task Web089Tests(bool live)
    {
        int before=checks;var clock=new Clock037();
        foreach(string host in new[]{"google.com","www.google.com","google.com.hk","www.google.com.hk","search.bilibili.com","www.bilibili.com"}){
            bool bili=host.Contains("bilibili");var http=new Http089((r,t)=>{
                Check(r.RequestUri!.Host==(bili?"s.search.bilibili.com":"suggestqueries.google.com"),"089 固定端点");
                return Task.FromResult(Reply089(bili?"{\"code\":0,\"result\":{\"tag\":[{\"value\":\"天气预报\"},{\"value\":\"天气预报\"},{\"value\":\"天气\"},{\"value\":4}]}}":"[\"天气\",[\"天气预报\",\"天气预报\",\"天气\",4]]"));});
            using var web=new WebSuggestions(()=>true,http,clock);Check((await web.SuggestAsync(Engine089(host),"天气",default)).SequenceEqual(["天气预报","天气"]),"089 域名及解析 "+host);
            await web.SuggestAsync(Engine089(host),"天气",default);Check(http.Calls==1,"089 缓存");
        }
        foreach(string body in new[]{"{}","{\"code\":1}","{\"code\":0,\"result\":null}","bad"}){
            using var web=new WebSuggestions(()=>true,new Http089((r,t)=>Task.FromResult(Reply089(body))));Check((await web.SuggestAsync(Engine089("www.bilibili.com"),"test",default)).Count==0,"089 畸形响应");
        }
        using(var web=new WebSuggestions(()=>true,new Http089((r,t)=>throw new Exception("不应联网"))))Check((await web.SuggestAsync(Engine089("google.com.example.test"),"test",default)).Count==0,"089 域名边界");
        string directory=TestResults.NewDirectory("089-");Directory.CreateDirectory(directory);bool enabled=true;var engine=Engine089("example.test");
        using(var history=new WebSearchHistory(()=>enabled,directory,clock)){
            foreach(string invalid in new[]{""," "," secret",new string('x',201)})history.Record(engine,invalid);Check(history.Count==0,"089 不记空白前导空白超长");
            history.Record(engine,"alpha ");history.Record(engine,"ALPHA");Check(history.Count==1&&history.Recent(engine,"",10).Single()=="ALPHA","089 去重裁尾移前");
            history.Record(engine,"beta alpha");history.Record(engine,"alphabet");history.Record(engine,"new alpha");
            Check(history.Recent(engine,"alpha",10).SequenceEqual(["alphabet","new alpha","beta alpha"]),"089 前缀优先同类新序排除完全相同");
            Check(history.Recent(engine with{Keyword="other"},"",10).Count==4,"089 改关键字保留历史");
            enabled=false;history.Record(engine,"disabled");Check(history.Count==4&&history.Recent(engine,"",10).Count==0,"089 关掉不记不返回但保留");enabled=true;
            history.Remove(engine,"ALPHABET");Check(history.Count==3,"089 Remove");history.Clear();Check(history.Count==0,"089 Clear");
            for(int i=0;i<60;i++){clock.Now+=TimeSpan.FromSeconds(1);history.Record(engine,"word"+i);}Check(history.Count==50&&history.Recent(engine,"",99).First()=="word59"&&history.Recent(engine,"",99).Last()=="word10","089 上限新序");
            history.Record(Engine089("second.test"),"separate");Check(history.Count==51,"089 按站点分隔");await history.Completion;
        }
        using(var history=new WebSearchHistory(()=>true,directory)){Check(history.Count==51,"089 持久化");history.Clear();await history.Completion;}
        File.WriteAllText(Path.Combine(directory,"web-search-history.json"),"broken");using(var history=new WebSearchHistory(()=>true,directory))Check(history.Count==0,"089 损坏为空");
        var store=new SettingsStore(Path.Combine(directory,"execute"),Defaults());
        using(var service=new LauncherSearch(store,new FixtureIndex(new FakeCatalog()),new FakeFiles(),new FakeExecutor())){
            await service.Indexing;var e=store.Current.Launcher.SearchEngines[0];
            foreach(string query in new[]{"直接搜索","联想结果","动作行查询"," leading"})await service.ExecuteAsync(new("web:"+LauncherSearch.BuildUrl(e,query),LauncherItemKind.Web,LauncherGroups.Web,"任意文案",null,new("search")),default);
            Check(service.WebSearchHistory.Recent(e,"",10).SequenceEqual(["动作行查询","联想结果","直接搜索"]),"089 最终 URL 实际搜索词与隐私前导空白");
        }
        await Icons089(directory,clock);
        if(live){
            using var client=new HttpClient(new HttpClientHandler{UseCookies=false,AllowAutoRedirect=false}){Timeout=TimeSpan.FromSeconds(3)};
            foreach(string url in new[]{"https://suggestqueries.google.com/complete/search?client=firefox&ie=utf-8&oe=utf-8&q=test","https://s.search.bilibili.com/main/suggest?term=test"})try{
                using var response=await client.GetAsync(url);response.EnsureSuccessStatusCode();using var json=System.Text.Json.JsonDocument.Parse(await response.Content.ReadAsStringAsync());var root=json.RootElement;
                bool valid=url.Contains("google")?root.ValueKind==System.Text.Json.JsonValueKind.Array&&root[1].ValueKind==System.Text.Json.JsonValueKind.Array:root.GetProperty("code").GetInt32()==0&&root.GetProperty("result").GetProperty("tag").ValueKind==System.Text.Json.JsonValueKind.Array;
                Console.WriteLine($"live089 host={new Uri(url).Host};format={valid}");
            }catch(Exception e){Console.WriteLine($"live089 host={new Uri(url).Host};unavailable={e.GetType().Name}");}
        }
        Console.WriteLine($"PASS web089 assertions={checks-before};realExecutions=0;systemClipboard=0");
    }
    static async Task Icons089(string root,Clock037 clock)
    {
        foreach(var pair in new[]{("google","google.com,www.google.com,google.com.hk,www.google.com.hk,google.co.jp,google.de"),("bing","bing.com,www.bing.com,cn.bing.com"),("bilibili","bilibili.com,www.bilibili.com,search.bilibili.com,space.bilibili.com"),("github","github.com"),("baidu","baidu.com,www.baidu.com")})foreach(string host in pair.Item2.Split(',')){
            var icon=EngineIcons.BuiltIn(host);Check(icon!=null&&Path.GetFileNameWithoutExtension(icon.Path)==pair.Item1&&File.Exists(icon.Path),"089 内置映射 "+host);
            Check(icon!.Kind==(pair.Item1 is "github" or "baidu"?EngineIconKind.Monochrome:EngineIconKind.Color)&&icon.Tint==(pair.Item1=="baidu"?"#2932E1":null),"089 图标种类色调");
        }
        using(var demo=new EngineIcons(root,false,new Http089((r,t)=>throw new Exception("演示不能联网")))){Check(demo.TryGet(Engine089("github.com"))!=null&&demo.TryGet(Engine089("custom.test"))==null,"089 演示仅内置");}
        var home=new Uri("https://custom.test/");
        var links=EngineIcons.Candidates(home,"<link rel='stylesheet' href='ignore'><link REL='shortcut ICON' href='/small.ico'><link href='//cdn.test/icon.png?a=1&amp;b=2' sizes='64x64' rel='apple-touch-icon'><link rel=icon href=javascript:bad>");
        Check(links.Length==2&&links[0].Host=="cdn.test"&&links[0].Query=="?a=1&b=2"&&links[1].AbsolutePath=="/small.ico","089 link 属性顺序引号实体优先级和协议");
        byte[] small=File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory,"Assets/Engines/bing.png")),large=File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory,"Assets/Engines/google.png"));
        byte[] ico=new byte[38+small.Length+large.Length];BinaryPrimitives.WriteUInt16LittleEndian(ico.AsSpan(2),1);BinaryPrimitives.WriteUInt16LittleEndian(ico.AsSpan(4),2);
        int offset=38;for(int i=0;i<2;i++){byte[] png=i==0?small:large;int at=6+16*i;ico[at]=ico[at+1]=(byte)(i==0?32:128);BinaryPrimitives.WriteUInt16LittleEndian(ico.AsSpan(at+4),1);BinaryPrimitives.WriteUInt16LittleEndian(ico.AsSpan(at+6),32);BinaryPrimitives.WriteUInt32LittleEndian(ico.AsSpan(at+8),(uint)png.Length);BinaryPrimitives.WriteUInt32LittleEndian(ico.AsSpan(at+12),(uint)offset);png.CopyTo(ico,offset);offset+=png.Length;}
        var gate=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);var handler=new Http089(async(r,t)=>{await gate.Task.WaitAsync(t);return r.RequestUri!.AbsolutePath=="/"?Reply089("<link rel='icon' type='image/png' sizes='128x128' href='/icon.png'>"):Bytes089(large);});
        using(var icons=new EngineIcons(Path.Combine(root,"icons"),true,handler,clock)){
            int ready=0;string? readyKey=null;icons.Ready+=(_,key)=>{ready++;readyKey=key;};var e=Engine089("custom.test");
            Parallel.For(0,30,_=>icons.TryGet(e));gate.SetResult();await icons.Completion;
            var icon=icons.TryGet(e);Check(icon!=null&&handler.Calls==2,"089 并发只取一次并缓存");Check(ready==1&&readyKey==e.UrlTemplate,"089 Ready 地址");
            Check(BinaryPrimitives.ReadInt32BigEndian(File.ReadAllBytes(icon!.Path).AsSpan(16))==128,"089 PNG 转换尺寸");
            icons.TryGet(e with{UrlTemplate="https://changed.test/?q={query}"});await icons.Completion;Check(handler.Calls==4,"089 改地址重取");
            File.Delete(icon.Path);icons.TryGet(e);await icons.Completion;Check(handler.Calls==6,"089 清缓存重取");
            await icons.ClearCacheAsync(()=>File.Delete(icon.Path),default);icons.TryGet(e);await icons.Completion;Check(handler.Calls==8,"089 数据页清缓存后重取");
        }
        using(var icons=new EngineIcons(Path.Combine(root,"ico"),true,new Http089((r,t)=>Task.FromResult(r.RequestUri!.AbsolutePath=="/"?Reply089("<html/>"):Bytes089(ico))))){var e=Engine089("ico.test");icons.TryGet(e);await icons.Completion;Check(BinaryPrimitives.ReadInt32BigEndian(File.ReadAllBytes(icons.TryGet(e)!.Path).AsSpan(16))==128,"089 ICO 最大帧");}
        var fail=new Http089((r,t)=>Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound)));string failures=Path.Combine(root,"failures");var failed=Engine089("failed.test");
        using(var icons=new EngineIcons(failures,true,fail,clock)){
            icons.TryGet(failed);await icons.Completion;int calls=fail.Calls;icons.TryGet(failed);Check(fail.Calls==calls&&calls==2,"089 失败缓存");
            clock.Now+=TimeSpan.FromDays(1.1);icons.TryGet(failed);await icons.Completion;Check(fail.Calls==calls+2,"089 一天后重试");
        }
        var restarted=new Http089((r,t)=>throw new Exception("失败缓存应跨重启"));using(var icons=new EngineIcons(failures,true,restarted,clock)){Check(icons.TryGet(failed)==null&&restarted.Calls==0,"089 失败持久缓存");}
        var oversized=new Http089((r,t)=>Task.FromResult(Bytes089(new byte[512*1024+1])));using(var icons=new EngineIcons(Path.Combine(root,"size"),true,oversized)){var e=Engine089("large.test");icons.TryGet(e);await icons.Completion;Check(icons.TryGet(e)==null&&oversized.Calls==2,"089 512KB 限制");}
        var activeStarted=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var active=new Http089(async(r,t)=>{activeStarted.TrySetResult();await Task.Delay(Timeout.Infinite,t);return Reply089("");});
        using(var icons=new EngineIcons(Path.Combine(root,"active"),true,active)){
            icons.TryGet(Engine089("active.test"));await activeStarted.Task;bool cleared=false;await icons.ClearCacheAsync(()=>cleared=true,default);Check(cleared&&icons.Completion.IsCompleted,"089 清缓存取消并等待在途请求");
        }
        var timeout=new Http089(async(r,t)=>{await Task.Delay(Timeout.Infinite,t);return Reply089("");});using(var icons=new EngineIcons(Path.Combine(root,"timeout"),true,timeout)){var e=Engine089("timeout.test");var watch=System.Diagnostics.Stopwatch.StartNew();icons.TryGet(e);await icons.Completion;Check(icons.TryGet(e)==null&&watch.Elapsed.TotalSeconds is >=5 and <9,"089 每请求三秒超时");}
    }
}
