// LaTeX 公式转成能读的文字（设计负责人，2026-11-13）：AI 回答里的 $…$ / $$…$$ 先用这个显示，真正的公式排版看任务 100 的调研。
// 认得：希腊字母、常用运算符和关系符、\frac、\sqrt、上下标（能换成上下标字符的就换，否则写成 ^(…)）、\text 一类、\mathbb、
// 重音（\vec \hat \bar \dot \tilde \overline）、\left \right 和大小修饰。认不得的命令、\begin 环境返回 ok = false，调用方照原文显示。
using System.Text;

namespace Pane.Views.Controls;

public static class TexText
{
    public static (string Text, bool Ok) Convert(string tex)
    {
        var parser = new Parser(tex);
        var text = parser.Sequence(stopAtBrace: false);
        return (Tidy(text), parser.Ok);
    }

    static string Tidy(string s)
    {
        var sb = new StringBuilder();
        foreach (var ch in s)
        {
            if (ch == ' ' && sb.Length > 0 && sb[^1] == ' ') continue;
            sb.Append(ch);
        }
        return sb.ToString().Trim();
    }

    sealed class Parser(string s)
    {
        int i;
        public bool Ok { get; private set; } = true;

        public string Sequence(bool stopAtBrace)
        {
            var sb = new StringBuilder();
            while (i < s.Length)
            {
                char ch = s[i];
                if (ch == '}') { if (stopAtBrace) { i++; return sb.ToString(); } i++; continue; }
                if (ch == '^' || ch == '_') { i++; sb.Append(Script(Argument(), ch == '^')); continue; }
                if (ch == '{') { i++; sb.Append(Sequence(stopAtBrace: true)); continue; }
                if (ch == '\\') { sb.Append(Command()); continue; }
                if (ch == '~') { sb.Append(' '); i++; continue; }
                if (ch == '&') { sb.Append("  "); i++; continue; }
                if (char.IsWhiteSpace(ch)) { sb.Append(' '); i++; continue; }
                sb.Append(ch is '+' or '-' && Unary(sb) ? (ch == '-' ? "−" : "+") : Operator(ch));
                i++;
            }
            return sb.ToString();
        }

        // 一元正负号（开头、左括号或另一个运算符之后）不留空格：−b、(−1)、= −x
        static bool Unary(StringBuilder sb)
        {
            int k = sb.Length - 1;
            while (k >= 0 && sb[k] == ' ') k--;
            return k < 0 || "([{=<>+−±∓×·÷,≤≥≠≈^_|".Contains(sb[k]);
        }

        // 运算符两边留空格，读起来更像公式
        static string Operator(char ch) => ch switch
        {
            '=' => " = ", '<' => " < ", '>' => " > ", '+' => " + ",
            '-' => " − ",
            '*' => " ∗ ",
            _ => ch.ToString(),
        };

        // 上下标、\frac 的参数：{…}、一个命令、或一个字符
        string Argument()
        {
            while (i < s.Length && s[i] == ' ') i++;
            if (i >= s.Length) return "";
            if (s[i] == '{') { i++; return Sequence(stopAtBrace: true).Trim(); }
            if (s[i] == '\\') return Command().Trim();
            return s[i++].ToString();
        }

        string? OptionalArgument()
        {
            while (i < s.Length && s[i] == ' ') i++;
            if (i >= s.Length || s[i] != '[') return null;
            int end = s.IndexOf(']', i);
            if (end < 0) return null;
            var inner = new Parser(s[(i + 1)..end]);
            var text = inner.Sequence(false);
            if (!inner.Ok) Ok = false;
            i = end + 1;
            return text.Trim();
        }

        string Command()
        {
            i++;   // 跳过 \
            if (i >= s.Length) return "";
            char first = s[i];
            if (!char.IsAsciiLetter(first))
            {
                i++;
                return first switch
                {
                    ',' or ':' or ';' or ' ' => " ",
                    '!' => "",
                    '\\' => "  ",
                    '{' or '}' or '%' or '$' or '#' or '&' or '_' => first.ToString(),
                    '|' => "‖",
                    _ => first.ToString(),
                };
            }
            int start = i;
            while (i < s.Length && char.IsAsciiLetter(s[i])) i++;
            var name = s[start..i];

            if (Symbols.TryGetValue(name, out var symbol)) return symbol;
            if (Functions.Contains(name)) return name + " ";
            switch (name)
            {
                case "frac" or "dfrac" or "tfrac" or "cfrac":
                {
                    var a = Argument(); var b = Argument();
                    return Wrap(a) + "/" + Wrap(b);
                }
                case "binom":
                {
                    var a = Argument(); var b = Argument();
                    return "C(" + a + ", " + b + ")";
                }
                case "sqrt":
                {
                    var n = OptionalArgument();
                    var x = Argument();
                    var root = n switch { null or "2" => "√", "3" => "∛", "4" => "∜", _ => Script(n, true) + "√" };
                    return root + Wrap(x);
                }
                case "text" or "textrm" or "textit" or "textbf" or "mathrm" or "mathit" or "mathbf" or "mathsf" or "mathtt" or "boldsymbol" or "bm" or "operatorname" or "mathcal" or "mathscr" or "mbox" or "emph":
                    return Argument();
                case "mathbb":
                {
                    var a = Argument();
                    return string.Concat(a.Select(c => DoubleStruck.TryGetValue(c, out var d) ? d : c.ToString()));
                }
                case "vec": return Accent(Argument(), '⃗');
                case "hat" or "widehat": return Accent(Argument(), '̂');
                case "bar" or "overline": return Accent(Argument(), '̅');
                case "dot": return Accent(Argument(), '̇');
                case "ddot": return Accent(Argument(), '̈');
                case "tilde" or "widetilde": return Accent(Argument(), '̃');
                case "underline": return Accent(Argument(), '̲');
                case "left" or "right" or "big" or "Big" or "bigg" or "Bigg" or "bigl" or "bigr" or "Bigl" or "Bigr" or "middle":
                    if (i < s.Length && s[i] == '.') i++;   // \left. 不画东西
                    return "";
                case "displaystyle" or "textstyle" or "scriptstyle" or "limits" or "nolimits":
                    return "";
                case "quad": return "  ";
                case "qquad": return "    ";
                case "pmod":
                    return " (mod " + Argument() + ")";
                case "boxed": return Argument();
                case "color" or "textcolor":
                    Argument();
                    return name == "textcolor" ? Argument() : "";
                default:
                    // \begin{…}、认不得的命令：交给调用方照原文显示
                    Ok = false;
                    return name;
            }
        }

        static string Wrap(string x)
        {
            x = x.Trim();
            if (x.Length <= 1 || x.All(char.IsLetterOrDigit) || x.StartsWith('(') && x.EndsWith(')') && x.Count(c => c == '(') == 1) return x;
            return "(" + x + ")";
        }

        static string Accent(string x, char mark) => x.Length == 0 ? "" : x + mark;

        static string Script(string x, bool up)
        {
            x = x.Trim();
            if (x.Length == 0) return "";
            var map = up ? Superscripts : Subscripts;
            var sb = new StringBuilder();
            foreach (var c in x.Replace(" ", ""))
            {
                var key = c == '−' ? '-' : c;
                if (!map.TryGetValue(key, out var mapped)) return (up ? "^" : "_") + (x.Length == 1 ? x : "(" + x + ")");
                sb.Append(mapped);
            }
            return sb.ToString();
        }
    }

    static readonly Dictionary<char, char> Superscripts = Pairs("0123456789+-=()niabcdefghijklmoprstuvwxyzABDEGHIJKLMNOPRTUVW′",
        "⁰¹²³⁴⁵⁶⁷⁸⁹⁺⁻⁼⁽⁾ⁿⁱᵃᵇᶜᵈᵉᶠᵍʰʲᵏˡᵐᵒᵖʳˢᵗᵘᵛʷˣʸᶻᴬᴮᴰᴱᴳᴴᴵᴶᴷᴸᴹᴺᴼᴾᴿᵀᵁⱽᵂ′");
    static readonly Dictionary<char, char> Subscripts = Pairs("0123456789+-=()aehijklmnoprstuvx",
        "₀₁₂₃₄₅₆₇₈₉₊₋₌₍₎ₐₑₕᵢⱼₖₗₘₙₒₚᵣₛₜᵤᵥₓ");

    static Dictionary<char, char> Pairs(string from, string to)
    {
        var d = new Dictionary<char, char>();
        for (int k = 0; k < from.Length && k < to.Length; k++) d[from[k]] = to[k];
        return d;
    }

    static readonly Dictionary<char, string> DoubleStruck = new()
    {
        ['N'] = "ℕ", ['Z'] = "ℤ", ['Q'] = "ℚ", ['R'] = "ℝ", ['C'] = "ℂ", ['P'] = "ℙ", ['H'] = "ℍ", ['E'] = "𝔼",
    };

    static readonly HashSet<string> Functions = ["sin", "cos", "tan", "cot", "sec", "csc", "arcsin", "arccos", "arctan", "sinh", "cosh", "tanh",
        "log", "ln", "lg", "exp", "lim", "max", "min", "sup", "inf", "det", "dim", "ker", "deg", "gcd", "arg", "Pr", "mod", "liminf", "limsup"];

    static readonly Dictionary<string, string> Symbols = new()
    {
        // 希腊字母
        ["alpha"] = "α", ["beta"] = "β", ["gamma"] = "γ", ["delta"] = "δ", ["epsilon"] = "ϵ", ["varepsilon"] = "ε", ["zeta"] = "ζ", ["eta"] = "η",
        ["theta"] = "θ", ["vartheta"] = "ϑ", ["iota"] = "ι", ["kappa"] = "κ", ["lambda"] = "λ", ["mu"] = "μ", ["nu"] = "ν", ["xi"] = "ξ",
        ["pi"] = "π", ["varpi"] = "ϖ", ["rho"] = "ρ", ["varrho"] = "ϱ", ["sigma"] = "σ", ["varsigma"] = "ς", ["tau"] = "τ", ["upsilon"] = "υ",
        ["phi"] = "ϕ", ["varphi"] = "φ", ["chi"] = "χ", ["psi"] = "ψ", ["omega"] = "ω",
        ["Gamma"] = "Γ", ["Delta"] = "Δ", ["Theta"] = "Θ", ["Lambda"] = "Λ", ["Xi"] = "Ξ", ["Pi"] = "Π", ["Sigma"] = "Σ", ["Upsilon"] = "Υ",
        ["Phi"] = "Φ", ["Psi"] = "Ψ", ["Omega"] = "Ω",
        // 运算和关系
        ["times"] = " × ", ["cdot"] = " · ", ["div"] = " ÷ ", ["pm"] = " ± ", ["mp"] = " ∓ ", ["ast"] = "∗", ["star"] = "⋆", ["circ"] = "∘", ["bullet"] = "•",
        ["oplus"] = " ⊕ ", ["otimes"] = " ⊗ ", ["cup"] = " ∪ ", ["cap"] = " ∩ ", ["setminus"] = " ∖ ", ["wedge"] = " ∧ ", ["land"] = " ∧ ", ["vee"] = " ∨ ", ["lor"] = " ∨ ",
        ["leq"] = " ≤ ", ["le"] = " ≤ ", ["geq"] = " ≥ ", ["ge"] = " ≥ ", ["neq"] = " ≠ ", ["ne"] = " ≠ ", ["approx"] = " ≈ ", ["equiv"] = " ≡ ", ["sim"] = " ∼ ",
        ["simeq"] = " ≃ ", ["cong"] = " ≅ ", ["propto"] = " ∝ ", ["ll"] = " ≪ ", ["gg"] = " ≫ ", ["leqslant"] = " ⩽ ", ["geqslant"] = " ⩾ ", ["prec"] = " ≺ ", ["succ"] = " ≻ ",
        ["in"] = " ∈ ", ["notin"] = " ∉ ", ["ni"] = " ∋ ", ["subset"] = " ⊂ ", ["subseteq"] = " ⊆ ", ["supset"] = " ⊃ ", ["supseteq"] = " ⊇ ",
        ["perp"] = " ⊥ ", ["parallel"] = " ∥ ", ["mid"] = " ∣ ", ["nmid"] = " ∤ ",
        ["to"] = " → ", ["rightarrow"] = " → ", ["leftarrow"] = " ← ", ["gets"] = " ← ", ["leftrightarrow"] = " ↔ ", ["Rightarrow"] = " ⇒ ", ["implies"] = " ⇒ ",
        ["Leftarrow"] = " ⇐ ", ["Leftrightarrow"] = " ⇔ ", ["iff"] = " ⇔ ", ["mapsto"] = " ↦ ", ["longrightarrow"] = " ⟶ ", ["Longrightarrow"] = " ⟹ ",
        ["uparrow"] = "↑", ["downarrow"] = "↓",
        // 大运算符和其他
        ["sum"] = "∑", ["prod"] = "∏", ["coprod"] = "∐", ["int"] = "∫", ["iint"] = "∬", ["iiint"] = "∭", ["oint"] = "∮", ["bigcup"] = "⋃", ["bigcap"] = "⋂",
        ["infty"] = "∞", ["partial"] = "∂", ["nabla"] = "∇", ["forall"] = "∀", ["exists"] = "∃", ["nexists"] = "∄", ["emptyset"] = "∅", ["varnothing"] = "∅",
        ["neg"] = "¬", ["lnot"] = "¬", ["angle"] = "∠", ["triangle"] = "△", ["degree"] = "°", ["prime"] = "′", ["hbar"] = "ℏ", ["ell"] = "ℓ",
        ["Re"] = "ℜ", ["Im"] = "ℑ", ["aleph"] = "ℵ", ["therefore"] = " ∴ ", ["because"] = " ∵ ",
        ["ldots"] = "…", ["dots"] = "…", ["cdots"] = "⋯", ["vdots"] = "⋮", ["ddots"] = "⋱",
        ["langle"] = "⟨", ["rangle"] = "⟩", ["lfloor"] = "⌊", ["rfloor"] = "⌋", ["lceil"] = "⌈", ["rceil"] = "⌉",
        ["lvert"] = "|", ["rvert"] = "|", ["vert"] = "|", ["lVert"] = "‖", ["rVert"] = "‖", ["Vert"] = "‖",
        ["lbrace"] = "{", ["rbrace"] = "}", ["colon"] = ":", ["cdotp"] = "·", ["percent"] = "%",
    };
}
