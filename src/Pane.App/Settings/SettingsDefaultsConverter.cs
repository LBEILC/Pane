using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;
using Pane.Contracts;

namespace Pane.Settings;

// 内层契约只负责原生的源生成读写，不再次调用缺省值转换器。
[JsonSerializable(typeof(LauncherSettings))]
[JsonSerializable(typeof(AiSettings))]
[JsonSerializable(typeof(IconSettings))]
[JsonSerializable(typeof(RenameRule))]
[JsonSourceGenerationOptions(UseStringEnumConverter = true)]
internal partial class SettingsRecordJson : JsonSerializerContext { }

internal abstract class SettingsDefaultsConverter<T>(JsonTypeInfo<T> contract, T defaults) : JsonConverter<T>
    where T : class
{
    readonly JsonObject defaultFields = JsonSerializer.SerializeToNode(defaults, contract)!.AsObject();

    public override T Read(ref Utf8JsonReader reader, Type type, JsonSerializerOptions options)
    {
        if (JsonNode.Parse(ref reader) is not JsonObject fields) throw new JsonException();
        foreach (var (name, value) in defaultFields)
            if (!fields.ContainsKey(name)) fields.Add(name, value?.DeepClone());
        return fields.Deserialize(contract)!;
    }

    public override void Write(Utf8JsonWriter writer, T value, JsonSerializerOptions options) =>
        JsonSerializer.Serialize(writer, value, contract);
}

internal sealed class LauncherDefaultsConverter() : SettingsDefaultsConverter<LauncherSettings>(
    SettingsRecordJson.Default.LauncherSettings, new LauncherSettings(null!));

internal sealed class AiDefaultsConverter() : SettingsDefaultsConverter<AiSettings>(
    SettingsRecordJson.Default.AiSettings, new AiSettings());

internal sealed class IconDefaultsConverter() : SettingsDefaultsConverter<IconSettings>(
    SettingsRecordJson.Default.IconSettings, new IconSettings());

internal sealed class RenameRuleDefaultsConverter() : SettingsDefaultsConverter<RenameRule>(
    SettingsRecordJson.Default.RenameRule, new RenameRule(default));
