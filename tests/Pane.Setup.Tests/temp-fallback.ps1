param([Parameter(Mandatory)][string]$Wrapper)
$ErrorActionPreference='Stop'
$wrapperPath=(Resolve-Path -LiteralPath $Wrapper).Path
$parent=[IO.Path]::GetFullPath([IO.Path]::GetTempPath())
$testTemp=Join-Path $parent ('Pane-104-'+[Guid]::NewGuid().ToString('N'))
$isolated=Join-Path $testTemp 'Pane-018-isolated'
$fallback=Join-Path $isolated 'Pane/Setup-Temp'
$log=Join-Path $fallback 'Pane-uninstall.log'
$stale=Join-Path $fallback ('Pane-Setup-'+[Guid]::NewGuid().ToString('N'))
$user=[Security.Principal.WindowsIdentity]::GetCurrent().User
$deny=[Security.AccessControl.FileSystemAccessRule]::new($user,'CreateFiles','Deny')
$original=@{}
function Deny-Create([string]$Directory){
    $original[$Directory]=Get-Acl -LiteralPath $Directory
    $acl=Get-Acl -LiteralPath $Directory
    $acl.AddAccessRule($deny)
    Set-Acl -LiteralPath $Directory -AclObject $acl
}
function Run-Wrapper {
    $info=[Diagnostics.ProcessStartInfo]::new($wrapperPath)
    $info.UseShellExecute=$false
    $info.CreateNoWindow=$true
    $info.EnvironmentVariables['TEMP']=$testTemp
    $info.EnvironmentVariables['TMP']=$testTemp
    $info.ArgumentList.Add('--self-test-root')
    $info.ArgumentList.Add($isolated)
    $process=[Diagnostics.Process]::Start($info)
    try{
        if(!$process.WaitForExit(90000)){
            $process.Kill($true)
            $process.WaitForExit()
            throw '隔离安装超时'
        }
        return $process.ExitCode
    }
    finally{$process.Dispose()}
}
try{
    [IO.Directory]::CreateDirectory($stale) | Out-Null
    [IO.File]::WriteAllText($stale+'.lease',$isolated)
    Deny-Create $testTemp
    if((Run-Wrapper) -ne 0){throw ('备用目录安装失败 '+[IO.File]::ReadAllText((Join-Path $isolated 'wrapper-error.txt')))}
    $source=[IO.File]::ReadAllText((Join-Path $isolated 'installed.txt'))
    if(!$source.StartsWith($fallback+'\',[StringComparison]::OrdinalIgnoreCase)){throw '未从备用目录安装'}
    if(!(Test-Path -LiteralPath $log)){throw '备用日志缺失'}
    if((Test-Path -LiteralPath $stale) -or (Test-Path -LiteralPath ($stale+'.lease'))){throw '旧目录或租约残留'}
    [IO.File]::Delete($log)
    Deny-Create $fallback
    if((Run-Wrapper) -eq 0){throw '双目录拒绝时应报错'}
    $message=[IO.File]::ReadAllText((Join-Path $isolated 'wrapper-error.txt'))
    if(!$message.Contains('准备临时文件') -or !$message.Contains($testTemp) -or !$message.Contains($fallback) -or ([regex]::Matches(($message.Split([char]10)[1..2] -join ' '),'0x80070005')).Count -lt 2){throw '两处拒绝原因不完整'}
    if($message.Contains('日志：') -or (Test-Path -LiteralPath $log)){throw '报告了不存在的日志'}
    'PASS setup-temp assertions=7'
}finally{
    foreach($dir in $original.Keys){Set-Acl -LiteralPath $dir -AclObject $original[$dir]}
    if([IO.Path]::GetDirectoryName($testTemp) -ne $parent.TrimEnd('\') -or [IO.Path]::GetFileName($testTemp) -notmatch '^Pane-104-[0-9a-f]{32}$'){throw '隔离目录无效'}
    if(Test-Path -LiteralPath $testTemp){Remove-Item -LiteralPath $testTemp -Recurse -Force}
}
