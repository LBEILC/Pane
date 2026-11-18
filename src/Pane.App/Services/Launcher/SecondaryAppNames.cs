using System.Diagnostics;
namespace Pane.Services.Launcher;

// 辅助程序和次要名字的排除规则集中在此；目录扫描仍沿用原有辅助程序范围。
internal static class SecondaryAppNames
{
    static readonly string[] UninstallParts=["uninstall","unins0","卸载","卸載"];
    static readonly string[] AuxiliaryParts=["帮助","幫助","說明","readme"," help"];
    static readonly string[] PortableParts=["setup","install","update","updater","crash","crashpad","helper","reporter"];
    static readonly HashSet<string> Generic=new(["launcher","update","updater","setup","uninstall","unins000","app","main","start","run","client","helper"],StringComparer.OrdinalIgnoreCase);
    static bool Help(string name)=>AuxiliaryParts.Any(p=>name.Contains(p,StringComparison.OrdinalIgnoreCase))||name.StartsWith("help",StringComparison.OrdinalIgnoreCase);
    static bool UninstallerFile(string name)
    {
        string stem=name.ToLowerInvariant();
        return stem is "uninst" or "uninstall" or "uninstaller"
            ||stem.StartsWith("unins",StringComparison.Ordinal)&&stem.Length>5&&stem.AsSpan(5).IndexOfAnyExceptInRange('0','9')<0
            ||stem.Contains("卸载")||stem.Contains("卸載")
            ||new[]{' ','_','-'}.Any(c=>stem.StartsWith("uninstall"+c,StringComparison.Ordinal)||stem.EndsWith(c+"uninstall",StringComparison.Ordinal)||stem.EndsWith(c+"uninstaller",StringComparison.Ordinal));
    }
    internal static bool Auxiliary(string name,string? target=null,IReadOnlySet<string>? registered=null)
    {
        string file=Path.GetFileName(target)??"";
        string combined=name+" "+file;
        if(Help(combined)||UninstallerFile(Path.GetFileNameWithoutExtension(file)))return true;
        if(!UninstallParts.Any(p=>combined.Contains(p,StringComparison.OrdinalIgnoreCase)))return false;
        return file.Length==0||file.Equals("msiexec.exe",StringComparison.OrdinalIgnoreCase)
            ||UninstallMatching.Normalize(target,false) is {} path&&registered?.Contains(path)==true;
    }
    internal static bool PortableAuxiliary(string name)
    {
        // install 与 uninstall 重叠，其他安装、更新和帮助程序仍按旧片段过滤。
        string remaining=name.Replace("uninstall"," ",StringComparison.OrdinalIgnoreCase);
        return Help(name)||UninstallerFile(name)||PortableParts.Any(p=>remaining.Contains(p,StringComparison.OrdinalIgnoreCase));
    }

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
