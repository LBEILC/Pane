param([string]$OldPackage,[string]$NewPackage)
$ErrorActionPreference='Stop'
$repo=Split-Path (Split-Path $PSScriptRoot -Parent) -Parent
if(!$OldPackage){$OldPackage=Join-Path $repo 'dist/Pane-Setup-0.1.294.exe'}
if(!$NewPackage){$NewPackage=(Get-Content -LiteralPath (Join-Path $repo 'dist/package.json') -Raw -Encoding UTF8 | ConvertFrom-Json).installer}
& dotnet @('build',(Join-Path $PSScriptRoot 'Pane.Setup.Tests.csproj'),'-c','Release','-p:NuGetAudit=false')
if($LASTEXITCODE -ne 0){throw '卸载链路测试构建失败'}
Add-Type -Path (Join-Path $repo 'experiments/027-idle/HiddenDesktop.cs')
$out=Join-Path $repo '.codex-runs/051'
New-Item -ItemType Directory -Force $out | Out-Null
$log=Join-Path $out 'hidden.log'
$code=[HiddenDesktop027]::Run((Join-Path $PSScriptRoot 'bin/Release/net10.0-windows/Pane.Setup.Tests.exe'),@($log,$OldPackage,$NewPackage),$repo,240000)
Get-Content -LiteralPath $log -Encoding UTF8
if($code -ne 0){throw "隐藏桌面卸载验证失败：$code"}
