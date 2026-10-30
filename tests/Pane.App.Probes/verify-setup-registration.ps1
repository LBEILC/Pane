param()
$ErrorActionPreference='Stop'
. "$PSScriptRoot/../../scripts/release-common.ps1"
$Root=Resolve-PaneRoot (Join-Path ([IO.Path]::GetTempPath()) ('Pane-018-script-'+[Guid]::NewGuid().ToString('N')))
$Isolated=$true
try {
    New-Item -ItemType Directory -Path (Join-Path $Root 'app') | Out-Null
    # 只借用已构建测试程序的版本资源，绝不启动它。
    Copy-Item -LiteralPath "$repo/tests/Pane.App.Tests/bin/Release/net10.0-windows/Pane.App.Tests.exe" -Destination (Join-Path $Root 'app/Pane.App.exe')
    $before=Get-PaneRegistration
    Register-PaneInstallation
    $fields=Get-PaneRegistration | ConvertFrom-Json
    if($fields.InstallLocation -ne $Root -or $fields.UninstallString -ne ('"{0}\app\Pane.App.exe" --uninstall' -f $Root)){throw '登记错误'}
    Set-PaneRegistration $before
    if(Test-Path -LiteralPath (Join-Path $Root 'uninstall.json')){throw '快照还原错误'}
    Register-PaneInstallation
    Remove-Item -LiteralPath (Join-Path $Root 'app/Pane.App.exe')
    & "$repo/scripts/uninstall.ps1" -Root $Root -Isolated
    if(Test-Path -LiteralPath $Root){throw '脚本卸载残留'}
    Write-Host 'PASS script registration: custom Root, command, rollback, isolated uninstall; realRegistryWrites=0'
} finally {
    if(Test-Path -LiteralPath $Root){
        if(!$Root.StartsWith([IO.Path]::GetTempPath(),[StringComparison]::OrdinalIgnoreCase) -or [IO.Path]::GetFileName($Root) -notlike 'Pane-018-script-*'){throw '清理路径错误'}
        Remove-PaneTree $Root ([IO.Path]::GetTempPath().TrimEnd('\'))
    }
}
