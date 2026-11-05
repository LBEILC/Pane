# 删除文件或目录树：遇到链接（符号链接、目录联接）只删链接本身、不进入；只读属性先去掉；支持超长路径。
function Remove-LinkSafeTree([string]$Path){
    $long='\\?\'+[IO.Path]::GetFullPath($Path).TrimEnd('\')
    if([IO.Directory]::Exists($long)){$item=[IO.DirectoryInfo]::new($long)}elseif([IO.File]::Exists($long)){$item=[IO.FileInfo]::new($long)}else{return}
    $link=[IO.FileAttributes]::ReparsePoint
    if($item.Attributes -band $link){if($item -is [IO.DirectoryInfo]){[IO.Directory]::Delete($item.FullName,$false)}else{$item.Delete()};return}
    if($item -is [IO.FileInfo]){if($item.IsReadOnly){$item.IsReadOnly=$false};$item.Delete();return}
    $dirs=[Collections.Generic.List[IO.DirectoryInfo]]::new()
    $stack=[Collections.Generic.Stack[IO.DirectoryInfo]]::new();$stack.Push($item)
    while($stack.Count){
        $dir=$stack.Pop();$dirs.Add($dir)
        foreach($child in $dir.GetFileSystemInfos()){
            if($child.Attributes -band $link){if($child -is [IO.DirectoryInfo]){[IO.Directory]::Delete($child.FullName,$false)}else{$child.Delete()}}
            elseif($child -is [IO.DirectoryInfo]){$stack.Push($child)}
            else{if($child.IsReadOnly){$child.IsReadOnly=$false};$child.Delete()}
        }
    }
    for($i=$dirs.Count-1;$i -ge 0;$i--){$dirs[$i].Attributes=[IO.FileAttributes]::Directory;$dirs[$i].Delete()}
}
