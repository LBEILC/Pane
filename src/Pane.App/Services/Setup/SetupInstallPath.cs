using Pane.Localization;
using Pane.Contracts;
using Microsoft.Win32.SafeHandles;
using System.Runtime.InteropServices;
using System.Text;
namespace Pane.Services.Setup;

internal static class SetupInstallPath
{
    internal const int MaxLength = 180; // 为 app 下的包内相对路径预留空间。
    static string Permission => Strings.Setup_PermissionRequired;
    static string FixedDisk => Strings.Setup_FixedDiskRequired;
    static string Occupied => Strings.Setup_DirectoryOccupied;
    static string TooLong => Strings.Setup_PathTooLong;
    public static InstallPathCheck Check(string folder, bool appendPane = true, bool allowExisting = false, IReadOnlyList<string>? systemDirectories = null)
    {
        string target = folder;
        try
        {
            if (folder.StartsWith(@"\\") || folder.StartsWith("//")) return new(target, FixedDisk);
            if (!Path.IsPathFullyQualified(folder)) return new(target, Permission);
            target = SetupFiles.Full(folder);
            if (appendPane && !Path.GetFileName(target).Equals("Pane", StringComparison.OrdinalIgnoreCase))
                target = Path.Combine(target, "Pane");
            if (target.Length > MaxLength) return new(target, TooLong);
            if (new DriveInfo(Path.GetPathRoot(target)!).DriveType != DriveType.Fixed) return new(target, FixedDisk);
            systemDirectories ??= new[] { Environment.SpecialFolder.ProgramFiles, Environment.SpecialFolder.ProgramFilesX86,
                Environment.SpecialFolder.Windows, Environment.SpecialFolder.CommonApplicationData }.Select(Environment.GetFolderPath).Where(p => p.Length > 0).ToArray();
            bool Protected(string path) => systemDirectories.Any(system => Within(path, system));
            if (Protected(target)) return new(target, Permission);
            SetupFiles.Check(target);
            if (File.Exists(target)) return new(target, Occupied);
            if (Directory.Exists(target) && Directory.EnumerateFileSystemEntries(target).Any() &&
                !allowExisting && !File.Exists(Path.Combine(target, "app", "Pane.App.exe"))) return new(target, Occupied);
            // 不创建候选目录；在最近的已存在祖先中验证创建和写入权限。
            var ancestor = new DirectoryInfo(target);
            while (!ancestor.Exists) ancestor = ancestor.Parent ?? throw new IOException();
            // 在任何写入探测之前解析真实祖先，并接回尚未创建的路径部分。
            string resolved = Path.GetFullPath(Path.Combine(FinalPath(ancestor.FullName), Path.GetRelativePath(ancestor.FullName, target)));
            if (resolved.StartsWith(@"\\") || new DriveInfo(Path.GetPathRoot(resolved)!).DriveType != DriveType.Fixed)
                return new(target, FixedDisk);
            if (Protected(resolved) || systemDirectories.Where(Directory.Exists).Any(system => Within(resolved, FinalPath(system))))
                return new(target, Permission);
            string probe = Path.Combine(ancestor.FullName, ".pane-write-" + Guid.NewGuid().ToString("N"));
            using (var stream = new FileStream(probe, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1, FileOptions.DeleteOnClose))
                stream.WriteByte(0);
            return new(target, null);
        }
        catch (PathTooLongException) { return new(target, TooLong); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        { return new(target, Permission); }
    }
    static bool Within(string path, string root) => path.Equals(root.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase)
        || path.StartsWith(root.TrimEnd('\\') + "\\", StringComparison.OrdinalIgnoreCase);
    static string FinalPath(string path)
    {
        using var handle = CreateFileW(path, 0, FileShare.ReadWrite | FileShare.Delete, 0, FileMode.Open, 0x02000000, 0);
        if (handle.IsInvalid) throw new IOException($"无法解析真实路径：{path}", new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error()));
        var buffer = new StringBuilder(512);
        uint length = GetFinalPathNameByHandleW(handle, buffer, (uint)buffer.Capacity, 0);
        if (length >= buffer.Capacity)
        { buffer.EnsureCapacity(checked((int)length + 1)); length = GetFinalPathNameByHandleW(handle, buffer, (uint)buffer.Capacity, 0); }
        if (length == 0 || length >= buffer.Capacity) throw new IOException($"无法解析真实路径：{path}", new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error()));
        string result = buffer.ToString();
        return result.StartsWith(@"\\?\UNC\", StringComparison.OrdinalIgnoreCase) ? @"\\" + result[8..]
            : result.StartsWith(@"\\?\") ? result[4..] : result;
    }
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, ExactSpelling = true, SetLastError = true)]
    static extern SafeFileHandle CreateFileW(string path, uint access, FileShare share, nint security, FileMode mode, uint flags, nint template);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, ExactSpelling = true, SetLastError = true)]
    static extern uint GetFinalPathNameByHandleW(SafeFileHandle handle, StringBuilder path, uint length, uint flags);
}
