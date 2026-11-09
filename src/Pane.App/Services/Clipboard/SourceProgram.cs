using System.Diagnostics;
using System.Runtime.InteropServices;

namespace Pane.Services;

internal static class SourceProgram
{
    internal static (string Path, string Display) Read(nint window)
    {
        string path = "", display = "";
        GetWindowThreadProcessId(window, out uint pid);
        if (pid != 0) try
        {
            using var process = Process.GetProcessById((int)pid);
            path = process.MainModule?.FileName ?? "";
            display = FileVersionInfo.GetVersionInfo(path).FileDescription ?? "";
        }
        catch (Exception e) when (e is System.ComponentModel.Win32Exception or InvalidOperationException or ArgumentException) { }
        return (path, display);
    }
    internal static string? Name(nint window)
    {
        var (path, display) = Read(window);
        string name = string.IsNullOrWhiteSpace(display) ? System.IO.Path.GetFileName(path) : display;
        return string.IsNullOrWhiteSpace(name) ? null : name;
    }
    [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(nint window, out uint pid);
}
