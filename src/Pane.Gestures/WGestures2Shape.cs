using System.Text.Json;

namespace Pane.Gestures;

internal static class WGestures2Shape
{
    internal static GestureButton Button(string key) => key switch
    {
        "MOUSE:1" => GestureButton.Right, "MOUSE:2" => GestureButton.Middle,
        "MOUSE:3" => GestureButton.X1, "MOUSE:4" => GestureButton.X2, _ => GestureButton.None
    };

    internal static (GesturePattern Pattern, bool Exact) Read(JsonElement gesture)
    {
        GestureButton button = GestureButton.None;
        GestureModifier modifier = GestureModifier.None;
        var points = new List<GesturePoint>();
        foreach (var element in gesture.EnumerateArray())
        {
            string type = element.GetProperty("$type").GetString() ?? "";
            if (type == "MoveToEdgeCorner") throw new NotSupportedException("热角 / 贴边");
            if (type == "KeyDown")
            {
                string key = element.GetProperty("Key").GetString() ?? "";
                if (key.StartsWith("KEYBOARD:", StringComparison.Ordinal)) throw new NotSupportedException("手势里按键盘键");
                if (button == GestureButton.None)
                {
                    button = Button(key);
                    if (button == GestureButton.None) throw new NotSupportedException(key == "MOUSE:0" ? "左键起手" : "未知触发键");
                }
                else
                {
                    if (modifier != GestureModifier.None) throw new NotSupportedException("手势含多个修饰动作");
                    modifier = key switch
                    {
                        "MOUSE:0" => GestureModifier.LeftClick, "MOUSE:2" => GestureModifier.MiddleClick,
                        "MOUSE:3" => GestureModifier.X1Click, "MOUSE:4" => GestureModifier.X2Click,
                        "VSCROLL:120" => GestureModifier.WheelUp, "VSCROLL:-120" => GestureModifier.WheelDown,
                        _ => throw new NotSupportedException("未知手势修饰键")
                    };
                }
                continue;
            }
            if (type != "Stroke") throw new NotSupportedException($"未知手势元素 {type}");
            if (button == GestureButton.None) throw new NotSupportedException("笔画前缺少触发键");
            if (modifier != GestureModifier.None) throw new NotSupportedException("修饰动作后还有笔画");
            var coords = element.GetProperty("P");
            if (coords.GetArrayLength() < 4 || coords.GetArrayLength() % 2 != 0) throw new FormatException("笔画坐标不完整");
            var values = coords.EnumerateArray();
            while (values.MoveNext())
            {
                int x = values.Current.GetInt32();
                values.MoveNext();
                var point = new GesturePoint(x, values.Current.GetInt32());
                if (points.Count == 0 || points[^1] != point) points.Add(point);
            }
        }
        if (button == GestureButton.None) throw new NotSupportedException("缺少触发键");
        if (points.Count == 0) return (new(button, [], modifier), true);
        if (points.Count < 2) throw new FormatException("笔画长度为零");
        var directions = Directions(points);
        bool exact = directions.Count == points.Count - 1;
        for (int i = 0; exact && i < points.Count; i++)
        {
            var p = points[i];
            exact = p.X % 50 == 0 && p.Y % 50 == 0;
            if (i == 0) continue;
            double dx = (double)p.X - points[i-1].X, dy = (double)p.Y - points[i-1].Y;
            exact &= (dx == 0 || dy == 0 || Math.Abs(dx) == Math.Abs(dy))
                && directions[i-1] == GestureRecognizer.Classify(dx, dy);
        }
        return (new(button, directions, modifier), exact);
    }

    static List<GestureDirection> Directions(List<GesturePoint> points)
    {
        double scale = Math.Max((double)points.Max(p => p.X) - points.Min(p => p.X),
            (double)points.Max(p => p.Y) - points.Min(p => p.Y));
        // Douglas–Peucker 保留相对轮廓显著的转角；显式栈也能处理闭合形状。
        var keep = new bool[points.Count];
        keep[0] = keep[^1] = true;
        var spans = new Stack<(int Start, int End)>();
        spans.Push((0, points.Count - 1));
        long work = 0;
        while (spans.TryPop(out var span))
        {
            var a = points[span.Start];
            double dx = (double)points[span.End].X - a.X, dy = (double)points[span.End].Y - a.Y;
            double length2 = dx * dx + dy * dy;
            double max = Math.Pow(scale * 0.05, 2);
            int split = -1;
            work += span.End - span.Start;
            for (int i = span.Start + 1; i < span.End; i++)
            {
                double x = (double)points[i].X - a.X, y = (double)points[i].Y - a.Y;
                double t = length2 == 0 ? 0 : Math.Clamp((x * dx + y * dy) / length2, 0, 1);
                x -= t * dx;
                y -= t * dy;
                double distance = x * x + y * y;
                if (distance > max) { max = distance; split = i; }
            }
            if (split < 0) continue;
            // 极长锯齿可能每次只切掉一个点；此时改为平分后分别简化，避免平方级扫描。
            if (work > 64L * points.Count) split = span.Start + (span.End - span.Start) / 2;
            keep[split] = true;
            spans.Push((split, span.End));
            spans.Push((span.Start, split));
        }

        var segments = new List<(double X, double Y)>();
        var previous = points[0];
        for (int i = 1; i < points.Count; i++)
        {
            if (!keep[i]) continue;
            double dx = (double)points[i].X - previous.X, dy = (double)points[i].Y - previous.Y;
            previous = points[i];
            if (dx * dx + dy * dy < Math.Pow(scale * 0.08, 2)) continue;
            if (segments.Count > 0 && GestureRecognizer.Classify(segments[^1].X, segments[^1].Y) == GestureRecognizer.Classify(dx, dy))
                segments[^1] = (segments[^1].X + dx, segments[^1].Y + dy);
            else segments.Add((dx, dy));
        }
        var directions = new List<GestureDirection>();
        foreach (var (dx, dy) in segments)
        {
            var dir = segments.Count == 1 ? GestureRecognizer.Classify(dx, dy) : GestureRecognizer.Cardinal(dx, dy);
            if (directions.Count == 0 || directions[^1] != dir) directions.Add(dir);
        }
        if (directions.Count == 0) throw new NotSupportedException("没有可转换的笔画");
        return directions;
    }
}
