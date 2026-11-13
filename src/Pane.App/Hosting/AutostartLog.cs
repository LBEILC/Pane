using System.Globalization;
using System.Runtime.CompilerServices;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using Pane.Services;

namespace Pane.Hosting;

// 注入时钟及运行信息，测试不依赖本机开机时间、令牌或用户数据目录。
internal sealed class AutostartLog(string directory,Func<DateTimeOffset> clock,DateTimeOffset boot,string version,string runtime,bool elevated)
{
    internal const int Limit=256*1024;
    static readonly object gate=new();
    string PathName=>Path.Combine(directory,"logs","autostart.log");

    internal void Automatic(bool existingInstance=false,Exception? error=null)=>Safely(()=>Append(
        $"event=autostart\tversion={Clean(version)}\truntime={runtime}\televated={elevated}\tboot={boot:O}\tresult="+
        (error!=null?"early-failure":existingInstance?"existing-instance":"started")+
        (error==null?"":$"\ttype={Clean(error.GetType().FullName)}\tmessage={Clean(SafeMessage(error))}")));

    internal void Manual(bool enabled,bool production,string executable,IStartupRegistry registry)=>Safely(()=>{
        if(!enabled||HasAutomatic())return;
        // 在 Align 修复旧路径或缺失项之前取证；此处只读，不修改注册表。
        var state=AutostartService.GetState(production,executable,registry);
        string? command=registry.Read();
        Append($"event=missing-autostart\tstate={state}\tcommand={Clean(command)}\tdisabled={registry.Disabled}"+
            $"\tuserFlags={Clean(registry.ReadCompatibility(executable,false))}\tmachineFlags={Clean(registry.ReadCompatibility(executable,true))}");
    });

    bool HasAutomatic(){
        var now=clock();
        foreach(string path in new[]{PathName,PathName+".1"}){
            if(!File.Exists(path))continue;
            foreach(string line in File.ReadLines(path)){
                var fields=line.Split('\t',3);
                if(fields.Length>=2&&fields[1]=="event=autostart"&&
                    DateTimeOffset.TryParseExact(fields[0],"O",CultureInfo.InvariantCulture,DateTimeStyles.None,out var time)&&time>=boot&&time<=now)return true;
            }
        }
        return false;
    }

    void Append(string fields){
        string line=$"{clock():O}\t{fields}{Environment.NewLine}";
        Directory.CreateDirectory(Path.GetDirectoryName(PathName)!);
        if(File.Exists(PathName)&&new FileInfo(PathName).Length+Encoding.UTF8.GetByteCount(line)>Limit)File.Move(PathName,PathName+".1",true);
        File.AppendAllText(PathName,line,new UTF8Encoding(false));
    }
    static string Clean(string? value)=>new((value??"").Take(4096).Select(c=>char.IsControl(c)?' ':c).ToArray());
    // 初始化服务的异常也可能含用户文本；沿用 CrashLog 的消息白名单。
    static string SafeMessage(Exception error)=>error.Message is "剪贴板历史初始化失败" or "托盘创建失败"?error.Message:"异常消息已脱敏";
    static void Safely(Action action){try{lock(gate)action();}catch(Exception){/* 诊断失败不能阻断启动。 */}}
}

internal static class AutostartDiagnostics
{
    static int starting;
    internal static Func<AutostartLog?> OpenLog=Create;
    static AutostartLog? Create(){
        try{
            var mode=RuntimeMode.Current;
            using var identity=WindowsIdentity.GetCurrent();
            return new(mode.DataDirectory,()=>DateTimeOffset.UtcNow,DateTimeOffset.UtcNow-TimeSpan.FromMilliseconds(Environment.TickCount64),
                Settings.SettingsStore.CurrentVersion(),RuntimeFeature.IsDynamicCodeSupported?"JIT":"AOT",
                new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator));
        }catch(Exception){return null;}
    }
    internal static void AfterSingleInstance(LaunchCommand command,bool entered){
        try{
            if(!entered)Ready();
            var log=OpenLog();
            if(command==LaunchCommand.Autostart){log?.Automatic(!entered);return;}
            if(command!=LaunchCommand.Show||log==null)return;
            // 只读已保存的意图，不提前加载/迁移设置或触发启动项对齐。
            string path=Path.Combine(RuntimeMode.Current.DataDirectory,"settings.json");
            if(!File.Exists(path))return;
            using var settings=JsonDocument.Parse(File.ReadAllText(path));
            bool enabled=settings.RootElement.TryGetProperty("General",out var general)&&
                general.TryGetProperty("StartWithWindows",out var start)&&start.ValueKind==JsonValueKind.True;
            log.Manual(enabled,RuntimeMode.Current.IsProduction,Environment.ProcessPath!,new WindowsStartupRegistry());
        }catch(Exception){/* 设置或注册表不可读时仍继续原有启动流程。 */}
    }
    internal static void Begin(LaunchCommand command)=>Interlocked.Exchange(ref starting,command==LaunchCommand.Autostart?1:0);
    internal static void Failed(LaunchCommand command,Exception error){if(command==LaunchCommand.Autostart)StartupFailed(error);}
    internal static void Ready()=>Interlocked.Exchange(ref starting,0);
    internal static void StartupFailed(Exception error){
        try{if(Interlocked.Exchange(ref starting,0)==1)OpenLog()?.Automatic(error:error);}catch(Exception){/* 诊断不可影响崩溃处理。 */}
    }
}
