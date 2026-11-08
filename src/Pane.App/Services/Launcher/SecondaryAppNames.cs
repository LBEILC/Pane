using System.Diagnostics;
namespace Pane.Services.Launcher;

// 辅助程序和次要名字的排除规则集中在此；目录扫描仍沿用原有辅助程序范围。
internal static class SecondaryAppNames
{
    static readonly string[] AuxiliaryParts=["uninstall","unins0","卸载","卸載","帮助","幫助","說明","readme"," help"];
    static readonly string[] PortableParts=["setup","install","update","updater","crash","crashpad","helper","reporter"];
    static readonly HashSet<string> Generic=new(["launcher","update","updater","setup","uninstall","unins000","app","main","start","run","client","helper"],StringComparer.OrdinalIgnoreCase);
    internal static bool Auxiliary(string name)=>AuxiliaryParts.Any(p=>name.Contains(p,StringComparison.OrdinalIgnoreCase))||name.StartsWith("help",StringComparison.OrdinalIgnoreCase);
    internal static bool PortableAuxiliary(string name)=>Auxiliary(name)||PortableParts.Any(p=>name.Contains(p,StringComparison.OrdinalIgnoreCase));

    internal static SearchName[] Collect(PinyinTable table,string display,string? target,bool packaged,Func<string,string?>? description=null)
    {
        if(packaged||string.IsNullOrWhiteSpace(target)||!Path.GetExtension(target).Equals(".exe",StringComparison.OrdinalIgnoreCase))return [];
        string? text=null;
        try{text=(description??ReadDescription)(target);}
        catch(Exception e) when(e is IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception){}
        return new[]{Path.GetFileNameWithoutExtension(target),text??""}.Select(n=>n.Trim())
            .Where(n=>n.Length>0&&!n.Equals(display.Trim(),StringComparison.OrdinalIgnoreCase)&&!PortableAuxiliary(n)&&!Generic.Contains(n))
            .Distinct(StringComparer.OrdinalIgnoreCase).Select(table.Create).ToArray();
    }
    // 仅 WindowsAppCatalog 构建阶段读取；搜索只访问已经分词的快照。
    static string? ReadDescription(string target)=>FileVersionInfo.GetVersionInfo(target).FileDescription;
}
