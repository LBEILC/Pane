# 提交数只写入文件版本，不参与发布号计算。
function Get-NextPaneVersion([version]$Previous, $Items, [string]$VersionOverride) {
    if ($VersionOverride) {
        if ($VersionOverride -notmatch '^\d+\.\d+\.\d+$' -or [version]$VersionOverride -le $Previous) {
            throw '手动版本必须是高于上次发布的三段版本号'
        }
        return ([version]$VersionOverride).ToString()
    }
    if ($Previous.Major -eq 0) { return '1.0.0' }
    if ($Items.kind -contains 'new' -or $Items.kind -contains 'improved') {
        return [version]::new($Previous.Major, $Previous.Minor + 1, 0).ToString()
    }
    return [version]::new($Previous.Major, $Previous.Minor, $Previous.Build + 1).ToString()
}

function Get-PaneReleaseVersion([string]$NotesDirectory, [string]$VersionOverride) {
    $previous = Get-ChildItem -LiteralPath $NotesDirectory -File -Filter '*.json' |
        Where-Object { $_.BaseName -match '^\d+\.\d+\.\d+$' } |
        ForEach-Object { [version]$_.BaseName } | Sort-Object -Descending | Select-Object -First 1
    if (!$previous) { $previous = [version]'0.0.0' }
    $next = Get-Content -LiteralPath (Join-Path $NotesDirectory 'next.json') -Raw -Encoding UTF8 | ConvertFrom-Json
    Get-NextPaneVersion $previous $next.items $VersionOverride
}
