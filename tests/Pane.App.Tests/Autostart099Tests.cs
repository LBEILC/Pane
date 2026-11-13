using Pane.Contracts;
using Pane.Hosting;
using Pane.Services;
using Pane.Settings;
using System.Text;

internal static class Autostart099Tests
{
    internal static int Run(PaneSettings defaults){
        int checks=0;
        void Check(bool ok,string name){checks++;if(!ok)throw new Exception("099: "+name);}
        string root=TestResults.NewDirectory("autostart099-");
        const string exe=@"C:\Pane\Pane.App.exe";
        var registry=new FakeRegistry();
        var service=new AutostartService(true,exe,new SettingsStore(root,defaults),registry);
        foreach(bool disabled in new[]{false,true})
        foreach(string? command in new string?[]{null,@"C:\Other\Pane.App.exe --autostart",@"C:\other.exe",'"'+exe.ToUpperInvariant()+"\" --autostart"})
        foreach(string? user in new string?[]{null,"~ RUNASADMIN","WIN8RTM RUNASADMIN HIGHDPIAWARE","NOT_RUNASADMIN"})
        foreach(string? machine in new string?[]{null,"~ RUNASADMIN","runasadmin"}){
            registry.Value=command;registry.Disabled=disabled;
            registry.User.Clear();registry.Machine.Clear();
            registry.User[exe.ToLowerInvariant()]=user;registry.Machine[exe.ToUpperInvariant()]=machine;
            var expected=command==null||!command.StartsWith('"')?AutostartState.Off:
                user is "~ RUNASADMIN" or "WIN8RTM RUNASADMIN HIGHDPIAWARE"||machine!=null?AutostartState.ElevationRequired:
                disabled?AutostartState.DisabledInTaskManager:AutostartState.On;
            Check(service.State==expected,"状态矩阵 "+checks);
        }
        registry.ThrowRead=true;
        Check(new AutostartService(false,exe,new SettingsStore(root,defaults),registry).State==AutostartState.Unavailable,"开发版优先且不读注册表");
        registry.ThrowRead=false;registry.Value='"'+exe+"\" --autostart";registry.Disabled=false;registry.User.Clear();registry.Machine.Clear();
        Check(!AutostartService.RequiresElevation("RUNASADMIN_SUFFIX")&&!AutostartService.RequiresElevation("X_RUNASADMIN"),"完整空格标记匹配");

        var boot=new DateTimeOffset(2026,11,13,0,0,0,TimeSpan.Zero);var now=boot.AddMinutes(1);
        string Folder(string name)=>Path.Combine(root,name);
        string Log(string name)=>Path.Combine(Folder(name),"logs","autostart.log");
        AutostartLog Logger(string name,string runtime="JIT")=>new(Folder(name),()=>now,boot,"0.1.999",runtime,false);
        Logger("normal").Automatic();
        var lines=File.ReadAllLines(Log("normal"));
        Check(lines.Length==1&&lines[0].Contains("result=started"),"正常启动一条");
        Check(lines[0].Contains("version=0.1.999\truntime=JIT\televated=False")&&lines[0].Contains($"boot={boot:O}"),"运行信息完整");
        Logger("duplicate","AOT").Automatic(true);
        lines=File.ReadAllLines(Log("duplicate"));
        Check(lines.Length==1&&lines[0].Contains("result=existing-instance")&&lines[0].Contains("runtime=AOT"),"已有实例一条");
        Logger("failure").Automatic(error:new IOException("托盘创建失败"));
        lines=File.ReadAllLines(Log("failure"));
        Check(lines.Length==1&&lines[0].Contains("type=System.IO.IOException\tmessage=托盘创建失败"),"失败类型消息保持单行 UTF-8");
        Logger("private").Automatic(error:new IOException("用户文本\r\n第二行\t尾部"));
        Check(File.ReadAllLines(Log("private")).Length==1&&!File.ReadAllText(Log("private")).Contains("用户文本")&&File.ReadAllText(Log("private")).Contains("message=异常消息已脱敏"),"未知异常消息不泄漏用户数据");
        Logger("normal").Manual(true,true,exe,registry);
        Check(File.ReadAllLines(Log("normal")).Length==1,"本次已自启不补记");
        Logger("duplicate").Manual(true,true,exe,registry);
        Check(File.ReadAllLines(Log("duplicate")).Length==1,"重复实例也属于本次自启");
        Logger("disabled").Manual(false,true,exe,registry);
        Check(!File.Exists(Log("disabled")),"设置关闭不补记");
        registry.User[exe]="~ RUNASADMIN HIGHDPIAWARE";registry.Disabled=true;
        Logger("missing").Manual(true,true,exe,registry);
        lines=File.ReadAllLines(Log("missing"));
        Check(lines.Length==1&&lines[0].Contains("event=missing-autostart\tstate=ElevationRequired"),"没有记录补记状态");
        Check(lines[0].Contains("command=\"C:\\Pane\\Pane.App.exe\" --autostart\tdisabled=True\tuserFlags=~ RUNASADMIN HIGHDPIAWARE\tmachineFlags="),"补记启动命令禁用和兼容标记");
        now=boot.AddMinutes(-1);Logger("previous").Automatic();now=boot.AddMinutes(1);
        Logger("previous").Manual(true,true,exe,registry);
        Check(File.ReadAllLines(Log("previous")).Length==2,"前一次开机的记录不算本次");
        now=boot;Logger("boundary").Automatic();now=boot.AddMinutes(1);Logger("boundary").Manual(true,true,exe,registry);
        Check(File.ReadAllLines(Log("boundary")).Length==1,"开机时间边界");

        Logger("rotation").Automatic();
        File.AppendAllText(Log("rotation"),new string('x',AutostartLog.Limit-(int)new FileInfo(Log("rotation")).Length));
        Logger("rotation").Manual(true,true,exe,registry);
        Check(!File.Exists(Log("rotation")+".1"),"已有记录不触发补记或轮转");
        Logger("rotation").Automatic(true);
        Check(new FileInfo(Log("rotation")+".1").Length==AutostartLog.Limit&&new FileInfo(Log("rotation")).Length<AutostartLog.Limit,"达到上限轮转");
        File.WriteAllText(Log("rotation"),"invalid line\n");
        Logger("rotation").Manual(true,true,exe,registry);
        Check(File.ReadAllText(Log("rotation"))=="invalid line\n","从备份识别本次自启");
        File.WriteAllText(Log("rotation"),new string('y',AutostartLog.Limit));Logger("rotation").Automatic();
        Check(File.ReadAllText(Log("rotation")+".1")[0]=='y'&&Directory.GetFiles(Path.GetDirectoryName(Log("rotation"))!).Length==2,"只保留两份");
        Directory.CreateDirectory(Folder("blocked"));File.WriteAllText(Path.Combine(Folder("blocked"),"logs"),"文件占据日志目录");
        Logger("blocked").Automatic();Logger("blocked").Manual(true,true,exe,registry);
        Check(File.ReadAllText(Path.Combine(Folder("blocked"),"logs"))=="文件占据日志目录","目录不可写不抛异常");
        registry.ThrowRead=true;Logger("read-failed").Manual(true,true,exe,registry);
        Check(!File.Exists(Log("read-failed")),"读取诊断失败不影响启动");
        Check(registry.Writes==0,"所有新增测试不写注册表");
        var original=AutostartDiagnostics.OpenLog;
        try{
            AutostartDiagnostics.OpenLog=()=>Logger("lifecycle");
            AutostartDiagnostics.Begin(LaunchCommand.Autostart);
            AutostartDiagnostics.AfterSingleInstance(LaunchCommand.Autostart,true);
            AutostartDiagnostics.StartupFailed(new IOException("托盘创建失败"));
            AutostartDiagnostics.Failed(LaunchCommand.Autostart,new IOException());
            Check(File.ReadAllLines(Log("lifecycle")).Length==2,"启动入口成功记录后早期失败补记且不重复");
            AutostartDiagnostics.Begin(LaunchCommand.Autostart);AutostartDiagnostics.Ready();
            AutostartDiagnostics.StartupFailed(new IOException());
            Check(File.ReadAllLines(Log("lifecycle")).Length==2,"就绪后的故障不记作早期失败");
            AutostartDiagnostics.Begin(LaunchCommand.Autostart);
            AutostartDiagnostics.AfterSingleInstance(LaunchCommand.Autostart,false);
            AutostartDiagnostics.StartupFailed(new IOException());
            Check(File.ReadAllLines(Log("lifecycle")).Length==3&&File.ReadAllLines(Log("lifecycle"))[^1].Contains("result=existing-instance"),"已有实例记录后结束启动阶段");
            AutostartDiagnostics.Begin(LaunchCommand.Show);AutostartDiagnostics.StartupFailed(new IOException());
            Check(File.ReadAllLines(Log("lifecycle")).Length==3,"手动启动不记自启故障");
        }finally{AutostartDiagnostics.Ready();AutostartDiagnostics.OpenLog=original;}
        Console.WriteLine($"PASS autostart099 assertions={checks}; realRegistry=0; userLogs=0");
        return checks;
    }

    sealed class FakeRegistry:IStartupRegistry
    {
        internal string? Value;internal bool ThrowRead;internal int Writes;
        internal Dictionary<string,string?> User=new(StringComparer.OrdinalIgnoreCase),Machine=new(StringComparer.OrdinalIgnoreCase);
        public bool Disabled {get;set;}
        public string? Read()=>ThrowRead?throw new IOException("模拟读取失败"):Value;
        public string? ReadCompatibility(string executable,bool allUsers)=>(allUsers?Machine:User).GetValueOrDefault(executable);
        public void Write(string command){Writes++;throw new InvalidOperationException("禁止写注册表");}
        public void Delete(){Writes++;throw new InvalidOperationException("禁止写注册表");}
    }
}
