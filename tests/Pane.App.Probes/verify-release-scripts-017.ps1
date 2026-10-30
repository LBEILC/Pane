$ErrorActionPreference='Stop'
$repo=Split-Path (Split-Path $PSScriptRoot -Parent) -Parent
. "$repo/scripts/release-common.ps1"
$testRoot=Join-Path ([IO.Path]::GetTempPath()) ('Pane-017-'+[Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $testRoot|Out-Null
$installRoot=Join-Path $testRoot 'installation'
$fakeDev=Join-Path $testRoot 'fake-dev'
$checks=0
function Check([bool]$Condition,[string]$Name){if(!$Condition){throw "验证失败：$Name"};$script:checks++;Write-Host "通过：$Name"}
try {
    $startupExe=[IO.Path]::GetFullPath((Join-Path $installRoot 'app/Pane.App.exe'))
    Check (Test-PaneStartupCommand ('"'+$startupExe+'" --autostart') "$installRoot/app/Pane.App.exe") '卸载识别当前路径和不同分隔符'
    Check (!(Test-PaneStartupCommand '"C:\Other\Pane.App.exe" --autostart' $startupExe)) '卸载保留其他安装的启动项'
    New-Item -ItemType Directory -Path "$fakeDev/clipboard" -Force|Out-Null
    [IO.File]::WriteAllText("$fakeDev/settings.json",'{"fake":true}')
    [IO.File]::WriteAllText("$fakeDev/gestures.json",'{"fake":true}')
    [IO.File]::WriteAllText("$fakeDev/clipboard/fixture.txt",'自造剪贴板测试数据')
    $image=[IO.Path]::GetFullPath((Join-Path $fakeDev 'clipboard/image.png'))
    $entry=@{Entry=@{Text=$image;Files=@($image);ContentPath=$image;Thumbnail=@{Path=$image};SourceIcon=@{ImagePath=$image}}}
    [IO.File]::WriteAllText("$fakeDev/clipboard/history.json",(ConvertTo-Json -InputObject @($entry) -Depth 10))
    [IO.File]::WriteAllText("$fakeDev/launcher-usage.json",'{}')
    $destination=Join-Path $testRoot 'migration'
    New-Item -ItemType Directory -Path "$destination/clipboard" -Force|Out-Null
    [IO.File]::WriteAllText("$destination/clipboard/fixture.txt",'保留已有假数据')
    $journal=[Collections.Generic.List[string]]::new()
    Copy-PaneData $fakeDev $destination $journal
    Check ([IO.File]::ReadAllText("$destination/clipboard/fixture.txt") -eq '保留已有假数据') '迁移不覆盖已有历史文件'
    Check ((Test-Path "$destination/settings.json") -and (Test-Path "$destination/launcher-usage.json")) '迁移设置与使用记录'
    $migrated=(ConvertFrom-Json -InputObject ([IO.File]::ReadAllText("$destination/clipboard/history.json")))[0].Entry
    Check ($migrated.ContentPath -eq ([IO.Path]::GetFullPath((Join-Path $destination 'clipboard/image.png'))) -and $migrated.Thumbnail.Path -eq $migrated.ContentPath -and $migrated.SourceIcon.ImagePath -eq $migrated.ContentPath -and $migrated.Text -eq $image -and $migrated.Files[0] -eq $image) '图片索引重定位且正文和文件列表不变'
    [IO.File]::WriteAllText("$fakeDev/gestures.json",'changed')
    Copy-PaneData $fakeDev $destination $journal
    Check ([IO.File]::ReadAllText("$destination/gestures.json") -eq '{"fake":true}') '已有 settings 时整个迁移跳过'

    & "$repo/scripts/install.ps1" -Root $installRoot -NoBuild -NoStart -Isolated
    Check ((Test-Path "$installRoot/app/Pane.App.exe") -and !(Test-Path "$installRoot/app.old")) '首次安装'
    [IO.File]::WriteAllText("$installRoot/app/version-fixture.txt",'version1')
    & "$repo/scripts/install.ps1" -Root $installRoot -NoBuild -NoStart -Isolated
    Check ((Test-Path "$installRoot/app.old/version-fixture.txt") -and !(Test-Path "$installRoot/app/version-fixture.txt")) '更新保存旧版'
    [IO.File]::WriteAllText("$installRoot/app/version-fixture.txt",'version2')

    $locked=[IO.File]::Open("$repo/src/Pane.App/bin/aot/Assets/pane.ico",[IO.FileMode]::Open,[IO.FileAccess]::Read,[IO.FileShare]::None)
    $failed=$false
    try {& "$repo/scripts/install.ps1" -Root $installRoot -NoBuild -NoStart -Isolated}catch{$failed=$true;Write-Host "预期复制失败：$($_.Exception.GetType().Name)"}finally{$locked.Dispose()}
    Check ($failed -and !(Test-Path "$installRoot/app.new") -and [IO.File]::ReadAllText("$installRoot/app/version-fixture.txt") -eq 'version2' -and [IO.File]::ReadAllText("$installRoot/app.old/version-fixture.txt") -eq 'version1') '复制中断后当前版和旧版都完整'

    & "$repo/scripts/install.ps1" -Root $installRoot -Rollback -NoStart -Isolated
    Check ([IO.File]::ReadAllText("$installRoot/app/version-fixture.txt") -eq 'version1' -and [IO.File]::ReadAllText("$installRoot/app.old/version-fixture.txt") -eq 'version2') '回退交换两个版本'
    New-Item -ItemType Directory -Path "$installRoot/data" -Force|Out-Null
    [IO.File]::WriteAllText("$installRoot/data/keep.txt",'假数据')
    & "$repo/scripts/uninstall.ps1" -Root $installRoot -Isolated
    Check (!(Test-Path "$installRoot/app") -and !(Test-Path "$installRoot/app.old") -and (Test-Path "$installRoot/data/keep.txt")) '卸载默认保留数据'
    & "$repo/scripts/uninstall.ps1" -Root $installRoot -Isolated -RemoveData
    Check (!(Test-Path $installRoot)) '显式 RemoveData 删除测试数据'
    Write-Host "PASS release-script assertions=$checks; registryWrites=0; realDataWrites=0"
}finally {Remove-PaneTree $testRoot ([IO.Path]::GetTempPath().TrimEnd('\'))}
