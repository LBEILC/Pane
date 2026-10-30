param([string]$PackageManifest)
$ErrorActionPreference='Stop'
$repo=Split-Path (Split-Path $PSScriptRoot -Parent) -Parent
if(!$PackageManifest){$PackageManifest=Join-Path $repo 'dist/package.json'}
$package=Get-Content -LiteralPath $PackageManifest -Raw -Encoding UTF8 | ConvertFrom-Json
. "$repo/scripts/release-common.ps1"
$Root=Join-Path ([IO.Path]::GetTempPath()) ('Pane-018-e2e-'+[Guid]::NewGuid().ToString('N'))
$DataDirectory=Join-Path $Root 'data';$Isolated=$true;$helper=$null
function Run-Installer([string]$Exe,[string[]]$Arguments){
    $child=Start-Process -FilePath $Exe -ArgumentList $Arguments -WindowStyle Hidden -PassThru
    $null=$child.Handle
    try {if(!$child.WaitForExit(60000)){throw '安装进程超时'};if($child.ExitCode -ne 0){throw "安装进程失败 $($child.ExitCode)"}}finally{if(!$child.HasExited){$child.Kill();$child.WaitForExit()};$child.Dispose()}
}
try {
    New-Item -ItemType Directory -Path $Root | Out-Null
    Run-Installer $package.installer @('--self-test-root',('"{0}"' -f $Root))
    $extracted=[IO.File]::ReadAllText((Join-Path $Root 'installed.txt')).Trim()
    if(Test-Path -LiteralPath $extracted){throw '安装外壳未清理临时文件'}
    if(!(Test-Path -LiteralPath "$Root/menu/Pane.lnk") -or !(Test-Path -LiteralPath "$Root/uninstall.json")){throw '缺少假系统集成输出'}
    Write-Host 'PASS wrapper: extracted -> setup headless -> installed -> exited -> temporary removed'
    $installRoot=[IO.Path]::GetFullPath((Join-Path $Root 'custom tools/Pane'))
    $exe=Join-Path $installRoot 'app/Pane.App.exe'
    if(!(Test-Path -LiteralPath $exe)){throw '自定义安装未完成'}
    $registration=Get-Content -LiteralPath "$Root/uninstall.json" -Raw -Encoding UTF8 | ConvertFrom-Json
    if($registration.InstallLocation -ne $installRoot){throw '登记位置不匹配'}
    Run-Installer $package.installer @('--self-test-root',('"{0}"' -f $Root))
    if(!(Test-Path -LiteralPath "$installRoot/app.old/Pane.App.exe")){throw '升级未保留旧版'}
    Write-Host 'PASS custom install -> upgrade; registered custom location; app.old preserved'
    Run-Installer $exe @('--uninstall','--self-test-root',('"{0}"' -f $Root))
    $helperInfo=Get-Content -LiteralPath "$Root/helper.txt" -Encoding UTF8
    $helper=$helperInfo[0]
    $relay=Get-Process -Id ([int]$helperInfo[1]) -ErrorAction SilentlyContinue
    if($relay){try{if(!$relay.WaitForExit(60000)){throw '卸载助手超时'}}finally{$relay.Dispose()}}
    if(!(Test-Path -LiteralPath "$Root/uninstalled.txt") -or (Test-Path -LiteralPath $installRoot)){throw '卸载未完成'}
    $uninstallTemp=[IO.File]::ReadAllText("$Root/uninstalled.txt").Trim()
    if(Test-Path -LiteralPath $uninstallTemp){throw '卸载临时应用副本未删除'}
    Write-Host "PASS relocated uninstall; appFiles=0; temporaryAppFiles=0; helperRemains=$(Test-Path -LiteralPath $helper)"
    if(Get-ChildItem -LiteralPath $Root -Filter '*error.txt'){throw '自测存在错误记录'}
    if(!(Test-Path -LiteralPath (Split-Path $installRoot -Parent))){throw '父目录被删除'}
    Write-Host 'PASS isolated setup; realRegistryWrites=0; realShortcuts=0; realClipboardReads=0'
}finally {
    foreach($file in Get-ChildItem -LiteralPath $Root -Filter '*error.txt' -ErrorAction SilentlyContinue){Get-Content -LiteralPath $file.FullName -Encoding UTF8}
    if(Test-Path -LiteralPath "$DataDirectory/logs"){Get-ChildItem -LiteralPath "$DataDirectory/logs" -Filter 'crash-*.txt' | ForEach-Object {Get-Content -LiteralPath $_.FullName -Encoding UTF8}}
    if($helper){
        $resolved=[IO.Path]::GetFullPath($helper)
        if([IO.Path]::GetDirectoryName($resolved) -ne [IO.Path]::GetTempPath().TrimEnd('\') -or [IO.Path]::GetFileName($resolved) -notmatch '^Pane-Cleanup-[0-9a-f]{32}\.exe$'){throw '助手清理路径无效'}
        Remove-Item -LiteralPath $resolved -Force -ErrorAction SilentlyContinue
    }
    $checkedRoot=Resolve-PaneRoot $Root
    if(!$checkedRoot.StartsWith([IO.Path]::GetTempPath(),[StringComparison]::OrdinalIgnoreCase) -or [IO.Path]::GetFileName($checkedRoot) -notlike 'Pane-018-e2e-*'){throw '清理根目录无效'}
    Remove-PaneTree $checkedRoot ([IO.Path]::GetTempPath().TrimEnd('\'))
}
