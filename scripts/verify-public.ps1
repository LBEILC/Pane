param([Parameter(Mandatory=$true)][string]$Root)
$ErrorActionPreference='Stop'
$oldPath=$env:PATH
try {
    if(!(Get-Command vswhere.exe -ErrorAction SilentlyContinue)){$env:PATH+=( ';'+(Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio/Installer'))}
    & powershell.exe @('-NoProfile','-ExecutionPolicy','Bypass','-File',(Join-Path $Root 'src/Pane.App/scripts/build.ps1'),'-Mode','jit')
    if($LASTEXITCODE -ne 0){throw '公开副本 JIT 构建失败'}
    # 这些控制台测试使用假平台，不执行真实输入、剪贴板或注册表操作。
    foreach($name in @('Pane.App.Tests','Pane.Gestures.Tests','Pane.Launcher.Tests','Pane.Clipboard.Tests','Pane.Ai.Tests')){
        $arguments=@('run','--project',(Join-Path $Root "tests/$name/$name.csproj"),'-c','Release')
        if($name -eq 'Pane.Gestures.Tests'){$arguments+=@('--','--unit-only')}
        if($name -eq 'Pane.Clipboard.Tests'){$arguments+=@('--',(Join-Path $Root 'src/Pane.App/bin/native/Pane.Native.dll'))}
        & dotnet @arguments
        if($LASTEXITCODE -ne 0){throw "单元测试失败：$name"}
    }
    # 全部通过后删掉副本里的构建输出（约 0.6 GB）：增量导出要求副本没有未跟踪文件。失败时不删，留作现场。
    . (Join-Path $PSScriptRoot 'remove-tree.ps1')
    $built=& git.exe @('-C',$Root,'-c','core.quotepath=false','ls-files','-o','-i','--exclude-standard','--directory','-z')
    if($LASTEXITCODE -ne 0){throw '读取副本构建输出失败'}
    foreach($relative in (($built -join '').Split([char]0) | Where-Object {$_ -match '^(src|tests)/[^/]+/(bin|obj)/$'})){Remove-LinkSafeTree (Join-Path $Root $relative)}
} finally {$env:PATH=$oldPath}
