param([Parameter(Mandatory=$true)][string]$Source,[Parameter(Mandatory=$true)][string]$Output,[Parameter(Mandatory=$true)][string]$Version,[string]$Date=(Get-Date -Format 'yyyy-MM-dd'))
$ErrorActionPreference='Stop'
# 先全部校验，再写生成目录；源文件永不改写。
$notes=@();$versions=@{}
foreach($file in Get-ChildItem -LiteralPath $Source -Filter '*.json' -File){
    try {
        $note=Get-Content -LiteralPath $file.FullName -Raw -Encoding UTF8 | ConvertFrom-Json
        $next=$file.Name -eq 'next.json'
        if($next -and $note.PSObject.Properties.Name -contains 'version'){throw 'next.json 不能带 version'}
        if($null -eq $note.items -or $note.items -isnot [Array]){throw 'items 必须是数组'}
        foreach($item in $note.items){
            if($item.kind -cnotin @('new','improved','fixed')){throw '无效 kind'}
            if($item.text -isnot [string] -or [string]::IsNullOrWhiteSpace($item.text)){throw 'text 不能为空'}
        }
        if($next){
            if($note.items.Count -eq 0){continue}
            $note | Add-Member -NotePropertyName version -NotePropertyValue $Version
            $note | Add-Member -NotePropertyName date -NotePropertyValue $Date -Force
        }
        if($note.version -notmatch '^\d+\.\d+\.\d+(\.\d+)?$'){throw '无效 version'}
        $v=[version]$note.version
        $key='{0}.{1}.{2}.{3}' -f $v.Major,$v.Minor,$v.Build,[Math]::Max(0,$v.Revision)
        if($versions.ContainsKey($key)){throw "重复版本，与 $($versions[$key]) 冲突"}
        $versions[$key]=$file.Name
        $notes+=,$note
    }catch{throw "$($file.Name): $($_.Exception.Message)"}
}
New-Item -ItemType Directory -Path $Output -Force | Out-Null
# 单个汇总文件避免增量构建残留旧 next 版本。
[IO.File]::WriteAllText((Join-Path $Output 'catalog.json'),(ConvertTo-Json -InputObject @($notes) -Depth 8),[Text.UTF8Encoding]::new($false))
$current=$notes | Where-Object { [version]$_.version -eq [version]$Version } | Select-Object -First 1
$lines=@()
foreach($section in @(@('new','新增'),@('improved','改进'),@('fixed','修复'))){
    $items=@($current.items | Where-Object {$_.kind -eq $section[0]})
    if($items.Count){if($lines.Count){$lines+=''};$lines+='## '+$section[1];$lines+='';foreach($item in $items){$lines+='- '+$item.text}}
}
[IO.File]::WriteAllText((Join-Path $Output 'release-notes.md'),(($lines -join "`n")+$(if($lines.Count){"`n"})),[Text.UTF8Encoding]::new($false))
