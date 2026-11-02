using System.Globalization;
using System.Text;
using Pane.Contracts;
namespace Pane.Services.Launcher;

internal sealed class PinyinTable
{
    internal static int Loads;
    readonly Dictionary<int,string[]> readings=[];
    public PinyinTable()
    {
        Interlocked.Increment(ref Loads);
        foreach(var line in File.ReadLines(Path.Combine(AppContext.BaseDirectory,"LauncherData","pinyin.tsv"))) {
            var fields=line.Split('\t');readings.Add(int.Parse(fields[0],NumberStyles.HexNumber,CultureInfo.InvariantCulture),fields[1].Split(','));
        }
    }
    public SearchName Create(string name)
    {
        var tokens=new List<NameToken>();int offset=0;
        foreach(var rune in name.EnumerateRunes()) {
            if(!Rune.IsWhiteSpace(rune)) {
                string[] variants=readings.TryGetValue(rune.Value,out var r)?r:[rune.ToString().ToLowerInvariant()];
                tokens.Add(new(offset,rune.Utf16SequenceLength,variants,variants.Select(s=>s[..1]).Distinct().ToArray()));
            }
            offset+=rune.Utf16SequenceLength;
        }
        return new(name,tokens.ToArray());
    }
    public SearchName CreateLocalized(string display, string simplified) => Create(simplified).WithDisplayName(display);
}
internal sealed record NameToken(int Offset,int Length,string[] Full,string[] Initials);
internal sealed record NameMatch(int Score,IReadOnlyList<TextSpan> Spans);
internal sealed class SearchName(string name,NameToken[] tokens)
{
    bool phoneticHighlights = true;
    // 简繁标题长度不同时不把简体位置套到显示文字上；直接文字匹配仍有正常高亮。
    public SearchName WithDisplayName(string display) => new(display, Tokens) { phoneticHighlights = display.Length == Name.Length };
    public string Name {get;}=name;
    readonly string lower=name.ToLowerInvariant();
    public NameToken[] Tokens {get;}=tokens;
    readonly NameToken[] allTokens=tokens;
    readonly NameToken[] wordInitials=tokens.Where((t,i)=>i==0||!Latin(t)||!Latin(tokens[i-1])||tokens[i-1].Offset+tokens[i-1].Length!=t.Offset).ToArray();
    static bool Latin(NameToken t)=>t.Length==1&&t.Full.Length==1&&t.Full[0].Length==1&&t.Full[0][0] is >= 'a' and <= 'z';
    public NameMatch? Match(string query)
    {
        var q=query.ToLowerInvariant();if(q.Length==0)return null;
        int i=lower.IndexOf(q,StringComparison.Ordinal);
        if(i==0)return new(100,[new(0,q.Length)]);
        var spans=Phonetic(q,true,true)??Phonetic(q,true,true,wordInitials);if(spans!=null)return new(90,phoneticHighlights?spans:[]);
        if(i>=0)return new(70,[new(i,q.Length)]);
        spans=Phonetic(q,false,true);if(spans!=null)return new(60,phoneticHighlights?spans:[]);
        spans=Phonetic(q,true,false)??Phonetic(q,true,false,wordInitials);if(spans!=null)return new(45,phoneticHighlights?spans:[]);
        spans=Phonetic(q,false,false);if(spans!=null)return new(35,phoneticHighlights?spans:[]);
        var hits=new List<TextSpan>();int j=0;
        for(int n=0;n<lower.Length&&j<q.Length;n++)if(lower[n]==q[j]){hits.Add(new(n,1));j++;}
        return j==q.Length&&q.Length>1?new(10,Merge(hits)):null;
    }
    IReadOnlyList<TextSpan>? Phonetic(string q,bool initials,bool prefix,NameToken[]? selected=null)
    {
        var tokens=selected??allTokens;
        // 按查询推进读音图，不展开多音字的笛卡尔积；失败状态合并，避免指数增长。
        var failed=new HashSet<(int,int,int)>();
        for(int start=0;start<tokens.Length;start++) {
            foreach(var reading in initials?tokens[start].Initials:tokens[start].Full)
                for(int offset=0;offset<(prefix?1:reading.Length);offset++) {
                    int take=Math.Min(reading.Length-offset,q.Length);
                    if(!q.AsSpan(0,take).SequenceEqual(reading.AsSpan(offset,take)))continue;
                    int end=take==q.Length?start:Walk(start+1,take);
                    if(end>=0)return Merge(tokens[start..(end+1)].Select(t=>new TextSpan(t.Offset,t.Length)));
                }
            if(prefix)break;
        }
        return null;
        int Walk(int index,int position) {
            if(index>=tokens.Length||!failed.Add((index,position,initials?1:0)))return -1;
            foreach(var reading in initials?tokens[index].Initials:tokens[index].Full) {
                int take=Math.Min(reading.Length,q.Length-position);
                if(!q.AsSpan(position,take).SequenceEqual(reading.AsSpan(0,take)))continue;
                if(position+take==q.Length)return index;
                int result=Walk(index+1,position+take);if(result>=0)return result;
            }
            return -1;
        }
    }
    static TextSpan[] Merge(IEnumerable<TextSpan> source)
    {
        var output=new List<TextSpan>();foreach(var s in source){if(output.Count>0&&output[^1].Start+output[^1].Length==s.Start)output[^1]=output[^1] with{Length=output[^1].Length+s.Length};else output.Add(s);}return output.ToArray();
    }
}
