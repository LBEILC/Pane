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
} finally {$env:PATH=$oldPath}
