param([string]$PackageMetadata,[string]$Report)
$ErrorActionPreference='Stop'
$repo=Split-Path (Split-Path $PSScriptRoot -Parent) -Parent
if(!$PackageMetadata){$PackageMetadata=Join-Path $repo 'dist/package.json'}
if(!$Report){$Report=Join-Path $repo 'out/payload-audit.json'}
$metadata=Get-Content -LiteralPath $PackageMetadata -Encoding UTF8 -Raw | ConvertFrom-Json
$assets=Get-Content -LiteralPath (Join-Path $repo 'src/Pane.App/obj/project.assets.json') -Encoding UTF8 -Raw | ConvertFrom-Json
$cache=@($assets.packageFolders.PSObject.Properties.Name)[0]
$candidates=@{}
foreach($library in $assets.libraries.PSObject.Properties){
    if($library.Value.type -ne 'package'){continue}
    $folder=Join-Path $cache $library.Value.path
    foreach($file in Get-ChildItem -LiteralPath $folder -Recurse -File){
        if(!$candidates.ContainsKey($file.Name)){$candidates[$file.Name]=@()}
        $candidates[$file.Name]+=,@{package=$library.Name;path=$file.FullName;length=$file.Length}
    }
}
$own=@('Pane.App.exe','Pane.Maintenance.exe','Pane.Native.dll','Pane.Effect.dll','resources.pri','Assets/pane.ico','Assets/pane-app.svg','LauncherData/pinyin.tsv','LauncherData/Unicode-LICENSE.txt')
$notices=@{'LICENSE'='LICENSE';'THIRD_PARTY_NOTICES.md'='THIRD_PARTY_NOTICES.md'}
foreach($file in Get-ChildItem -LiteralPath (Join-Path $repo 'src/Pane.App/ThirdParty') -File){$notices['ThirdParty/'+$file.Name]='src/Pane.App/ThirdParty/'+$file.Name}
$rows=@(foreach($file in Get-ChildItem -LiteralPath $metadata.stage -Recurse -File){
    $path=$file.FullName.Substring($metadata.stage.TrimEnd('\','/').Length+1).Replace('\','/')
    $hash=(Get-FileHash -LiteralPath $file.FullName -Algorithm SHA256).Hash
    $matches=@(foreach($candidate in $candidates[$file.Name]){
        if($candidate.length -eq $file.Length -and (Get-FileHash -LiteralPath $candidate.path -Algorithm SHA256).Hash -eq $hash){$candidate.package}
    })
    $matches=@($matches | Select-Object -Unique)
    if($notices.ContainsKey($path)){
        if((Get-FileHash -LiteralPath (Join-Path $repo $notices[$path]) -Algorithm SHA256).Hash -ne $hash){throw "许可文件不一致：$path"}
        $kind='notice'
    } elseif($matches.Count){$kind='package'} elseif($own -contains $path){$kind='application-or-data'}else{throw "未归属的 payload 文件：$path"}
    [pscustomobject]@{path=$path;sha256=$hash;kind=$kind;packages=$matches}
})
foreach($path in $notices.Keys){if($rows.path -notcontains $path){throw "payload 缺少许可证：$path"}}
# 核对真正传给安装外壳的 ZIP，并确认其字节完整嵌入最终安装程序。
$zipPath=Join-Path (Split-Path $metadata.stage -Parent) 'payload.zip'
Add-Type -AssemblyName System.IO.Compression.FileSystem
$archive=[IO.Compression.ZipFile]::OpenRead($zipPath)
$sha=[Security.Cryptography.SHA256]::Create()
try {
    if($archive.Entries.Count -ne $rows.Count){throw 'ZIP 文件数与 payload 不一致'}
    foreach($entry in $archive.Entries){
        $stream=$entry.Open()
        try{$hash=[BitConverter]::ToString($sha.ComputeHash($stream)).Replace('-','')}finally{$stream.Dispose()}
        $row=@($rows | Where-Object {$_.path -eq $entry.FullName.Replace('\','/')})
        if($row.Count -ne 1 -or $row[0].sha256 -ne $hash){throw "ZIP 内容不一致：$($entry.FullName)"}
    }
    $zip=[IO.File]::ReadAllBytes($zipPath);$installer=[IO.File]::ReadAllBytes($metadata.installer)
    $zipHash=[BitConverter]::ToString($sha.ComputeHash($zip));$found=$false
    for($offset=0;$offset -le $installer.Length-$zip.Length;$offset++){
        if($installer[$offset] -eq 80 -and $installer[$offset+1] -eq 75 -and $installer[$offset+2] -eq 3 -and $installer[$offset+3] -eq 4){
            if([BitConverter]::ToString($sha.ComputeHash($installer,$offset,$zip.Length)) -eq $zipHash){$found=$true;break}
        }
    }
    if(!$found){throw '安装程序中未找到完整 payload ZIP'}
} finally {$archive.Dispose();$sha.Dispose()}
New-Item -ItemType Directory -Force -Path (Split-Path ([IO.Path]::GetFullPath($Report)) -Parent)|Out-Null
[pscustomobject]@{version=$metadata.version;files=$rows.Count;embeddedZipVerified=$true;entries=$rows} | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath $Report -Encoding UTF8
Write-Host "payload=$($rows.Count); licenses=$($notices.Count); embeddedZipVerified=True"
$rows | ForEach-Object {$_.packages} | Group-Object | ForEach-Object {Write-Host "$($_.Name): $($_.Count)"}
