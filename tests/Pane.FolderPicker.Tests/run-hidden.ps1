$ErrorActionPreference='Stop'
$repo=Split-Path (Split-Path $PSScriptRoot -Parent) -Parent
& dotnet @('build',(Join-Path $PSScriptRoot 'Pane.FolderPicker.Tests.csproj'),'-c','Release','-p:NuGetAudit=false')
if($LASTEXITCODE -ne 0){throw '文件夹选择框测试构建失败'}
Add-Type -Path (Join-Path $repo 'experiments/027-idle/HiddenDesktop.cs')
$out=Join-Path $repo '.codex-runs/035'
New-Item -ItemType Directory -Force $out | Out-Null
$log=Join-Path $out 'folder-picker.log'
$code=[HiddenDesktop027]::Run((Join-Path $PSScriptRoot 'bin/Release/net10.0-windows/Pane.FolderPicker.Tests.exe'),@($log),$repo,30000)
Get-Content -LiteralPath $log
if($code -ne 0){throw "隐藏桌面选择框验证失败：$code"}
