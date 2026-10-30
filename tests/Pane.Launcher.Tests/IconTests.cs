using System.Text.Json;
using Pane.Services.Icons;
using Pane.Services.Launcher;
using Pane.Settings;

static partial class Program
{
    static byte[] TestImage(int width,int height,byte red=250,byte green=80,byte blue=30){var d=new byte[256*256*4];for(int y=0;y<height;y++)for(int x=0;x<width;x++){int i=((y+(256-height)/2)*256+x+(256-width)/2)*4;d[i]=red;d[i+1]=green;d[i+2]=blue;d[i+3]=255;}return d;}
    static async Task IconTests(){
        int start=checks;
        foreach(int thickness in new[]{1,2}){
            var framed=TestImage(32,32);for(int y=4;y<252;y++)for(int x=4;x<252;x++)if(Math.Min(Math.Min(x-4,251-x),Math.Min(y-4,251-y))<thickness)framed[(y*256+x)*4+3]=40;
            var clean=IconRenderer.Analyze(framed);Check(clean.Width==32&&clean.Height==32,"淡框不计入包围盒 "+thickness);Check(framed[(4*256+4)*4+3]==40,"分析不修改原像素");
            for(int y=4;y<252;y++)for(int x=4;x<252;x++)if(Math.Min(Math.Min(x-4,251-x),Math.Min(y-4,251-y))<thickness)framed[(y*256+x)*4+3]=100;
            Check(IconRenderer.Analyze(framed).Width==248,"有实质透明度的边框保留");
        }
        DeduplicationTests();
        var shellFrame=TestImage(32,32);byte[] bands=[38,38,77,51,26];
        for(int y=0;y<256;y++)for(int x=0;x<256;x++){int edge=Math.Min(Math.Min(x,255-x),Math.Min(y,255-y));if(edge<5)shellFrame[(y*256+x)*4+3]=bands[edge];}
        Check(IconRenderer.Analyze(shellFrame).Width==32,"Shell 实测细框及抗锯齿阴影不计入包围盒");
        for(int y=0;y<256;y++)for(int x=0;x<256;x++)if(Math.Min(Math.Min(x,255-x),Math.Min(y,255-y))<5)shellFrame[(y*256+x)*4+3]=40;
        Check(IconRenderer.Analyze(shellFrame).Width==256,"没有细主边的宽透明边框保留");
        var full=IconRenderer.Analyze(TestImage(200,200));Check(IconRenderer.Decide(full,IconMode.Auto)==IconKind.Full,"实心方形为满幅");
        var wide=IconRenderer.Analyze(TestImage(240,100));Check(IconRenderer.Decide(wide,IconMode.Auto)==IconKind.Plate,"彩色横图为底板");
        var gray=IconRenderer.Analyze(TestImage(240,100,30,30,30));Check(IconRenderer.Decide(gray,IconMode.Auto)==IconKind.Neutral,"灰色横图为中性底板");
        foreach(var item in new[]{(170,200,false),(171,200,true),(236,200,false),(235,200,true)}){var a=IconRenderer.Analyze(TestImage(item.Item1,item.Item2));Check((IconRenderer.Decide(a,IconMode.Auto)==IconKind.Full)==item.Item3,"宽高比严格边界 "+a.Aspect);}
        // 选择采样数可被 10 整除的画布，以构造精确的 0.9 满幅程度。
        int side=Enumerable.Range(40,200).First(s=>Samples(s).Count%10==0);var points=Samples(side);var pixels=TestImage(side,side);int removed=points.Count/10;
        for(int j=0;j<removed;j++){var (x,y)=points[j];pixels[((y+(256-side)/2)*256+x+(256-side)/2)*4+3]=200;}
        var boundary=IconRenderer.Analyze(pixels);Check(boundary.Fill==.9&&IconRenderer.Decide(boundary,IconMode.Auto)==IconKind.Full,"构造图的满幅 0.9 包含边界");
        var next=points[removed];pixels[((next.Y+(256-side)/2)*256+next.X+(256-side)/2)*4+3]=200;
        Check(IconRenderer.Decide(IconRenderer.Analyze(pixels),IconMode.Auto)==IconKind.Plate,"满幅低于 0.9");
        // 只留一个参与颜色抽样的像素，避免平均累加舍入改变精确阈值。
        var color=TestImage(240,100,250,210,210);for(int i=0;i<color.Length;i+=16)color[i+3]=127;int sample=Enumerable.Range(0,color.Length/16).First(i=>color[i*16]==250)*16;color[sample+3]=255;
        var sat=IconRenderer.Analyze(color);Check(sat.Saturation==.16&&IconRenderer.Decide(sat,IconMode.Auto)==IconKind.Plate,"构造图的饱和度 0.16 彩色");
        color[sample+1]=211;color[sample+2]=211;Check(IconRenderer.Decide(IconRenderer.Analyze(color),IconMode.Auto)==IconKind.Neutral,"饱和度低于 0.16");
        Check(IconRenderer.Decide(full,IconMode.ForceMask)==IconKind.Plate&&IconRenderer.Decide(gray,IconMode.Raw)==IconKind.Raw,"强制底板与原样");
        Check(IconRenderer.Analyze(new byte[256*256*4]).Empty,"透明图标");
        var settings=new IconSettings{Overrides=new(){["a"]=IconMode.ForceMask,["b"]=IconMode.Raw},CustomIcons=new(){["a"]="custom.png"}};
        Check(IconProcessing.Mode(settings,"a")==IconMode.ForceMask&&IconProcessing.Mode(settings with{MaskByDefault=false},"a")==IconMode.Raw,"全局关闭优先");
        string dir=Path.Combine(AppContext.BaseDirectory,"results","icons-"+Guid.NewGuid().ToString("N"));var store=new SettingsStore(dir,Defaults());store.Save(Defaults() with{Icons=settings});store.Load();
        Check(store.Current.Icons.Overrides["b"]==IconMode.Raw&&store.Current.Icons.CustomIcons["a"]=="custom.png","图标设置读写");
        string json=JsonSerializer.Serialize(Defaults(),SettingsJson.Default.PaneSettings);using(var doc=JsonDocument.Parse(json))File.WriteAllText(Path.Combine(dir,"settings.json"),"{"+string.Join(',',doc.RootElement.EnumerateObject().Where(p=>p.Name!="Icons").Select(p=>"\""+p.Name+"\":"+p.Value.GetRawText()))+"}");store.Load();
        Check(store.Current.Icons.MaskByDefault&&store.Current.Icons.Overrides.Count==0&&store.Current.Icons.CustomIcons.Count==0,"旧设置缺少 Icons 使用默认值");
        string source=Path.Combine(dir,"test.png");IconRenderer.Save(source,IconRenderer.Render(full,IconKind.Raw,true));
        var snapshot=IndexBuilder.Build(new FakeCatalog(1),[],default);snapshot=snapshot with{Snapshot=snapshot.Snapshot with{Apps=[snapshot.Snapshot.Apps[0] with{Icon=source}]}};
        var first=new List<IconMeasurement>();var processed=IconProcessing.Apply(snapshot,dir,new(),default,first.Add);Check(first.Count==1&&!first[0].Cached&&first[0].Error==null,"首次生成双主题缓存");
        var second=new List<IconMeasurement>();IconProcessing.Apply(snapshot,dir,new(),default,second.Add);Check(second[0].Cached,"二次全部命中");
        string key=IconProcessing.Key(source,IconMode.Auto,true);Check(key!=IconProcessing.Key(source,IconMode.Raw,true)&&key!=IconProcessing.Key(source,IconMode.Auto,false)&&key!=IconProcessing.Key(source,IconMode.Auto,true,"future"),"模式主题版本失效");
        File.SetLastWriteTimeUtc(source,DateTime.UtcNow.AddMinutes(1));Check(key!=IconProcessing.Key(source,IconMode.Auto,true),"原始图标元数据变更失效");
        var overridden=new List<IconMeasurement>();IconProcessing.Apply(snapshot,dir,new(){Overrides=new(){["app:1"]=IconMode.Raw}},default,overridden.Add);Check(!overridden[0].Cached&&overridden[0].Kind=="Raw","逐应用设置触发新缓存");
        string custom=Path.Combine(dir,"custom.png");IconRenderer.Save(custom,IconRenderer.Render(gray,IconKind.Raw,true));
        var replaced=new List<IconMeasurement>();var customSettings=new IconSettings{CustomIcons=new(){["app:1"]=custom}};
        IconProcessing.Apply(snapshot,dir,customSettings,default,replaced.Add);Check(!replaced[0].Cached&&replaced[0].Kind=="Neutral"&&replaced[0].Original==custom,"自定义图片替换后再分类");
        var customAgain=new List<IconMeasurement>();IconProcessing.Apply(snapshot,dir,customSettings,default,customAgain.Add);Check(customAgain[0].Cached,"自定义图片缓存命中");
        File.SetLastWriteTimeUtc(custom,DateTime.UtcNow.AddMinutes(1));var changed=new List<IconMeasurement>();IconProcessing.Apply(snapshot,dir,customSettings,default,changed.Add);Check(!changed[0].Cached,"自定义图片变更失效");
        using(var search=new LauncherSearch(store,new CachedIconIndex(processed),new FakeFiles(),new FakeExecutor())){
            var dark=await search.SearchAsync("wx",default);store.Save(store.Current with{General=store.Current.General with{Theme=ThemeMode.Light}});var light=await search.SearchAsync("wx",default);
            Check(dark.Items.First(i=>i.Id=="app:1").Icon.ImagePath==processed.Snapshot.Apps[0].DarkIcon&&light.Items.First(i=>i.Id=="app:1").Icon.ImagePath==processed.Snapshot.Apps[0].LightIcon,"下一次搜索使用新主题路径");
            store.Save(store.Current with{Icons=new(){MaskByDefault=false}});await search.Indexing;
            Check((await search.SearchAsync("wx",default)).Items.First(i=>i.Id=="app:1").Icon.ImagePath==source,"旧设置快照在新遮罩未完成时回退原图");
        }
        var counting=new CountingIndex();using(var search=new LauncherSearch(store,counting,new FakeFiles(),new FakeExecutor())){await search.Indexing;store.Save(store.Current with{Icons=settings});await search.Indexing;Check(counting.Count==2,"图标设置更改触发索引");}
        var migrationDir=Path.Combine(dir,"migration");Directory.CreateDirectory(migrationDir);
        var oldUses=new Dictionary<string,AppUse>{["old-app"]=new(7,DateTimeOffset.UtcNow),["app:1"]=new(3,DateTimeOffset.UtcNow.AddDays(-1))};
        File.WriteAllText(Path.Combine(migrationDir,"launcher-usage.json"),JsonSerializer.Serialize(oldUses,LauncherJson.Default.DictionaryStringAppUse));
        var migrationStore=new SettingsStore(migrationDir,Defaults());migrationStore.Save(Defaults() with{Launcher=Defaults().Launcher with{PinnedApps=["old-app","app:1"]}});
        var migratedIndex=processed with{Metrics=processed.Metrics with{Aliases=new(){["old-app"]="app:1"}}};
        using(var search=new LauncherSearch(migrationStore,new CachedIconIndex(migratedIndex),new FakeFiles(),new FakeExecutor())){
            await search.Indexing;var home=await search.SearchAsync("",default);
            Check(home.Items.Count(i=>i.Group=="已固定"&&i.Id=="app:1")==1&&migrationStore.Current.Launcher.PinnedApps[0]=="old-app","旧固定 Id 解析到保留应用且不改写设置");
            var stored=JsonSerializer.Deserialize(File.ReadAllText(Path.Combine(migrationDir,"launcher-usage.json")),LauncherJson.Default.DictionaryStringAppUse)!;
            Check(stored.Count==1&&stored["app:1"].Count==10,"加载与重建发布后迁移次数只持久化一次");
        }
        Console.WriteLine($"icon assertions={checks-start}");
    }
    static void DeduplicationTests(){
        var a=new CatalogEntry("任务计划程序","",@"C:\Windows\system32\taskschd.msc","/s","shell:a",0,"old-a","",0);
        var b=a with{Name="Task Scheduler",Target=@"c:\WINDOWS\system32\taskschd.msc",Args="/S",Source=1,LegacyId="old-b"};
        var groups=AppDeduplication.Groups([b,a]);Check(groups.Length==1&&groups[0][0].Name=="任务计划程序","同目标不同名合并并优先 Shell 本地化名");
        Check(AppDeduplication.Groups([a,b with{Name=a.Name,Args="/other"}]).Length==2,"同名不同参数保留");
        var shared=a with{LegacyId="shared"};var variants=AppDeduplication.Resolve([shared,shared with{Args="/other"},b, b with{Args="/other",LegacyId="other-link"}]);
        Check(variants.Select(g=>g.StableId).Distinct().Count()==2&&variants[0].StableId=="shared"&&variants[1].StableId=="other-link","旧 AppsFolder Id 冲突时保留各自原有 Id");
        Check(variants.SelectMany(g=>g.MergedIds).SequenceEqual(["old-b"]),"同一个旧 Id 只归属原先保留的入口");
        string oldEmpty=AppDeduplication.StableId(AppDeduplication.Identity("",a.Target,"",a.Parsing));
        var restored=AppDeduplication.Resolve([a with{Id="Dev",LegacyId=oldEmpty},a with{Id="Plain",Args="",LegacyId=oldEmpty}]);
        Check(restored[0].StableId==oldEmpty&&restored[1].StableId==AppDeduplication.StableId("Plain")&&restored[0].StableId!=restored[1].StableId,"恢复原先被漏掉的无参数入口时不抢占旧 Id");
        Check(AppDeduplication.Groups([a with{Id="Package!App",Target=""},b with{Id="package!app",Target="other"}]).Length==1,"商店应用按 AUMID 去重");
        Check(AppDeduplication.Groups([a with{Id="Package!App",Target=""},b with{Id="Package!Other",Target=""}]).Length==2,"不同商店应用保留");
        var now=DateTimeOffset.UtcNow;var uses=new Dictionary<string,AppUse>{["old-a"]=new(3,now.AddDays(-1)),["old-b"]=new(7,now)};var aliases=new Dictionary<string,string>{["old-b"]="old-a"};
        Check(AppDeduplication.MergeUsage(uses,aliases)&&uses.Count==1&&uses["old-a"]==new AppUse(10,now),"合并次数及最近使用时间");
        Check(!AppDeduplication.MergeUsage(uses,aliases)&&uses["old-a"].Count==10,"重复发布不重复累计");
    }
    static List<(int X,int Y)> Samples(int side){var list=new List<(int,int)>();for(int y=0;y<side;y+=2)for(int x=0;x<side;x+=2)if(Math.Pow(Math.Abs(x/(double)side*2-1),5)+Math.Pow(Math.Abs(y/(double)side*2-1),5)<=.92)list.Add((x,y));return list;}
    sealed class CachedIconIndex(PersistedIndex value):ILauncherIndex {public PersistedIndex? Load()=>value;public Task<PersistedIndex> BuildAsync(FavoriteEntry[] f,CancellationToken t)=>Task.FromResult(value);}
    sealed class CountingIndex:ILauncherIndex {public int Count;public PersistedIndex? Load()=>null;public Task<PersistedIndex> BuildAsync(FavoriteEntry[] f,CancellationToken t){Count++;return Task.FromResult(IndexBuilder.Build(new FakeCatalog(),f,t));}}
}
