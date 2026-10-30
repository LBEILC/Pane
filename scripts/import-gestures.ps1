param([Parameter(Mandatory=$true)][string]$ControlPrefix,[string]$SourceDirectory='')
$ErrorActionPreference='Stop'
$prefix=[IO.Path]::GetFullPath($ControlPrefix)
if(!(Test-Path -LiteralPath ($prefix+'.ready')) -or (Test-Path -LiteralPath ($prefix+'.stopped'))){throw '请先用 src/Pane.App/scripts/run.ps1 启动 Pane，并传入输出的控制前缀。'}
Remove-Item -LiteralPath ($prefix+'.import-result') -ErrorAction SilentlyContinue
[IO.File]::WriteAllText($prefix+'.import-tmp',$SourceDirectory)
Move-Item -LiteralPath ($prefix+'.import-tmp') -Destination ($prefix+'.import-wgestures')
$end=[DateTime]::UtcNow.AddSeconds(20)
while(!(Test-Path -LiteralPath ($prefix+'.import-result'))){if([DateTime]::UtcNow -gt $end){throw '导入等待超时'};Start-Sleep -Milliseconds 100}
$result=[IO.File]::ReadAllText($prefix+'.import-result');Write-Output $result
if($result.StartsWith('error=')){throw '导入失败，见上面的结果。'}
