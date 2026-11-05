using Pane.Localization;
// 定时关机的界面接线（设计负责人，058）：定时窗口、到点前提醒。docs/design.md「定时关机」。
// 提醒面板只在提醒阶段存在：服务进入提醒（Reminding）时建，在工作区右下角显示、不抢焦点；退出提醒就收起并释放。
// 它登记在二级窗口表里（跟着逐帧推进、收起后释放），但不是 ISecondaryView：没有任务栏按钮，点外面不收起。
using Pane.Contracts;
using Pane.Hosting;
using Pane.Views;
namespace Pane;
public sealed partial class App
{
    IPowerTimer? powerTimer;
    /// <summary>定时关机服务（058）。「定时关机」能力启动时由工程赋值（演示模式用 DesignData/PowerTimerSample），停止时置 null。</summary>
    public IPowerTimer? PowerTimer
    {
        get=>powerTimer;
        set
        {
            if(ReferenceEquals(powerTimer,value))return;
            if(powerTimer!=null)powerTimer.Changed-=OnPowerTimerChanged;
            powerTimer=value;
            if(value!=null)value.Changed+=OnPowerTimerChanged;
            OnPowerTimerChanged(null,EventArgs.Empty);
        }
    }

    // 可能在后台线程触发
    void OnPowerTimerChanged(object? sender,EventArgs e)=>queue.TryEnqueue(UpdateReminder);

    /// <summary>打开定时窗口（启动器「定时关机…」、托盘）。</summary>
    public void ShowTimer()
    {
        if(PowerTimer is not { } timer)return;
        Present(SecondaryId.Timer,Strings.Module_PowerTimer_Name,()=>new TimerView(timer),v=>((TimerView)v).Open());
    }

    void UpdateReminder()
    {
        if(closing)return;
        bool remind=PowerTimer?.Current is {Reminding:true};
        bool shown=secondaries.TryGetValue(SecondaryId.Reminder,out var panel);
        if(remind&&!shown)ShowReminder();
        else if(!remind&&shown)panel!.Hide();
    }

    void ShowReminder()
    {
        var view=new PowerReminderView(PowerTimer!);
        var panel=new GlassPanel(Strings.Module_PowerTimer_Name,view,view,Glass){Placement=PanelPlacement.Tray,HideOnBlur=()=>false};
        WatchFrames(panel);
        panel.Hidden+=(_,_)=>{if(secondaries.TryGetValue(SecondaryId.Reminder,out var p)&&p==panel)secondaries.Remove(SecondaryId.Reminder);panel.Dispose();};
        panel.Prepare();secondaries[SecondaryId.Reminder]=panel;
        // 主显示器（托盘所在）工作区的右下角，离边 16
        var monitor=GlassPanel.MonitorFromPoint(new GlassPanel.Point(),1);
        var info=new GlassPanel.MonitorInfo{Size=System.Runtime.InteropServices.Marshal.SizeOf<GlassPanel.MonitorInfo>()};
        GlassPanel.GetMonitorInfoW(monitor,ref info);
        panel.Show(true,info.Work.Right-16,info.Work.Bottom-16,false);
        WakeFrames();
    }
}
