param([string]$PackageManifest,[switch]$Inner,[switch]$UnitOnly,[switch]$FailureOnly)
$ErrorActionPreference='Stop'
$repo=Split-Path (Split-Path $PSScriptRoot -Parent) -Parent
$out=Join-Path $repo '.codex-runs/061'
trap { $_ | Out-String | Add-Content -LiteralPath (Join-Path $out 'driver-errors.log') -Encoding UTF8; exit 1 }
New-Item -ItemType Directory -Force $out | Out-Null
if(!$PackageManifest){$PackageManifest=Join-Path $repo 'dist/package.json'}
if(!$Inner){
    Add-Type -Path (Join-Path $repo 'experiments/027-idle/HiddenDesktop.cs')
    $arguments=@('-NoProfile','-ExecutionPolicy','Bypass','-File',$PSCommandPath,'-Inner','-PackageManifest',$PackageManifest)
    if($UnitOnly){$arguments+='-UnitOnly'}
    if($FailureOnly){$arguments+='-FailureOnly'}
    $code=[HiddenDesktop027]::Run((Get-Command powershell.exe).Source,$arguments,$repo,600000)
    if($code -ne 0){throw "061 隐藏桌面验证失败：$code，见 $out"}
    return
}
function Run-Checked([string]$Exe,[string[]]$Arguments,[string]$Log){
    & $Exe @Arguments 2>&1 | Out-File -LiteralPath (Join-Path $out $Log) -Encoding UTF8
    if($LASTEXITCODE -ne 0){throw "验证失败：$Log，退出码 $LASTEXITCODE"}
}
$appTests=Join-Path $repo 'tests/Pane.App.Tests/bin/Release/net10.0-windows/Pane.App.Tests.exe'
if($UnitOnly){Run-Checked $appTests @('--setup-only') 'unit.log';return}
$package=Get-Content -LiteralPath $PackageManifest -Raw -Encoding UTF8 | ConvertFrom-Json
$fixture=Join-Path $PSScriptRoot 'bin/Release/net10.0-windows/Pane.Setup.Tests.exe'
Run-Checked $fixture @('--061',$package.installer,$package.stage,(Join-Path $out 'hidden-061.log')) 'hidden-061-process.log'
if($FailureOnly){return}
Run-Checked $fixture @((Join-Path $out 'hidden-051.log'),(Join-Path $repo 'dist/Pane-Setup-0.1.294.exe'),$package.installer) 'hidden-051-process.log'
Run-Checked $appTests @('--update-e2e',(Join-Path $repo 'dist/Pane-Setup-0.1.323.exe'),$package.installer,(Join-Path $out 'update-e2e')) 'update-e2e.log'
Run-Checked (Get-Command powershell.exe).Source @('-NoProfile','-ExecutionPolicy','Bypass','-File',(Join-Path $repo 'tests/Pane.App.Probes/verify-setup-018.ps1'),'-PackageManifest',$PackageManifest) 'setup-018.log'
Run-Checked (Get-Command powershell.exe).Source @('-NoProfile','-ExecutionPolicy','Bypass','-File',(Join-Path $repo 'tests/Pane.App.Probes/verify-setup-registration.ps1')) 'registration.log'
Run-Checked (Get-Command powershell.exe).Source @('-NoProfile','-ExecutionPolicy','Bypass','-File',(Join-Path $repo 'tests/Pane.App.Probes/verify-release-scripts-017.ps1')) 'release-scripts.log'
