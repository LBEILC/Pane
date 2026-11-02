using Pane.Localization;
// 软件内更新的界面接线（设计负责人）：设置 → 关于里的「更新」组读 Updates；下载好时托盘提示一次（每个版本一次）。
// 真实服务由工程在 023 里赋给 Updates（在 StartUpdates 之前）；演示模式用 DesignData/UpdateSample。
using Pane.Contracts;
namespace Pane;
public sealed partial class App
{
    /// <summary>软件内更新服务；023 之前正式版为 null（关于页只显示版本）。</summary>
    public IUpdateService? Updates {get;set;}
    string? notifiedUpdate;
    void StartUpdates()
    {
        if(demo)Updates??=new Pane.DesignData.UpdateSample();
        if(Updates is not {} u)return;
        u.Changed+=(_,_)=>queue.TryEnqueue(()=>{
            if(closing||u.State!=UpdateState.Ready||u.Latest is not {} r||notifiedUpdate==r.Version)return;
            notifiedUpdate=r.Version;
            shell?.NotifyOnce(string.Format(Strings.Update_ReadyNotification, (object?)(r.Version)));
        });
    }
}
