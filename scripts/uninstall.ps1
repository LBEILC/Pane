param([string]$Root=(Join-Path $env:LOCALAPPDATA 'Programs\Pane'),[switch]$RemoveData,[switch]$Isolated,[string]$DataDirectory)
. "$PSScriptRoot/release-common.ps1"
$Root=Resolve-PaneRoot $Root
if(!$DataDirectory){$DataDirectory=if($Isolated){Join-Path $Root 'data'}else{Join-Path $env:APPDATA 'Pane'}}
$DataDirectory=Resolve-PaneRoot $DataDirectory
if($Isolated -and ($Root -eq (Join-Path $env:LOCALAPPDATA 'Programs\Pane') -or $DataDirectory -eq (Join-Path $env:APPDATA 'Pane'))){throw '隔离测试不能使用真实安装或数据目录'}
$exe=Join-Path $Root 'app/Pane.App.exe'
if(Test-Path -LiteralPath $exe){Invoke-PaneProcess $exe @('--exit') -Wait}
if(!$Isolated){
    $key=[Microsoft.Win32.Registry]::CurrentUser.OpenSubKey('Software\Microsoft\Windows\CurrentVersion\Run',$true)
    try {if($key){$value=$key.GetValue('Pane');if(Test-PaneStartupCommand $value $exe){$key.DeleteValue('Pane');Write-Host '已删除本安装位置的启动项'}}}finally{if($key){$key.Dispose()}}
}
# 只删除安装器管理的子目录，数据默认保留。
$registration=Get-PaneRegistration
$location=if($registration -is [string]){($registration | ConvertFrom-Json).InstallLocation}elseif($registration){$registration['InstallLocation']}
if($location -and [string]::Equals([IO.Path]::GetFullPath($location),$Root,[StringComparison]::OrdinalIgnoreCase)){Set-PaneRegistration $null}
foreach($name in @('app','app.old')){Remove-PaneTree (Join-Path $Root $name) $Root}
if($RemoveData){Remove-PaneTree $DataDirectory (Split-Path $DataDirectory -Parent);Write-Host '已删除数据目录'}
if((Test-Path -LiteralPath $Root) -and !(Get-ChildItem -LiteralPath $Root -Force)){Remove-Item -LiteralPath $Root}
Write-Host '卸载完成'
