using System.Globalization;
using System.Xml.Linq;

namespace Pane.Hosting;

internal sealed record NativeCrashEvent(DateTime Utc, string RecordId, string Code, string Module, string Offset)
{
    public static NativeCrashEvent? Match(string xml, CrashSession session)
    {
        try
        {
            var root = XElement.Parse(xml);
            XNamespace ns = "http://schemas.microsoft.com/win/2004/08/events/event";
            var system = root.Element(ns + "System");
            string? id = system?.Element(ns + "EventID")?.Value;
            string? provider = (string?)system?.Element(ns + "Provider")?.Attribute("Name");
            if (!(id == "1000" && provider == "Application Error" || id == "1001" && provider == "Windows Error Reporting")) return null;
            var fields = root.Element(ns + "EventData")?.Elements(ns + "Data")
                .Where(e => e.Attribute("Name") != null).ToDictionary(e => (string)e.Attribute("Name")!, e => e.Value);
            if (fields == null) return null;
            string Get(string name) => fields.GetValueOrDefault(name, "");
            // System/Execution 的 PID 是事件提供者，不能代替故障进程。
            // 常见 1001 只有 P1…P10，缺少会话身份时绝不按应用名猜测。
            if (!Number(Get("ProcessId"), out ulong pid) || pid != (ulong)session.ProcessId ||
                !Number(Get("ProcessCreationTime"), out ulong time) || time != (ulong)session.CreationTime ||
                !string.Equals(Get("AppName"), session.Executable, StringComparison.OrdinalIgnoreCase)) return null;
            if (!DateTime.TryParse((string?)system?.Element(ns + "TimeCreated")?.Attribute("SystemTime"),
                CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var utc) ||
                utc < DateTime.FromFileTimeUtc(session.CreationTime)) return null;
            string record = system?.Element(ns + "EventRecordID")?.Value ?? "";
            if (!ulong.TryParse(record, out _)) return null;
            return new(utc, record, Clean(Get("ExceptionCode")), FileName(Get("ModuleName")), Clean(Get("FaultingOffset")));
        }
        catch (System.Xml.XmlException) { return null; }
        catch (ArgumentException) { return null; }
    }
    static bool Number(string text, out ulong value) => text.StartsWith("0x", StringComparison.OrdinalIgnoreCase)
        ? ulong.TryParse(text.AsSpan(2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out value)
        : ulong.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out value);
    static string Clean(string text) => text.Replace('\r', ' ').Replace('\n', ' ');
    static string FileName(string text) => Clean(text.Replace('\\', '/').Split('/')[^1]);
    public string Format(CrashSession session) =>
        $"UTC: {Utc:O}\nVersion: {session.Version}\nRuntime: {session.Runtime}\nSession: {session.Identity}\nType: 原生崩溃（未捕获）\nExceptionCode: {Code}\nModule: {Module}\nOffset: {Offset}\nEventRecordID: {RecordId}\n";
}
