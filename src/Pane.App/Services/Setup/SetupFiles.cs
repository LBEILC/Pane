using System.Runtime.InteropServices;

namespace Pane.Services.Setup;

// 安装器内部文件边界；拒绝链接，避免递归操作越过目标目录。
internal static class SetupFiles
{
    public static string Full(string path) => Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar);
    public static void Check(string path)
    {
        if (IsLink(path)) throw new IOException($"对象本身是链接：{path}"); // 不翻译：内部异常
    }
    static bool IsLink(string path)
    {
        try { if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) == 0) return false; }
        catch (FileNotFoundException) { return false; }
        catch (DirectoryNotFoundException) { return false; }
        // 查对象自身的 tag，不打开目标或读取云文件内容；云占位符不属于名称代理。
        nint handle = FindFirstFileW(Path.TrimEndingDirectorySeparator(Path.GetFullPath(path)), out var data);
        if (handle == -1)
        {
            int error = Marshal.GetLastWin32Error();
            if (error is 2 or 3) return false;
            throw new IOException($"无法读取重解析标记：{path}", new System.ComponentModel.Win32Exception(error));
        }
        try { return (data.Attributes & FileAttributes.ReparsePoint) != 0 && IsNameSurrogate(data.ReparseTag); }
        finally { FindClose(handle); }
    }
    internal static bool IsNameSurrogate(uint tag) => (tag & 0x20000000u) != 0;

    // WIN32_FIND_DATAW 共 592 字节；只读取属性和 dwReserved0，保留其余字段空间。
    [StructLayout(LayoutKind.Explicit, Size = 592)]
    struct FindData
    {
        [FieldOffset(0)] public FileAttributes Attributes;
        [FieldOffset(36)] public uint ReparseTag;
    }
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, ExactSpelling = true, SetLastError = true)]
    static extern nint FindFirstFileW(string path, out FindData data);
    [DllImport("kernel32.dll", ExactSpelling = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    static extern bool FindClose(nint handle);
    public static void Copy(string source, string destination)
    {
        ValidateTree(source); Check(destination);
        if (Directory.Exists(destination)) ValidateTree(destination);
        CopyTree(source, destination);
    }
    static void CopyTree(string source, string destination)
    {
        Directory.CreateDirectory(destination);
        foreach (string file in Directory.GetFiles(source))
        { Check(file); File.Copy(file, Path.Combine(destination, Path.GetFileName(file))); }
        foreach (string directory in Directory.GetDirectories(source))
            CopyTree(directory, Path.Combine(destination, Path.GetFileName(directory)));
    }
    public static void Delete(string path)
    {
        Check(path);
        if (!Directory.Exists(path)) return;
        // 先验证整棵树，再删除，避免部分删除后才发现链接。
        ValidateTree(path);
        Directory.Delete(path, true);
    }
    static void ValidateTree(string path)
    {
        Check(path);
        foreach (string item in Directory.EnumerateFileSystemEntries(path))
        {
            if (IsLink(item)) throw new IOException($"树里有链接：{item}（目录：{path}）"); // 不翻译：内部异常
            if (Directory.Exists(item)) ValidateTree(item);
        }
    }
    public static long Size(string path)
    {
        if (!Directory.Exists(path)) return 0;
        long size = 0;
        foreach (string item in Directory.EnumerateFileSystemEntries(path))
        {
            if (IsLink(item)) continue;
            size += Directory.Exists(item) ? Size(item) : new FileInfo(item).Length;
        }
        return size;
    }
    public static string? VersionOf(string exe)
    {
        if (!File.Exists(exe)) return null;
        var value = System.Diagnostics.FileVersionInfo.GetVersionInfo(exe).FileVersion;
        return Version.TryParse(value, out var version) ? $"{version.Major}.{version.Minor}.{version.Build}" : null;
    }
    public static int CompareVersions(string first, string second) => Parse(first).CompareTo(Parse(second));
    internal static Version ReleaseVersion(Version version) => new(version.Major, version.Minor, Math.Max(0, version.Build));
    static Version Parse(string text)
    {
        var version = Version.Parse(text.Split('+')[0]);
        return ReleaseVersion(version);
    }
}

internal sealed record SetupPaths(string Root, string Data)
{
    public SetupPaths ResolveInstalled(string? isolatedRoot = null)
    {
        string? location = SetupRegistration.Installed(this, isolatedRoot)?.GetValueOrDefault("InstallLocation") as string;
        if (isolatedRoot != null && !string.IsNullOrEmpty(location)
            && !SetupFiles.Full(location).StartsWith(SetupFiles.Full(isolatedRoot) + "\\", StringComparison.OrdinalIgnoreCase)
            && !SetupFiles.Full(location).Equals(SetupFiles.Full(isolatedRoot), StringComparison.OrdinalIgnoreCase))
            throw new IOException("自测安装位置必须在隔离根目录内"); // 不翻译：诊断异常
        return string.IsNullOrEmpty(location) ? this : this with { Root = SetupFiles.Full(location) };
    }
    public string App => Path.Combine(Root, "app");
    public string Uninstaller => Path.Combine(Root, "Uninstall Pane.exe");
    public string Exe => Path.Combine(App, "Pane.App.exe");
    public static SetupPaths Production => new(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", "Pane"), Pane.Settings.SettingsStore.ProductionDirectory);
    public static SetupPaths Isolated(string root)
    {
        root = SetupFiles.Full(root);
        string temp = SetupFiles.Full(Path.GetTempPath()) + Path.DirectorySeparatorChar;
        if (!root.StartsWith(temp, StringComparison.OrdinalIgnoreCase) || !Path.GetFileName(root).StartsWith("Pane-018-", StringComparison.Ordinal))
            throw new IOException("自测根目录必须是临时目录下的 Pane-018-* 独立目录"); // 不翻译：诊断异常
        SetupFiles.Check(root);
        return new(root, Path.Combine(root, "data"));
    }
}

internal static class SetupRegistration
{
    // 发布脚本也从本定义读取固定的产品标识。
    internal const string ProductCode = "{03C4C620-F0FD-437A-BED3-DFE6C3DBBDB3}";
    internal const string Legacy = "Pane";
    internal static string Key(string name) => @"Software\Microsoft\Windows\CurrentVersion\Uninstall\" + name;
    internal static string FileName(string root, string name) =>
        Path.Combine(root, name == Legacy ? "uninstall-legacy.json" : "uninstall.json");

    internal static Dictionary<string, object>? Read(string name, string? root)
    {
        if (root == null)
        {
            using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(Key(name));
            return key?.GetValueNames().ToDictionary(n => n, n => key.GetValue(n)!, StringComparer.OrdinalIgnoreCase);
        }
        string file = FileName(root, name);
        if (!File.Exists(file)) return null;
        using var json = System.Text.Json.JsonDocument.Parse(File.ReadAllText(file));
        return json.RootElement.EnumerateObject().ToDictionary(p => p.Name, p =>
            p.Value.ValueKind == System.Text.Json.JsonValueKind.Number ? (object)p.Value.GetInt32() : p.Value.GetString()!,
            StringComparer.OrdinalIgnoreCase);
    }

    static bool SamePath(string? path, string target)
    {
        if (string.IsNullOrWhiteSpace(path) || !Path.IsPathFullyQualified(path)) return false;
        try { return SetupFiles.Full(path).Equals(SetupFiles.Full(target), StringComparison.OrdinalIgnoreCase); }
        catch (Exception e) when (e is ArgumentException or NotSupportedException or IOException) { return false; }
    }

    static bool CommandMatches(Dictionary<string, object> fields, SetupPaths target)
    {
        if (fields.GetValueOrDefault("UninstallString") is not string command) return false;
        command = command.Trim();
        string executable;
        if (command.StartsWith('"'))
        {
            int end = command.IndexOf('"', 1);
            if (end < 1 || (end + 1 < command.Length && !char.IsWhiteSpace(command[end + 1]))) return false;
            executable = command[1..end];
        }
        else executable = command.Split([' ', '\t'], 2)[0];
        return SamePath(executable, target.Uninstaller) || SamePath(executable, target.Exe);
    }

    static bool IsCustomPaneLocation(Dictionary<string, object> fields, SetupPaths candidate) =>
        SamePath(fields.GetValueOrDefault("DisplayIcon") as string, candidate.Exe) && CommandMatches(fields, candidate);

    internal static bool Matches(Dictionary<string, object> fields, SetupPaths target) =>
        SamePath(fields.GetValueOrDefault("InstallLocation") as string, target.Root) || CommandMatches(fields, target);

    internal static Dictionary<string, object>? Installed(SetupPaths target, string? root)
    {
        var current = Read(ProductCode, root);
        if (current != null) return current;
        var old = Read(Legacy, root);
        if (old == null) return null;
        // 自选位置需由 Pane 图标路径和卸载命令共同佐证，避免采用同名软件的安装目录。
        if (old.GetValueOrDefault("InstallLocation") is string location && Path.IsPathFullyQualified(location)
            && (SamePath(location, target.Root) || IsCustomPaneLocation(old, target with { Root = location }))) return old;
        if (!CommandMatches(old, target)) return null;
        old["InstallLocation"] = target.Root;
        return old;
    }
}
