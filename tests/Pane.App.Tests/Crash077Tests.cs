using Pane.Hosting;
using System.Diagnostics;
using System.Xml.Linq;

static class Crash077Tests
{
    public static async Task<int> Run()
    {
        int checks = 0;
        void Check(bool condition, string name) { checks++; if (!condition) throw new Exception("077: " + name); }
        var session = new CrashSession(1234, new DateTime(2026, 1, 2, 3, 4, 5, DateTimeKind.Utc).ToFileTimeUtc(), "Pane.App.exe", "0.1.fixture", "AOT");
        string Xml(CrashSession identity, string module = @"D:\private\extension.dll", string id = "1000", string provider = "Application Error")
        {
            XNamespace ns = "http://schemas.microsoft.com/win/2004/08/events/event";
            return new XElement(ns + "Event",
                new XElement(ns + "System", new XElement(ns + "Provider", new XAttribute("Name", provider)),
                    new XElement(ns + "EventID", id), new XElement(ns + "EventRecordID", "321"),
                    new XElement(ns + "TimeCreated", new XAttribute("SystemTime", "2026-01-02T04:04:05Z"))),
                new XElement(ns + "EventData", new[] {
                    ("ProcessId", $"0x{identity.ProcessId:x}"), ("ProcessCreationTime", $"0x{identity.CreationTime:x}"),
                    ("AppName", identity.Executable), ("ModuleName", module), ("ExceptionCode", "c0000409"), ("FaultingOffset", "00001234")
                }.Select(p => new XElement(ns + "Data", new XAttribute("Name", p.Item1), p.Item2)))).ToString();
        }
        var match = NativeCrashEvent.Match(Xml(session), session);
        Check(match != null, "精确身份匹配");
        Check(NativeCrashEvent.Match(Xml(session with { CreationTime = session.CreationTime + 1 }), session) == null, "拒绝复用 PID，保留 100ns 精度");
        Check(NativeCrashEvent.Match(Xml(session with { ProcessId = 1235 }), session) == null, "拒绝同名索引进程");
        Check(NativeCrashEvent.Match(Xml(session with { Executable = "other.exe" }), session) == null, "拒绝不同 exe");
        Check(NativeCrashEvent.Match(Xml(session), session with { Executable = "PANE.APP.EXE" }) != null, "文件名不区分大小写");
        Check(NativeCrashEvent.Match(Xml(session, id: "1001", provider: "Windows Error Reporting"), session) != null, "有完整身份的 1001");
        Check(NativeCrashEvent.Match(Xml(session, id: "1001", provider: "Other"), session) == null, "拒绝其他提供者");
        Check(NativeCrashEvent.Match(Xml(session).Replace("ProcessCreationTime", "P1"), session) == null, "缺少创建时间不推测");
        Check(NativeCrashEvent.Match("<broken", session) == null, "损坏 XML 忽略");
        string content = match!.Format(session);
        Check(content.Contains("Module: extension.dll\n") && !content.Contains("private") && !content.Contains(@"D:\"), "模块目录脱敏");
        Check(content.Contains("Type: 原生崩溃（未捕获）") && content.Contains("ExceptionCode: c0000409") && content.Contains("Offset: 00001234"), "故障字段");
        Check(content.Contains("UTC: 2026-01-02T04:04:05.0000000Z") && content.Contains("Version: 0.1.fixture") && content.Contains("Runtime: AOT") && content.Contains("EventRecordID: 321"), "来源和时间字段");
        Check(CrashSession.Parse(session.Serialize()) == session, "标记往返");
        Check(CrashSession.Parse("broken") == null, "损坏标记");
        string dir = TestResults.NewDirectory("crash077-");
        var store = new CrashSessionStore(dir);
        store.Write(session);
        Check(File.Exists(store.MarkerPath), "标记落盘");
        Check(store.TakePrevious() == session && !File.Exists(store.MarkerPath), "消费并清除旧标记");
        var log = new CrashLog(dir);
        await new NativeCrashRecovery(dir).RecoverAsync(session, (s, _) => NativeCrashEvent.Match(Xml(s), s));
        Check(log.Pending() != null && log.HasSession(session), "匹配后生成可通知记录");
        int calls = 0;
        await new NativeCrashRecovery(dir).RecoverAsync(session, (_, _) => { calls++; return match; });
        Check(calls == 0 && Directory.GetFiles(Path.Combine(dir, "logs"), "crash-*.txt").Length == 1, "已有记录不查询不重复");
        log.MarkNotified(log.Pending()!);
        Check(log.Pending() == null, "沿用一次通知");

        string emptyDir = TestResults.NewDirectory("crash077-empty-");
        var emptyStore = new CrashSessionStore(emptyDir);
        emptyStore.Write(session);
        var emptyRecovery = new NativeCrashRecovery(emptyDir);
        var current = session with { ProcessId = 4567 };
        emptyRecovery.Begin(current, (_, _) => null);
        await emptyRecovery.Completion;
        Check(new CrashLog(emptyDir).Pending() == null, "无事件不生成");
        Check(CrashSession.Parse(File.ReadAllText(emptyStore.MarkerPath)) == current, "旧标记已消费，新标记保留");
        emptyRecovery.Stop();
        Check(!File.Exists(emptyStore.MarkerPath), "正常退出清标记");
        emptyRecovery.Begin(current, (_, _) => { calls++; return match; });
        await emptyRecovery.Completion;
        Check(calls == 0, "正常启动不查事件");
        Check(!emptyRecovery.EndSession(0x16, 0) && File.Exists(emptyStore.MarkerPath), "取消关机保留标记");
        Check(!emptyRecovery.EndSession(0x11, 1) && File.Exists(emptyStore.MarkerPath), "查询关机阶段保留标记");
        Check(emptyRecovery.EndSession(0x16, 1) && !File.Exists(emptyStore.MarkerPath), "WM_ENDSESSION 清标记");

        foreach (string name in new[] { "PANE_DEMO", "PANE_VERIFY", "PANE_OBSERVE", "PANE_RESULT" })
            Check(!NativeCrashRecovery.Enabled(true, key => key == name ? "1" : null), "隔离模式 " + name);
        Check(!NativeCrashRecovery.Enabled(false, _ => null) && NativeCrashRecovery.Enabled(true, _ => null), "生产模式门控");

        string deniedDir = TestResults.NewDirectory("crash077-denied-");
        var deniedStore = new CrashSessionStore(deniedDir);
        deniedStore.Write(session);
        var deniedRecovery = new NativeCrashRecovery(deniedDir);
        deniedRecovery.Begin(current, (_, _) => throw new UnauthorizedAccessException());
        await deniedRecovery.Completion;
        Check(new CrashLog(deniedDir).Pending() == null && CrashSession.Parse(File.ReadAllText(deniedStore.MarkerPath)) == current, "无权读取不误报且消费旧标记");
        deniedRecovery.Stop();

        string timeoutDir = TestResults.NewDirectory("crash077-timeout-");
        var timeoutRecovery = new NativeCrashRecovery(timeoutDir);
        using var release = new ManualResetEventSlim();
        var finished = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var watch = Stopwatch.StartNew();
        await timeoutRecovery.RecoverAsync(session, (_, _) => { release.Wait(); finished.SetResult(); return match; });
        Check(watch.Elapsed < TimeSpan.FromSeconds(3) && !finished.Task.IsCompleted, "两秒等待上限");
        release.Set(); await finished.Task;
        Check(new CrashLog(timeoutDir).Pending() == null, "超时返回结果不能补写");
        Console.WriteLine($"PASS crash077 assertions={checks}; realEventQueries=0");
        return checks;
    }
}
