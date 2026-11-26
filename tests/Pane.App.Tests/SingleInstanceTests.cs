using Pane.Hosting;

internal static class SingleInstanceTests
{
    internal static int Run()
    {
        int checks = 0;
        void Verify(LaunchCommand command, Exception failure, int expectedCode, bool dialog, bool logged)
        {
            int dialogs = 0;
            var errors = new List<Exception>();
            int code = SingleInstance.ContactExisting(command, _ => throw failure, () => dialogs++, errors.Add);
            if (code != expectedCode || dialogs != (dialog ? 1 : 0) ||
                !errors.SequenceEqual(logged ? [failure] : Array.Empty<Exception>()))
                throw new Exception($"握手异常处理不符合预期：{command}，{failure.GetType().Name}");
            checks++;
        }
        Verify(LaunchCommand.Show, new UnauthorizedAccessException(), 0, true, false);
        Verify(LaunchCommand.Exit, new UnauthorizedAccessException(), 1, false, false);
        Verify(LaunchCommand.Show, new InvalidOperationException(), 1, false, true);
        Console.WriteLine($"PASS single instance assertions={checks}");
        return checks;
    }
}
