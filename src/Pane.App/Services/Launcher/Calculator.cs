using System.Globalization;
namespace Pane.Services.Launcher;
internal static class Calculator
{
    // 安全的四则运算（不用任何求值器），同原型 calc()
    public static string? Calc(string src)
    {
        var s = new string(src.Replace('×', '*').Replace('÷', '/').Where(c => !char.IsWhiteSpace(c)).ToArray());
        if (s.Length > 512 || s.Length == 0 || s.Any(c => !"0123456789.+-*/%^()".Contains(c)) || !s.Any(char.IsDigit)) return null;
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
