param([string]$Root = (Split-Path $PSScriptRoot -Parent), [switch]$AsObject)
$ErrorActionPreference = 'Stop'
# 不依赖 Roslyn 或额外包，兼容 Windows PowerShell；识别字符串而不是逐行搜索引号。
if (-not ('PaneStringScan' -as [type])) {
    Add-Type -ReferencedAssemblies System.Xml.Linq,System.Xml,System.Core -TypeDefinition @'
using System;
using System.Collections.Generic;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;
public sealed class PaneStringHit {
    public int Line; public string Content;
    public PaneStringHit(int line, string content) { Line = line; Content = content; }
}
public sealed class PaneStringScan {
    string text; int offset;
    readonly List<PaneStringHit> hits = new List<PaneStringHit>();
    readonly HashSet<int> ignored = new HashSet<int>();
    static readonly Regex Han = new Regex(@"[\p{IsCJKUnifiedIdeographs}\p{IsCJKUnifiedIdeographsExtensionA}\p{IsCJKCompatibilityIdeographs}]");
    int Line(int at) { int line = 1; for (int i = 0; i < at; i++) if (text[i] == '\n') line++; return line; }
    bool At(string value) { return offset + value.Length <= text.Length && string.CompareOrdinal(text, offset, value, 0, value.Length) == 0; }
    void Code(bool hole) {
        int depth = 0;
        while (offset < text.Length) {
            if (At("//")) {
                int start = offset; while (offset < text.Length && text[offset] != '\n') offset++;
                if (Regex.IsMatch(text.Substring(start, offset - start), @"^//\s*不翻译")) ignored.Add(Line(start));
            } else if (At("/*")) {
                int end = text.IndexOf("*/", offset + 2, StringComparison.Ordinal); offset = end < 0 ? text.Length : end + 2;
            } else if (text[offset] == '\'') {
                offset++; while (offset < text.Length) { char c = text[offset++]; if (c == '\\' && offset < text.Length) offset++; else if (c == '\'') break; }
            } else if (text[offset] == '"' || At("@\"") || At("$\"") || At("$@\"") || At("@$\"") || At("$$")) {
                ReadString();
            } else if (hole && text[offset] == '}' && depth == 0) { offset++; return; }
            else { if (hole && text[offset] == '{') depth++; else if (hole && text[offset] == '}') depth--; offset++; }
        }
    }
    void ReadString() {
        int start = offset; bool verbatim = false, interpolated = false;
        while (offset < text.Length && (text[offset] == '$' || text[offset] == '@')) { verbatim |= text[offset] == '@'; interpolated |= text[offset] == '$'; offset++; }
        if (offset >= text.Length || text[offset] != '"') return;
        int quotes = 0; while (offset + quotes < text.Length && text[offset + quotes] == '"') quotes++;
        var value = new StringBuilder();
        if (quotes >= 3) {
            offset += quotes; int end = text.IndexOf(new string('"', quotes), offset, StringComparison.Ordinal);
            if (end < 0) end = text.Length;
            value.Append(text.Substring(offset, end - offset)); offset = Math.Min(text.Length, end + quotes);
        } else {
            offset++;
            while (offset < text.Length) {
                char c = text[offset++];
                if (c == '"') { if (verbatim && offset < text.Length && text[offset] == '"') { value.Append('"'); offset++; } else break; }
                else if (!verbatim && c == '\\' && offset < text.Length) {
                    char escape = text[offset++];
                    if (escape == 'u' || escape == 'U') {
                        int length = escape == 'u' ? 4 : 8, code;
                        if (offset + length <= text.Length && int.TryParse(text.Substring(offset, length), System.Globalization.NumberStyles.HexNumber, System.Globalization.CultureInfo.InvariantCulture, out code) && code <= 0x10ffff) {
                            value.Append(code <= 0xffff ? ((char)code).ToString() : char.ConvertFromUtf32(code)); offset += length;
                        }
                    } else value.Append(escape);
                } else if (interpolated && c == '{') {
                    if (offset < text.Length && text[offset] == '{') { value.Append('{'); offset++; }
                    else Code(true);
                } else value.Append(c);
            }
        }
        if (Han.IsMatch(value.ToString())) hits.Add(new PaneStringHit(Line(start), text.Substring(start, offset - start)));
    }
    public static PaneStringHit[] CSharp(string source) {
        var scan = new PaneStringScan { text = source }; scan.Code(false);
        return scan.hits.FindAll(h => {
            int last = h.Line; foreach (char c in h.Content) if (c == '\n') last++;
            return !scan.ignored.Contains(h.Line) && !scan.ignored.Contains(last);
        }).ToArray();
    }
    public static PaneStringHit[] Xaml(string source) {
        var results = new List<PaneStringHit>();
        var doc = XDocument.Parse(source, LoadOptions.SetLineInfo | LoadOptions.PreserveWhitespace);
        foreach (var element in doc.Descendants()) foreach (var attribute in element.Attributes())
            if (Han.IsMatch(attribute.Value)) results.Add(new PaneStringHit(((IXmlLineInfo)attribute).LineNumber, attribute.Name + "=" + attribute.Value));
        return results.ToArray();
    }
}
'@
}
$resolvedRoot = (Resolve-Path -LiteralPath $Root).Path
function SourceFiles([string]$Directory) {
    foreach ($entry in Get-ChildItem -LiteralPath $Directory) {
        if ($entry.PSIsContainer) {
            if ($entry.Name -notin 'Localization','DesignData','bin','obj' -and -not ($entry.Attributes -band [IO.FileAttributes]::ReparsePoint)) { SourceFiles $entry.FullName }
        } elseif ($entry.Extension -in '.cs','.xaml') { $entry }
    }
}
SourceFiles $resolvedRoot | Sort-Object FullName | ForEach-Object {
    $file = $_
    $source = [IO.File]::ReadAllText($file.FullName)
    $hits = if ($file.Extension -eq '.cs') { [PaneStringScan]::CSharp($source) } else { [PaneStringScan]::Xaml($source) }
    foreach ($hit in $hits) {
        $relative = $file.FullName.Substring($resolvedRoot.Length).TrimStart('\','/').Replace('\','/')
        if ($AsObject) { [pscustomobject]@{ File=$relative; Line=$hit.Line; Content=$hit.Content } }
        else { '{0}:{1}: {2}' -f $relative,$hit.Line,($hit.Content -replace '\r?\n','\n') }
    }
}
