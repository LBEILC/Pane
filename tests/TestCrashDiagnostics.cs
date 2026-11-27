using System.Runtime.CompilerServices;

namespace Pane.Diagnostics;

internal static class TestCrashDiagnostics
{
    [ModuleInitializer]
    internal static void Initialize() => ProcessErrorMode.ReportUnhandledToStandardError();
}
