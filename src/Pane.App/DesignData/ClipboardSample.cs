// 界面开发用的剪贴板假数据，照原型 prototype/data.js 的 clips。不读写真实剪贴板。
using Pane.Contracts;

namespace Pane.DesignData;

public sealed class ClipboardSample : IClipboardHistory
{
    static readonly Dictionary<string, LauncherIcon> Sources = new()
    {
        ["Edge"] = new("globe", null, "#4fd1c5", "#1673c9"),
        ["微信"] = new("chat", null, "#3ddc84", "#13a85a"),
        ["终端"] = new("terminal", null, "#4a4f5c", "#1e2128"),
        ["截图工具"] = new("scissors", null, "#b993ff", "#7b4fe0"),
        ["文件资源管理器"] = new("folder", null, "#ffd36b", "#f0a020"),
        ["Photoshop"] = new("image", null, "#3ec1ff", "#0b3a66"),
        ["记事本"] = new("note", null, "#7cc4ff", "#3d8bdb"),
        ["Visual Studio Code"] = new("code", null, "#3fa9f5", "#1769c4"),
    };

    readonly List<ClipEntry> entries;

    public ClipboardSample()
    {
        var now = DateTimeOffset.Now;
        int id = 0;
        ClipEntry T(ClipKind kind, string text, string source, int ago, bool pinned = false) =>
            new((id++).ToString(), kind, text, null, null, null, null, source, Sources.GetValueOrDefault(source), now.AddMinutes(-ago), pinned);
        ClipEntry I(string text, int w, int h, string from, string to, string source, int ago) =>
            new((id++).ToString(), ClipKind.Image, text, null, new ClipPicture(null, from, to), w, h, source, Sources.GetValueOrDefault(source), now.AddMinutes(-ago), false);

        entries =
        [
            T(ClipKind.Text, "pane@example.com", "Edge", 9000, pinned: true) with { Tags = ["常用"] },
            T(ClipKind.Text, "上海市徐汇区漕溪北路 88 号 6 楼 前台转设计组", "微信", 20000, pinned: true) with { Tags = ["常用", "地址"] },
            T(ClipKind.Code, "dotnet publish src/Pane.App -c Release -r win-x64", "终端", 3000, pinned: true),
            T(ClipKind.Text, "您好，文件已收到，我们核对后今天下班前回复您。", "微信", 12000, pinned: true) with { Tags = ["回复模板"] },
            T(ClipKind.Color, "#5874C9", "Photoshop", 26000, pinned: true),
            T(ClipKind.Text, "发票抬头：示例科技有限公司　税号 91310000XXXXXXXX0X", "记事本", 40000, pinned: true) with { Tags = ["常用", "工作", "发票"] },
            T(ClipKind.Link, "https://github.com/example/pane/issues", "Edge", 50000, pinned: true),
            T(ClipKind.Code, "git commit -m \"[设计] 网页风格原型\" -- prototype/index.html", "终端", 2),
            I("截图", 1280, 720, "#f8b195", "#6c5b7b", "截图工具", 6),
            T(ClipKind.Link, "https://learn.microsoft.com/windows/apps/winui/winui3/", "Edge", 14),
            T(ClipKind.Text, "| 项目 | 预算 | 负责人 |\n| --- | --- | --- |\n| 官网改版 | 12 万 | 设计组 |", "Edge", 22) with { Rich = true, Tags = ["工作"] },
            T(ClipKind.Text, "会议改到周四下午三点，地点不变，记得带上打印好的方案和预算表。", "微信", 31) with { Tags = ["工作"] },
            new((id++).ToString(), ClipKind.Files, null, [@"D:\设计\设计稿-v3.fig", @"D:\设计\图标导出.zip", @"D:\设计\说明.txt"], null, null, null,
                "文件资源管理器", Sources["文件资源管理器"], now.AddMinutes(-48), false),
            T(ClipKind.Color, "#3B82F6", "Photoshop", 65),
            T(ClipKind.Text, "磨砂玻璃的层次自下而上是：背后内容模糊、亮度层、着色层、噪点，最后是一圈细微的高光边框。", "记事本", 120) with { Rich = true },
            I("照片", 4032, 3024, "#a1c4fd", "#1b4332", "微信", 190),
            T(ClipKind.Code, "SetWindowCompositionAttribute(hwnd, ref data);", "Visual Studio Code", 260),
            T(ClipKind.Text, "快递单号 SF1402 8873 6650", "微信", 1600),
            T(ClipKind.Link, "https://www.example.com/blog/acrylic-material-deep-dive", "Edge", 2900),
            T(ClipKind.Text, "WiFi：Pane-Studio-5G", "记事本", 4300),
            I("截图", 1280, 720, "#f8b195", "#6c5b7b", "截图工具", 12000) with { Original = ClipOriginal.Expired, SourceDevice = "笔记本" },
            I("照片", 4032, 3024, "#a1c4fd", "#1b4332", "微信", 4800) with { Original = ClipOriginal.NotSynced, SourceDevice = "笔记本" },
        ];
        // 截图核对用（Pane 云）：设了 PANE_DESIGN_SYNC 时，最上面多几条另一台电脑「笔记本」刚同步来的，含正在下载原图的图片
        if (Environment.GetEnvironmentVariable("PANE_DESIGN_SYNC") is { Length: > 0 } and not "off")
            entries.InsertRange(7,
            [
                T(ClipKind.Text, "周五的评审改到 10 点，会议室 B", "微信", 1) with { SourceDevice = "笔记本" },
                I("截图", 1920, 1080, "#43cea2", "#185a9d", "截图工具", 3) with { SourceDevice = "笔记本", Original = ClipOriginal.Downloading },
                I("照片", 4000, 3000, "#f6d365", "#fda085", "微信", 5) with { SourceDevice = "笔记本", Original = ClipOriginal.NotSynced },
            ]);
        // 截图核对用：PANE_DESIGN_CLIP_PINS=N 只保留前 N 条固定（看没有固定、固定很少时的已固定区；默认 7 条）
        if (int.TryParse(Environment.GetEnvironmentVariable("PANE_DESIGN_CLIP_PINS"), out var keepPins))
            for (var i = 0; i < entries.Count; i++)
                if (entries[i].Pinned && keepPins-- <= 0) entries[i] = entries[i] with { Pinned = false };
        // 截图核对用：PANE_DESIGN_CLIP_IMAGES=「缩略图路径|原图宽|原图高;…」时，再插几条用真实位图的图片条目（固定在最前，看各种比例的排版）
        var real = Environment.GetEnvironmentVariable("PANE_DESIGN_CLIP_IMAGES");
        if (!string.IsNullOrEmpty(real))
        {
            int ago = 0;
            foreach (var item in real.Split(';', StringSplitOptions.RemoveEmptyEntries))
            {
                var p = item.Split('|');
                entries.Add(new((id++).ToString(), ClipKind.Image, null, null, new ClipPicture(p[0]), int.Parse(p[1]), int.Parse(p[2]),
                    "截图工具", Sources["截图工具"], now.AddMinutes(-ago++), true));   // 固定，排在最前面
            }
        }
    }

    public event EventHandler? Changed;

    public IReadOnlyList<ClipEntry> Snapshot() =>
        entries.OrderByDescending(e => e.Pinned).ThenByDescending(e => e.CopiedAt).ToList();

    public Task PasteAsync(string id, bool plainText, nint targetWindow, CancellationToken cancellationToken)
    {
        // 假数据：粘贴后挪到最前（已固定的保持原位），不碰真实剪贴板
        var i = entries.FindIndex(e => e.Id == id);
        if (i >= 0 && !entries[i].Pinned) { entries[i] = entries[i] with { CopiedAt = DateTimeOffset.Now }; Changed?.Invoke(this, EventArgs.Empty); }
        return Task.CompletedTask;
    }

    public void SetPinned(string id, bool pinned)
    {
        var i = entries.FindIndex(e => e.Id == id);
        if (i < 0) return;
        entries[i] = entries[i] with { Pinned = pinned };
        Changed?.Invoke(this, EventArgs.Empty);
    }

    public void Delete(string id)
    {
        if (entries.RemoveAll(e => e.Id == id) > 0) Changed?.Invoke(this, EventArgs.Empty);
    }

    public void ClearUnpinned()
    {
        if (entries.RemoveAll(e => !e.Pinned && e.Tags.Count == 0) > 0) Changed?.Invoke(this, EventArgs.Empty);
    }

    // ---- 2026-11-23（106）：标签、编辑、粘贴方式（假数据：只改内存，不碰真实剪贴板） ----
    readonly List<ClipTag> tags = [new("常用", 1), new("工作", 4), new("回复模板", 2), new("地址", null), new("发票", 5)];

    public IReadOnlyList<ClipTag> Tags() => tags.ToList();

    public void SetTags(string id, IReadOnlyList<string> names)
    {
        var i = entries.FindIndex(e => e.Id == id);
        if (i < 0) return;
        var list = new List<string>();
        foreach (var raw in names)
        {
            var name = raw.Trim();
            if (name.Length == 0) continue;
            var def = tags.Find(t => string.Equals(t.Name, name, StringComparison.OrdinalIgnoreCase));
            if (def is null) { def = new ClipTag(name, null); tags.Add(def); }
            if (!list.Contains(def.Name, StringComparer.OrdinalIgnoreCase)) list.Add(def.Name);
        }
        entries[i] = entries[i] with { Tags = list };
        Changed?.Invoke(this, EventArgs.Empty);
    }

    public void UpdateTag(string name, string newName, int? color)
    {
        var i = tags.FindIndex(t => string.Equals(t.Name, name, StringComparison.OrdinalIgnoreCase));
        if (i < 0) return;
        newName = newName.Trim();
        if (newName.Length == 0) newName = tags[i].Name;
        if (tags.Any(t => t != tags[i] && string.Equals(t.Name, newName, StringComparison.OrdinalIgnoreCase))) throw new ArgumentException(newName);
        var old = tags[i].Name;
        tags[i] = new ClipTag(newName, color);
        for (var k = 0; k < entries.Count; k++)
            if (entries[k].Tags.Contains(old)) entries[k] = entries[k] with { Tags = entries[k].Tags.Select(t => t == old ? newName : t).ToList() };
        Changed?.Invoke(this, EventArgs.Empty);
    }

    public void DeleteTag(string name)
    {
        tags.RemoveAll(t => string.Equals(t.Name, name, StringComparison.OrdinalIgnoreCase));
        for (var k = 0; k < entries.Count; k++)
            if (entries[k].Tags.Contains(name)) entries[k] = entries[k] with { Tags = entries[k].Tags.Where(t => t != name).ToList() };
        Changed?.Invoke(this, EventArgs.Empty);
    }

    public void UpdateText(string id, string text)
    {
        var i = entries.FindIndex(e => e.Id == id);
        if (i < 0 || string.IsNullOrEmpty(text)) return;
        entries[i] = entries[i] with { Text = text, Rich = false };
        Changed?.Invoke(this, EventArgs.Empty);
    }

    public Task PasteAsAsync(string id, ClipPasteAs kind, nint targetWindow, CancellationToken cancellationToken) => PasteAsync(id, false, targetWindow, cancellationToken);
}
