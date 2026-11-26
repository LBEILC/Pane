using System.Diagnostics.CodeAnalysis;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization.Metadata;
using Pane.Settings;
using Pane.Services.Cloud;

internal static partial class Program
{
    static async Task RestoreLauncher153()
    {
        var root = Directory.CreateTempSubdirectory("Pane-153-");
        try
        {
            var store = new SettingsStore(root.FullName, Defaults());
            store.Load();
            static bool AllOn(LauncherSettings l) =>
                l.RememberWebSearches && l.RememberOpenedPrograms && l.SearchSystemSettings && l.LearnSelections;
            Check(AllOn(store.Current.Launcher) && store.Current.LauncherSwitchesRestored, "全新安装");
            var path = Path.Combine(root.FullName, "settings.json");
            var json = JsonNode.Parse(File.ReadAllText(path))!.AsObject();
            json.Remove(nameof(PaneSettings.LauncherSwitchesRestored));
            json.Remove(nameof(PaneSettings.LauncherSwitchesRestoredAt));
            foreach (string key in new[] { "RememberWebSearches", "RememberOpenedPrograms", "SearchSystemSettings", "LearnSelections" })
                json["Launcher"]![key] = false;
            File.WriteAllText(path, json.ToJsonString());
            store.Load();
            var disk = JsonSerializer.Deserialize(File.ReadAllText(path), SettingsJson.Default.PaneSettings)!;
            Check(AllOn(store.Current.Launcher) && AllOn(disk.Launcher) && disk.LauncherSwitchesRestored &&
                disk.LauncherSwitchesRestoredAt > 0, "四个显式关闭的开关已恢复并写回");
            await using var account = new AccountService(root.FullName, "1", new Fake());
            await using var sync = new SyncService(store, account);
            Check(await sync.RestoredSwitchWins153(), "首次同步采用恢复时间，后续较新的关闭仍生效");
            store.Save(disk with { Launcher = disk.Launcher with { LearnSelections = false } });
            store.Load();
            Check(!store.Current.Launcher.LearnSelections && store.Current.LauncherSwitchesRestoredAt == disk.LauncherSwitchesRestoredAt,
                "恢复后再次关闭并重读");
        }
        finally { root.Delete(true); }
    }
    [UnconditionalSuppressMessage("Trimming", "IL2070", Justification = "仅在未裁剪的测试入口反射设置模型。")]
    [UnconditionalSuppressMessage("Trimming", "IL2072", Justification = "仅在未裁剪的测试入口构造值类型零值。")]
    static void MissingSettingDefaults153()
    {
        var visited = new HashSet<Type>();
        var failures = new List<string>();
        int fields = 0;
        void Visit(Type type)
        {
            if (!visited.Add(type)) return;
            if (type.IsArray) { Visit(type.GetElementType()!); return; }
            if (type.IsGenericType)
                foreach (var argument in type.GetGenericArguments()) Visit(argument);
            var raw = SettingsRecordJson.Default.GetTypeInfo(type);
            var info = SettingsJson.Default.GetTypeInfo(type) ?? raw;
            var shape = raw ?? info;
            if (shape?.Kind != JsonTypeInfoKind.Object || info == null) return;

            // 必填构造参数没有声明默认值，使用其类型零值；可选参数使用声明值。
            var ctor = type.GetConstructors().Single();
            var parameters = ctor.GetParameters().Select(p => p.HasDefaultValue ? p.DefaultValue :
                p.ParameterType.IsValueType ? Activator.CreateInstance(p.ParameterType) : null).ToArray();
            var defaults = JsonSerializer.SerializeToNode(ctor.Invoke(parameters), info)!.AsObject();
            foreach (var prop in shape.Properties)
            {
                if (prop.Get == null || !defaults.ContainsKey(prop.Name)) continue;
                Visit(prop.PropertyType);
                if (prop.Set == null) continue;
                var sample = defaults.DeepClone().AsObject();
                sample.Remove(prop.Name);
                var result = JsonSerializer.SerializeToNode(sample.Deserialize(info), info)!;
                fields++;
                if (!JsonNode.DeepEquals(defaults[prop.Name], result[prop.Name]))
                    failures.Add($"{type.Name}.{prop.Name}");
            }
        }
        Visit(typeof(PaneSettings));
        Console.WriteLine($"逐字段缺省检查 {fields} 项");
        Check(failures.Count == 0, string.Join(", ", failures));
    }
}

namespace Pane.Services.Cloud
{
    internal sealed partial class SyncService
    {
        // 走真实的采集、解密和按字段合并路径，远端只提供合成记录。
        internal async Task<bool> RestoredSwitchWins153()
        {
            const string path = "Launcher.LearnSelections";
            byte[] key = new byte[32];
            var options = new Pane.Contracts.SyncOptions(Usage: false, Notes: false);
            var journal = new SyncJournal();
            Capture(journal, key, "local", options);
            long at = store.Current.LauncherSwitchesRestoredAt;
            string id = SyncCrypto.Id(key, "settings", path);
            var record = journal.Records[id];
            if ((long?)record.Body?["at"] != at || !record.Dirty ||
                journal.Settings.ContainsKey(nameof(PaneSettings.LauncherSwitchesRestored)) ||
                journal.Settings.ContainsKey(nameof(PaneSettings.LauncherSwitchesRestoredAt))) return false;
            var patch = new JsonObject();
            async Task ReceiveFalse(long time, long seq)
            {
                var body = new JsonObject { ["v"]=1, ["k"]=path, ["val"]=false, ["at"]=time };
                var wire = new JsonObject { ["id"]=id, ["c"]="settings", ["seq"]=seq, ["dev"]="remote",
                    ["data"]=SyncCrypto.Encrypt(key, "settings", id, body.ToJsonString()) };
                await Receive(journal, wire, key, "local", options, patch, default, initial:seq == 1);
            }
            await ReceiveFalse(at - 1, 1);
            if (patch.Count != 0 || !record.Dirty || (bool?)record.Body?["val"] != true) return false;
            await ReceiveFalse(at + 1, 2);
            return (bool?)patch[path] == false;
        }
    }
}