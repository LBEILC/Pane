using System.Text.Json.Nodes;

namespace Pane.Settings;

// 导出时剔除，导入时从当前文件取回；新增本机专属设置只维护这里。
internal static class LocalSettingsFields
{
    internal static readonly string[][] Paths=[["Storage"],["General","StartWithWindows"],["Launcher","Position"]];
    internal static void Apply(JsonObject target,JsonObject? local)
    {
        foreach(var path in Paths){
            JsonObject parent=target;JsonNode? source=local;
            foreach(string key in path[..^1]){
                if(parent[key] is not JsonObject)parent[key]=new JsonObject();
                parent=(JsonObject)parent[key]!;source=source?[key];
            }
            parent.Remove(path[^1]);
            if(source is JsonObject obj&&obj.TryGetPropertyValue(path[^1],out var value))parent[path[^1]]=value?.DeepClone();
        }
    }
}
