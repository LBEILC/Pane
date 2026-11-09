using System.Diagnostics;
using System.Runtime.CompilerServices;
using Pane.Contracts;
using Pane.Services.Words;

int checks = 0;
void Check(bool ok, string message) { checks++; if (!ok) throw new Exception(message); }
void Invariants(WordSplit split)
{
    int at = 0;
    foreach (var piece in split.Pieces)
    {
        Check(piece.Start >= at && piece.Length > 0, "顺序与重叠");
        Check(split.Source[at..piece.Start].All(char.IsWhiteSpace), "空隙只能是空白");
        Check(piece.Text == split.Source.Substring(piece.Start, piece.Length), "原文范围");
        at = piece.Start + piece.Length;
    }
    Check(split.Source[at..].All(char.IsWhiteSpace), "末尾只能剩空白");
}
string[] samples = [
    "明天下午三点在会议室开产品评审会，记得带上笔记本电脑。",
    "收货地址：上海市浦东新区张江路 123 号 4 栋 501 室，电话 138 0013 8000，邮编 201203。",
    "文档在 https://example.com/docs/getting-started?lang=zh#install ，有问题发邮件到 support@example.com。",
    "2026 年 11 月 9 日 14:30 到账 ¥1,280.50，订单号 A20261109-0042，折扣 85%。",
    "用 WinUI 3 和 C# 写的 Pane 支持 NativeAOT，安装包只有 37 MB。",
    "`var segments = segmenter.GetTokens(text);` 返回 IReadOnlyList<WordSegment>。",
    "請把檔案存到桌面，然後重新啟動應用程式。",
    "好的👌 收到！明天见～（周三）",
    "张伟推荐的《三体》第二部叫《黑暗森林》，作者是刘慈欣。",
    "Pane is a lightweight launcher, clipboard manager and gesture tool for Windows 11."
];
var service = new TextSegmenter();
foreach (var sample in samples) { var split = await service.SplitAsync(sample, default); Invariants(split); Check(split.Source == sample && !split.Truncated, "十条样本原文"); }
foreach (var (text, kind) in new (string, WordKind)[] {
    ("https://example.com/a?x=1#b",WordKind.Link),("support@example.com",WordKind.Email),
    ("138 0013 8000",WordKind.Phone),("138-0013-8000",WordKind.Phone),("13800138000",WordKind.Phone),
    ("021-12345678",WordKind.Phone),("400-123-4567",WordKind.Phone),
    ("2026-11-09",WordKind.Number),("2026 年 11 月 9 日",WordKind.Number),("14:30",WordKind.Number),("9:00-18:00",WordKind.Number),
    ("¥1,280.50",WordKind.Number),("A20261109-0042",WordKind.Number),("85%",WordKind.Number),("3.14",WordKind.Number),("12345",WordKind.Number),
    ("123",WordKind.Word),("4",WordKind.Word),("501",WordKind.Word),("1234",WordKind.Word),("C#",WordKind.Word),("C++",WordKind.Word) })
{
    var split = await service.SplitAsync(text, default); Invariants(split);
    Check(split.Pieces.Count == 1 && split.Pieces[0].Text == text && split.Pieces[0].Kind == kind, "整块 " + text);
}
var merged = await service.SplitAsync("上海市浦东新区张江路", default);
Check(string.Join("|", merged.Pieces.Select(p => p.Text)) == "上海市|浦东新区|张江路", "系统长词合并");
Console.WriteLine("merge=" + string.Join("|", merged.Pieces.Select(p => p.Text)));
var punctuation = await service.SplitAsync("》， \t\r\n！", default);
Check(punctuation.Pieces.Select(p => p.Text).SequenceEqual(["》", "，", "！"]) && punctuation.Pieces.All(p => p.Kind == WordKind.Punctuation), "逐标点与空白");
var link = await service.SplitAsync("https://example.com/a)，。", default);
Check(link.Pieces.Select(p => p.Text).SequenceEqual(["https://example.com/a", ")", "，", "。"]), "链接尾标点");
foreach (var text in new[] { "", " \t\r\n", "》，！", "👌😀👩‍💻", new string('中', 21000), new string('a', 19999) + "😀尾" })
{
    var split = await service.SplitAsync(text, default); Invariants(split);
    Check(split.Truncated == (text.Length > 20000) && split.Source.Length <= 20000, "截断标志");
    Check(split.Source.Length == 0 || !char.IsHighSurrogate(split.Source[^1]), "代理对截断");
}
using (var canceled = new CancellationTokenSource())
{
    canceled.Cancel();
    try { await service.SplitAsync(samples[0], canceled.Token); throw new Exception("应取消"); }
    catch (OperationCanceledException) { checks++; }
}
int attempts = 0;
var fallback = new TextSegmenter(() => { attempts++; throw new InvalidOperationException("模拟激活失败"); });
foreach (string text in samples.Concat(["😀 汉字 abc123 2026-11-09", " \n"])) Invariants(await fallback.SplitAsync(text, default));
Check(attempts == 1, "失败激活只试一次");
foreach (var result in new[] { new SelectionResult("测试", 42, SelectionFailure.None), new SelectionResult("", 42, SelectionFailure.Empty), new SelectionResult(null, 42, SelectionFailure.None) { Files = ["D:\\fixture.txt"] }, new SelectionResult(null, 42, SelectionFailure.Timeout), new SelectionResult(null, 42, SelectionFailure.Elevated) })
{
    int shown = 0, empty = 0;
    var reader = new FakeReader(result);
    await WordsSelection.ReadAsync(reader, h => h == 42 ? "测试程序" : null, (text, source, clipboard) => { Check(reader.Completed && text == "测试" && source == "测试程序" && !clipboard, "先读取后显示参数"); shown++; }, () => empty++, default);
    Check(shown == (result.Text == "测试" ? 1 : 0) && shown + empty == 1, "选区分支");
}
string combined = string.Join('\n', samples);
string input = string.Concat(Enumerable.Repeat(combined, 20000 / combined.Length + 1))[..20000];
await service.SplitAsync(input, default);
List<double> times = [];
for (int i = 0; i < 21; i++) { var watch = Stopwatch.StartNew(); var split = await service.SplitAsync(input, default); times.Add(watch.Elapsed.TotalMilliseconds); if (i == 0) Invariants(split); }
Console.WriteLine($"PASS checks={checks};mode={(RuntimeFeature.IsDynamicCodeSupported ? "JIT" : "AOT")};length={input.Length};medianMs={times.Order().ElementAt(10):F3};samples=21;fallbackActivations={attempts}");

sealed class FakeReader(SelectionResult result) : ISelectionReader
{
    internal bool Completed;
    public async Task<SelectionResult> ReadAsync(CancellationToken token) { await Task.Yield(); Completed = true; return result; }
}
