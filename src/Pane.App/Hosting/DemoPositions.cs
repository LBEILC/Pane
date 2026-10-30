using System.Runtime.InteropServices;
using Pane.Hosting;

namespace Pane;

// 演示控制独立放在 Hosting；不改变视图或产品服务接口。
public sealed partial class App
{
    Microsoft.UI.Dispatching.DispatcherQueueTimer? positionsTimer;
    int positionIndex=-1;
    bool positionWaiting,positionHiding;
    void StartPositions()
    {
        if(!demo||!controlsEnabled)return;
        positionsTimer=queue.CreateTimer();positionsTimer.Interval=TimeSpan.FromMilliseconds(16);
        positionsTimer.Tick+=(_,_)=>{
            if(closing){positionsTimer.Stop();return;}
            try {TickPositions();}catch(Exception ex){File.WriteAllText(output+".error",ex.ToString());Shutdown();}
        };
        positionsTimer.Start();
    }
    void TickPositions()
    {
        if(!ready)return;
        if(positionIndex<0){
            if(!Take("positions",out _))return;
            if(cycleIndex>=0||pendingAck!=null)throw new InvalidOperationException("positions 不能和其他演示命令同时执行");
            Command("hide");positionIndex=0;positionHiding=true;positionWaiting=false;
            File.WriteAllText(output+".positions-diagnostics","");
        }
        if(!Settled)return;
        if(positionHiding){
            if(positionIndex==18){positionIndex=-1;Ack("positioned");return;}
            string name=positionIndex<9?"clipboard":"super";
            current=panels[name];current.ShowGrid(positionIndex%9);
            positionHiding=false;return;
        }
        if(!positionWaiting){
            var line=$"{positionIndex+1};panel={(positionIndex<9?"clipboard":"super")};{current!.WindowDiagnostics()}";
            File.AppendAllText(output+".positions-diagnostics",line+"\n");
            File.WriteAllText(output+".position-"+(positionIndex+1),line);
            positionWaiting=true;
        }
        if(Environment.GetEnvironmentVariable("PANE_CYCLE_CAPTURE")=="1"&&!Take("position-next",out _))return;
        current!.Hide();positionIndex++;positionHiding=true;positionWaiting=false;
    }
}
