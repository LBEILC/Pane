$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot '../scripts/release-version.ps1')
function Assert-NextVersion($Previous, $Kinds, $Override, $Expected) {
    $items = @($Kinds | ForEach-Object { [pscustomobject]@{kind = $_} })
    $actual = Get-NextPaneVersion $Previous $items $Override
    if ($actual -ne $Expected) { throw "上版 $Previous 更新 $Kinds 预期 $Expected，实际 $actual" }
}
Assert-NextVersion '0.1.814' @('new') '' '1.0.0'
Assert-NextVersion '1.0.0' @('fixed') '' '1.0.1'
Assert-NextVersion '1.0.1' @('fixed','improved') '' '1.1.0'
Assert-NextVersion '1.0.0' @('new') '' '1.1.0'
Assert-NextVersion '1.1.0' @('fixed') '2.0.0' '2.0.0'
Assert-NextVersion '1.1.0' @() '' '1.1.1'
Write-Host 'PASS release version (6 cases)'
