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
    // 稳定 Id 沿用 013 的输入和哈希规则，去重键独立规范化。
    public static string Identity(string id,string target,string args,string parsing)=>
        id.Contains('!')||target.Length==0?(id.Length>0?id:parsing):Environment.ExpandEnvironmentVariables(target).Trim().ToUpperInvariant()+"\n"+args;
    public static string StableId(string identity)=>"app:"+Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(identity)));
    public static string Key(CatalogEntry row)=>row.Id.Contains('!')?"package:"+row.Id:
        row.Target.Length>0?"target:"+Environment.ExpandEnvironmentVariables(row.Target).Trim()+"\n"+row.Args:"shell:"+(row.Id.Length>0?row.Id:row.Parsing);
    public static CatalogEntry[][] Groups(IEnumerable<CatalogEntry> rows)=>rows.GroupBy(Key,StringComparer.OrdinalIgnoreCase)
        // AppsFolder 和快捷方式名称均来自 Shell；优先 AppsFolder 的系统本地化名称。
        .Select(g=>g.OrderBy(r=>r.Source).ToArray()).ToArray();
    public static CatalogGroup[] Resolve(IEnumerable<CatalogEntry> entries){
        var rows=entries.ToArray();
        // 旧枚举丢失 AppsFolder 参数：同一个旧 Id 可能出现在多个新分组中。
        // 它只属于旧版最先保留的条目，不能把一个使用记录迁移到几个不同入口。
        var owners=rows.GroupBy(r=>r.LegacyId).ToDictionary(g=>g.Key,g=>Key(g.First()));
        return Groups(rows).Select(group=>{
            var app=group[0];string key=Key(app);
            var owned=group.Where(r=>StringComparer.OrdinalIgnoreCase.Equals(owners[r.LegacyId],key)).Select(r=>r.LegacyId).Distinct().ToArray();
            string id=owned.FirstOrDefault()??StableId(Identity(app.Id,app.Target,app.Args,app.Parsing));
            // 原先被错误去重而从未入库的无参数入口，目标哈希可能已属于另一个入口。
            // 沿用 Shell 身份回退和 SHA256 规则生成新 Id，不重分配任何已存在的 Id。
            if(owned.Length==0&&owners.TryGetValue(id,out var owner)&&!StringComparer.OrdinalIgnoreCase.Equals(owner,key))id=StableId(Identity(app.Id,"","",app.Parsing));
            return new CatalogGroup(group,id,owned.Where(old=>old!=id).ToArray());
        }).ToArray();
    }
    public static bool MergeUsage(Dictionary<string,AppUse> uses,IReadOnlyDictionary<string,string> aliases){
        bool changed=false;foreach(var (oldId,id) in aliases){if(oldId==id||!uses.Remove(oldId,out var old))continue;var current=uses.GetValueOrDefault(id);uses[id]=new((int)Math.Min(int.MaxValue,(long)(current?.Count??0)+old.Count),current!=null&&current.LastUsed>old.LastUsed?current.LastUsed:old.LastUsed);changed=true;}return changed;
    }
}
