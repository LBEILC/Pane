using System;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace Pane.Diagnostics;

internal static class ProcessErrorMode
{
    [DllImport("kernel32.dll")]
    private static extern uint GetErrorMode();

    [DllImport("kernel32.dll")]
    private static extern uint SetErrorMode(uint mode);

    [DllImport("kernel32.dll")]
    private static extern int WerSetFlags(uint flags);

    [ModuleInitializer]
    internal static void Initialize()
    {
        const uint failCriticalErrors = 1, noGpFaultErrorBox = 2;
        const uint noWerUi = 32;
        SetErrorMode(GetErrorMode() | failCriticalErrors | noGpFaultErrorBox);
        WerSetFlags(noWerUi);
    }

    internal static void ReportUnhandledToStandardError()
    {
        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
        {
            Console.Error.WriteLine(args.ExceptionObject);
            Console.Error.Flush();
            Environment.ExitCode = 1;
        };
    }
}
