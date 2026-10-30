param(
    [Parameter(Mandatory=$true)][ValidatePattern('^\d+\.\d+\.\d+$')][string]$Version,
    [switch]$FakeServer,
    [string]$Artifacts
)
$ErrorActionPreference='Stop'
$repo=Split-Path $PSScriptRoot -Parent
if(!$Artifacts){$Artifacts=Join-Path $repo 'dist'}
$arguments=@('run','--project',(Join-Path $repo 'tests/Pane.App.Tests/Pane.App.Tests.csproj'),'-c','Release','--','--verify-release',$Version,[IO.Path]::GetFullPath($Artifacts))
if($FakeServer){$arguments+='--fake-server'}
& dotnet @arguments
if($LASTEXITCODE -ne 0){throw '发布核对失败'}
