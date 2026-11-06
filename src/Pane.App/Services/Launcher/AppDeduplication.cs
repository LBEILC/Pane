using System.Security.Cryptography;
using System.Text;
namespace Pane.Services.Launcher;

internal sealed record CatalogEntry(string Name,string Id,string Target,string Args,string Parsing,int Source,string LegacyId,string IconResource,int IconIndex);
internal sealed record CatalogGroup(CatalogEntry[] Rows,string StableId,string[] MergedIds);
internal sealed record CatalogEvidence(string Name,string Key,string[] Names,string Resource,int ResourceIndex,int Width,int Height,bool Direct);
[System.Text.Json.Serialization.JsonSerializable(typeof(List<CatalogEvidence>))]
internal partial class CatalogEvidenceJson:System.Text.Json.Serialization.JsonSerializerContext { }
internal static class AppDeduplication
{
    // 路径与 AUMID 不区分大小写；参数可能是令牌、配置名或 URL，必须逐字比较。
    sealed class EntryKeyComparer:IEqualityComparer<string>{
        static int Split(string key){int i=key.IndexOf('\n');return i<0?key.Length:i;}
        public bool Equals(string? x,string? y){
            if(x==null||y==null)return x==y;int a=Split(x),b=Split(y);
            return x.AsSpan(0,a).Equals(y.AsSpan(0,b),StringComparison.OrdinalIgnoreCase)&&x.AsSpan(a).SequenceEqual(y.AsSpan(b));
        }
        public int GetHashCode(string key){int split=Split(key);return HashCode.Combine(string.GetHashCode(key.AsSpan(0,split),StringComparison.OrdinalIgnoreCase),string.GetHashCode(key.AsSpan(split),StringComparison.Ordinal));}
    }
    static readonly IEqualityComparer<string> Keys=new EntryKeyComparer();
    // 稳定 Id 沿用 013 的输入和哈希规则，去重键独立规范化。
    public static string Identity(string id,string target,string args,string parsing)=>
        id.Contains('!')||target.Length==0?(id.Length>0?id:parsing):Environment.ExpandEnvironmentVariables(target).Trim().ToUpperInvariant()+"\n"+args;
    public static string StableId(string identity)=>"app:"+Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(identity)));
    static string PreviousKey(CatalogEntry row)=>row.Id.Contains('!')?"package:"+row.Id:
        row.Target.Length>0?"target:"+Environment.ExpandEnvironmentVariables(row.Target).Trim()+"\n"+row.Args:"shell:"+(row.Id.Length>0?row.Id:row.Parsing);
    static bool IsExecutable(string path){path=Environment.ExpandEnvironmentVariables(path).Trim();return Path.IsPathFullyQualified(path)&&Path.GetExtension(path).Equals(".exe",StringComparison.OrdinalIgnoreCase);}
    public static string Key(CatalogEntry row)=>row.Id.Contains('!')&&!IsExecutable(row.Target)?"package:"+row.Id+(row.Args.Length>0?"\n"+row.Args:""):
        row.Target.Length>0?"target:"+Environment.ExpandEnvironmentVariables(row.Target).Trim()+"\n"+row.Args:"shell:"+(row.Id.Length>0?row.Id:row.Parsing)+(row.Args.Length>0?"\n"+row.Args:"");
    // 相同 AUMID 是跨来源证据；有多个目标或参数时不猜测缺失的入口。
    internal static CatalogEntry[] Prepare(IEnumerable<CatalogEntry> entries){
        var rows=entries.Select(r=>r.Target.Length==0&&IsExecutable(r.Id)?r with{Target=r.Id}:r).ToArray();
        var links=rows.Where(r=>r.Id.Length>0).GroupBy(r=>r.Id,StringComparer.OrdinalIgnoreCase)
            .Where(g=>g.Any(r=>r.Source==0)&&g.Any(r=>r.Source==1))
            .ToDictionary(g=>g.Key,g=>g.Where(r=>r.Target.Length>0).ToArray(),StringComparer.OrdinalIgnoreCase);
        return rows.Select(r=>{
            if(r.Target.Length>0||!links.TryGetValue(r.Id,out var matches))return r;
            var targets=matches.Where(m=>r.Args.Length==0||r.Args==m.Args).GroupBy(Key,Keys).ToArray();
            return targets.Length==1?r with{Target=targets[0].First().Target,Args=targets[0].First().Args}:r;
        }).ToArray();
    }
    public static CatalogEntry[][] Groups(IEnumerable<CatalogEntry> rows)=>Group(Prepare(rows),Key);
    static CatalogEntry[][] OriginalGroups(IEnumerable<CatalogEntry> rows,Func<CatalogEntry,string> key,IEqualityComparer<string>? comparer=null)=>rows.GroupBy(key,comparer??Keys)
        // AppsFolder 和快捷方式名称均来自 Shell；优先 AppsFolder 的系统本地化名称。
        .Select(g=>g.OrderBy(r=>r.Source).ToArray()).ToArray();
    static CatalogEntry[][] Group(IEnumerable<CatalogEntry> rows,Func<CatalogEntry,string> key){
        var result=new List<List<CatalogEntry>>();
        var targets=new Dictionary<string,Dictionary<string,List<CatalogEntry>>>(StringComparer.OrdinalIgnoreCase);
        foreach(var original in OriginalGroups(rows,key)){
            var first=original[0];List<CatalogEntry>? destination=null;
            // 以原分组的显示入口为准；Shell 和没有完整 exe 目标的包仍按原规则分组。
            if(Key(first).StartsWith("target:",StringComparison.Ordinal)){
                string target=Environment.ExpandEnvironmentVariables(first.Target).Trim();
                if(!targets.TryGetValue(target,out var names))targets[target]=names=new(StringComparer.OrdinalIgnoreCase);
                if(!names.TryGetValue(first.Name,out destination)){
                    destination=[];names.Add(first.Name,destination);result.Add(destination);
                }
            }
            if(destination==null){destination=[];result.Add(destination);}
            destination.AddRange(original);
        }
        // 稳定排序：同来源沿用原分组顺序，组内沿用枚举顺序，不改启动参数。
        return result.Select(g=>g.OrderBy(r=>r.Source).ToArray()).ToArray();
    }
    public static CatalogGroup[] Resolve(IEnumerable<CatalogEntry> entries){
        var rows=entries.ToArray();
        var prepared=Prepare(rows);
        // 先重现 053 的稳定 Id，再合并，避免新规则改变旧 Id 的所有权判定。
        var current=ResolveCore(prepared,Key);
        var membership=new Dictionary<CatalogEntry,CatalogGroup>();
        foreach(var group in current)foreach(var row in group.Rows)membership[row]=group;
        var groups=Group(prepared,Key).Select(rowsInGroup=>{
            string id=membership[rowsInGroup[0]].StableId;
            var oldGroups=rowsInGroup.Select(r=>membership[r]).Distinct();
            return new CatalogGroup(rowsInGroup,id,oldGroups.SelectMany(g=>g.MergedIds.Prepend(g.StableId)).Where(old=>old!=id).Distinct().ToArray());
        }).ToArray();
        // 同时覆盖 053 以前的 Id，包括旧参数冲突时的 Shell 身份回退。
        var previous=ResolveCore(rows,PreviousKey,StringComparer.OrdinalIgnoreCase);
        var retained=groups.SelectMany(g=>g.MergedIds.Prepend(g.StableId)).ToHashSet();
        var positions=new Dictionary<CatalogEntry,int>();for(int i=0;i<rows.Length;i++)positions.TryAdd(rows[i],i);
        var destinations=new Dictionary<CatalogEntry,string>();foreach(var group in groups)foreach(var row in group.Rows)destinations[row]=group.StableId;
        var extra=new Dictionary<string,List<string>>();
        foreach(var old in previous){
            if(!retained.Add(old.StableId))continue;
            string id=destinations[prepared[positions[old.Rows[0]]]];
            if(!extra.TryGetValue(id,out var ids))extra[id]=ids=[];
            ids.Add(old.StableId);
        }
        return groups.Select(g=>g with{MergedIds=g.MergedIds.Concat(extra.GetValueOrDefault(g.StableId)??[]).Distinct().ToArray()}).ToArray();
    }
    static CatalogGroup[] ResolveCore(CatalogEntry[] rows,Func<CatalogEntry,string> keyOf,IEqualityComparer<string>? comparer=null){
        comparer??=Keys;
        // 旧枚举丢失 AppsFolder 参数：同一个旧 Id 可能出现在多个新分组中。
        // 它只属于旧版最先保留的条目，不能把一个使用记录迁移到几个不同入口。
        var owners=rows.GroupBy(r=>r.LegacyId).ToDictionary(g=>g.Key,g=>keyOf(g.First()));
        return OriginalGroups(rows,keyOf,comparer).Select(group=>{
            var app=group[0];string key=keyOf(app);
            var owned=group.Where(r=>comparer.Equals(owners[r.LegacyId],key)).Select(r=>r.LegacyId).Distinct().ToArray();
            string id=owned.FirstOrDefault()??StableId(Identity(app.Id,app.Target,app.Args,app.Parsing));
            // 原先被错误去重而从未入库的无参数入口，目标哈希可能已属于另一个入口。
            // 沿用 Shell 身份回退和 SHA256 规则生成新 Id，不重分配任何已存在的 Id。
            if(owned.Length==0&&owners.TryGetValue(id,out var owner)&&!comparer.Equals(owner,key))id=StableId(Identity(app.Id,"","",app.Parsing));
            return new CatalogGroup(group,id,owned.Where(old=>old!=id).ToArray());
        }).ToArray();
    }
    public static bool MergeUsage(Dictionary<string,AppUse> uses,IReadOnlyDictionary<string,string> aliases){
        bool changed=false;foreach(var (oldId,id) in aliases){if(oldId==id||!uses.Remove(oldId,out var old))continue;var current=uses.GetValueOrDefault(id);uses[id]=new((int)Math.Min(int.MaxValue,(long)(current?.Count??0)+old.Count),current!=null&&current.LastUsed>old.LastUsed?current.LastUsed:old.LastUsed);changed=true;}return changed;
    }
}
