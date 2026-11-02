param()
$ErrorActionPreference='Stop'
$repo=Split-Path (Split-Path $PSScriptRoot -Parent) -Parent
$fixture=Join-Path $repo ('.codex-runs/049/scan-fixture-'+[Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $fixture -Force | Out-Null
$code=@"
// var comment = "注释";
/* "块注释" */
var shown = "界面文字";
var kept = "保留默认值"; // 不翻译：用户数据
var interpolated = `$"数量：{value /* 中文注释 */}";
var verbatim = @"跨行
中文";
var escaped = "\u4E2D\u6587";
var marker = "// 不翻译：只是字符串";
"@
[IO.File]::WriteAllText((Join-Path $fixture 'sample.cs'),$code)
[IO.File]::WriteAllText((Join-Path $fixture 'sample.xaml'),'<Grid><!-- Text="注释" --><TextBlock Text="中文属性" /></Grid>')
New-Item -ItemType Directory -Path (Join-Path $fixture 'DesignData'),(Join-Path $fixture 'Localization') | Out-Null
[IO.File]::WriteAllText((Join-Path $fixture 'DesignData/sample.cs'),'var s = "演示";')
[IO.File]::WriteAllText((Join-Path $fixture 'Localization/sample.cs'),'var s = "资源";')
$hits=@(& "$repo/src/Pane.App/scripts/scan-strings.ps1" -Root $fixture -AsObject)
$checks=0
function Check([bool]$ok,[string]$name){$script:checks++;if(!$ok){throw $name}}
Check ($hits.Count -eq 6) '只报告六个字串，不报告注释、保留行和排除目录'
Check (@($hits|Where-Object {$_.Line -eq 3 -and $_.Content -eq '"界面文字"'}).Count -eq 1) '普通字串和行号'
Check (@($hits|Where-Object {$_.Content -match '保留默认值'}).Count -eq 0) '不翻译标记'
Check (@($hits|Where-Object {$_.Content -match '数量'}).Count -eq 1) '插值字串'
Check (@($hits|Where-Object {$_.Content -match '跨行'}).Count -eq 1) '逐字多行字串'
Check (@($hits|Where-Object {$_.Content -match 'u4E2D'}).Count -eq 1) '转义中文'
Check (@($hits|Where-Object {$_.Content -match '只是字符串'}).Count -eq 1) '字串中的注释标记不排除'
Check (@($hits|Where-Object {$_.File -eq 'sample.xaml' -and $_.Line -eq 1}).Count -eq 1) 'XAML 属性及注释'
Write-Output "PASS string scanner assertions=$checks; fixtures=$fixture"
