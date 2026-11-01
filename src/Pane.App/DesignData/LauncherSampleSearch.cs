// 界面开发用的假数据服务，照原型 prototype/data.js、app.js 的 searchLauncher。
// 不枚举真实应用、不查真实文件、不执行任何东西。
using System.Globalization;
using Pane.Contracts;

namespace Pane.DesignData;

public sealed class LauncherSampleSearch : ILauncherSearch
{
    sealed record Entry(string Name, string Py, string Ab, string Sub, string Glyph, string? From = null, string? To = null, string? Hotkey = null);

    static readonly Entry[] Apps =
    [
        // 原型 launcher.html 的「多个应用」演示：搜 py 出一排 Python
        new("IDLE (Python 3.13 64-bit)", "idle python", "idle", "应用", "code", "#ffd84d", "#3b77a8"),
        new("Python 3.13 (64-bit)", "python", "python", "应用", "terminal", "#4b8bbe", "#2a5d86"),
        new("Python 3.13 Module Docs", "python module docs", "pmd", "应用", "doc", "#6aa5d6", "#ffd84d"),
        new("PyCharm", "pycharm", "pc", "应用", "code", "#21d789", "#087cfa"),
        new("微信", "weixin", "wx", "应用", "chat", "#3ddc84", "#13a85a", "Alt 1"),
        new("Visual Studio Code", "visual studio code", "vsc", "应用", "code", "#3fa9f5", "#1769c4"),
        new("记事本", "jishiben", "jsb", "应用", "note", "#7cc4ff", "#3d8bdb"),
        new("文件资源管理器", "wenjianziyuanguanliqi", "wjzyglq", "应用", "folder", "#ffd36b", "#f0a020"),
        new("设置", "shezhi", "sz", "系统", "gear", "#9aa3b5", "#5d6577"),
        new("Microsoft Edge", "edge liulanqi", "edge", "应用", "globe", "#4fd1c5", "#1673c9"),
        new("终端", "zhongduan terminal", "zd", "Windows Terminal", "terminal", "#4a4f5c", "#1e2128"),
        new("网易云音乐", "wangyiyunyinyue", "wyyyy", "应用", "music", "#ff6b6b", "#d62d3a"),
        new("计算器", "jisuanqi", "jsq", "系统", "calc", "#8b95a8", "#3f4757"),
        new("画图", "huatu paint", "ht", "系统", "brush", "#ff9ad5", "#e0569f"),
        new("截图工具", "jietugongju", "jtgj", "系统", "scissors", "#b993ff", "#7b4fe0"),
        new("任务管理器", "renwuguanliqi", "rwglq", "系统", "pulse", "#7aa2ff", "#4359d6"),
        new("腾讯会议", "tengxunhuiyi", "txhy", "应用", "video", "#5aa9ff", "#2a6af2"),
        new("Photoshop", "photoshop ps", "ps", "应用", "image", "#3ec1ff", "#0b3a66"),
    ];
    static readonly Entry[] Files =
    [
        new("报价单-2026Q4.xlsx", "baojiadan", "bjd", @"D:\工作\客户\星河科技", "doc"),
        new("客户报价汇总.docx", "kehubaojiahuizong", "khbjhz", @"D:\工作\客户", "doc"),
        new("报价模板", "baojiamuban", "bjmb", @"D:\工作\模板", "folder"),
        new("报价截图.png", "baojiajietu", "bjjt", @"C:\Users\你\Pictures\Screenshots", "image"),
        new("2026 Q3 季度汇报.pptx", "jiduhuibao", "jdhb", @"D:\工作\汇报", "doc"),
        new("家庭预算.xlsx", "jiatingyusuan", "jtys", @"D:\个人\账本", "doc"),
        new("旅行照片", "lvxingzhaopian", "lxzp", @"E:\照片\2026", "folder"),
        new("需求与范围.md", "xuqiuyufanwei", "xqyfw", @"D:\项目\Pane\docs", "doc"),
        new("简历-2026.pdf", "jianli", "jl", @"D:\个人", "doc"),
    ];
    static readonly Entry[] Commands =
    [
        new("锁屏", "suoping", "sp", "命令", "lock"),
        new("睡眠", "shuimian", "sm", "命令", "moon"),
        new("重启", "chongqi", "cq", "命令", "restart"),
        new("关机", "guanji", "gj", "命令", "power"),
        new("清空回收站", "qingkonghuishouzhan", "qkhsz", "命令", "trash"),
        new("注销", "zhuxiao logoff sign out", "zx", "命令", "logout"),
        new("休眠", "xiumian hibernate", "xm", "命令", "moon"),
        new("打开剪贴板历史", "dakaijiantieban", "dkjtb", "Pane", "clipboard"),
    ];
    // 系统设置和组件（037）：拼音里顺带放别名和英文名，演示「PATH」「环境变量」都能搜到
    static readonly Entry[] SystemSettings =
    [
        new("编辑系统环境变量", "bianjixitonghuanjingbianliang huanjingbianliang xitongbianliang path environment", "bjxthjbl", "系统属性 · 高级", "gear"),
        new("设备管理器", "shebeiguanliqi device manager devmgmt", "sbglq", "管理工具", "monitor"),
        new("编辑组策略", "bianjizucelve gpedit group policy", "bjzcl", "管理工具 · 需要管理员权限", "shield"),
        new("Windows 更新", "windows gengxin update", "wgx", "设置", "refresh"),
        new("蓝牙和其他设备", "lanya bluetooth", "ly", "设置", "gear"),
        new("程序和功能", "chengxuhegongneng xiezai uninstall", "cxhgn", "控制面板", "app"),
        new("服务", "fuwu services", "fw", "管理工具", "gear"),
    ];
    static readonly string[] History = ["git status", "git log --oneline -20", "python -m http.server 8000", "ping 1.1.1.1"];
    static readonly string[] Programs = ["python", "pwsh", "ping", "git", "node"];
    static readonly string[] Recents = ["微信", "Visual Studio Code", "Microsoft Edge", "终端", "网易云音乐", "截图工具", "记事本", "设置"];
    static readonly string[] Pinned = ["文件资源管理器", "计算器", "腾讯会议", "Photoshop"];

    public Task<LauncherResult> SearchAsync(string query, CancellationToken cancellationToken)
    {
        var raw = query;
        var t = raw.Trim();
        var q = t.ToLowerInvariant();
        var items = new List<LauncherItem>();
        if (q.Length == 0)
        {
            foreach (var n in Recents) items.Add(Tile(Apps.First(a => a.Name == n), "最近使用"));
            foreach (var n in Pinned) items.Add(Tile(Apps.First(a => a.Name == n), "已固定"));
            return Task.FromResult(new LauncherResult(items, true));
        }
        if (t.StartsWith('>'))
        {
            var cmd = t[1..].Trim();
            if (cmd.Length == 0) { items.Add(new("terminal:hint", LauncherItemKind.Action, "终端", "输入要在终端里运行的命令", "按 Ctrl+Shift+Enter 以管理员身份运行", new("terminal"))); return Task.FromResult(new LauncherResult(items, false)); }
            items.Add(new("terminal:" + cmd, LauncherItemKind.Action, "终端", "在终端里运行", cmd, new("terminal"), Hotkey: "Enter"));
            var first = cmd.Split(' ')[0];
            foreach (var c in History.Where(h => h.StartsWith(cmd, StringComparison.OrdinalIgnoreCase) && h != cmd)
                         .Concat(cmd.Contains(' ') ? [] : Programs.Where(n => n.StartsWith(first, StringComparison.OrdinalIgnoreCase) && n != first)).Take(6))
                items.Add(new("complete:" + c, LauncherItemKind.Action, "补全", c, History.Contains(c) ? "最近运行过" : "程序", new(History.Contains(c) ? "refresh" : "terminal")));
            return Task.FromResult(new LauncherResult(items, false));
        }
        var v = Calc(raw);
        if (v is not null) items.Add(new("calc", LauncherItemKind.Calculation, "计算", v, t + " =", new("equal")));
        Pick(Apps, LauncherItemKind.App, "应用", 14);
        Pick(Files, LauncherItemKind.File, "文件", 3);
        int fileTotal = Files.Count(e => Match(e, q) is not null) * 4;   // 假数据：假装 Everything 还有更多
        Pick(Commands, LauncherItemKind.Command, "命令", 3);
        Pick(SystemSettings, LauncherItemKind.Command, "设置", 4);
        items.Add(new("ai:translate", LauncherItemKind.Ai, "AI", $"翻译“{t}”", "用 AI 翻译", new("translate")));
        items.Add(new("ai:ask", LauncherItemKind.Ai, "AI", $"问 AI：“{t}”", "单轮回答，可以复制或粘贴", new("ai"), Hotkey: "Tab"));
        items.Add(new("web", LauncherItemKind.Web, "网页", $"在网页中搜索“{t}”", "默认搜索引擎", new("search")));
        return Task.FromResult(new LauncherResult(items, false, fileTotal));

        void Pick(Entry[] list, LauncherItemKind kind, string group, int limit)
        {
            foreach (var (e, m) in list.Select(e => (e, m: Match(e, q))).Where(x => x.m is not null)
                         .OrderByDescending(x => x.m!.Value.score).Take(limit))
            {
                IReadOnlyList<TextSpan>? spans = m!.Value.span is { } s ? [s] : null;
                var glyph = kind == LauncherItemKind.File && e.Glyph == "folder" ? "folder" : e.Glyph;
                items.Add(new(e.Name, kind, group, e.Name, e.Sub, new(glyph, DesignIcon(kind), e.From, e.To), spans, e.Hotkey));
            }
        }
    }

    public Task ExecuteAsync(LauncherItem item, CancellationToken cancellationToken) => Task.CompletedTask;

    static LauncherItem Tile(Entry e, string group) => new(e.Name, LauncherItemKind.App, group, e.Name, e.Sub, new(e.Glyph, DesignIcon(LauncherItemKind.App), e.From, e.To));

    // 设计预览：PANE_DESIGN_ICON 指向一张 PNG 时，所有应用都用它当真实图标（看真实图标的排版）
    static string? DesignIcon(LauncherItemKind kind) =>
        kind == LauncherItemKind.App && Environment.GetEnvironmentVariable("PANE_DESIGN_ICON") is { Length: > 0 } p ? p : null;

    // 匹配：名称前缀、首字母、名称包含、全拼前缀、首字母包含、全拼包含、模糊子序列（同原型）
    static (int score, TextSpan? span)? Match(Entry e, string q)
    {
        var name = e.Name.ToLowerInvariant();
        var py = e.Py.Replace(" ", "");
        var aligned = e.Ab.Length == e.Name.Length;
        int i;
        if (name.StartsWith(q, StringComparison.Ordinal)) return (100, new TextSpan(0, q.Length));
        if (e.Ab.StartsWith(q, StringComparison.Ordinal)) return (90, aligned ? new TextSpan(0, q.Length) : null);
        if ((i = name.IndexOf(q, StringComparison.Ordinal)) >= 0) return (70, new TextSpan(i, q.Length));
        if (py.StartsWith(q, StringComparison.Ordinal)) return (60, null);
        if ((i = e.Ab.IndexOf(q, StringComparison.Ordinal)) >= 0) return (45, aligned ? new TextSpan(i, q.Length) : null);
        if (py.Contains(q, StringComparison.Ordinal)) return (35, null);
        int j = 0;
        foreach (var c in name) if (j < q.Length && c == q[j]) j++;
        return j == q.Length && q.Length > 1 ? (10, null) : null;
    }

    // 安全的四则运算（不用任何求值器），同原型 calc()
    static string? Calc(string src)
    {
        var s = new string(src.Replace('×', '*').Replace('÷', '/').Where(c => !char.IsWhiteSpace(c)).ToArray());
        if (s.Length == 0 || s.Any(c => !"0123456789.+-*/%^()".Contains(c)) || !s.Any(char.IsDigit)) return null;
        if (!s.TrimStart('-').Any(c => "+-*/%^".Contains(c))) return null;
        int i = 0;
        try
        {
            var v = Expr();
            if (i != s.Length || double.IsNaN(v) || double.IsInfinity(v)) return null;
            return double.Parse(v.ToString("G12", CultureInfo.InvariantCulture), CultureInfo.InvariantCulture).ToString(CultureInfo.InvariantCulture);
        }
        catch (FormatException) { return null; }

        char Peek() => i < s.Length ? s[i] : '\0';
        double Primary()
        {
            if (Peek() == '(') { i++; var v = Expr(); if (Peek() != ')') throw new FormatException(); i++; return v; }
            int start = i;
            while (i < s.Length && (char.IsDigit(s[i]) || s[i] == '.')) i++;
            if (start == i || !double.TryParse(s[start..i], NumberStyles.Float, CultureInfo.InvariantCulture, out var n)) throw new FormatException();
            return n;
        }
        double Unary() => Peek() == '-' ? (i++ >= 0 ? -Unary() : 0) : Peek() == '+' ? (i++ >= 0 ? Unary() : 0) : Primary();
        double Factor() { var b = Unary(); if (Peek() == '^') { i++; return Math.Pow(b, Factor()); } return b; }
        double Term()
        {
            var v = Factor();
            while (Peek() is '*' or '/' or '%') { var o = s[i++]; var r = Factor(); v = o == '*' ? v * r : o == '/' ? v / r : v % r; }
            return v;
        }
        double Expr()
        {
            var v = Term();
            while (Peek() is '+' or '-') { var o = s[i++]; var r = Term(); v = o == '+' ? v + r : v - r; }
            return v;
        }
    }
}

/// <summary>附件动作的假实现：什么都不做（不打开文件、不写剪贴板）。</summary>
public sealed class LauncherAttachmentActionsSample : ILauncherAttachmentActions
{
    public Task RunAsync(string actionId, ClipEntry attachment, CancellationToken cancellationToken) => Task.CompletedTask;
}
