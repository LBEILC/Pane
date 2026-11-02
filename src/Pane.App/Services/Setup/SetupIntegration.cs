using Microsoft.Win32;
using Pane.Contracts;
using Pane.Settings;
using System.Runtime.InteropServices;
using System.Text.Json;
namespace Pane.Services.Setup;

internal sealed class SetupIntegration(SetupPaths paths, PaneSettings defaults, string? isolatedRoot = null) : ISetupIntegration
{
    const string UninstallKey = @"Software\Microsoft\Windows\CurrentVersion\Uninstall\Pane";
    readonly IStartupRegistry startup = isolatedRoot == null ? new WindowsStartupRegistry() : new FileStartupRegistry(isolatedRoot);
    string Menu => Path.Combine(isolatedRoot == null ? Environment.GetFolderPath(Environment.SpecialFolder.Programs) : Path.Combine(isolatedRoot, "menu"), "Pane.lnk");
    string Desktop => Path.Combine(isolatedRoot == null ? Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory) : Path.Combine(isolatedRoot, "desktop"), "Pane.lnk");
    string SettingsFile => Path.Combine(paths.Data, "settings.json");
    string FakeRegistration => Path.Combine(isolatedRoot!, "uninstall.json");
    public void SetPaths(SetupPaths value) => paths = value;
    public string? InstallLocation => ReadRegistration()?.GetValueOrDefault("InstallLocation") as string;
    public string? InstalledVersion => ReadRegistration()?.GetValueOrDefault("DisplayVersion") as string;
    public bool StartWithWindows => AutostartService.PointsTo(startup.Read(), paths.Exe) && !startup.Disabled;
    public static Dictionary<string, object> Registration(SetupPaths paths, string version, long bytes) => new()
    {
        // TODO(design): 发布者暂用产品名 Pane，待确定发布主体后替换。
        ["DisplayName"] = "Pane", ["DisplayVersion"] = version, ["Publisher"] = "Pane",
        ["DisplayIcon"] = paths.Exe, ["InstallLocation"] = paths.Root,
        ["EstimatedSize"] = (int)Math.Min(int.MaxValue, (bytes + 1023) / 1024),
        ["UninstallString"] = $"\"{paths.Uninstaller}\"", ["NoModify"] = 1, ["NoRepair"] = 1
    };
    sealed record Snapshot(string? Startup, Dictionary<string, object>? Registration, Dictionary<string, byte[]?> Files);
    public object Capture() => new Snapshot(startup.Read(), ReadRegistration(), new[] { Menu, Desktop, SettingsFile, SettingsFile + ".tmp" }.ToDictionary(p => p, ReadBytes));
    static byte[]? ReadBytes(string path) { SetupFiles.Check(path); return File.Exists(path) ? File.ReadAllBytes(path) : null; }
    public void Restore(object snapshot)
    {
        var value = (Snapshot)snapshot;
        if (value.Startup == null) startup.Delete(); else startup.Write(value.Startup);
        WriteRegistration(value.Registration);
        foreach (var item in value.Files)
        {
            SetupFiles.Check(item.Key);
            if (item.Value == null) File.Delete(item.Key);
            else { Directory.CreateDirectory(Path.GetDirectoryName(item.Key)!); File.WriteAllBytes(item.Key, item.Value); }
        }
    }
    public void Apply(string version, long bytes, SetupOptions options)
    {
        // 不调用 Load：读取设置时不得重命名损坏文件或创建用户数据目录。
        PaneSettings settings = ReadSettings(paths.Data, defaults);
        var store = new SettingsStore(paths.Data, settings);
        new AutostartService(true, paths.Exe, store, startup).Set(options.StartWithWindows);
        WriteRegistration(Registration(paths, version, bytes));
        Shortcut(Menu);
        if (options.DesktopShortcut) Shortcut(Desktop); else File.Delete(Desktop);
    }
    public void Remove()
    {
        if (AutostartService.PointsTo(startup.Read(), paths.Exe)) startup.Delete();
        File.Delete(Menu); File.Delete(Desktop); WriteRegistration(null);
    }
    public static PaneSettings ReadSettings(string data, PaneSettings fallback)
    {
        try { return JsonSerializer.Deserialize(File.ReadAllText(Path.Combine(data, "settings.json")), SettingsJson.Default.PaneSettings) ?? fallback; }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException or NotSupportedException) { return fallback; }
    }
    Dictionary<string, object>? ReadRegistration()
    {
        if (isolatedRoot != null)
        {
            if (!File.Exists(FakeRegistration)) return null;
            using var json = JsonDocument.Parse(File.ReadAllText(FakeRegistration));
            return json.RootElement.EnumerateObject().ToDictionary(p => p.Name, p => p.Value.ValueKind == JsonValueKind.Number ? (object)p.Value.GetInt32() : p.Value.GetString()!);
        }
        using var key = Registry.CurrentUser.OpenSubKey(UninstallKey);
        return key?.GetValueNames().ToDictionary(n => n, n => key.GetValue(n)!);
    }
    void WriteRegistration(Dictionary<string, object>? fields)
    {
        if (isolatedRoot != null)
        {
            if (fields == null) { File.Delete(FakeRegistration); return; }
            Directory.CreateDirectory(isolatedRoot);
            using var stream = File.Create(FakeRegistration);
            using var writer = new Utf8JsonWriter(stream);
            writer.WriteStartObject();
            foreach (var field in fields) { if (field.Value is int number) writer.WriteNumber(field.Key, number); else writer.WriteString(field.Key, (string)field.Value); }
            writer.WriteEndObject(); return;
        }
        Registry.CurrentUser.DeleteSubKeyTree(UninstallKey, false);
        if (fields == null) return;
        using var key = Registry.CurrentUser.CreateSubKey(UninstallKey);
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
        public void Write(string command) { Directory.CreateDirectory(root); File.WriteAllText(PathName, command); }
        public void Delete() => File.Delete(PathName);
    }
}
