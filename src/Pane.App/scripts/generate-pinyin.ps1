param([string]$Source = "$PSScriptRoot/../../../docs/research/refs/013/Unihan.zip")
$ErrorActionPreference='Stop'
Add-Type -AssemblyName System.IO.Compression.FileSystem
$zip=[IO.Compression.ZipFile]::OpenRead([IO.Path]::GetFullPath($Source))
$map=@{}
try {
 $reader=[IO.StreamReader]::new($zip.GetEntry('Unihan_Readings.txt').Open())
 try { while($null-ne($line=$reader.ReadLine())) {
  if($line -notmatch '^U\+([0-9A-F]+)\s+(kMandarin|kHanyuPinyin|kXHC1983|kHanyuPinlu)\s+(.+)$'){continue}
  $code=[Convert]::ToInt32($Matches[1],16);$field=$Matches[2];$value=$Matches[3]
  if(!$map.ContainsKey($code)){$map[$code]=[Collections.Generic.HashSet[string]]::new()}
  if($field-ne 'kMandarin'){$value=[regex]::Replace($value,'[0-9.,*]+:|\([0-9]+\)','')}
  foreach($reading in ($value -split '[, ]+')) {
   $normal=$reading.ToLowerInvariant().Normalize([Text.NormalizationForm]::FormD)
   $plain=-join ($normal.ToCharArray() | Where-Object {[Globalization.CharUnicodeInfo]::GetUnicodeCategory($_)-ne [Globalization.UnicodeCategory]::NonSpacingMark})
   if($plain -match '^[a-z]+$'){[void]$map[$code].Add($plain)}
  }
 }}finally{$reader.Dispose()}
}finally{$zip.Dispose()}
$dest=[IO.Path]::GetFullPath("$PSScriptRoot/../Services/Launcher/Data")
[IO.Directory]::CreateDirectory($dest)|Out-Null
$lines=foreach($code in ($map.Keys|Sort-Object)){if($map[$code].Count){'{0:X}`t{1}' -f $code, (($map[$code]|Sort-Object)-join ',')}}
$lines=$lines|ForEach-Object {$_.Replace('`t',"`t")}
[IO.File]::WriteAllLines("$dest/pinyin.tsv",[string[]]$lines,[Text.UTF8Encoding]::new($false))
Copy-Item -LiteralPath "$PSScriptRoot/../../../docs/research/refs/013/Unicode-LICENSE.txt" -Destination "$dest/Unicode-LICENSE.txt"
Write-Host "characters=$($lines.Count);bytes=$((Get-Item -LiteralPath "$dest/pinyin.tsv").Length)"
