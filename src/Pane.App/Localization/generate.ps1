param([Parameter(Mandatory)][string]$Output)
$ErrorActionPreference='Stop'
[xml]$resources=Get-Content -LiteralPath "$PSScriptRoot/Strings.resx" -Encoding UTF8 -Raw
$lines=@('// 自动从 Strings.resx 生成；请修改资源文件。','namespace Pane.Localization;','public static partial class Strings','{')
foreach($entry in $resources.root.data){
    $name=[string]$entry.name
    if($name -cnotmatch '^[A-Z][A-Za-z0-9]*(_[A-Z][A-Za-z0-9]*)+$'){throw "无效资源键：$name"}
    $lines+='    public static string '+$name+' => manager.GetString(nameof('+$name+'), Culture)!;'
}
$lines+='}'
[IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($Output))|Out-Null
[IO.File]::WriteAllLines($Output,$lines,[Text.UTF8Encoding]::new($false))
