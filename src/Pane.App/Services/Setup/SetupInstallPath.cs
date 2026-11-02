using Pane.Localization;
using Pane.Contracts;
namespace Pane.Services.Setup;

internal static class SetupInstallPath
{
    internal const int MaxLength = 180; // 为 app 下的包内相对路径预留空间。
    static string Permission => Strings.Setup_PermissionRequired;
    static string FixedDisk => Strings.Setup_FixedDiskRequired;
    static string Occupied => Strings.Setup_DirectoryOccupied;
    static string TooLong => Strings.Setup_PathTooLong;
    public static InstallPathCheck Check(string folder, bool appendPane = true, bool allowExisting = false)
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
            foreach (var special in new[] { Environment.SpecialFolder.ProgramFiles, Environment.SpecialFolder.ProgramFilesX86,
                Environment.SpecialFolder.Windows, Environment.SpecialFolder.CommonApplicationData })
            {
                string system = Environment.GetFolderPath(special);
                if (system.Length > 0 && (target.Equals(system, StringComparison.OrdinalIgnoreCase) ||
                    target.StartsWith(system.TrimEnd('\\') + "\\", StringComparison.OrdinalIgnoreCase))) return new(target, Permission);
            }
            SetupFiles.Check(target);
            if (File.Exists(target)) return new(target, Occupied);
            if (Directory.Exists(target) && Directory.EnumerateFileSystemEntries(target).Any() &&
                !allowExisting && !File.Exists(Path.Combine(target, "app", "Pane.App.exe"))) return new(target, Occupied);
            // 不创建候选目录；在最近的已存在祖先中验证创建和写入权限。
            var ancestor = new DirectoryInfo(target);
            while (!ancestor.Exists) ancestor = ancestor.Parent ?? throw new IOException();
            string probe = Path.Combine(ancestor.FullName, ".pane-write-" + Guid.NewGuid().ToString("N"));
            using (var stream = new FileStream(probe, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1, FileOptions.DeleteOnClose))
                stream.WriteByte(0);
            return new(target, null);
        }
        catch (PathTooLongException) { return new(target, TooLong); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        { return new(target, Permission); }
    }
}
