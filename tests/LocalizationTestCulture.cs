using System.Runtime.CompilerServices;

internal static class LocalizationTestCulture
{
    [ModuleInitializer]
    internal static void Initialize() => Pane.Localization.Strings.Initialize("zh-Hans");
}
