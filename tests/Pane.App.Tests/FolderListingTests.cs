using System.Diagnostics;
using System.Security.AccessControl;
using System.Security.Principal;
using Pane.Contracts;
using Pane.Services.Rename;

static class FolderListingTests
{
    public static async Task<int> Run()
    {
        int checks=0;
        void Check(bool ok,string name){if(!ok)throw new Exception("folder: "+name);checks++;}
        async Task Throws<T>(Func<Task> action) where T:Exception {try{await action();throw new Exception("missing "+typeof(T).Name);}catch(T){checks++;}}
        string root=Path.Combine(AppContext.BaseDirectory,"results","folder-"+Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var service=new BatchRenamer();
        string FileAt(string relative,FileAttributes attributes=FileAttributes.Normal){string path=Path.Combine(root,relative);Directory.CreateDirectory(Path.GetDirectoryName(path)!);File.WriteAllText(path,"");File.SetAttributes(path,attributes);return path;}
        string denied=Path.Combine(root,"denied");DirectorySecurity? original=null;
        try{
            FileAt("10.jpg");FileAt("2.jpg");FileAt("hidden.txt",FileAttributes.Hidden);FileAt("system.txt",FileAttributes.System);
            FileAt("hidden/secret.txt");File.SetAttributes(Path.Combine(root,"hidden"),FileAttributes.Directory|FileAttributes.Hidden);
            FileAt("system/secret.txt");File.SetAttributes(Path.Combine(root,"system"),FileAttributes.Directory|FileAttributes.System);
            FileAt("sub10/2.jpg");FileAt("sub2/10.jpg");FileAt("sub2/2.jpg");FileAt("sub2/deep/1.jpg");
            // 联接点由测试脚本预先创建，目标为夹具本身，误跟随就会形成循环。
            string junction=Path.Combine(root,"junction");
            string script=Path.Combine(root,"junction.ps1");File.WriteAllText(script,"param($Link,$Target)\n$ErrorActionPreference='Stop'\nNew-Item -ItemType Junction -Path $Link -Target $Target | Out-Null");
            var start=new ProcessStartInfo("powershell.exe"){UseShellExecute=false,CreateNoWindow=true};
            foreach(var arg in new[]{"-NoProfile","-NonInteractive","-ExecutionPolicy","RemoteSigned","-File",script,junction,root})start.ArgumentList.Add(arg);
            using(var process=Process.Start(start)!){await process.WaitForExitAsync();Check(process.ExitCode==0&&File.GetAttributes(junction).HasFlag(FileAttributes.ReparsePoint),"junction fixture exit="+process.ExitCode);}
            File.Delete(script);
            FileAt("denied/private.txt");
            var directory=new DirectoryInfo(denied);original=directory.GetAccessControl();
            var restricted=directory.GetAccessControl();restricted.AddAccessRule(new FileSystemAccessRule(WindowsIdentity.GetCurrent().User!,FileSystemRights.ListDirectory,AccessControlType.Deny));directory.SetAccessControl(restricted);
            bool inaccessible=false;try{Directory.GetFileSystemEntries(denied);}catch(UnauthorizedAccessException){inaccessible=true;}
            Check(inaccessible,"denied fixture");
            var listing=await service.ListFolderAsync(root,default);
            Check(listing.Direct.Select(Path.GetFileName).SequenceEqual(new[]{"2.jpg","10.jpg"}),"direct natural sort and filters");
            Check(listing.Nested.Select(p=>Path.GetRelativePath(root,p).Replace('\\','/')).SequenceEqual(new[]{"sub2/2.jpg","sub2/10.jpg","sub2/deep/1.jpg","sub10/2.jpg"}),"nested sort and excluded directories");
            Check(!listing.Truncated,"small tree complete");
            await Throws<UnauthorizedAccessException>(()=>service.ListFolderAsync(denied,default));
            await Throws<DirectoryNotFoundException>(()=>service.ListFolderAsync(Path.Combine(root,"missing","folder"),default));
            await Throws<IOException>(()=>service.ListFolderAsync(junction,default));
            using(var cancel=new CancellationTokenSource()){cancel.Cancel();await Throws<OperationCanceledException>(()=>service.ListFolderAsync(root,cancel.Token));}
            string large=Path.Combine(root,"large");Directory.CreateDirectory(large);
            for(int i=0;i<10000;i++)FileAt($"large/{i/1000}/{i}.txt");
            var watch=Stopwatch.StartNew();var capped=await service.ListFolderAsync(large,default);watch.Stop();
            Check(capped.Truncated&&capped.Direct.Count+capped.Nested.Count==IBatchRenamer.MaxFolderFiles,"cap");
            Check(watch.Elapsed.TotalMilliseconds<300,"10000 files under 300 ms");
            Console.WriteLine($"folder files=10000; returned=2000; milliseconds={watch.Elapsed.TotalMilliseconds:F2}; denied=verified; junction=verified");
            string exact=Path.Combine(root,"exact");Directory.CreateDirectory(exact);
            for(int i=0;i<2000;i++)FileAt($"exact/{i}.txt");
            var full=await service.ListFolderAsync(exact,default);Check(full.Direct.Count==2000&&!full.Truncated,"exact cap not truncated");
        }finally{
            if(original!=null){var restore=new DirectorySecurity();restore.SetSecurityDescriptorBinaryForm(original.GetSecurityDescriptorBinaryForm(),AccessControlSections.Access);new DirectoryInfo(denied).SetAccessControl(restore);}
            // 先解除联接点，再清理仅由本测试创建的根目录。
            string link=Path.Combine(root,"junction");if(Directory.Exists(link))Directory.Delete(link);
            foreach(string file in Directory.EnumerateFiles(root,"*",SearchOption.AllDirectories))File.SetAttributes(file,FileAttributes.Normal);
            Directory.Delete(root,true);
        }
        Console.WriteLine($"PASS folder assertions={checks}");return checks;
    }
}
