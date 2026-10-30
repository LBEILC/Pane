namespace Pane.Hosting;

internal static class StartupDirectory
{
    public static void Initialize(string[] args)
    {
        // 保留调用者传入的相对路径语义，再释放启动目录的句柄。
        foreach (string name in new[] { "PANE_DATA_DIR", "PANE_RESULT" })
            if (Environment.GetEnvironmentVariable(name) is { Length: > 0 } value)
                Environment.SetEnvironmentVariable(name, Path.GetFullPath(value));
        for (int i = 1; i < args.Length; i++)
            if (args[0] == "--launcher-index" || args[i - 1] is "--restore-winv" or "--setup-launch-probe" or "--package" or "--temp-dir" or "--self-test-root")
                args[i] = Path.GetFullPath(args[i]);
        // 启动器直接打开的程序（命令行等）会继承这里，优先用户目录，和「运行」对话框一致；
        // 受限环境里用户目录可能为空，退回 Windows 目录。两者都不受安装与卸载影响，不在其中写入文件。
        string home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        Environment.CurrentDirectory = Directory.Exists(home) ? home : Environment.GetFolderPath(Environment.SpecialFolder.Windows);
    }
}
