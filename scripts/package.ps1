param([switch]$NoBuild,[string]$PolicyPath,[switch]$SkipPatchHistory,[string]$VersionOverride)
$ErrorActionPreference='Stop'
$repo=Split-Path $PSScriptRoot -Parent
. (Join-Path $PSScriptRoot 'release-version.ps1')
$version=Get-PaneReleaseVersion (Join-Path $repo 'src/Pane.App/ReleaseNotes') $VersionOverride
. (Join-Path $PSScriptRoot 'forbidden-scan.ps1')
# 公开副本不携带内部政策；在副本打包时须显式提供政策文件。
if(!$PolicyPath){$PolicyPath=Join-Path $repo 'docs/internal.md'}
$rules=@(Read-ForbiddenRules $PolicyPath)
$dotnet=(Get-Command dotnet.exe -ErrorAction Stop).Source
. (Join-Path $PSScriptRoot 'remove-tree.ps1')
$setupBin=Join-Path $repo 'src/Pane.Setup/bin'
$work=Join-Path $setupBin ('package-'+[Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $work -Force | Out-Null
# 失败时整个临时目录留作现场，写明原因；成功打包时只保留最近 3 次失败。
trap {
    if($work -and (Test-Path -LiteralPath $work)){
        [IO.File]::WriteAllText((Join-Path $work 'FAILED.txt'),"$([DateTime]::Now.ToString('s'))`r`n$($_ | Out-String)",[Text.UTF8Encoding]::new($false))
        Write-Host "打包失败，现场保留在：$work"
    }
    break
}
$previousCl=$env:CL
$previousLink=$env:_LINK_
try {
$env:CL=("$previousCl /Brepro").Trim()
$env:_LINK_=("$previousLink /Brepro").Trim()
if($NoBuild){
    $last=Join-Path $repo 'dist/package.json'
    $source=if(Test-Path -LiteralPath $last){(Get-Content -LiteralPath $last -Encoding UTF8 -Raw | ConvertFrom-Json).source}else{Join-Path $repo 'src/Pane.App/bin/aot'}
}
else {
    # 每次发布到新目录，防止旧元包留下的 SDK 文件混入组件包输出。
    & "$repo/src/Pane.App/scripts/build.ps1" -Mode aot -VersionOverride $VersionOverride -OutputRoot "$work/app-build"
    if($LASTEXITCODE -ne 0){throw '应用构建失败'}
    $source=Join-Path $work 'app-build/aot'
}
if(!(Test-Path -LiteralPath "$source/Pane.App.exe")){throw '缺少 AOT 构建'}
$v=[Diagnostics.FileVersionInfo]::GetVersionInfo("$source/Pane.App.exe")
$builtVersion="$($v.FileMajorPart).$($v.FileMinorPart).$($v.FileBuildPart)"
if($builtVersion -ne $version){throw "应用版本 $builtVersion 与待打包版本 $version 不一致，请重新构建"}
$stage=Join-Path $work 'payload'
New-Item -ItemType Directory -Path $stage | Out-Null
foreach($file in Get-ChildItem -LiteralPath $source -File -Recurse){
    if($file.Extension -eq '.pdb' -or $file.Name -eq 'release-notes.md'){continue}
    $relative=$file.FullName.Substring($source.TrimEnd('\').Length+1)
    $target=Join-Path $stage $relative
    New-Item -ItemType Directory -Path (Split-Path $target -Parent) -Force | Out-Null
    Copy-Item -LiteralPath $file.FullName -Destination $target
}
foreach($notice in @('LICENSE','LICENSE-EXCEPTION.md','THIRD_PARTY_NOTICES.md')){Copy-Item -LiteralPath (Join-Path $repo $notice) -Destination $stage}
Copy-Item -LiteralPath (Join-Path $repo 'src/Pane.App/ThirdParty') -Destination $stage -Recurse
# 小助手不嵌入 payload；卸载时负责搬移应用、等待退出及清理临时应用副本。
& $dotnet @('publish',"$repo/src/Pane.Setup/Pane.Setup.csproj",'-c','Release','-r','win-x64',"-p:Version=$version",'-p:AssemblyName=Pane.Maintenance','-o',"$work/helper")
if($LASTEXITCODE -ne 0){throw '卸载助手构建失败'}
Copy-Item -LiteralPath "$work/helper/Pane.Maintenance.exe" -Destination $stage
& $dotnet @('publish',"$repo/src/Pane.Setup/Pane.Setup.csproj",'-c','Release','-r','win-x64',"-p:Version=$version",'-p:AssemblyName=Uninstall Pane','-o',"$work/uninstaller")
if($LASTEXITCODE -ne 0){throw '独立卸载程序构建失败'}
Copy-Item -LiteralPath "$work/uninstaller/Uninstall Pane.exe" -Destination $stage
Add-Type -AssemblyName System.IO.Compression.FileSystem
$zip=Join-Path $work 'payload.zip'
[IO.Compression.ZipFile]::CreateFromDirectory($stage,$zip,[IO.Compression.CompressionLevel]::Optimal,$false)
& $dotnet @('publish',"$repo/src/Pane.Setup/Pane.Setup.csproj",'-c','Release','-r','win-x64',"-p:Version=$version",'-p:AssemblyName=Pane.Setup',"-p:PayloadPath=$zip",'-o',"$work/wrapper")
if($LASTEXITCODE -ne 0){throw '安装外壳构建失败'}
# 扫描安装外壳及未压缩 payload，避免压缩掩盖泄漏；通过后才写发布目录。
Assert-ForbiddenFiles -Files (@("$work/wrapper/Pane.Setup.exe")+@(Get-ChildItem -LiteralPath $stage -File -Recurse | ForEach-Object {$_.FullName})) -Rules $rules
$dist=Join-Path $repo 'dist'
New-Item -ItemType Directory -Path $dist -Force | Out-Null
Copy-Item -LiteralPath (Join-Path $source 'release-notes.md') -Destination (Join-Path $dist 'release-notes.md') -Force
Assert-ForbiddenFiles -Files @((Join-Path $dist 'release-notes.md')) -Rules $rules
$installer=Join-Path $dist "Pane 安装 $version.exe"
Copy-Item -LiteralPath "$work/wrapper/Pane.Setup.exe" -Destination $installer -Force
$releaseInstaller=Join-Path $dist "Pane-Setup-$version.exe"
Copy-Item -LiteralPath $installer -Destination $releaseInstaller -Force
$portableName="Pane-Portable-$version.zip"
$portableRoot=Join-Path $work 'portable-stage/Pane'
New-Item -ItemType Directory -Path (Split-Path $portableRoot -Parent) | Out-Null
Copy-Item -LiteralPath $stage -Destination $portableRoot -Recurse
[IO.File]::WriteAllBytes((Join-Path $portableRoot 'portable'),[byte[]]@())
$portableZip=Join-Path $work $portableName
[IO.Compression.ZipFile]::CreateFromDirectory($portableRoot,$portableZip,[IO.Compression.CompressionLevel]::Optimal,$true)
$restored=Join-Path $work 'portable-check'
[IO.Compression.ZipFile]::ExtractToDirectory($portableZip,$restored)
Assert-ForbiddenFiles -Files @(Get-ChildItem -LiteralPath $restored -File -Recurse | ForEach-Object {$_.FullName}) -Rules $rules
$portable=Join-Path $dist $portableName
Copy-Item -LiteralPath $portableZip -Destination $portable -Force
$attachments=@($releaseInstaller,$portable)+@(& "$PSScriptRoot/release-patches.ps1" -Stage $stage -Dist $dist -Version $version -SkipHistory:$SkipPatchHistory)
$checksums=foreach($file in $attachments){
    $checksum=(Get-FileHash -LiteralPath $file -Algorithm SHA256).Hash.ToLowerInvariant()
    "$checksum  $([IO.Path]::GetFileName($file))"
}
$sums=Join-Path $dist 'SHA256SUMS.txt'
[IO.File]::WriteAllText($sums,($checksums -join "`n")+"`n",[Text.UTF8Encoding]::new($false))
$raw=Get-ChildItem -LiteralPath $source -Recurse -File | Measure-Object Length -Sum
$manifest=Join-Path $dist "Pane-Manifest-$version.json"
Assert-ForbiddenFiles -Files @($installer,$releaseInstaller,$manifest,$sums) -Rules $rules
foreach($attachment in $attachments | Select-Object -Skip 3){
    if([IO.Path]::GetFileName($attachment) -notmatch ('^Pane-Patch-(\d+\.\d+\.\d+)-'+[regex]::Escape($version)+'\.zip$')){throw "未知附件：$attachment"}
    $from=$Matches[1]
    Assert-ReleasePatch -Patch $attachment -Baseline (Join-Path $dist "payloads/$from") -Manifest $manifest -From $from -To $version -Rules $rules
}
$payload=Get-ChildItem -LiteralPath $stage -Recurse -File | Measure-Object Length -Sum
$result=[ordered]@{version=$version;source=$source;stage=$stage;installer=$installer;rawFiles=$raw.Count;rawBytes=$raw.Sum;files=$payload.Count;bytes=$payload.Sum;compressedBytes=(Get-Item -LiteralPath $zip).Length;installerBytes=(Get-Item -LiteralPath $installer).Length}
$result.attachments=$attachments+@($sums)
$result | ConvertTo-Json | Set-Content -LiteralPath "$dist/package.json" -Encoding UTF8
# 成功后删掉其他打包临时目录（每个约 0.2～0.3 GB）。保留本次（package.json 指向它）、-NoBuild 用到的来源、
# 最近 3 次失败的现场，以及 2 小时内新建的（可能有另一次打包正在跑）。
$keep=@([IO.Path]::GetFullPath($work).TrimEnd('\'))
if($source -match '[\\/](package-[0-9a-f]{32})[\\/]'){$keep+=[IO.Path]::GetFullPath((Join-Path $setupBin $Matches[1])).TrimEnd('\')}
$packages=@(Get-ChildItem -LiteralPath $setupBin -Directory -Filter 'package-*')
$keep+=@($packages | Where-Object {Test-Path -LiteralPath (Join-Path $_.FullName 'FAILED.txt')} | Sort-Object LastWriteTime -Descending | Select-Object -First 3 | ForEach-Object {$_.FullName.TrimEnd('\')})
foreach($old in $packages){
    if($keep -contains $old.FullName.TrimEnd('\') -or $old.CreationTime -gt [DateTime]::Now.AddHours(-2)){continue}
    try {Remove-LinkSafeTree $old.FullName}catch{Write-Warning "旧打包目录没删掉：$($old.FullName)：$($_.Exception.Message)"}
}
$result | ConvertTo-Json


} finally { $env:CL=$previousCl; $env:_LINK_=$previousLink }
