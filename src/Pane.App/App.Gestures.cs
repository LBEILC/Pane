using Pane.Contracts;
using Pane.Gestures;
using Pane.Views;
using Windows.UI;
namespace Pane;
public sealed partial class App
{
    GestureService? gestureService;
    ISelectionReader? selectionReader;
    SuperView? superView;
    CancellationTokenSource? selectionCancellation;
    GestureHint? lastHint;
    bool wasPaused;
    Task? trailListener;
    void StartGestures(string data){
        string path=Path.Combine(data,"gestures.json");
        gestureService=new(GestureConfigStore.LoadOrDefaults(path),Environment.GetEnvironmentVariable("PANE_OBSERVE")=="1");
        gestureService.StateChanged+=(_,_)=>queue.TryEnqueue(()=>{
            if(closing)return;bool paused=gestureService.IsPaused;shell!.GesturesPaused=paused;
            if(paused!=wasPaused){wasPaused=paused;gestures!.Hint(new(GestureHintKind.Notice,[],CommandName:paused?"手势已暂停":"手势已恢复"));}
            if(gestureService.State==GestureServiceState.WGesturesRunning)shell.NotifyOnce("检测到 WGestures 正在运行，Pane 的鼠标手势先不启用");
        });
        gestureService.PauseHotkeyUnavailable+=(_,key)=>queue.TryEnqueue(()=>shell?.NotifyOnce("暂停热键被占用："+key));
        gestureService.EmergencyExitRequested+=(_,_)=>queue.TryEnqueue(Shutdown);
        gestureService.Progress+=(_,p)=>queue.TryEnqueue(()=>{if(closing)return;DrainTrail();lastHint=new(GestureHintKind.Drawing,p.Directions.Select(d=>(GestureArrow)d).ToArray(),Modifier(p.Modifier),p.CommandName);gestures!.Hint(lastHint);});
        gestureService.Finished+=(_,p)=>queue.TryEnqueue(()=>{if(closing)return;DrainTrail();if(p.Kind==GestureOutcomeKind.ReplayedClick){gestures!.Dismiss();return;}/* 设计改：动了一点又快速松开，先弹出的「…」要收起 */var kind=p.Kind==GestureOutcomeKind.Executed?GestureHintKind.Executed:p.Kind==GestureOutcomeKind.NotRecognized?GestureHintKind.NotRecognized:GestureHintKind.Failed;gestures!.Hint(new(kind,lastHint?.Arrows??[],lastHint?.Modifier??GestureHintModifier.None,p.CommandName,p.FailureReason));});
        gestureService.SuperPanelRequested+=(_,p)=>queue.TryEnqueue(()=>_ = ShowSuper(p));
        ApplySuperSettings();ApplyGestureDisplay();gestureService.Start();
        trailListener=ListenTrail();
    }
    async Task ListenTrail(){
        try{
            while(await gestureService!.Trail.WaitToReadAsync(cancellation.Token).ConfigureAwait(false)){
                var drained=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                if(!queue.TryEnqueue(()=>{try{if(!closing){WakeFrames();DrainTrail();}}finally{drained.TrySetResult();}}))break;
                await drained.Task.WaitAsync(cancellation.Token).ConfigureAwait(false);
            }
        }catch(OperationCanceledException) when(cancellation.IsCancellationRequested){}
    }
    void ApplySuperSettings(){var s=Store.Current.SuperPanel;gestureService?.ConfigureSuperPanel(s.Enabled?(int)s.TriggerButton:0,s.HoldMilliseconds,s.PauseInFullscreen,s.DisabledPrograms);}
    void ApplyGestureDisplay(){if(gestureService==null||gestures==null)return;var s=gestureService.Config.Settings;gestures.ShowTrail=s.ShowTrail;gestures.FadeTrail=s.FadeTrail;gestures.ShowHint=s.ShowCommandName;}
    void DrainTrail(){if(gestureService==null||gestures==null)return;while(gestureService.Trail.TryRead(out var p)){
        if(p.Phase==TrailPhase.Begin){lastHint=null;Color? color=null;if(gestureService.Config.Settings.TrailColors.TryGetValue(p.Button,out string? hex)){uint rgb=Convert.ToUInt32(hex[1..],16);color=Color.FromArgb(255,(byte)(rgb>>16),(byte)(rgb>>8),(byte)rgb);}gestures.TrailBegin(p.PhysicalX,p.PhysicalY,color);}
        else if(p.Phase==TrailPhase.Move)gestures.TrailMove(p.PhysicalX,p.PhysicalY);else gestures.TrailEnd();
    }}
    static GestureHintModifier Modifier(GestureModifier m)=>m switch{GestureModifier.WheelUp=>GestureHintModifier.WheelUp,GestureModifier.WheelDown=>GestureHintModifier.WheelDown,GestureModifier.LeftClick=>GestureHintModifier.LeftClick,GestureModifier.MiddleClick=>GestureHintModifier.MiddleClick,GestureModifier.X1Click=>GestureHintModifier.X1Click,GestureModifier.X2Click=>GestureHintModifier.X2Click,_=>GestureHintModifier.None};
    async Task ShowSuper(SuperPanelRequest request){
        selectionCancellation?.Cancel();var pending=new CancellationTokenSource();selectionCancellation=pending;
        try{var result=await selectionReader!.ReadAsync(pending.Token);if(pending.IsCancellationRequested||closing)return;
            superView!.SetSelection(result);if(controlsEnabled)File.AppendAllText(output+".selection-order","SetSelection\n");
            foreach(var panel in panels.Values)panel.Hide();current=panels["super"];current.Show(true,request.PhysicalX,request.PhysicalY,Environment.GetEnvironmentVariable("PANE_VERIFY")!="1");
            if(controlsEnabled)File.AppendAllText(output+".selection-order","Show\n");
        }catch(OperationCanceledException){}catch(Exception e){if(!closing){superView!.SetSelection(new(null,0,SelectionFailure.Unsupported));current=panels["super"];current.Show(true,request.PhysicalX,request.PhysicalY,Environment.GetEnvironmentVariable("PANE_VERIFY")!="1");if(controlsEnabled)File.AppendAllText(output+".selection-errors",e.GetType().Name+"\n");}}finally{if(selectionCancellation==pending)selectionCancellation=null;pending.Dispose();}
    }
    // 设置页「鼠标手势」分区用：读当前配置、保存并立即生效、从 WGestures 预览 / 导入（设计接线）
    string GesturePath=>Path.Combine(Store.DirectoryPath,"gestures.json");
    public GestureConfig GestureConfig=>gestureService?.Config??LoadGestureFile();
    // 演示模式没有手势服务：读数据目录里的配置；设计预览可用 PANE_DESIGN_GESTURES 指定一份 gestures.json
    GestureConfig LoadGestureFile(){
        if(File.Exists(GesturePath))return GestureConfigStore.Load(GesturePath);
        var preview=Environment.GetEnvironmentVariable("PANE_DESIGN_GESTURES");
        return demo&&!string.IsNullOrEmpty(preview)&&File.Exists(preview)?GestureConfigStore.Load(preview):GestureConfigStore.LoadOrDefaults(GesturePath);
    }
    public GestureServiceState? GestureState=>gestureService?.State;
    public void SaveGestures(GestureConfig config){GestureConfigStore.Validate(config);GestureConfigStore.Save(GesturePath,config);gestureService?.Apply(config);ApplyGestureDisplay();}
    public void ResetGestures()=>SaveGestures(GestureConfigStore.Defaults);
    /// <summary>预览 WGestures 的配置：path 为 null 自动查找，也可以是用户选的文件（043）。没找到时 <see cref="WGesturesSearched"/> 是找过的位置。</summary>
    public ImportPreview? PreviewWGestures(string? path=null){var importer=new WGesturesImporter();var preview=importer.Preview(path);WGesturesSearched=importer.SearchedLocations;return preview;}
    public IReadOnlyList<string> WGesturesSearched {get;private set;}=[];
    public void ImportWGestures(ImportPreview preview)=>SaveGestures(new WGesturesImporter().Import(preview,true));
    void ImportGestures(string source){
        try{var importer=new WGesturesImporter();var preview=importer.Preview(string.IsNullOrWhiteSpace(source)?null:source.Trim())??throw new InvalidDataException("未找到 WGestures 配置");
            var config=importer.Import(preview,true);GestureConfigStore.Save(Path.Combine(Store.DirectoryPath,"gestures.json"),config);gestureService?.Apply(config);ApplyGestureDisplay();
            File.WriteAllText(output+".import-result",$"global={preview.GlobalCount};rules={preview.AppRuleCount};disabled={preview.DisabledAppCount};unsupported={preview.Unsupported.Count}\n"+string.Join("\n",preview.Unsupported.Select(p=>$"{p.Where}: {p.GestureText}: {p.Reason}")));
        }catch(Exception e){File.WriteAllText(output+".import-result","error="+e.Message);}
    }
}
