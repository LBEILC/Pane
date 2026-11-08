using System.Globalization;
using System.Text.RegularExpressions;
using Pane.Contracts;
using Pane.Localization;
namespace Pane.Services.Launcher;

internal sealed record ParsedPowerTimer(PowerAction Action, DateTimeOffset At, double? Minutes);
internal static class PowerTimerParser
{
    // 不翻译：与原型 parse() 一致的输入词和语法。
    const string Act = "(关机|重启|睡眠|gj|cq|sm|shutdown|restart|sleep)";
    const string Num = @"(\d+(?:\.\d+)?|十[一二]|[一两二三四五六七八九十半])";
    const string Unit = "(分钟|分|min|m|个小时|小时|h)";
    static readonly Dictionary<string, double> Numbers = new() { ["一"]=1,["两"]=2,["二"]=2,["三"]=3,["四"]=4,["五"]=5,["六"]=6,["七"]=7,["八"]=8,["九"]=9,["十"]=10,["十一"]=11,["十二"]=12,["半"]=.5 }; // 不翻译
    internal static PowerAction Action(string word) => word switch { "关机" or "gj" or "shutdown" => PowerAction.Shutdown, "重启" or "cq" or "restart" => PowerAction.Restart, _ => PowerAction.Sleep }; // 不翻译
    static Match Match(string s, string pattern) => Regex.Match(s, pattern, RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(100));
    public static ParsedPowerTimer? Parse(string input, DateTimeOffset now)
    {
        var s = input.Trim().ToLowerInvariant().Replace('：', ':'); Match r;
        if ((r=Match(s, $@"^{Num}\s*{Unit}\s*后?\s*{Act}$")).Success)
            return Relative(r.Groups[3].Value, Number(r.Groups[1].Value), r.Groups[2].Value, now);
        if ((r=Match(s, $@"^(上午|下午|晚上)?\s*(\d{{1,2}})(?::(\d{{2}})|点(半|\d{{1,2}})?分?)\s*{Act}$")).Success)
            return Absolute(r.Groups[5].Value, r.Groups[2].Value, r.Groups[3].Value, r.Groups[4].Value, r.Groups[1].Value, now);
        if ((r=Match(s, $@"^{Act}\s*(\d+(?:\.\d+)?)\s*{Unit}?$")).Success)
            return Relative(r.Groups[1].Value, Number(r.Groups[2].Value), r.Groups[3].Value, now);
        if ((r=Match(s, $@"^{Act}\s*(\d{{1,2}})(?::(\d{{2}})|点(半|\d{{1,2}})?)$")).Success)
            return Absolute(r.Groups[1].Value, r.Groups[2].Value, r.Groups[3].Value, r.Groups[4].Value, "", now);
        return null;
    }
    static double Number(string s) => Numbers.TryGetValue(s, out var n) ? n : double.Parse(s, CultureInfo.InvariantCulture);
    static ParsedPowerTimer? Relative(string action, double n, string unit, DateTimeOffset now)
    {
        var minutes = unit is "个小时" or "小时" or "h" ? n*60 : n; // 不翻译
        return minutes >= 1 && minutes <= 1440 ? new(Action(action), now.AddMinutes(minutes), minutes) : null;
    }
    static ParsedPowerTimer? Absolute(string action, string hour, string minute, string point, string part, DateTimeOffset now)
    {
        int h=int.Parse(hour), m=minute.Length>0?int.Parse(minute):point=="半"?30:point.Length>0?int.Parse(point):0; // 不翻译
        if ((part is "下午" or "晚上") && h<12) h+=12; // 不翻译
        if(h>23 || m>59)return null;
        var at=new DateTimeOffset(now.Year,now.Month,now.Day,h,m,0,now.Offset);
        if(at<=now)at=at.AddDays(1);
        return at-now>=TimeSpan.FromMinutes(1) && at-now<=TimeSpan.FromHours(24)?new(Action(action),at,null):null;
    }
}
internal sealed class PowerTimerSource(Func<IPowerTimer?> provider, TimeProvider? clock = null) : LauncherSource(LauncherGroups.Timer, -10, "power:")
{
    readonly TimeProvider clock = clock ?? TimeProvider.System;
    public override int Limit(SourceContext c) => 2;
    internal static string Name(PowerAction action) => action switch { PowerAction.Shutdown=>Strings.PowerTimer_Shutdown,PowerAction.Restart=>Strings.PowerTimer_Restart,_=>Strings.PowerTimer_Sleep };
    internal static string Span(double minutes)
    {
        var h=(int)Math.Floor(minutes/60); var m=(int)Math.Floor(minutes%60+.5);
        return h==0?string.Format(Strings.PowerTimer_Minutes,m):m==0?string.Format(Strings.PowerTimer_Hours,h):string.Format(Strings.PowerTimer_HoursMinutes,h,m);
    }
    static string Clock(DateTimeOffset at,DateTimeOffset now) => at.Date>now.Date?string.Format(Strings.PowerTimer_Tomorrow,at.ToString("HH:mm",CultureInfo.InvariantCulture)):at.ToString("HH:mm",CultureInfo.InvariantCulture);
    static string State(PowerTimerState state,DateTimeOffset now) => string.Format(Strings.PowerTimer_AtTitle,Clock(state.At,now),Name(state.Action));
    public override Task<IReadOnlyList<LauncherItem>> SearchAsync(SourceContext c,CancellationToken token)
    {
        token.ThrowIfCancellationRequested(); var service=provider(); if(service==null)return Rows([]);
        var now=clock.GetLocalNow();var state=service.Current;var s=c.Query.Trim().ToLowerInvariant();var result=new List<LauncherItem>();
        if(s.Length==0)return Rows(result);
        var p=PowerTimerParser.Parse(s,now);
        if(p!=null)
        {
            var action=p.Action.ToString().ToLowerInvariant();var at=Clock(p.At,now);
            var title=p.Minutes is {} minutes?string.Format(Strings.PowerTimer_AfterTitle,Span(minutes),Name(p.Action)):string.Format(Strings.PowerTimer_AtTitle,at,Name(p.Action));
            var subtitle=state==null?string.Format(Strings.PowerTimer_RowSubtitle,at):string.Format(Strings.PowerTimer_RowReplaces,at,State(state,now));
            result.Add(new($"power:{action}@{p.At.ToUnixTimeSeconds()}",LauncherItemKind.Command,Group,title,subtitle,new(p.Action switch{PowerAction.Shutdown=>"power",PowerAction.Restart=>"restart",_=>"moon"})));
        }
        string[] cancel=["取消定时","取消关机","qxds"]; // 不翻译
        if(state!=null && s.Length>=2 && cancel.Any(w=>w.StartsWith(s,StringComparison.Ordinal)))
            result.Add(new("power:cancel",LauncherItemKind.Command,Group,string.Format(Strings.PowerTimer_CancelTitle,State(state,now)),string.Format(Strings.PowerTimer_Remaining,Span(Math.Ceiling(Math.Max(0,(state.At-now).TotalMinutes)))),new("close")));
        string[] panel=["定时关机","定时","dsgj","ds","关机","重启","睡眠","gj","cq","sm"]; // 不翻译
        if(p==null && panel.Any(w=>w==s || w.Length>2 && s.Length>=2 && w.StartsWith(s,StringComparison.Ordinal)))
            result.Add(new("power:panel",LauncherItemKind.Command,Group,Strings.PowerTimer_PanelTitle,state==null?Strings.PowerTimer_PanelSubtitle:string.Format(Strings.PowerTimer_PanelCurrent,State(state,now),Span(Math.Ceiling(Math.Max(0,(state.At-now).TotalMinutes)))),new("timer")));
        return Rows(result);
    }
    public override Task ExecuteAsync(LauncherItem item,SourceContext c,CancellationToken token)
    {
        token.ThrowIfCancellationRequested();var service=provider();if(service==null || item.Id=="power:panel")return Task.CompletedTask;
        if(item.Id=="power:cancel"){service.Cancel();return Task.CompletedTask;}
        var parts=item.Id[6..].Split('@');
        if(parts.Length!=2 || !long.TryParse(parts[1],out var seconds))return Task.CompletedTask;
        PowerAction? action=parts[0] switch {"shutdown"=>PowerAction.Shutdown,"restart"=>PowerAction.Restart,"sleep"=>PowerAction.Sleep,_=>null}; // 不翻译
        // 设计定（2026-11-05）：结果行出现到按回车之间会过几秒，到点离现在不足 1 分钟时按 1 分钟定，不报错
        var at=DateTimeOffset.FromUnixTimeSeconds(seconds).ToLocalTime();var earliest=clock.GetLocalNow().AddMinutes(1);
        if(action is {} a)service.Schedule(a,at<earliest?earliest:at);
        return Task.CompletedTask;
    }
}
