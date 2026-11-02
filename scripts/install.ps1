param([string]$Root=(Join-Path $env:LOCALAPPDATA 'Programs\Pane'),[switch]$NoBuild,[switch]$NoStart,[switch]$Rollback,[switch]$CopyDevData,[switch]$Isolated,[string]$DataDirectory,[string]$DevDataDirectory)
. "$PSScriptRoot/release-common.ps1"
$Root=Resolve-PaneRoot $Root
if(!$DataDirectory){$DataDirectory=if($Isolated){Join-Path $Root 'data'}else{Join-Path $env:APPDATA 'Pane'}}
$DataDirectory=[IO.Path]::GetFullPath($DataDirectory)
if($Isolated -and ($Root -eq (Join-Path $env:LOCALAPPDATA 'Programs\Pane') -or $DataDirectory -eq (Join-Path $env:APPDATA 'Pane'))){throw '隔离测试不能使用真实安装或数据目录'}
if(!$DevDataDirectory){$DevDataDirectory=Join-Path $repo 'src/Pane.App/bin/data'}
$DevDataDirectory=[IO.Path]::GetFullPath($DevDataDirectory)
$app=Join-Path $Root 'app';$old=Join-Path $Root 'app.old';$stage=Join-Path $Root 'app.new';$saved=Join-Path $Root 'app.previous'
foreach($path in @($app,$old,$stage,$saved)){Assert-PanePath $path $Root}
if((Test-Path -LiteralPath $stage) -or (Test-Path -LiteralPath $saved)){throw '存在未完成的安装目录，请先检查 app.new / app.previous'}
$source=Join-Path $repo 'src/Pane.App/bin/aot'
if($Rollback){if(!(Test-Path -LiteralPath "$old/Pane.App.exe")){throw '没有可回退的旧版'}}
elseif(!$NoBuild){& "$repo/src/Pane.App/scripts/build.ps1" -Mode aot;if($LASTEXITCODE -ne 0){throw '构建失败'}}
if(!$Rollback -and !(Test-Path -LiteralPath "$source/Pane.App.exe")){throw '缺少 AOT 构建，请先构建'}
if($CopyDevData -and ($DataDirectory.StartsWith($app+"\",[StringComparison]::OrdinalIgnoreCase) -or $DataDirectory -eq $app)){throw '数据目录不能放在 app 内'}
$wasRunning=Test-PaneRunning (Join-Path $app 'Pane.App.exe');$stopped=$false
$createdRoot=!(Test-Path -LiteralPath $Root);$movedCurrent=$false;$movedOld=$false;$activated=$false;$started=$null
$journal=[Collections.Generic.List[string]]::new()
$registration=Get-PaneRegistration
$registrationChanged=$false
$uninstaller=Join-Path $Root 'Uninstall Pane.exe'
Assert-PanePath $uninstaller $Root
$uninstallerBefore=if(Test-Path -LiteralPath $uninstaller){[IO.File]::ReadAllBytes($uninstaller)}else{$null}
$uninstallerChanged=$false
try {
    New-Item -ItemType Directory -Force -Path $Root|Out-Null
    if(!$Rollback){New-Item -ItemType Directory -Path $stage|Out-Null;foreach($item in Get-ChildItem -LiteralPath $source -Force){Copy-Item -LiteralPath $item.FullName -Destination $stage -Recurse};Write-Host '已复制 AOT 文件到 app.new'}
    if(!$Rollback -and !(Test-Path -LiteralPath (Join-Path $stage 'Uninstall Pane.exe'))){
        $v=[Diagnostics.FileVersionInfo]::GetVersionInfo((Join-Path $stage 'Pane.App.exe'))
        $version="$($v.FileMajorPart).$($v.FileMinorPart).$($v.FileBuildPart)"
        $helperBuild=Join-Path $repo ('src/Pane.Setup/bin/script-'+[Guid]::NewGuid().ToString('N'))
        & dotnet @('publish',(Join-Path $repo 'src/Pane.Setup/Pane.Setup.csproj'),'-c','Release','-r','win-x64',"-p:Version=$version",'-p:AssemblyName=Uninstall Pane','-o',$helperBuild)
        if($LASTEXITCODE -ne 0){throw '卸载程序构建失败'}
        Copy-Item -LiteralPath (Join-Path $helperBuild 'Uninstall Pane.exe') -Destination $stage
        Copy-Item -LiteralPath (Join-Path $helperBuild 'Uninstall Pane.exe') -Destination (Join-Path $stage 'Pane.Maintenance.exe')
    }
    if($CopyDevData){Copy-PaneData $DevDataDirectory $DataDirectory $journal}
    Invoke-PaneProcess $(if($Rollback){"$old/Pane.App.exe"}else{"$stage/Pane.App.exe"}) @('--exit') -Wait;$stopped=$true
    if(!$Rollback -and (Test-Path -LiteralPath $old)){Move-PaneTree $old $saved $Root;$movedOld=$true}
    if(Test-Path -LiteralPath $app){Move-PaneTree $app $(if($Rollback){$stage}else{$old}) $Root;$movedCurrent=$true}
    Move-PaneTree $(if($Rollback){$old}else{$stage}) $app $Root;$activated=$true
    $uninstallerChanged=$true
    if(Test-Path -LiteralPath (Join-Path $app 'Uninstall Pane.exe')){Copy-Item -LiteralPath (Join-Path $app 'Uninstall Pane.exe') -Destination $uninstaller -Force}
    elseif(Test-Path -LiteralPath $uninstaller){Remove-Item -LiteralPath $uninstaller}
    if(!$NoStart){$started=Invoke-PaneProcess "$app/Pane.App.exe" @();Start-Sleep -Seconds 2;if($started.HasExited){throw "启动失败：$($started.ExitCode)"}}
    $registrationChanged=$true;Register-PaneInstallation
    if($Rollback -and $movedCurrent){Move-PaneTree $stage $old $Root;$movedCurrent=$false}
    if($movedOld){Remove-PaneTree $saved $Root}
    Write-Host $(if($Rollback){'已回退，替换前版本保留在 app.old'}else{'安装/更新完成，上一版本保留在 app.old'})
}catch {
    $failure=$_
    if($uninstallerChanged){if($null -eq $uninstallerBefore){Remove-Item -LiteralPath $uninstaller -ErrorAction SilentlyContinue}else{[IO.File]::WriteAllBytes($uninstaller,$uninstallerBefore)}}
    if($registrationChanged){Set-PaneRegistration $registration}
    if($started -and !$started.HasExited){Invoke-PaneProcess "$app/Pane.App.exe" @('--exit') -Wait}
    if($activated){Move-PaneTree $app $(if($Rollback){$old}else{$stage}) $Root}
    if($movedCurrent){Move-PaneTree $(if($Rollback){$stage}else{$old}) $app $Root}
    if($movedOld -and (Test-Path -LiteralPath $saved)){Move-PaneTree $saved $old $Root}
    if(!$Rollback){Remove-PaneTree $stage $Root}
    for($i=$journal.Count-1;$i -ge 0;$i--){$path=$journal[$i];if(Test-Path -LiteralPath $path){Remove-Item -LiteralPath $path -Force}}
    if($createdRoot -and (Test-Path -LiteralPath $Root) -and !(Get-ChildItem -LiteralPath $Root -Force)){Remove-Item -LiteralPath $Root}
    if($stopped -and $wasRunning -and (Test-Path -LiteralPath "$app/Pane.App.exe")){$restored=Invoke-PaneProcess "$app/Pane.App.exe" @('--autostart');$restored.Dispose();Write-Host '失败后已恢复旧版运行'}
    throw $failure
}finally{if($started){$started.Dispose()}}
