$ErrorActionPreference='Stop'
$repo=Split-Path $PSScriptRoot -Parent
function Resolve-PaneRoot([string]$Path){
    $full=[IO.Path]::GetFullPath($Path).TrimEnd('\')
    if($full -eq [IO.Path]::GetPathRoot($full).TrimEnd('\')){throw '不能把磁盘根目录作为安装目录'}
    if($full -eq [IO.Path]::GetFullPath($repo).TrimEnd('\')){throw '不能把仓库根目录作为安装目录'}
    $ancestor=$full
    while($ancestor){
        if((Test-Path -LiteralPath $ancestor) -and ((Get-Item -LiteralPath $ancestor -Force).Attributes -band [IO.FileAttributes]::ReparsePoint)){throw "安装目录经过链接：$ancestor"}
        $ancestor=Split-Path $ancestor -Parent
    }
    return $full
}
function Assert-PanePath([string]$Path,[string]$Parent){
    $full=[IO.Path]::GetFullPath($Path)
    if(!$full.StartsWith($Parent.TrimEnd('\')+'\',[StringComparison]::OrdinalIgnoreCase)){throw "路径越界：$full"}
    $part=$full
    while($part -and $part.Length -ge $Parent.Length){
        if(Test-Path -LiteralPath $part){if((Get-Item -LiteralPath $part -Force).Attributes -band [IO.FileAttributes]::ReparsePoint){throw "不处理链接目录：$part"}}
        $part=Split-Path $part -Parent
    }
}
function Remove-PaneTree([string]$Path,[string]$Parent){
    Assert-PanePath $Path $Parent
    if(Test-Path -LiteralPath $Path){
        foreach($item in Get-ChildItem -LiteralPath $Path -Recurse -Force){if($item.Attributes -band [IO.FileAttributes]::ReparsePoint){throw '不递归处理链接'}}
        Remove-Item -LiteralPath $Path -Recurse -Force
    }
}
function Move-PaneTree([string]$From,[string]$To,[string]$Parent){
    Assert-PanePath $From $Parent;Assert-PanePath $To $Parent
    Move-Item -LiteralPath $From -Destination $To
}
function Invoke-PaneProcess([string]$Exe,[string[]]$Arguments,[switch]$Wait){
    $names=@('PANE_DATA_DIR','PANE_INSTANCE','PANE_RESULT','PANE_DEMO','PANE_OBSERVE','PANE_VERIFY','PANE_DEV_HOTKEYS','PANE_BIN_DIR')
    $saved=@{}
    try {
        foreach($name in $names){$saved[$name]=[Environment]::GetEnvironmentVariable($name);[Environment]::SetEnvironmentVariable($name,$null)}
        if($Isolated){$env:PANE_DATA_DIR=$DataDirectory;$env:PANE_INSTANCE=$Root;$env:PANE_OBSERVE='1';$env:PANE_DEV_HOTKEYS='launcher=Ctrl+Alt+Shift+L;clipboard=Ctrl+Alt+Shift+C'}
        $options=@{FilePath=$Exe;WindowStyle='Hidden';PassThru=$true}
        if($Arguments.Count){$options.ArgumentList=$Arguments}
        $process=Start-Process @options
        $null=$process.Handle
        if($Wait){try {if(!$process.WaitForExit(12000)){throw '退出请求超时'};if($process.ExitCode -ne 0){throw "退出失败：$($process.ExitCode)"}}finally{$process.Dispose()}}
        else {return $process}
    }finally{foreach($name in $names){[Environment]::SetEnvironmentVariable($name,$saved[$name])}}
}
function Copy-PaneData([string]$Source,[string]$Destination,$Journal){
    if(Test-Path -LiteralPath (Join-Path $Destination 'settings.json')){Write-Host '已有正式设置，跳过开发数据迁移';return}
    foreach($name in @('settings.json','gestures.json','launcher-usage.json','clipboard')){
        $sourcePath=Join-Path $Source $name
        if(!(Test-Path -LiteralPath $sourcePath)){continue}
        $files=if((Get-Item -LiteralPath $sourcePath).PSIsContainer){@(Get-ChildItem -LiteralPath $sourcePath -Recurse -File)}else{@(Get-Item -LiteralPath $sourcePath)}
        foreach($file in $files){
            Assert-PanePath $file.FullName $Source
            $relative=$file.FullName.Substring($Source.TrimEnd('\').Length+1);$target=Join-Path $Destination $relative
            Assert-PanePath $target $Destination
            if(Test-Path -LiteralPath $target){continue}
            $parent=Split-Path $target -Parent
            $missing=@();while(!(Test-Path -LiteralPath $parent)){$missing+=,$parent;$parent=Split-Path $parent -Parent}
            for($i=$missing.Count-1;$i -ge 0;$i--){New-Item -ItemType Directory -Path $missing[$i]|Out-Null;$Journal.Add($missing[$i])}
            $Journal.Add($target);Copy-Item -LiteralPath $file.FullName -Destination $target
        }
    }
    # 历史索引存的是绝对路径；只重定位本次复制的图片和来源图标字段，正文与用户文件列表原样保留。
    $history=[IO.Path]::GetFullPath((Join-Path $Destination 'clipboard/history.json'))
    if($Journal.Contains($history)){
        $entries=ConvertFrom-Json -InputObject ([IO.File]::ReadAllText($history))
        $oldPrefix=(Join-Path $Source 'clipboard').TrimEnd('\')+'\'
        $newPrefix=(Join-Path $Destination 'clipboard').TrimEnd('\')+'\'
        foreach($row in $entries){
            foreach($field in @(@($row.Entry,'ContentPath'),@($row.Entry.Thumbnail,'Path'),@($row.Entry.SourceIcon,'ImagePath'))){
                $object=$field[0];$property=$field[1]
                if($null -eq $object){continue}
                $value=$object.$property
                if($value -is [string] -and $value.StartsWith($oldPrefix,[StringComparison]::OrdinalIgnoreCase)){$object.$property=$newPrefix+$value.Substring($oldPrefix.Length)}
            }
        }
        $serialized=@(foreach($row in $entries){ConvertTo-Json -InputObject $row -Depth 100})
        [IO.File]::WriteAllText($history,('['+($serialized -join ',')+']'),[Text.UTF8Encoding]::new($false))
    }
}

function Test-PaneRunning([string]$Exe){
    $processes=[Diagnostics.Process]::GetProcessesByName('Pane.App')
    try {foreach($process in $processes){try{if([string]::Equals($process.MainModule.FileName,$Exe,[StringComparison]::OrdinalIgnoreCase)){return $true}}catch [ComponentModel.Win32Exception]{}catch [InvalidOperationException]{}};return $false}
    finally{foreach($process in $processes){$process.Dispose()}}
}
function Test-PaneStartupCommand([object]$Value,[string]$Exe){
    if($Value -isnot [string]){return $false}
    if($Value -match '^"([^"]+)"(?:\s|$)' -or $Value -match '^(\S+)(?:\s|$)'){
        try {return [string]::Equals([IO.Path]::GetFullPath($Matches[1]),[IO.Path]::GetFullPath($Exe),[StringComparison]::OrdinalIgnoreCase)}catch{return $false}
    }
    return $false
}

# 脚本安装与图形安装使用同一个卸载登记位置；隔离测试只写文件。
function Get-PaneRegistration {
    if($Isolated){
        $file=Join-Path $Root 'uninstall.json'
        if(Test-Path -LiteralPath $file){return [IO.File]::ReadAllText($file)}
        return $null
    }
    $key=[Microsoft.Win32.Registry]::CurrentUser.OpenSubKey('Software\Microsoft\Windows\CurrentVersion\Uninstall\Pane')
    if(!$key){return $null}
    try {$fields=@{};foreach($name in $key.GetValueNames()){$fields[$name]=$key.GetValue($name)};return $fields}
    finally {$key.Dispose()}
}
function Set-PaneRegistration($Fields) {
    if($Isolated){
        $file=Join-Path $Root 'uninstall.json'
        if($null -eq $Fields){if(Test-Path -LiteralPath $file){Remove-Item -LiteralPath $file};return}
        $json=if($Fields -is [string]){$Fields}else{$Fields | ConvertTo-Json}
        [IO.File]::WriteAllText($file,$json,[Text.UTF8Encoding]::new($false));return
    }
    $name='Software\Microsoft\Windows\CurrentVersion\Uninstall\Pane'
    [Microsoft.Win32.Registry]::CurrentUser.DeleteSubKeyTree($name,$false)
    if($null -eq $Fields){return}
    $key=[Microsoft.Win32.Registry]::CurrentUser.CreateSubKey($name)
    try {foreach($field in $Fields.Keys){$key.SetValue($field,$Fields[$field])}}
    finally {$key.Dispose()}
}
function Register-PaneInstallation {
    $exe=Join-Path $Root 'app/Pane.App.exe'
    $version=[Diagnostics.FileVersionInfo]::GetVersionInfo($exe)
    $bytes=(Get-ChildItem -LiteralPath (Join-Path $Root 'app') -File -Recurse | Measure-Object Length -Sum).Sum
    Set-PaneRegistration @{
        DisplayName='Pane';DisplayVersion="$($version.FileMajorPart).$($version.FileMinorPart).$($version.FileBuildPart)";Publisher='Pane'
        DisplayIcon=$exe;InstallLocation=$Root;EstimatedSize=[int][Math]::Min([int]::MaxValue,[Math]::Ceiling($bytes/1024))
        UninstallString=('"{0}" --uninstall' -f $exe);NoModify=1;NoRepair=1
    }
}
