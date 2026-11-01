using System.Runtime.Versioning;
using System.Text;
using System.Text.Json;
using Pane.Gestures;
[assembly:SupportedOSPlatform("windows")]

static partial class Program
{
    static int checks;
    static readonly GestureTarget Target=new((nint)123,456,@"C:\Windows\System32\cmd.exe","ConsoleWindowClass",new(-20,30),new(40,-50));
    static void Check(bool condition,string name){checks++;if(!condition)throw new Exception(name);}
    static void Throws<T>(Action action,string name) where T:Exception {try{action();}catch(T){checks++;return;}throw new Exception(name);}
    static GestureBinding Binding(string name,GestureDirection[] dirs,GestureModifier modifier=0,bool immediate=false,GestureCommand? command=null)=>new(name,new(GestureButton.Right,dirs,modifier),command??new DoNothingCommand(),immediate);
    static void Main(string[] args)
    {
        if(args.Length==2&&args[0]=="--import"){
            var importer=new WGesturesImporter();var preview=importer.Preview()??throw new InvalidDataException("未找到 WGestures 配置");
            Console.WriteLine($"导入预览：全局 {preview.GlobalCount}，程序规则 {preview.AppRuleCount}，禁用 {preview.DisabledAppCount}");
            foreach(var item in preview.Unsupported)Console.WriteLine($"{item.Where} / {item.GestureText}：{item.Reason}");
            if(preview.Unsupported.Count!=0)throw new InvalidDataException("存在无法转换的条目，已停止试用");
            GestureConfigStore.Save(args[1],importer.Import(preview,true));return;
        }
        // 013 只运行纯逻辑测试，禁止真实导入和外部命令执行。
        bool unitOnly=args.Contains("--unit-only");
        string output=args.Length>0&&!unitOnly?args[0]:Path.Combine(AppContext.BaseDirectory,"results");Directory.CreateDirectory(output);
        Recognizer();Matching();Sessions();Commands();QueueTests();Config(output);Defaults(output);Import(output);ImportCompatibility(output);if(!unitOnly){RealImport(output);HarmlessCommand(output);}
        Console.WriteLine($"PASS managed assertions={checks}; realInput=0; clipboard=0; volume=0");
    }
    static GestureDirection[] Recognize(GesturePoint[] points,double dpi=96,bool diagonal=false)
    {var r=new GestureRecognizer(points[0],dpi,diagonal);foreach(var p in points.Skip(1))r.Add(p);return r.Directions.ToArray();}
    static void Recognizer()
    {
        // 明确的轨迹测试数据：包含负坐标、转角噪声、五段和缩放。
        GesturePoint[][] singles=[ [new(0,0),new(0,-60)], [new(0,0),new(60,-60)], [new(0,0),new(60,0)], [new(0,0),new(60,60)], [new(0,0),new(0,60)], [new(0,0),new(-60,60)], [new(0,0),new(-60,0)], [new(0,0),new(-60,-60)] ];
        for(int d=0;d<8;d++)Check(Recognize(singles[d]).SequenceEqual([(GestureDirection)d]),$"direction {d}");
        foreach(double degree in new[]{19.99,20.01,69.99,70.01,109.99,110.01}){double a=degree*Math.PI/180;var direction=GestureRecognizer.Classify(Math.Sin(a),-Math.Cos(a));int expected=degree<20?0:degree<70?1:degree<110?2:3;Check((int)direction==expected,$"boundary {degree}");}
        Check(Recognize([new(0,0),new(1,25),new(-2,50),new(1,75),new(0,100)]).SequenceEqual([GestureDirection.Down]),"jitter straight");
        Check(Recognize([new(0,0),new(0,30),new(2,60),new(12,70),new(35,73),new(70,74)]).SequenceEqual([GestureDirection.Down,GestureDirection.Right]),"corner not diagonal");
        Check(Recognize([new(0,0),new(0,60),new(60,60),new(60,0),new(120,0),new(120,60)]).SequenceEqual([GestureDirection.Down,GestureDirection.Right,GestureDirection.Up,GestureDirection.Right,GestureDirection.Down]),"five strokes");
        Check(Recognize([new(0,0),new(0,-60),new(60,-60),new(60,0)]).SequenceEqual([GestureDirection.Up,GestureDirection.Right,GestureDirection.Down]),"volume path");
        Check(Recognize([new(0,0),new(60,0),new(60,60),new(120,60),new(120,120)]).SequenceEqual([GestureDirection.Right,GestureDirection.Down,GestureDirection.Right,GestureDirection.Down]),"command path");
        Check(Recognize([new(-200,-200),new(-200,-100),new(-100,-100)],192).SequenceEqual([GestureDirection.Down,GestureDirection.Right]),"DPI negative coordinates");
        Check(Recognize([new(0,0),new(0,95)],192).Length==0,"scaled threshold below");
        Check(Recognize([new(0,0),new(60,60),new(120,60)]).All(d=>((int)d&1)==0),"diagonal first converges cardinal");
        Check(Recognize([new(0,0),new(0,60),new(60,120)],96,true).SequenceEqual([GestureDirection.Down,GestureDirection.DownRight]),"optional diagonal multi");
        var longPath=Enumerable.Range(0,31).Select(i=>new GesturePoint(i%2==0?0:60,i*60)).ToArray();Check(Recognize(longPath,96,true).Length==30,"unlimited strokes");
    }
    static void Matching()
    {
        var global=Binding("global",[GestureDirection.Down]);var local=global with{Name="override"};
        var c=GestureConfigStore.Empty with{Global=[global],Apps=[new(Target.ExePath,null,AppRuleMode.InheritAndOverride,[local]),new(Target.ExePath,"Other",AppRuleMode.Disabled,[])]};
        Check(GestureMatcher.Match(c,global.Pattern)==global,"global");
        Check(GestureMatcher.Match(c,global.Pattern,Target.ExePath.ToUpperInvariant())==local,"full path ignore case override");
        Check(GestureMatcher.Match(c,global.Pattern,Target.ExePath,"Other")==null,"class specific disabled");
        Check(GestureMatcher.Match(c,global.Pattern,@"C:\Other\cmd.exe")==global,"same basename does not override");
        Check(GestureMatcher.Match(c,global.Pattern,"unknown")==global,"unknown global fallback");
    }
    static void Sessions()
    {
        var normal=Binding("base",[]);var up=Binding("up",[],GestureModifier.WheelUp,true);var left=Binding("left",[],GestureModifier.LeftClick);
        var c=GestureConfigStore.Empty with{Global=[normal,up,left]};
        var s=new GestureSession(c,GestureButton.Right,Target,96);var requests=new List<CommandRequest>();s.Command+=requests.Add;
        s.Modify(GestureModifier.WheelUp);s.Modify(GestureModifier.WheelUp);s.End();Check(requests.Count==2&&requests.All(r=>r.Binding==up),"immediate repeated no base at release");
        requests.Clear();s=new(c,GestureButton.Right,Target,96);s.Command+=requests.Add;s.Modify(GestureModifier.LeftClick);Check(requests.Count==0,"deferred modifier waits");s.End();Check(requests.Count==1&&requests[0].Binding==left,"deferred on release");
        requests.Clear();s=new(c,GestureButton.Right,Target,96);s.Command+=requests.Add;s.Modify(GestureModifier.WheelUp);s.Move(new(500,500));s.Modify(GestureModifier.WheelUp);s.End();Check(requests.Count==2,"freeze path after immediate");
        foreach(GestureCommand command in new GestureCommand[]{new VolumeCommand(VolumeAction.WheelAdjust,2),new TaskSwitchCommand()}){
            var aware=up with{Command=command};requests.Clear();s=new(c with{Global=[aware]},GestureButton.Right,Target,96);s.Command+=requests.Add;
            s.Modify(GestureModifier.WheelUp);s.Modify(GestureModifier.MiddleClick);s.Modify(GestureModifier.WheelDown);s.End();
            Check(requests.Select(r=>r.Phase).SequenceEqual([CommandPhase.BeginAware,CommandPhase.ModifyAware,CommandPhase.ModifyAware,CommandPhase.EndAware]),"aware lifetime");
        }
        requests.Clear();s=new(c,GestureButton.Right,Target,96);s.Command+=requests.Add;s.Modify(GestureModifier.LeftClick);s.End(true);Check(requests.Count==0,"cancel deferred");
        requests.Clear();s=new(c,GestureButton.Right,Target,96);s.Command+=requests.Add;s.Modify(GestureModifier.WheelDown);s.End();Check(requests.Count==0,"unknown modifier never base");
    }
    static void Commands()
    {
        var f=new Fake();using var e=new CommandExecutor(f);
        f.Held.Add(163);e.Execute(new HotkeyCommand([new(KeyModifiers.Ctrl,67)]),Target);
        Check(f.Log.SequenceEqual(["activate","up:163","down:162","down:67","up:67","up:162","down:163"]),"physical modifiers restored");
        f.Clear();e.Execute(new HotkeyCommand([new(KeyModifiers.Alt,32),new(0,69),new(0,75)]),Target);
        Check(f.Log.SequenceEqual(["activate","down:164","down:32","up:32","up:164","down:69","up:69","down:75","up:75"]),"cmd menu sequence");
        f.Clear();e.Execute(new SendTextCommand("^v{enter}"),Target);Check(f.Log.SequenceEqual(["activate","down:162","down:86","up:86","up:162","down:13","up:13"]),"sendkeys paste enter");
        Check(SendKeysParser.Parse("+(ab){LEFT 3}{F24}{+}{{}{}}").Count==9,"groups escapes repeats");
        Throws<NotSupportedException>(()=>SendKeysParser.Parse("{UNKNOWN}"),"unknown syntax reported");Throws<FormatException>(()=>SendKeysParser.Parse("^(abc"),"unclosed group");
        f.Clear();e.Execute(new SendTextCommand("你好"),Target);Check(f.Log.Count==5,"unicode key pairs");
        f.Clear();e.Execute(new WebSearchCommand("https://example.test/?q={query}"),Target);Check(f.Log.Contains("open:https://example.test/?q=a%20%26%E4%B8%AD"),"search encodes selection");
        f.Clear();e.Execute(new RunCommand("echo %WG_PROCID%",true,false),Target);Check(f.Log.Contains("run:echo %WG_PROCID%:False:C:\\fixture"),"command working folder");Check(f.Variables!["WG_ENDPOINT_Y"]=="-50"&&f.Variables.Count==6,"six variables actual Y");
        foreach(WindowAction action in Enum.GetValues<WindowAction>()){f.Clear();e.Execute(new WindowCommand(action),Target);Check(f.Log.SequenceEqual(["activate","window:"+action]),"window "+action);}
        f.Clear();e.Execute(new VolumeCommand(VolumeAction.Up,2),Target);Check(f.Log.SequenceEqual(["down:175","up:175","down:175","up:175"]),"volume steps");
        f.Clear();e.Execute(new VolumeCommand(VolumeAction.ToggleMute,9),Target);Check(f.Log.SequenceEqual(["down:173","up:173"]),"mute once");
        f.Clear();e.Execute(new TaskSwitchCommand(),Target);Check(f.Log.SequenceEqual(["down:164","down:9","up:9","up:164"]),"task switch releases Alt");
        f.Clear();e.BeginAware(new TaskSwitchCommand());e.ModifyAware(new TaskSwitchCommand(),GestureModifier.WheelUp);e.EndAware();Check(f.Log.SequenceEqual(["down:164","down:9","up:9","down:160","down:9","up:9","up:160","up:164"]),"aware task reverse then release");
        f.Clear();f.Held.Add(164);e.BeginAware(new TaskSwitchCommand());e.EndAware();Check(f.Log.Last()=="down:164","preexisting Alt restored");
        f.Clear();e.BeginAware(new VolumeCommand(VolumeAction.WheelAdjust,1));Check(f.Log.SequenceEqual(["down:175","up:175","down:174","up:174"]),"volume begin follows source");
        f.Clear();e.ModifyAware(new VolumeCommand(VolumeAction.WheelAdjust,2),GestureModifier.WheelDown);Check(f.Log.Count==4,"aware volume down");
        f.Clear();e.ModifyAware(new VolumeCommand(VolumeAction.WheelAdjust,2),GestureModifier.MiddleClick);Check(f.Log.SequenceEqual(["down:173","up:173"]),"aware middle mute");
        f.Clear();e.Execute(new OpenCommand("fixture"),Target);e.Execute(new PauseGesturesCommand(),Target);e.Execute(new DoNothingCommand(),Target);Check(f.Log.SequenceEqual(["open:fixture","pause"]),"open pause nothing");
        f.Clear();f.ThrowKey=67;Throws<InvalidOperationException>(()=>e.Execute(new HotkeyCommand([new(KeyModifiers.Ctrl,67)]),Target),"key failure");Check(f.Log.TakeLast(2).SequenceEqual(["up:67","up:162"]),"failure releases attempted keys");
        f.Clear();f.Held.Add(163);f.ThrowKey=67;Throws<InvalidOperationException>(()=>e.Execute(new HotkeyCommand([new(KeyModifiers.Ctrl,67)]),Target),"failure with physical modifier");Check(f.Log.Last()=="down:163","failure restores physical modifier");
        f.Clear();f.ThrowKey=9;Throws<InvalidOperationException>(()=>e.BeginAware(new TaskSwitchCommand()),"task begin failure");Check(f.Log.TakeLast(2).SequenceEqual(["up:9","up:164"]),"task failure restores Alt");
        f.Clear();e.BeginAware(new TaskSwitchCommand());e.Dispose();Check(f.Log.Last()=="up:164","dispose during task restores Alt");
    }
    static void Config(string output)
    {
        var c=GestureConfigStore.Empty with{Settings=GestureConfigStore.Empty.Settings with{AllowDiagonalMultiStroke=true}};string path=Path.Combine(output,"config.json");GestureConfigStore.Save(path,c);Check(GestureConfigStore.Load(path).Settings.AllowDiagonalMultiStroke,"optional setting roundtrip");
        string json=File.ReadAllText(path);File.WriteAllText(path,json.Replace("\"Version\": 1","\"Version\": 99"));Throws<InvalidDataException>(()=>GestureConfigStore.Load(path),"unknown version");
        File.WriteAllText(path,json.Insert(1,"\"Mystery\":1,"));Throws<InvalidDataException>(()=>GestureConfigStore.Load(path),"unknown field");
    }
    static void Import(string output)
    {
        string directory=Path.Combine(output,"fixture");Directory.CreateDirectory(directory);
        string[] commands=[
            "\"$type\":\"HotKeyCommand\",\"Modifiers\":[17],\"Keys\":[67]",
            "\"$type\":\"SendTextCommand\",\"Text\":\"^v{enter}\"",
            "\"$type\":\"WebSearchCommand\",\"SearchEngineUrl\":\"https://example.test/{0}\"",
            "\"$type\":\"CmdCommand\",\"Code\":\"echo %WG_PROCID%\",\"ShowWindow\":false,\"AutoSetWorkingDir\":true",
            "\"$type\":\"WindowControlCommand\",\"ChangeWindowStateTo\":5",
            "\"$type\":\"ChangeAudioVolumeCommand\",\"Delta\":2",
            "\"$type\":\"TaskSwitcherCommand\"",
            "\"$type\":\"OpenFileCommand\",\"FilePath\":\"fixture\"",
            "\"$type\":\"GotoUrlCommand\",\"Url\":\"https://example.test\"",
            "\"$type\":\"PauseWGesturesCommand\"", "\"$type\":\"DoNothingCommand\"", "\"$type\":\"ScriptCommand\"",
            "\"$type\":\"SendTextCommand\",\"Text\":\"{UNKNOWN}\"" ];
        string intents=string.Join(',',commands.Select((c,i)=>"{\"Name\":\"fixture"+i+"\",\"Gesture\":{\"GestureButton\":1,\"Dirs\":[0],\"Modifier\":1},\"ExecuteOnModifier\":true,\"Command\":{"+c+"}}"));
        File.WriteAllText(Path.Combine(directory,"gestures.wg2"),"{\"FileVersion\":\"3\",\"Global\":{\"GestureIntents\":["+intents+"]},\"Apps\":{\"fixture.exe\":{\"ExecutablePath\":\"C:\\\\fixture.exe\",\"IsGesturingEnabled\":false,\"GestureIntents\":[]}}}");
        File.WriteAllText(Path.Combine(directory,"config.plist"),"<plist><dict><key>PathTrackerTriggerButton</key><integer>15</integer><key>GestureParserDisableInFullScreenMode</key><false/><key>GestureViewShowPath</key><false/><key>GestureViewFadeOut</key><false/><key>GestureViewShowCommandName</key><false/><key>GestureViewMainPathColor</key><integer>-14165512</integer></dict></plist>");
        var importer=new WGesturesImporter();var preview=importer.Preview(directory)!;Check(preview.Unsupported.Count==2,"script and bad SendKeys reported");var config=importer.Import(preview,true);
        Check(config.Global.Count==11&&preview.DisabledAppCount==1,"all command types imported");Check((int)config.Settings.EnabledButtons==15&&!config.Settings.PauseInFullscreen&&!config.Settings.ShowTrail&&!config.Settings.FadeTrail&&!config.Settings.ShowCommandName,"settings imported");
        Check(importer.Import(preview,false).Settings.TrailColors[GestureButton.Right]=="#5874c9","optional palette");Roundtrip(config,Path.Combine(output,"fixture-roundtrip.json"));
    }
    static void Roundtrip(GestureConfig c,string path){GestureConfigStore.Save(path,c);string before=File.ReadAllText(path);GestureConfigStore.Save(path,GestureConfigStore.Load(path));Check(before==File.ReadAllText(path),"JSON roundtrip exact");}
    static void RealImport(string output)
    {
        var importer=new WGesturesImporter();var preview=importer.Preview()??throw new Exception("本机 WGestures 配置未找到");var c=importer.Import(preview,true);
        Check(preview.GlobalCount==52&&preview.AppRuleCount==7&&preview.DisabledAppCount==4,"local 52 / 7 / 4");Check(preview.Unsupported.Count==0,"local all supported");
        Roundtrip(c,Path.Combine(output,"imported.json"));
        var lines=new List<string>{"| # | 触发键 | 方向 | 修饰 | 即时 | 名称 | 转换命令 |","|---:|---|---|---|---|---|---|"};
        int index=0;foreach(var b in c.Global)lines.Add(Row(++index,b));
        foreach(var app in c.Apps){lines.Add("");lines.Add($"程序：`{Path.GetFileName(app.ExePath)}`，模式：{app.Mode}，{app.Bindings.Count} 条。");foreach(var b in app.Bindings)lines.Add(Row(++index,b));}
        File.WriteAllLines(Path.Combine(output,"import-table.md"),lines);
        Console.WriteLine($"IMPORT global={preview.GlobalCount};apps={preview.AppRuleCount};disabled={preview.DisabledAppCount};unsupported={preview.Unsupported.Count};colors={string.Join(',',c.Settings.TrailColors.Values)}");
    }
    static string Row(int i,GestureBinding b)=>$"| {i} | {b.Pattern.Button} | {string.Concat(b.Pattern.Directions.Select(d=>"↑↗→↘↓↙←↖"[(int)d]))} | {b.Pattern.Modifier} | {b.ExecuteOnModifier} | {b.Name} | {Describe(b.Command)} |";
    static string Describe(GestureCommand c)=>c switch {HotkeyCommand h=>string.Join(", ",h.Chords.Select(k=>$"{k.Modifiers}+0x{k.VirtualKey:X2}")),SendTextCommand t=>t.Sequence,WebSearchCommand w=>w.UrlTemplate,RunCommand r=>r.CommandLine.Replace("\r","").Replace("\n"," / "),_=>c.ToString()??""};
    static void HarmlessCommand(string output)
    {
        string full=Path.GetFullPath(output);string file=Path.Combine(full,"harmless.txt");if(File.Exists(file))File.Delete(file);
        new WindowsSystem(()=>{}).Run("(echo %WG_PROCID%&echo %WG_ENDPOINT_Y%&cd)>harmless.txt",false,full,CommandExecutor.Variables(Target));
        string value="";for(int i=0;i<100;i++){try{value=File.ReadAllText(file);if(value.Contains(full,StringComparison.OrdinalIgnoreCase))break;}catch(IOException){}Thread.Sleep(20);}
        Check(value.Contains("456")&&value.Contains("-50")&&value.Contains(full,StringComparison.OrdinalIgnoreCase),"real hidden harmless command variables directory");
    }
}

sealed class Fake : ISystemOperations
{
    public List<string> Log=[];public HashSet<ushort> Held=[];public ushort ThrowKey;public int TabDelay;public IReadOnlyDictionary<string,string>? Variables;
    public void Clear(){Log.Clear();Held.Clear();ThrowKey=0;}
    public void Activate(GestureTarget target)=>Log.Add("activate");
    public bool IsKeyDown(ushort key)=>Held.Contains(key);
    public void Key(ushort key,bool down){Log.Add((down?"down:":"up:")+key);if(down&&key==9&&TabDelay>0)Thread.Sleep(TabDelay);if(down&&key==ThrowKey)throw new InvalidOperationException("假失败");}
    public void Character(char character,bool down)=>Log.Add((down?"char-down:":"char-up:")+character);
    public (ushort Key,KeyModifiers Modifiers) MapCharacter(char c)=>((ushort)char.ToUpperInvariant(c),0);
    public string ReadSelection(GestureTarget target){Log.Add("selection");return "a &中";}
    public string? ExplorerFolder(GestureTarget target)=>@"C:\fixture";
    public void Run(string command,bool showWindow,string directory,IReadOnlyDictionary<string,string> variables){Log.Add($"run:{command}:{showWindow}:{directory}");Variables=variables;}
    public void Window(GestureTarget target,WindowAction action)=>Log.Add("window:"+action);
    public void Open(string target)=>Log.Add("open:"+target);
    public void Pause()=>Log.Add("pause");
}
