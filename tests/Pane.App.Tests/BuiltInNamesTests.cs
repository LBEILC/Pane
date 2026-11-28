using Pane.Localization;
using Pane.Settings;

// 内置 AI 动作和搜索站点的名字存的是简体：没改过名时按界面语言显示，改过的照原样（2026-11-28）
static class BuiltInNamesTests
{
    static int checks;
    static void Check(bool ok,string name){checks++;if(!ok)throw new Exception(name);}
    internal static int Run()
    {
        var before=Strings.Culture;
        try
        {
            var ai=new AiSettings();
            var engines=new LauncherSettings(new(KeyModifiers.Alt,32)).SearchEngines;
            Strings.Culture=System.Globalization.CultureInfo.GetCultureInfo("zh-Hant");
            Check(ai.Actions.Select(a=>a.Label).SequenceEqual(["翻譯","總結","潤色","解釋"]),"traditional action labels");
            Check(engines.Select(e=>e.Label).SequenceEqual(["Bing","Google","百度","GitHub","嗶哩嗶哩"]),"traditional engine labels");
            Check(ai.Actions[1] with{Name="摘要"} is {Label:"摘要"},"renamed action kept");
            Check(new SearchEngine("必应","x","https://example.com/?q={query}").Label=="必应","custom keyword kept");
            var json=System.Text.Json.JsonSerializer.Serialize(ai.Actions[1],SettingsJson.Default.AiActionSettings);
            Check(!json.Contains("Label")&&System.Text.Json.JsonSerializer.Deserialize(json,SettingsJson.Default.AiActionSettings)!.Name=="总结","stored name unchanged");
            Strings.Culture=System.Globalization.CultureInfo.GetCultureInfo("zh-Hans");
            Check(ai.Actions.Select(a=>a.Label).SequenceEqual(ai.Actions.Select(a=>a.Name)),"simplified labels equal stored names");
            Check(engines.Select(e=>e.Label).SequenceEqual(engines.Select(e=>e.Name)),"simplified engine labels equal stored names");
        }
        finally{Strings.Culture=before;}
        return checks;
    }
}
