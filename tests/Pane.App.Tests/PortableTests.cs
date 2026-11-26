using Pane.Contracts;
using Pane.Hosting;
using Pane.Services;
using Pane.Services.Updates;
using Pane.Settings;

internal static class PortableTests
{
    public static async Task<int> Run(PaneSettings defaults)
    {
        int count = 0;
        void Check(bool ok, string label) { if (!ok) throw new Exception(label); count++; }
        string root = Directory.CreateTempSubdirectory("Pane-portable-").FullName;
        try
        {
            string app = Directory.CreateDirectory(Path.Combine(root,"Pane")).FullName;
            string installed = Path.Combine(root,"Roaming","Pane"), isolated = Path.Combine(root,"override");
            Check(RuntimeMode.Resolve(app,null,installed).DataDirectory == installed, "安装目录");
            var normal = RuntimeMode.Resolve(app,isolated,installed);
            Check(!normal.IsPortable && normal.DataDirectory == isolated, "安装版隔离目录");

            File.WriteAllText(Path.Combine(app,"portable"), "");
            var mode = RuntimeMode.Resolve(app,null,installed);
            Check(mode.IsProduction && mode.IsPortable && mode.DataDirectory == Path.Combine(app,"data"), "便携目录");
            Check(RuntimeMode.Resolve(app,isolated,installed).DataDirectory == isolated, "覆盖便携目录");
            var before = Directory.GetFileSystemEntries(root,"*",SearchOption.AllDirectories);
            string? messagePath = null;
            Check(mode.CheckWritable(app, path => messagePath = path, _ => throw new UnauthorizedAccessException()) == 1
                && messagePath == app && before.SequenceEqual(Directory.GetFileSystemEntries(root,"*",SearchOption.AllDirectories)), "只读时提示并退出");
            Check(mode.CheckWritable(app, _ => throw new Exception()) == 0, "可写检查");

            var store = new SettingsStore(mode.DataDirectory,defaults);
            store.Load();
            string exe = Path.Combine(app,"Pane.App.exe");
            var registry = new ReleaseTests.RegistryFake();
            var autostart = new AutostartService(true,exe,store,registry,true);
            autostart.Align();
            Check(!store.Current.General.StartWithWindows && registry.Writes == 0, "首次启动零写入");
            autostart.Set(true);
            Check(AutostartService.PointsTo(registry.Value,exe), "启动项指向便携程序");
            registry.Value = null;
            autostart.Align();
            Check(registry.Writes == 1 && registry.Value == null, "换机不注册");
            registry.Value = "\"" + Path.Combine(root,"old","Pane.App.exe") + "\" --autostart";
            autostart.Align();
            Check(AutostartService.PointsTo(registry.Value,exe), "移动后修正");
            Check(!Directory.Exists(installed), "安装目录无新增文件");

            File.WriteAllBytes(Path.Combine(mode.DataDirectory,UpdateService.MirrorSecretFile),System.Security.Cryptography.RandomNumberGenerator.GetBytes(32));
            await using var server = new UpdateServer();
            using var updates = new UpdateService("0.1.99",mode.DataDirectory,()=>true,_=>throw new Exception(),server.Url,portable:true);
            await updates.CheckAsync(default);
            Check(updates.MirrorCdk.Length == 0 && File.ReadAllLines(Path.Combine(mode.DataDirectory,"logs","credentials.log")).Length == 1,"异机密文视为空并记录一次");
            Check(updates.State == UpdateState.Available && updates.Latest?.PageUrl != null && server.Downloads == 0, "只检查更新");
            try { await updates.DownloadAsync(default); throw new Exception("允许便携下载"); } catch (InvalidOperationException) { count++; }
            try { updates.InstallAndRestart(); throw new Exception("允许便携安装"); } catch (InvalidOperationException) { count++; }
            Console.WriteLine($"PASS portable assertions={count}; simulatedRegistryWrites={registry.Writes}; externalDownloads=0");
            return count;
        }
        finally { Directory.Delete(root,true); }
    }
}
