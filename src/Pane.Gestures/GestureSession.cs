namespace Pane.Gestures;

public enum CommandPhase { Execute, BeginAware, ModifyAware, EndAware }
public sealed record CommandRequest(GestureBinding Binding, GestureTarget Target, CommandPhase Phase, GestureModifier Modifier);

// 捕获后的一次识别会话，不调用系统 API，测试与真实服务共用。
public sealed class GestureSession
{
    readonly GestureConfig config;
    readonly GestureButton button;
    readonly GestureRecognizer recognizer;
    GestureTarget target;
    GestureModifier modifier;
    GestureBinding? aware;
    bool frozen, immediateOccurred, modifierOccurred, ended;
    public event Action<CommandRequest>? Command;
    public event Action<GestureProgress>? Progress;
    public GestureSession(GestureConfig config,GestureButton button,GestureTarget target,double dpi)
    {this.config=config;this.button=button;this.target=target;recognizer=new(target.Start,dpi,config.Settings.AllowDiagonalMultiStroke);}
    GestureBinding? Match(GestureModifier m)=>GestureMatcher.Match(config,new(button,recognizer.Directions,m),target.ExePath,target.WindowClass);
    public GestureProgress Current=>new(button,recognizer.Directions.ToArray(),modifier,aware?.Name??Match(modifier)?.Name);
    public void Move(GesturePoint point)
    {
        if(ended)return;target=target with {End=point};
        if(!frozen)recognizer.Add(point);
        Progress?.Invoke(Current);
    }
    public void Modify(GestureModifier value)
    {
        if(ended)return;modifierOccurred=true;modifier=value;
        if(aware!=null){Command?.Invoke(new(aware,target,CommandPhase.ModifyAware,value));Progress?.Invoke(Current);return;}
        var binding=Match(value);
        if(binding==null){var basic=Match(GestureModifier.None);if(basic?.Command is TaskSwitchCommand or VolumeCommand {Action:VolumeAction.WheelAdjust})binding=basic;}
        if(binding!=null&&(binding.ExecuteOnModifier||binding.Command is TaskSwitchCommand or VolumeCommand {Action:VolumeAction.WheelAdjust}))
        {
            immediateOccurred=true;frozen=true;
            if(binding.Command is TaskSwitchCommand or VolumeCommand {Action:VolumeAction.WheelAdjust}){
                aware=binding;Command?.Invoke(new(binding,target,CommandPhase.BeginAware,value));
            }else Command?.Invoke(new(binding,target,CommandPhase.Execute,value));
        }
        Progress?.Invoke(Current);
    }
    public bool End(bool cancel=false)
    {
        if(ended)return false;ended=true;
        if(aware!=null){Command?.Invoke(new(aware,target,CommandPhase.EndAware,modifier));return true;}
        if(cancel)return false;
        var binding=Match(modifierOccurred?modifier:GestureModifier.None);
        if(binding!=null&&!binding.ExecuteOnModifier&&(!immediateOccurred||modifierOccurred&&binding.Pattern.Modifier!=GestureModifier.None)){
            Command?.Invoke(new(binding,target,CommandPhase.Execute,modifier));return true;
        }
        return immediateOccurred;
    }
}
