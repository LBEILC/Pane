using System.Diagnostics;
using System.Reflection;
using System.Runtime.CompilerServices;

namespace Pane.Hosting;

internal sealed class NativeCrashRecovery(string dataDirectory)
{
    public static readonly TimeSpan QueryLimit = TimeSpan.FromSeconds(2);
    public static NativeCrashRecovery? Current { get; private set; }
    readonly CrashSessionStore store = new(dataDirectory);
    public CrashSession? Session { get; private set; }
    public Task Completion { get; private set; } = Task.CompletedTask;
    public static bool Enabled(bool production, Func<string, string?> environment) => production &&
        environment("PANE_DEMO") != "1" && environment("PANE_VERIFY") != "1" && environment("PANE_OBSERVE") != "1" &&
        string.IsNullOrWhiteSpace(environment("PANE_RESULT"));
    public static void Start()
    {
        if (!Enabled(RuntimeMode.Current.IsProduction, Environment.GetEnvironmentVariable)) return;
        using var process = Process.GetCurrentProcess();
        var session = new CrashSession(process.Id, process.StartTime.ToUniversalTime().ToFileTimeUtc(),
            Path.GetFileName(Environment.ProcessPath!),
            typeof(CrashLog).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "unknown",
            RuntimeFeature.IsDynamicCodeSupported ? "JIT" : "AOT");
        Current = new(RuntimeMode.Current.DataDirectory);
        Current.Begin(session, NativeCrashEvents.Find);
    }
    internal void Begin(CrashSession session, Func<CrashSession, CancellationToken, NativeCrashEvent?> find)
    {
        var previous = store.TakePrevious();
        Session = session;
        store.Write(session);
        // 旧标记已经被消费；后台查询永远不删除或覆盖新会话标记。
        Completion = previous == null ? Task.CompletedTask : RecoverAsync(previous, find);
    }
    internal async Task RecoverAsync(CrashSession previous, Func<CrashSession, CancellationToken, NativeCrashEvent?> find)
    {
        using var deadline = new CancellationTokenSource(QueryLimit);
        var token = deadline.Token;
        try
        {
            var log = new CrashLog(dataDirectory);
            var found = await Task.Run(() => log.HasSession(previous) ? null : find(previous, token))
                .WaitAsync(token).ConfigureAwait(false);
            if (found != null && !deadline.IsCancellationRequested) log.WriteNative(previous, found);
        }
        catch (Exception) { /* 诊断失败不阻止启动，也不据此提示崩溃。 */ }
    }
    public void Stop() => store.Clear();
    public bool EndSession(uint message, nuint ended)
    {
        if (message != 0x16 || ended == 0) return false;
        Stop();
        return true;
    }
}
