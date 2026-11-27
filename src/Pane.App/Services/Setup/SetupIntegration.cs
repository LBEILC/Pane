using Microsoft.Win32;
using Pane.Contracts;
using Pane.Settings;
using System.Runtime.InteropServices;
using System.Text.Json;
namespace Pane.Services.Setup;

internal sealed class SetupIntegration(SetupPaths paths, PaneSettings defaults, string? isolatedRoot = null, IStartupRegistry? registry = null) : ISetupIntegration
{
    readonly IStartupRegistry startup = registry ?? (isolatedRoot == null ? new WindowsStartupRegistry() : new FileStartupRegistry(isolatedRoot));
    string Menu => Path.Combine(isolatedRoot == null ? Environment.GetFolderPath(Environment.SpecialFolder.Programs) : Path.Combine(isolatedRoot, "menu"), "Pane.lnk");
    string Desktop => Path.Combine(isolatedRoot == null ? Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory) : Path.Combine(isolatedRoot, "desktop"), "Pane.lnk");
    string SettingsFile => Path.Combine(paths.Data, "settings.json");
    string RegistrationPath(string name) => isolatedRoot == null ? @"HKCU\" + SetupRegistration.Key(name) : SetupRegistration.FileName(isolatedRoot, name);
    public void SetPaths(SetupPaths value) => paths = value;
    public string? InstallLocation => SetupRegistration.Installed(paths, isolatedRoot)?.GetValueOrDefault("InstallLocation") as string;
    public string? InstalledVersion => SetupRegistration.Installed(paths, isolatedRoot)?.GetValueOrDefault("DisplayVersion") as string;
    public bool StartWithWindows => AutostartService.PointsTo(startup.Read(), paths.Exe) && !startup.Disabled;
    public static Dictionary<string, object> Registration(SetupPaths paths, string version, long bytes) => new()
    {
        ["DisplayName"] = "Pane", ["DisplayVersion"] = version, ["Publisher"] = "LBEILC",
        ["DisplayIcon"] = paths.Exe, ["InstallLocation"] = paths.Root,
        ["EstimatedSize"] = (int)Math.Min(int.MaxValue, (bytes + 1023) / 1024),
        ["UninstallString"] = $"\"{paths.Uninstaller}\"", ["NoModify"] = 1, ["NoRepair"] = 1
    };
    sealed record Snapshot(string? Startup, Dictionary<string, object>? Registration, Dictionary<string, object>? OwnedLegacy, Dictionary<string, byte[]?> Files);
    public object Capture()
    {
        // 只记录操作和路径，不记录注册表值、快捷方式内容或设置内容。
        T Read<T>(string operation, string path, Func<T> read)
        {
            try { return read(); }
            catch (Exception error) { UninstallChain.Log(operation, "failed", error, path); throw; }
        }
        var run = Read("capture-startup", isolatedRoot == null ? @"HKCU\Software\Microsoft\Windows\CurrentVersion\Run" : Path.Combine(isolatedRoot, "run.txt"), startup.Read);
        Dictionary<string, object>? CaptureKey(string name) => Read("capture-registration", RegistrationPath(name), () => SetupRegistration.Read(name, isolatedRoot));
        var registration = CaptureKey(SetupRegistration.ProductCode);
        var legacy = CaptureKey(SetupRegistration.Legacy);
        var files = new Dictionary<string, byte[]?>();
        foreach (string path in new[] { Menu, Desktop, SettingsFile, SettingsFile + ".tmp" })
            files[path] = Read("capture-file", path, () => ReadBytes(path));
        return new Snapshot(run, registration, legacy != null && SetupRegistration.Matches(legacy, paths) ? legacy : null, files);
    }
    static byte[]? ReadBytes(string path) => File.Exists(path) ? File.ReadAllBytes(path) : null;
    public void Restore(object snapshot)
    {
        var value = (Snapshot)snapshot;
        try { if (value.Startup == null) startup.Delete(); else startup.Write(value.Startup); }
        catch (Exception error) when (AutostartService.AccessDenied(error)) { UninstallChain.Log("restore-startup", "skipped", error); }
        WriteRegistration(SetupRegistration.ProductCode, value.Registration);
        if (value.OwnedLegacy != null && SetupRegistration.Read(SetupRegistration.Legacy, isolatedRoot) == null)
            WriteRegistration(SetupRegistration.Legacy, value.OwnedLegacy);
        foreach (var item in value.Files)
        {
            SetupFiles.Check(item.Key);
            if (item.Value == null) File.Delete(item.Key);
            else { Directory.CreateDirectory(Path.GetDirectoryName(item.Key)!); File.WriteAllBytes(item.Key, item.Value); }
        }
    }
    public bool Apply(string version, long bytes, SetupOptions options)
    {
        // 不调用 Load：读取设置时不得重命名损坏文件或创建用户数据目录。
        PaneSettings settings = ReadSettings(paths.Data, defaults);
        SetupFiles.Check(SettingsFile); SetupFiles.Check(SettingsFile + ".tmp");
        var store = new SettingsStore(paths.Data, settings);
        bool blocked = false;
        new AutostartService(true, paths.Exe, store, startup).Set(options.StartWithWindows, error =>
        {
            blocked = options.StartWithWindows;
            UninstallChain.Log(blocked ? "write-startup" : "delete-startup", "skipped", error);
        });
        WriteRegistration(SetupRegistration.ProductCode, Registration(paths, version, bytes));
        RemoveLegacy();
        Shortcut(Menu);
        if (options.DesktopShortcut) Shortcut(Desktop); else { SetupFiles.Check(Desktop); File.Delete(Desktop); }
        return blocked;
    }
    public void Remove()
    {
        if (AutostartService.PointsTo(startup.Read(), paths.Exe)) startup.Delete();
        SetupFiles.Check(Menu); SetupFiles.Check(Desktop);
        File.Delete(Menu); File.Delete(Desktop);
        WriteRegistration(SetupRegistration.ProductCode, null);
        RemoveLegacy();
    }
    public static PaneSettings ReadSettings(string data, PaneSettings fallback)
    {
        try { return JsonSerializer.Deserialize(File.ReadAllText(Path.Combine(data, "settings.json")), SettingsJson.Default.PaneSettings) ?? fallback; }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException or NotSupportedException) { return fallback; }
    }
    void RemoveLegacy()
    {
        var legacy = SetupRegistration.Read(SetupRegistration.Legacy, isolatedRoot);
        if (legacy == null) return;
        if (SetupRegistration.Matches(legacy, paths)) WriteRegistration(SetupRegistration.Legacy, null);
        else UninstallChain.Log("remove-legacy-registration", "skipped", path: RegistrationPath(SetupRegistration.Legacy), details: "安装目标不一致");
    }
    void WriteRegistration(string name, Dictionary<string, object>? fields)
    {
        if (isolatedRoot != null)
        {
            string file = RegistrationPath(name);
            SetupFiles.Check(file);
            if (fields == null) { File.Delete(file); return; }
            Directory.CreateDirectory(isolatedRoot);
            using var stream = File.Create(file);
            using var writer = new Utf8JsonWriter(stream);
            writer.WriteStartObject();
            foreach (var field in fields) { if (field.Value is int number) writer.WriteNumber(field.Key, number); else writer.WriteString(field.Key, (string)field.Value); }
            writer.WriteEndObject(); return;
        }
        Registry.CurrentUser.DeleteSubKeyTree(SetupRegistration.Key(name), false);
        if (fields == null) return;
        using var key = Registry.CurrentUser.CreateSubKey(SetupRegistration.Key(name));
        foreach (var field in fields) key.SetValue(field.Key, field.Value);
    }
    void Shortcut(string path)
    {
        SetupFiles.Check(path); Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        if (isolatedRoot != null) File.WriteAllText(path, paths.Exe);
        else Marshal.ThrowExceptionForHR(CreatePaneShortcut(path, paths.Exe, paths.App));
    }
    [DllImport("Pane.Native.dll", CharSet = CharSet.Unicode)]
    static extern int CreatePaneShortcut(string path, string executable, string directory);

    sealed class FileStartupRegistry(string root) : IStartupRegistry
    {
        string PathName => Path.Combine(root, "run.txt");
        public string? Read() => File.Exists(PathName) ? File.ReadAllText(PathName) : null;
        public bool Disabled => false;
        public string? ReadCompatibility(string executable,bool allUsers)=>null;
        public void Write(string command) { SetupFiles.Check(PathName); Directory.CreateDirectory(root); File.WriteAllText(PathName, command); }
        public void Delete() { SetupFiles.Check(PathName); File.Delete(PathName); }
    }
}
