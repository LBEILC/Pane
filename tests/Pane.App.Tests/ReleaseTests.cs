using Pane.Hosting;
using Pane.Services;
using Pane.Settings;
using Pane.Contracts;
internal static class ReleaseTests
{
    public static int Run(PaneSettings defaults){
        int count=0;
        void Check(bool ok,string name){count++;if(!ok)throw new Exception("017: "+name);}
        var root=Path.Combine(Path.GetTempPath(),"Pane-017-unit-"+Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try {
            string project=Path.Combine(root,"project"),deploy=Path.Combine(root,"release"),production=Path.Combine(root,"roaming");
            Directory.CreateDirectory(project);File.WriteAllText(Path.Combine(project,"Pane.App.csproj"),"");
            var dev=RuntimeMode.Resolve(Path.Combine(project,"bin","aot"),null,production);
            Check(!dev.IsProduction&&dev.DataDirectory==Path.Combine(project,"bin","data"),"开发目录");
            var prod=RuntimeMode.Resolve(deploy,null,production);Check(prod.IsProduction&&prod.DataDirectory==production,"正式目录");
            var overridden=RuntimeMode.Resolve(deploy,Path.Combine(root,"override"),production);
            Check(overridden.IsProduction&&overridden.DataDirectory==Path.Combine(root,"override"),"路径覆盖不改变正式身份");
            Check(!RuntimeMode.Resolve(project,production,production).IsProduction,"覆盖仍是开发版");
            Check(LaunchArguments.Parse([])==LaunchCommand.Show,"默认显示");
            Check(LaunchArguments.Parse(["--autostart"])==LaunchCommand.Autostart,"静默启动");
            Check(LaunchArguments.Parse(["--exit"])==LaunchCommand.Exit,"退出");
            Check(LaunchArguments.Parse(["show-settings"])==LaunchCommand.Invalid,"未知命令不转发");
            Check(LaunchArguments.Parse(["--exit","--autostart"])==LaunchCommand.Invalid,"组合参数拒绝");
            var store=new SettingsStore(Path.Combine(root,"data"),defaults);store.Load();
            string exe=Path.Combine(root,"new path","Pane.App.exe"),oldExe=Path.Combine(root,"old path","Pane.App.exe");
            var fake=new RegistryFake();var service=new AutostartService(true,exe,store,fake);
            service.Align();Check(fake.Writes==0&&service.State==AutostartState.Off,"首次运行不注册");
            var development=new AutostartService(false,exe,store,fake);development.Align();
            Check(development.State==AutostartState.Unavailable&&development.UnavailableReason=="开发版不注册开机启动"&&fake.Reads==1,"开发版不读取注册表");
            try {development.Set(true);throw new Exception("允许开发注册");}catch(InvalidOperationException){count++;}
            service.Set(true);Check(fake.Value==$"\"{exe}\" --autostart"&&store.Current.General.StartWithWindows&&service.State==AutostartState.On,"注册并保存意图");
            fake.Value=$"\"{oldExe}\" --autostart";service.Align();Check(AutostartService.PointsTo(fake.Value,exe),"旧路径对齐");
            fake.Value=null;service.Align();Check(AutostartService.PointsTo(fake.Value,exe),"丢失启动项补写");
            fake.Disabled=true;int before=fake.Writes;service.Align();Check(service.State==AutostartState.DisabledInTaskManager&&before==fake.Writes,"禁用不改");
            fake.Value=$"\"{oldExe}\" --autostart";service.Align();Check(AutostartService.PointsTo(fake.Value,exe)&&service.State==AutostartState.DisabledInTaskManager,"禁用项迁移路径但保留禁用状态");
            fake.Value=null;service.Align();Check(fake.Value==null&&service.State==AutostartState.Off,"禁用不补写，缺少 Run 值显示关闭");
            fake.Disabled=false;service.Set(false);Check(service.State==AutostartState.Off&&!store.Current.General.StartWithWindows,"关闭同步设置");
            fake.Value=$"\"{oldExe}\" --autostart";before=fake.Writes;service.Align();Check(before==fake.Writes,"false 不主动修复");
            service.Set(false);Check(fake.Value==null,"删除旧 Pane 路径");
            fake.Value="\"C:\\Other.exe\" --autostart";before=fake.Writes;service.Align();
            try {service.Set(false);throw new Exception("删除其他程序");}catch(InvalidOperationException){count++;}
            Check(fake.Writes==before&&fake.Value.Contains("Other.exe"),"保留其他程序");
            fake.Value=null;fake.FailWrite=true;
            try {service.Set(true);throw new Exception("写失败未抛出");}catch(IOException){count++;}
            Check(!store.Current.General.StartWithWindows,"注册失败不保存成功意图");
            Console.WriteLine($"PASS release assertions={count}; registryWrites=0");
            return count;
        }finally {Directory.Delete(root,true);}
    }
    sealed class RegistryFake:IStartupRegistry
    {
        public string? Value;public int Writes,Reads;public bool FailWrite;
        public bool Disabled {get;set;}
        public string? Read(){Reads++;return Value;}
        public void Write(string command){if(FailWrite)throw new IOException("模拟权限失败");Writes++;Value=command;}
        public void Delete(){Writes++;Value=null;}
    }
}
