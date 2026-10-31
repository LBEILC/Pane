using System.Diagnostics;
using System.Threading.Channels;
namespace Pane.Gestures;

public sealed record HookStatistics(ulong Events,double MeanMicroseconds,double MaxMicroseconds,int Error,int UnhookSucceeded){public ulong ReinstallCount {get;init;}};

public sealed class GestureService : IGestureService
{
    GestureConfig config;
    readonly bool observe;
    readonly bool isolatedTest;
    volatile bool stop,paused,hotkeysSuspended;
    public bool IsPaused=>paused;
    public int RegisteredHotkeyCount=>hotkeyRegistered?1:0;
    public void SetHotkeysSuspended(bool value){hotkeysSuspended=value;WakePolicy();}
    public void ConfigureSuperPanel(int button,int hold,bool fullscreen,string[] disabled){Volatile.Write(ref superPolicy,new(button,hold,fullscreen,disabled));WakePolicy();}
    sealed record SuperPolicy(int Button,int Hold,bool Fullscreen,string[] Disabled);
    SuperPolicy superPolicy=new(4,400,true,[]);
    bool started,hotkeyRegistered;
    GestureServiceState state;
    readonly Channel<TrailSample> trail=Channel.CreateBounded<TrailSample>(new BoundedChannelOptions(4096){FullMode=BoundedChannelFullMode.DropOldest,SingleWriter=true});
    readonly CommandQueue commands=new();
    readonly Thread serviceThread,commandThread;
    readonly CommandExecutor executor;
    readonly WindowsSystem system;
    readonly object inputGate=new();
    readonly SemaphoreSlim slowSlots=new(2);
    readonly AutoResetEvent wake=new(false);
    int policyChanged=1;
    void WakePolicy(){Interlocked.Exchange(ref policyChanged,1);wake.Set();}
    GestureSession? session;
    GestureButton button;
    int lastX,lastY;
    KeyChord? registeredChord;
    long lastPolicy,lastProcessCheck;
    bool wgRunning;
    public GestureConfig Config=>Volatile.Read(ref config);
    public GestureServiceState State=>state;
    public ChannelReader<TrailSample> Trail=>trail.Reader;
    public event EventHandler? StateChanged;
    public event EventHandler<GestureProgress>? Progress;
    public event EventHandler<GestureOutcome>? Finished;
    public event EventHandler<SuperPanelRequest>? SuperPanelRequested;
    public event EventHandler<string>? PauseHotkeyUnavailable;
    public event EventHandler? EmergencyExitRequested;
    public GestureService(GestureConfig config,bool observeOnly=false)
    {
        GestureConfigStore.Validate(config);this.config=config;
        // 强制正常捕获只允许在从未显示的测试桌面，且必须显式隔离数据与实例。
        isolatedTest=Environment.GetEnvironmentVariable("PANE_TEST_GESTURES")=="1"
            &&Environment.GetEnvironmentVariable("PANE_VERIFY")=="1"
            &&!string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("PANE_DATA_DIR"))
            &&!string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("PANE_INSTANCE"));
        observe=observeOnly&&!isolatedTest;
        system=new WindowsSystem(()=>SetPaused(!paused),()=>!stop);executor=new(system);
        serviceThread=new Thread(Loop){IsBackground=true,Name="Pane 手势服务"};
        commandThread=new Thread(CommandLoop){IsBackground=true,Name="Pane 手势命令"};
    }
    public void Start(){if(started)throw new InvalidOperationException("服务已启动");started=true;commandThread.Start();serviceThread.Start();}
    public void Apply(GestureConfig value){GestureConfigStore.Validate(value);Volatile.Write(ref config,value);if(started&&!observe)Win.PaneCancelCapture();WakePolicy();}
    public void SetPaused(bool value){if(paused==value)return;paused=value;if(value&&started)Win.PaneCancelCapture();WakePolicy();StateChanged?.Invoke(this,EventArgs.Empty);}
    public static bool IsWGesturesRunning(){
        int present=Win.PaneWGesturesPresent();if(present>=0)return present!=0;
        // 快照失败沿用既有查询；绝不把查询失败当成另一个手势工具已退出。
        var ps=Process.GetProcessesByName("WGestures");try{return ps.Length>0;}finally{foreach(var p in ps)p.Dispose();}
    }
    void SetState(GestureServiceState value){if(state==value)return;state=value;StateChanged?.Invoke(this,EventArgs.Empty);}
    void Outcome(GestureOutcome value)=>Finished?.Invoke(this,value);
    void Queue(CommandRequest request)
    {
        if(observe)return;
        commands.Add(request);
    }
    void CommandLoop()
    {
        foreach(var request in commands.GetConsumingEnumerable())
        {
            if(stop&&request.Phase!=CommandPhase.EndAware)continue;
            if(request.Phase==CommandPhase.Execute && request.Binding.Command is WebSearchCommand or RunCommand or OpenCommand or WindowCommand)
            {
                if(!slowSlots.Wait(0)){Outcome(new(GestureOutcomeKind.Failed,request.Binding.Name,"慢操作仍在等待系统，当前请求已放弃"));continue;}
                // 外部 COM、Shell 可能阻塞；独立有界线程不占用按键与修饰会话通道。
                int completion=0;long deadline=Environment.TickCount64+1500;
                var timeout=new Timer(_=>{if(Interlocked.CompareExchange(ref completion,2,0)==0&&!stop)Outcome(new(GestureOutcomeKind.Failed,request.Binding.Name,"系统操作等待超时；已提交给系统的操作可能仍在结束"));},null,1500,Timeout.Infinite);
                var thread=new Thread(()=>{try{
                    using var slowExecutor=new CommandExecutor(new WindowsSystem(()=>SetPaused(!paused),()=>!stop&&Environment.TickCount64<deadline));
                    ExecuteRequest(request,result=>{if(Interlocked.CompareExchange(ref completion,1,0)==0)Outcome(result);},slowExecutor);
                }finally{timeout.Dispose();slowSlots.Release();}}){IsBackground=true,Name="Pane 外部命令"};thread.SetApartmentState(ApartmentState.STA);thread.Start();
            }
            else lock(inputGate)ExecuteRequest(request);
        }
        try{lock(inputGate)executor.Dispose();}catch(Exception e){Outcome(new(GestureOutcomeKind.Failed,null,"按键恢复失败："+SafeReason(e)));}
    }
    void ExecuteRequest(CommandRequest r,Action<GestureOutcome>? report=null,CommandExecutor? runner=null)
    {
        report??=Outcome;runner??=executor;
        try{
            if(r.Phase==CommandPhase.Execute)runner.Execute(r.Binding.Command,r.Target);
            else if(r.Phase==CommandPhase.BeginAware)runner.BeginAware(r.Binding.Command);
            else if(r.Phase==CommandPhase.ModifyAware)runner.ModifyAware(r.Binding.Command,r.Modifier);
            else runner.EndAware();
            if(!stop)report(new(GestureOutcomeKind.Executed,r.Binding.Name,null));
        }catch(Exception e){if(!stop)report(new(GestureOutcomeKind.Failed,r.Binding.Name,SafeReason(e)));}
    }
    static string SafeReason(Exception e)=>e is InvalidOperationException or NotSupportedException?e.Message:$"系统操作失败：{e.GetType().Name}（0x{e.HResult:X8}）";
    void Loop()
    {
        bool hook=false,emergency=false;
        try{
            if(isolatedTest&&Win.PaneEnterTestDesktop()==0)throw new InvalidOperationException("无法建立隔离手势测试桌面");
            nint[] handles=[wake.SafeWaitHandle.DangerousGetHandle()];
            Win.PaneSetWakeEvent(handles[0]);
            // 观察模式明确允许与 WGestures 并存，但绝不拦截或执行。
            wgRunning=IsWGesturesRunning()&&!isolatedTest;
            PublishPressPolicy();
            if(observe||!wgRunning){hook=Win.PaneStart(observe?1:0)!=0;if(!hook){SetState(GestureServiceState.HookFailed);return;}}
            {emergency=isolatedTest?Win.RegisterHotKey(0,102,0x4000|1|2|4,0x87):Win.RegisterHotKey(0,102,0x4000|1|2,0x7B)||Win.RegisterHotKey(0,102,0x4000|2|4,0x7B);if(!emergency)PauseHotkeyUnavailable?.Invoke(this,"应急退出 Ctrl+Alt+F12 / Ctrl+Shift+F12");}
            SetState(!observe&&wgRunning?GestureServiceState.WGesturesRunning:GestureServiceState.Running);
            while(!stop)
            {
                long now=Environment.TickCount64;
                if(!observe&&now-lastProcessCheck>=1000){lastProcessCheck=now;bool previous=wgRunning;wgRunning=IsWGesturesRunning()&&!isolatedTest;if(previous!=wgRunning)WakePolicy();}
                if(!observe&&wgRunning&&hook){Win.PaneCancelCapture();session?.End(true);session=null;Win.PaneStop();hook=false;trail.Writer.TryWrite(new(button,lastX,lastY,TrailPhase.End));}
                if(!observe&&!wgRunning&&!hook){hook=Win.PaneStart(0)!=0;if(!hook){SetState(GestureServiceState.HookFailed);break;}}
                if(Interlocked.Exchange(ref policyChanged,0)!=0||!observe&&!wgRunning&&!paused&&Win.PanePolicyDirty()!=0&&now-lastPolicy>=20){lastPolicy=now;RefreshPolicy();}
                while(Win.PeekMessageW(out var msg,0,0x312,0x312,1)){
                    if(msg.W==101)SetPaused(!paused);else if(msg.W==102)EmergencyExitRequested?.Invoke(this,EventArgs.Empty);
                }
                while(Win.PanePoll(out var e)!=0)Consume(e);
                if(Win.PaneOverflow()!=0){session?.End(true);session=null;Outcome(new(GestureOutcomeKind.Failed,null,"捕获队列溢出，本次手势取消"));}
                // 仅在事件导致策略失效时等待节流尾沿；静止时只保留共存进程检查。
                now=Environment.TickCount64;
                uint wait=observe?uint.MaxValue:(uint)Math.Max(0,1000-(now-lastProcessCheck));
                if(!observe&&!wgRunning&&!paused&&Win.PanePolicyDirty()!=0)wait=Math.Min(wait,(uint)Math.Max(0,20-(now-lastPolicy)));
                if(Win.MsgWaitForMultipleObjectsEx(1,handles,wait,0x80,4)==uint.MaxValue)throw new System.ComponentModel.Win32Exception();
            }
        }catch(Exception e){SetState(GestureServiceState.HookFailed);Outcome(new(GestureOutcomeKind.Failed,null,SafeReason(e)));}
        finally{
            session?.End(true);session=null;Win.PaneStop();
            Win.PaneSetWakeEvent(0);
            if(hotkeyRegistered)Win.UnregisterHotKey(0,101);if(emergency)Win.UnregisterHotKey(0,102);
            hotkeyRegistered=false;trail.Writer.TryComplete();commands.CompleteAdding();
            if(isolatedTest)Win.PaneLeaveTestDesktop();
        }
    }
    void RefreshPolicy()
    {
        PublishPressPolicy();
        var c=Config;
        var desiredChord=hotkeysSuspended?null:c.Settings.PauseHotkey;
        if(registeredChord!=desiredChord){
            if(hotkeyRegistered)Win.UnregisterHotKey(0,101);hotkeyRegistered=false;registeredChord=desiredChord;
            if(registeredChord is {} k){uint mods=0x4000|((k.Modifiers&KeyModifiers.Alt)!=0?1u:0)|((k.Modifiers&KeyModifiers.Ctrl)!=0?2u:0)|((k.Modifiers&KeyModifiers.Shift)!=0?4u:0)|((k.Modifiers&KeyModifiers.Win)!=0?8u:0);
                hotkeyRegistered=Win.RegisterHotKey(0,101,mods,k.VirtualKey);if(!hotkeyRegistered)PauseHotkeyUnavailable?.Invoke(this,$"{k.Modifiers}+{(char)k.VirtualKey}");}
        }
        if(observe){SetState(paused?GestureServiceState.Paused:GestureServiceState.Running);return;}
        if(paused||wgRunning){
            SetState(wgRunning?GestureServiceState.WGesturesRunning:GestureServiceState.Paused);
            Win.PaneSuperPolicy(0,0);Win.PanePolicy(0,0,0,0,96,1,0,0);
            if(session!=null){Win.PaneCancelCapture();session.End(true);session=null;trail.Writer.TryWrite(new(button,lastX,lastY,TrailPhase.End));}
            return;
        }
        ulong revision=Win.PanePolicyBegin();
        var (target,dpi,full)=WindowTargets.UnderCursor();
        bool blocked=paused||wgRunning||c.Settings.PauseInFullscreen&&full||GestureMatcher.Rule(c,target.ExePath,target.WindowClass)?.Mode==AppRuleMode.Disabled;
        SetState(wgRunning?GestureServiceState.WGesturesRunning:paused?GestureServiceState.Paused:c.Settings.PauseInFullscreen&&full?GestureServiceState.PausedByFullscreen:GestureServiceState.Running);
        var sp=Volatile.Read(ref superPolicy);
        bool superBlocked=paused||wgRunning||sp.Fullscreen&&full||sp.Disabled.Any(p=>string.Equals(p,target.ExePath,StringComparison.OrdinalIgnoreCase));
        Win.PaneSuperPolicy(superBlocked?0:sp.Button,sp.Hold);
        bool allBlocked=blocked&&(superBlocked||sp.Button==0);
        Win.PanePolicy(target.Window,target.ProcessId,target.Start.X,target.Start.Y,dpi,allBlocked?1:0,blocked?0:(int)c.Settings.EnabledButtons,c.Settings.StationaryTimeoutMs??0);
        Win.PanePolicyCommit(revision);
        if((paused||wgRunning)&&session!=null){Win.PaneCancelCapture();session.End(true);session=null;trail.Writer.TryWrite(new(button,lastX,lastY,TrailPhase.End));}
    }
    GestureConfig? publishedPressConfig;
    SuperPolicy? publishedSuperPolicy;
    bool publishedPressPaused;
    void PublishPressPolicy()
    {
        var c=Config;var sp=Volatile.Read(ref superPolicy);bool blocked=paused||wgRunning;
        if(ReferenceEquals(c,publishedPressConfig)&&ReferenceEquals(sp,publishedSuperPolicy)&&blocked==publishedPressPaused)return;
        Win.PanePressPolicyBegin((int)c.Settings.EnabledButtons,c.Settings.StationaryTimeoutMs??0,c.Settings.PauseInFullscreen?1:0,blocked?1:0,sp.Button,sp.Hold,sp.Fullscreen?1:0);
        foreach(var rule in c.Apps)Win.PanePressPolicyRule(rule.ExePath,rule.WindowClass,rule.Mode==AppRuleMode.Disabled?1:0,0);
        foreach(var path in sp.Disabled)Win.PanePressPolicyRule(path,null,1,1);
        Win.PanePressPolicyCommit();
        publishedPressConfig=c;publishedSuperPolicy=sp;publishedPressPaused=blocked;
    }
    void Consume(Win.NativeEvent e)
    {
        lastX=e.X;lastY=e.Y;
        switch(e.Kind){
            case 1:
                session?.End(true);button=(GestureButton)e.Value;
                var target=WindowTargets.Describe(e.Window,new(e.X,e.Y),new(e.X,e.Y));
                session=new(Config,button,target,e.Dpi);session.Command+=Queue;session.Progress+=p=>Progress?.Invoke(this,p);
                trail.Writer.TryWrite(new(button,e.X,e.Y,TrailPhase.Begin));break;
            case 2:session?.Move(new(e.X,e.Y));trail.Writer.TryWrite(new(button,e.X,e.Y,TrailPhase.Move));break;
            case 3:session?.Modify((GestureModifier)e.Value);break;
            case 4:
                if(session!=null&&!session.End(e.Value<0)&&e.Value>=0)Outcome(new(GestureOutcomeKind.NotRecognized,null,null));
                session=null;trail.Writer.TryWrite(new(button,e.X,e.Y,TrailPhase.End));break;
            case 5:
                session?.End(true);session=null;
                if(!observe){bool ok=Win.PaneReplayAt(e.Value,e.X,e.Y,e.Dpi)!=0;Outcome(new(ok?GestureOutcomeKind.ReplayedClick:GestureOutcomeKind.Failed,null,ok?null:"补发点击失败"));}
                trail.Writer.TryWrite(new(button,e.X,e.Y,TrailPhase.End));break;
            case 6:session?.End(true);session=null;trail.Writer.TryWrite(new(button,e.X,e.Y,TrailPhase.End));SuperPanelRequested?.Invoke(this,new(e.X,e.Y,(GestureButton)e.Value));break;
            case 8:
                try{
                    string root=Environment.GetEnvironmentVariable("PANE_DATA_DIR")??Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),"Pane");
                    string folder=Path.Combine(root,"logs");Directory.CreateDirectory(folder);
                    File.AppendAllText(Path.Combine(folder,"hook.log"),$"{DateTimeOffset.UtcNow:O} {(e.Value switch {1=>"raw-input-without-hook",3=>"replay-rate-circuit-breaker",4=>"reinstall-rate-circuit-breaker",_=>"slow-hook-callback"})}\n");
                }catch(IOException){}catch(UnauthorizedAccessException){}
                break;
            case 7:session?.End(true);session=null;trail.Writer.TryWrite(new(button,e.X,e.Y,TrailPhase.End));break;
        }
    }
    public HookStatistics Statistics(){Win.PaneStats(out var n,out var mean,out var max,out var error,out var unhook);return new(n,mean,max,error,unhook){ReinstallCount=Win.PaneReinstallCount()};}
    public async ValueTask DisposeAsync()
    {
        stop=true;wake.Set();if(!started){wake.Dispose();return;}Win.PaneCancelCommands();
        await Task.Run(()=>serviceThread.Join()).ConfigureAwait(false);
        await Task.Run(()=>commandThread.Join()).ConfigureAwait(false);
        wake.Dispose();
    }
}
