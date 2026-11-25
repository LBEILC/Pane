namespace Pane.Hosting;

internal static class Probe119
{
    static readonly object Gate = new();
    internal static void Log(string value)
    {
        lock (Gate)
            File.AppendAllText(Environment.GetEnvironmentVariable("PANE_RESULT") + ".trace", value + "\n");
    }

    internal static bool Foreground(nint input)
    {
        var before = Native.GetForegroundWindow();
        var result = Native.SetForegroundWindow(input);
        Log($"activate {input} {before} {result} {Native.GetForegroundWindow()}");
        return result;
    }

    internal static void Shown(nint input, nint visual, bool passive, bool shown) =>
        Log($"show {input} {visual} {passive} {shown}");
}
