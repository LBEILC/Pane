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
            T(ClipKind.Text, "pane@example.com", "Edge", 9000, pinned: true),
            T(ClipKind.Text, "上海市徐汇区漕溪北路 88 号 6 楼 前台转设计组", "微信", 20000, pinned: true),
            T(ClipKind.Code, "git commit -m \"[设计] 网页风格原型\" -- prototype/index.html", "终端", 2),
            I("截图", 1280, 720, "#f8b195", "#6c5b7b", "截图工具", 6),
            T(ClipKind.Link, "https://learn.microsoft.com/windows/apps/winui/winui3/", "Edge", 14),
            T(ClipKind.Text, "会议改到周四下午三点，地点不变，记得带上打印好的方案和预算表。", "微信", 31),
            new((id++).ToString(), ClipKind.Files, null, [@"D:\设计\设计稿-v3.fig", @"D:\设计\图标导出.zip", @"D:\设计\说明.txt"], null, null, null,
                "文件资源管理器", Sources["文件资源管理器"], now.AddMinutes(-48), false),
            T(ClipKind.Color, "#3B82F6", "Photoshop", 65),
            T(ClipKind.Text, "磨砂玻璃的层次自下而上是：背后内容模糊、亮度层、着色层、噪点，最后是一圈细微的高光边框。", "记事本", 120),
            I("照片", 4032, 3024, "#a1c4fd", "#1b4332", "微信", 190),
            T(ClipKind.Code, "SetWindowCompositionAttribute(hwnd, ref data);", "Visual Studio Code", 260),
            T(ClipKind.Text, "快递单号 SF1402 8873 6650", "微信", 1600),
            T(ClipKind.Link, "https://www.example.com/blog/acrylic-material-deep-dive", "Edge", 2900),
            T(ClipKind.Text, "WiFi：Pane-Studio-5G", "记事本", 4300),
        ];
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
        if (entries.RemoveAll(e => !e.Pinned) > 0) Changed?.Invoke(this, EventArgs.Empty);
    }
}
