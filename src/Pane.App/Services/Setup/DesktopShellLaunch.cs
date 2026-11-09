using System.Runtime.InteropServices;
using System.Text;

namespace Pane.Services.Setup;

internal static class DesktopShellLaunch
{
    public static int Launch(string executable, string[] arguments, bool foreground)
    {
        try { return LaunchPaneFromDesktop(executable, string.Join(" ", arguments.Select(Quote)), Path.GetDirectoryName(executable)!, foreground); }
        catch (Exception error) when (error is DllNotFoundException or EntryPointNotFoundException or BadImageFormatException)
        { return error.HResult; }
    }

    // ShellExecute 接收命令行，按 Windows argv 规则编码，不经过命令解释器。
    internal static string Quote(string value)
    {
        var result = new StringBuilder("\"");
        int slashes = 0;
        foreach (char character in value)
        {
            if (character == '\\') { slashes++; continue; }
            result.Append('\\', character == '"' ? slashes * 2 + 1 : slashes);
            result.Append(character); slashes = 0;
        }
        return result.Append('\\', slashes * 2).Append('"').ToString();
    }

    [DllImport("Pane.Native.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    static extern int LaunchPaneFromDesktop(string executable, string arguments, string directory, [MarshalAs(UnmanagedType.Bool)] bool foreground);
}
