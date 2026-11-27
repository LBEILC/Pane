param(
    [Parameter(Mandatory=$true)][ValidatePattern('^\d+\.\d+\.\d+$')][string]$Version,
    [switch]$FakeServer,
    [string]$Artifacts
)
$ErrorActionPreference='Stop'
$repo=Split-Path $PSScriptRoot -Parent
if(!$Artifacts){$Artifacts=Join-Path $repo 'dist'}
$arguments=@('run','--project',(Join-Path $repo 'tests/Pane.App.Tests/Pane.App.Tests.csproj'),'-c','Release','--','--verify-release',$Version,[IO.Path]::GetFullPath($Artifacts))
# 有本地便携包时，假服务器也提供完整附件，核对下载、解压及隔离启动。
if($FakeServer){$arguments+='--fake-server'}
& dotnet @arguments
if($LASTEXITCODE -ne 0){throw '发布核对失败'}
# 发布核对同时覆盖发布脚本的卸载登记与旧版迁移，全部使用隔离目录。
& (Join-Path $repo "tests/Pane.App.Probes/verify-setup-registration.ps1")
