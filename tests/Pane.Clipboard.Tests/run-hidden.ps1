$ErrorActionPreference='Stop'
$repo=Split-Path (Split-Path $PSScriptRoot -Parent) -Parent
Add-Type -Path (Join-Path $repo 'experiments/027-idle/HiddenDesktop.cs')
$out=Join-Path $repo '.codex-runs/039'
New-Item -ItemType Directory -Force $out | Out-Null
$code=[HiddenDesktop027]::Run((Join-Path $PSScriptRoot 'bin/Release/net10.0-windows/Pane.Clipboard.Tests.exe'),@('--hidden-paste',(Join-Path $out 'hidden.log')),$repo,15000)
Get-Content -LiteralPath (Join-Path $out 'hidden.log')
if($code -ne 0){throw "隐藏桌面测试失败：$code"}
