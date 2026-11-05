using Pane.Services;

static partial class Program
{
    sealed class TimerClock : TimeProvider
    {
        internal DateTimeOffset Now = new(2026,11,5,12,0,0,TimeSpan.Zero);
        public override DateTimeOffset GetUtcNow()=>Now;
        public override TimeZoneInfo LocalTimeZone=>TimeZoneInfo.Utc;
        readonly List<FakeTimer> timers=[];
        public override ITimer CreateTimer(TimerCallback callback,object? state,TimeSpan dueTime,TimeSpan period)
        {var timer=new FakeTimer(this,callback,state,Now+dueTime);timers.Add(timer);return timer;}
        internal void Advance(TimeSpan by){Now+=by;foreach(var t in timers.ToArray())if(!t.Disposed && t.At<=Now){t.Disposed=true;t.Callback(t.State);}}
        sealed class FakeTimer(TimerClock clock,TimerCallback callback,object? state,DateTimeOffset at):ITimer
        {
            internal bool Disposed;internal DateTimeOffset At=at;internal TimerCallback Callback=callback;internal object? State=state;
            public bool Change(TimeSpan dueTime,TimeSpan period){At=clock.Now+dueTime;return !Disposed;}
            public void Dispose()=>Disposed=true;
            public ValueTask DisposeAsync(){Dispose();return ValueTask.CompletedTask;}
        }
    }
    static async Task PowerTimer058Tests()
    {
        var clock=new TimerClock();int calls=0,changes=0;var results=new List<Pane.Contracts.PowerTimerResult>();
        using var service=new PowerTimerService(clock,_=>{calls++;return Task.CompletedTask;});
        service.Changed+=(_,_)=>changes++;service.Finished+=(_,r)=>results.Add(r);
        var now=clock.Now;
        var source=new Pane.Services.Launcher.PowerTimerSource(()=>service,clock);
        var context=new Pane.Services.Launcher.SourceContext("",Pane.Services.Launcher.LauncherSnapshot.Empty,Defaults().Launcher,new Dictionary<string,Pane.Services.Launcher.AppUse>());
        async Task<IReadOnlyList<Pane.Contracts.LauncherItem>> Rows(string q)=>await source.SearchAsync(context with{Query=q},default);
        foreach(var q in new[]{"30分钟后关机","23:00关机","23点半睡眠","1.5小时后重启","半小时后睡眠","gj 45","关机","定时关机"})Check((await Rows(q)).Count==1,"原型示例:"+q);
        Check((await Rows("取消定时")).Count==0,"无定时不能取消");
        foreach(var q in new[]{"0分钟关机","25小时关机","23:60关机","24点关机","notepad","30","关机键盘","十三小时关机"})Check((await Rows(q)).Count==0,"无效输入:"+q);
        foreach(var q in new[]{"1分钟关机","24小时关机","一小时后关机","两小时关机","十二小时关机","gj 1h","gj 23:00","30分关机","下午3点关机"})Check((await Rows(q)).Count==1,"有效输入:"+q);
        Check(Pane.Services.Launcher.PowerTimerParser.Parse("下午3点关机",now)!.At.Hour==15,"下午时刻");
        Check(Pane.Services.Launcher.PowerTimerParser.Parse("11点关机",now)!.At.Date==now.AddDays(1).Date,"已过明天");
        Check(Pane.Services.Launcher.PowerTimerParser.Parse("1.5小时后重启",now)!.Minutes==90,"小数小时");
        Check(Pane.Services.Launcher.PowerTimerSource.Span(1.2)==string.Format(Pane.Localization.Strings.PowerTimer_Minutes,1),"原型时长四舍五入");
        foreach(var delay in new[]{TimeSpan.Zero,TimeSpan.FromSeconds(59),TimeSpan.FromHours(25)})
        {try{service.Schedule(Pane.Contracts.PowerAction.Shutdown,now+delay);throw new Exception("应拒绝范围");}catch(ArgumentOutOfRangeException){checks++;}}
        service.Schedule(Pane.Contracts.PowerAction.Shutdown,now.AddHours(24));Check(service.ArmedTimers==2,"24小时两次性计时器");
        service.Schedule(Pane.Contracts.PowerAction.Restart,now.AddMinutes(2));Check(service.Current!.Action==Pane.Contracts.PowerAction.Restart,"替换");
        clock.Advance(TimeSpan.FromMinutes(1));Check(service.Current!.Reminding && calls==0,"提前60秒提醒");
        service.Postpone(TimeSpan.FromMinutes(10));Check(!service.Current!.Reminding && service.Current.At==clock.Now.AddMinutes(10),"推迟从现在起算");
        service.Cancel();clock.Advance(TimeSpan.FromHours(25));Check(service.Current==null && calls==0 && service.ArmedTimers==0,"取消旧回调");
        service.Schedule(Pane.Contracts.PowerAction.Sleep,clock.Now.AddMinutes(2));clock.Advance(TimeSpan.FromMinutes(1));clock.Advance(TimeSpan.FromMinutes(1));
        Check(calls==1 && results[^1].Outcome==Pane.Contracts.PowerTimerOutcome.Executed && service.Current==null,"睡眠执行结束");
        service.SystemTimeChanged();Check(calls==1 && service.Current==null,"恢复不重复睡眠");
        service.Schedule(Pane.Contracts.PowerAction.Shutdown,clock.Now.AddMinutes(2));clock.Now+=TimeSpan.FromSeconds(150);service.SystemTimeChanged();
        Check(service.Current!.Reminding && service.Current.At==clock.Now.AddSeconds(60),"过点30秒重排完整提醒");
        service.Cancel();service.Schedule(Pane.Contracts.PowerAction.Shutdown,clock.Now.AddMinutes(2));clock.Now+=TimeSpan.FromMinutes(7);service.SystemTimeChanged();
        Check(service.Current==null && results[^1].Outcome==Pane.Contracts.PowerTimerOutcome.Missed && calls==1,"过点5分钟错过");
        service.Schedule(Pane.Contracts.PowerAction.Restart,clock.Now.AddHours(1));var at=service.Current!.At;clock.Now-=TimeSpan.FromMinutes(30);service.SystemTimeChanged();
        Check(service.Current!.At==at && !service.Current.Reminding && service.ArmedTimers==2,"改时钟重新排");
        clock.Now=at.AddSeconds(-30);service.SystemTimeChanged();
        Check(service.Current!.Reminding && service.Current.At==clock.Now.AddSeconds(60),"距离到点30秒完整提醒");
        Check((await Rows("取消定时")).Single().Id=="power:cancel","原型取消示例");
        var row=(await Rows("30分钟关机")).Single();await source.ExecuteAsync(row,context,default);Check(service.Current!.Action==Pane.Contracts.PowerAction.Shutdown,"来源执行定时");
        await source.ExecuteAsync((await Rows("取消定时")).Single(),context,default);Check(service.Current==null,"来源执行取消");
        var disabled=new Pane.Services.Launcher.PowerTimerSource(()=>null,clock);Check((await disabled.SearchAsync(context with{Query="30分钟关机"},default)).Count==0,"停用来源");
        var directory=TestResults.NewDirectory();Directory.CreateDirectory(directory);
        var store=new Pane.Settings.SettingsStore(directory,Defaults());
        using(var search=new Pane.Services.Launcher.LauncherSearch(store,new FixtureIndex(new FakeCatalog()),new FakeFiles(),new FakeExecutor()))
        {
            search.PowerTimerProvider=()=>service;await search.Indexing;
            var rows=await search.SearchAsync("30分钟关机",default);
            Check(rows.Items[0].Group==Pane.Contracts.LauncherGroups.Timer,"定时来源排最前");
            await search.ExecuteAsync(new("power:panel",Pane.Contracts.LauncherItemKind.Command,Pane.Contracts.LauncherGroups.Timer,"","",new("timer")),default);
            Check(service.Current==null,"面板由界面处理");
            search.PowerTimerProvider=()=>null;
            Check(!(await search.SearchAsync("30分钟关机",default)).Items.Any(x=>x.Group==Pane.Contracts.LauncherGroups.Timer),"集成停用不出结果");
        }
        var sleepDone=new TaskCompletionSource();int sleepCalls=0;
        using(var sleeping=new PowerTimerService(clock,_=>{sleepCalls++;return sleepDone.Task;}))
        {
            Pane.Contracts.PowerTimerResult? result=null;sleeping.Finished+=(_,r)=>result=r;
            sleeping.Schedule(Pane.Contracts.PowerAction.Sleep,clock.Now.AddMinutes(1));clock.Advance(TimeSpan.FromMinutes(1));sleeping.SystemTimeChanged();
            Check(sleepCalls==1 && sleeping.Current==null && result==null,"睡眠调用尚未返回时恢复广播不重复排程");
            sleepDone.SetResult();
            Check(result?.Outcome==Pane.Contracts.PowerTimerOutcome.Executed,"睡眠返回后才报告完成");
        }
        using(var failure=new PowerTimerService(clock,_=>Task.FromException(new Exception("private exception"))))
        {Pane.Contracts.PowerTimerResult? result=null;failure.Finished+=(_,r)=>result=r;failure.Schedule(Pane.Contracts.PowerAction.Restart,clock.Now.AddMinutes(1));clock.Advance(TimeSpan.FromMinutes(1));Check(result?.Outcome==Pane.Contracts.PowerTimerOutcome.Failed && !result.Detail!.Contains("private"),"失败脱敏");}
        service.Schedule(Pane.Contracts.PowerAction.Shutdown,clock.Now.AddMinutes(2));service.Dispose();Check(service.Current==null && service.ArmedTimers==0,"停用释放取消");
        Check(changes>0,"状态事件");Console.WriteLine($"058 PASS assertions={checks}; realPowerCalls=0");
    }
}
