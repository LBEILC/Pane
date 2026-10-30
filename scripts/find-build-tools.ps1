function Find-PaneVsWhere {
    $command=Get-Command vswhere.exe -ErrorAction SilentlyContinue
    if($command){return $command.Source}
    $candidate=Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio/Installer/vswhere.exe'
    if(Test-Path -LiteralPath $candidate){return $candidate}
    throw '缺少 vswhere.exe；请提供已有 Visual Studio 构建工具的 PATH。'
}
