namespace Pane.Gestures;

public readonly record struct GesturePoint(int X, int Y);

public sealed class GestureRecognizer
{
    readonly List<GestureDirection> directions = [];
    GesturePoint anchor;
    readonly double threshold;
    readonly bool diagonalMultiStroke;
    double firstDx, firstDy;
    public IReadOnlyList<GestureDirection> Directions => directions;
    public GestureRecognizer(GesturePoint start, double dpi = 96, bool allowDiagonalMultiStroke = false)
    {
        anchor = start;
        // WGestures 在 1920 宽屏幕按下时取 2.5% = 48px；按本任务改为每屏 DPI 的 DIP 阈值。
        threshold = 48 * Math.Clamp(dpi, 48, 768) / 96;
        diagonalMultiStroke = allowDiagonalMultiStroke;
    }
    public bool Add(GesturePoint point)
    {
        double dx = point.X - anchor.X, dy = point.Y - anchor.Y;
        if (dx * dx + dy * dy < threshold * threshold) return false;
        var direction = Classify(dx, dy);
        if (directions.Count == 0) { firstDx = dx; firstDy = dy; }
        if (!diagonalMultiStroke && directions.Count > 0)
        {
            if (directions.Count == 1 && ((int)directions[0] & 1) != 0)
            {
                if (direction != directions[0])
                {
                    double cosine = (firstDx * dx + firstDy * dy) /
                        Math.Sqrt((firstDx * firstDx + firstDy * firstDy) * (dx * dx + dy * dy));
                    if (cosine > Math.Cos(36 * Math.PI / 180)) direction = directions[0];
                    else { directions[0] = Cardinal(firstDx, firstDy); direction = Cardinal(dx, dy); }
                }
            }
            else direction = Cardinal(dx, dy);
        }
        anchor = point;
        if (directions.Count > 0 && directions[^1] == direction) return false;
        directions.Add(direction);
        return true;
    }
    internal static GestureDirection Cardinal(double dx, double dy) => Math.Abs(dx) > Math.Abs(dy)
        ? (dx > 0 ? GestureDirection.Right : GestureDirection.Left)
        : (dy > 0 ? GestureDirection.Down : GestureDirection.Up);
    public static GestureDirection Classify(double dx, double dy)
    {
        double angle = (Math.Atan2(dx, -dy) * 180 / Math.PI + 360) % 360;
        int quadrant = (int)(angle / 90);
        double local = angle % 90;
        return (GestureDirection)((quadrant * 2 + (local <= 20 ? 0 : local <= 70 ? 1 : 2)) % 8);
    }
}

public static class GestureMatcher
{
    static readonly string WindowsDirectory = NormalizePath(Environment.GetFolderPath(Environment.SpecialFolder.Windows));

    static string NormalizePath(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) return "";
        path = path.Replace('/', '\\');
        if (path.StartsWith(@"\\?\UNC\", StringComparison.OrdinalIgnoreCase)) path = @"\\" + path[8..];
        else if (path.StartsWith(@"\\?\", StringComparison.Ordinal) && path.Length > 6 && path[5] == ':') path = path[4..];
        try { return Path.GetFullPath(path).TrimEnd('\\'); }
        catch (Exception e) when (e is ArgumentException or IOException or NotSupportedException) { return ""; }
    }

    // 两个候选文件夹都是目标路径的祖先时，较长的前缀就是更深的一层。
    static int MatchRank(string rule, string target)
    {
        if (target.Length == 0 || rule.Length == 0) return 0;
        if (target.Equals(rule, StringComparison.OrdinalIgnoreCase)) return int.MaxValue;
        int end = rule.LastIndexOf('\\');
        if (end <= 0 || (end == 2 && rule[1] == ':')) return 0;
        var folder = rule.AsSpan(0, end);
        if (WindowsDirectory.Length == 0 || folder.Equals(WindowsDirectory, StringComparison.OrdinalIgnoreCase) ||
            (end > WindowsDirectory.Length && folder[WindowsDirectory.Length] == '\\' &&
             folder.StartsWith(WindowsDirectory, StringComparison.OrdinalIgnoreCase))) return 0;
        return target.Length > end && target[end] == '\\' &&
            target.AsSpan().StartsWith(folder, StringComparison.OrdinalIgnoreCase) ? end : 0;
    }

    internal static bool SuperExcluded(string[] paths, string exePath)
    {
        if (paths.Length == 0) return false;
        string target = NormalizePath(exePath);
        return paths.Any(path => MatchRank(NormalizePath(path), target) > 0);
    }

    public static AppRule? Rule(GestureConfig config, string exePath, string? windowClass)
    {
        if (config.Apps.Count == 0) return null;
        string target = NormalizePath(exePath);
        AppRule? best = null;
        int bestRank = 0;
        foreach (var rule in config.Apps)
        {
            if (rule.WindowClass is not null && rule.WindowClass != windowClass) continue;
            int rank = MatchRank(NormalizePath(rule.ExePath), target);
            if (rank > bestRank || (rank > 0 && rank == bestRank && best?.WindowClass is null && rule.WindowClass is not null))
            {
                best = rule;
                bestRank = rank;
            }
        }
        return best;
    }

    public static GestureBinding? Match(GestureConfig config, GesturePattern pattern, string exePath = "", string? windowClass = null)
    {
        var rule = Rule(config, exePath, windowClass);
        if (rule?.Mode == AppRuleMode.Disabled) return null;
        // WGestures 以触发键、方向和修饰作为覆盖键，名称只是显示文字。
        return rule?.Bindings.FirstOrDefault(b => Same(b.Pattern, pattern))
            ?? config.Global.FirstOrDefault(b => Same(b.Pattern, pattern));
    }
    public static bool Same(GesturePattern a, GesturePattern b) =>
        a.Button == b.Button && a.Modifier == b.Modifier && a.Directions.SequenceEqual(b.Directions);
}
