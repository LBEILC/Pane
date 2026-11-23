using System.IO.Compression;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using Pane.Contracts;
using Pane.Services.Clipboard;
using Pane.Settings;

static partial class Program
{
    [DllImport("user32.dll",CharSet=CharSet.Unicode)] static extern nint CreateWindowStationW(string? name,uint flags,uint access,nint attributes);
    [DllImport("user32.dll")] static extern bool SetProcessWindowStation(nint station);
    [DllImport("user32.dll")] static extern nint GetProcessWindowStation();
    [DllImport("user32.dll",CharSet=CharSet.Unicode)] static extern nint CreateDesktopW(string name,nint device,nint mode,uint flags,uint access,nint attributes);
    [DllImport("user32.dll")] static extern bool SetThreadDesktop(nint desktop);
    [DllImport("user32.dll")] static extern bool EmptyClipboard();

    // 保留真实格式写入；焦点和 Ctrl+V 由已有 PasteTests 单独验证。
    sealed class IsolatedWriter:IClipboardWriter
    {
        public Task PasteAsync(ClipEntry entry,bool plain,nint window,CancellationToken token)=>PasteAsync(entry,plain,window,token,null);
        public Task PasteAsync(ClipEntry entry,bool plain,nint window,CancellationToken token,RichFormats? formats){
            WindowsClipboardWriter.WriteFormats(entry,ClipboardWriteMode.HistoryPaste,plain,formats);return Task.CompletedTask;
        }
    }
    static void Clipboard106()
    {
        var station=CreateWindowStationW(null,1,0xf037f,0);
        if(station==0||!SetProcessWindowStation(station))throw new InvalidOperationException("无法进入测试窗口站");
        var desktop=CreateDesktopW("Clipboard106",0,0,0,0x10000000,0);
        if(desktop==0||!SetThreadDesktop(desktop)||station!=GetProcessWindowStation())throw new InvalidOperationException("无法进入测试桌面");
        try{Clipboard106Cases();}
        finally{
            Check(NativeClip.OpenClipboard(0),"打开独立剪贴板以清空");
            try{Check(EmptyClipboard(),"清空独立剪贴板");}finally{NativeClip.CloseClipboard();}
        }
        Console.WriteLine($"PASS clipboard106 assertions={checks}; isolatedWindowStation=1");
    }
    static void Clipboard106Cases()
    {
        string root=TestRoot();Directory.CreateDirectory(root);
        Environment.SetEnvironmentVariable("TMP",root);
        Environment.SetEnvironmentVariable("TEMP",root);
        var store=new SettingsStore(Path.Combine(root,"source"),Defaults());
        store.Save(store.Current with{Clipboard=store.Current.Clipboard with{Limit=200,PanelHeight=450,KeyHints=ClipKeyHints.Always}});
        var source=new FakeSource();var platform=new WindowsClipboardCapture(store);
        T Read<T>(Func<T> action){
            Check(platform.Open(),"打开独立窗口站的剪贴板");
            try{return action();}finally{platform.Close();}
        }
        ClipboardContent ReadContent()=>Read(()=>platform.Read(platform.Metadata()));
        uint Format(string name)=>NativeClip.RegisterClipboardFormatW(name);
        byte[] Raw(uint format){
            var memory=NativeClip.GetClipboardData(format);var ptr=NativeClip.GlobalLock(memory);
            try{var bytes=new byte[(int)NativeClip.GlobalSize(memory)];Marshal.Copy(ptr,bytes,0,bytes.Length);return bytes;}
            finally{NativeClip.GlobalUnlock(memory);}
        }
        string id,index=Path.Combine(store.DirectoryPath,"clipboard","history.json");
        var disk=new ClipboardStorage();
        var formats=new RichFormats(Encoding.UTF8.GetBytes("Version:1.0\r\nStartHTML:0000000045\r\n<html>字</html>\0"),Encoding.ASCII.GetBytes(@"{\rtf1 fixture}"+"\0"));
        using(var history=new ClipboardHistory(store,source,new IsolatedWriter(),new WindowsClipboardImages())){
            history.Ready.GetAwaiter().GetResult();
            source.Emit(new(Text:"字"));var original=history.Snapshot()[0];id=original.Id;
            WindowsClipboardWriter.WriteFormats(original,ClipboardWriteMode.UserCopy,false,formats);
            var originalBytes=Read(()=>Raw(13));var captured=ReadContent();source.Emit(captured);
            Check(history.Snapshot().Single().Rich,"连续重复更新格式");
            int changed=0;history.Changed+=(_,_)=>Interlocked.Increment(ref changed);
            history.SetTags(id,[" A ","a","B"," "]);history.UpdateTag("a","Alpha",2);
            Check(history.Tags().SequenceEqual([new ClipTag("Alpha",2),new ClipTag("B",null)])&&history.Snapshot()[0].Tags.SequenceEqual(["Alpha","B"]),"标签顺序、色点和规范名");
            Check(changed>=2,"修改标签后通知");
            history.UpdateTag("Alpha","Alpha",5);Check(history.Tags()[0].Color==5,"单独改色");
            try{history.UpdateTag("B","alPHa",3);Check(false,"应拒绝重名");}catch(ArgumentException){Check(true,"拒绝重名");}
            for(int i=0;i<205;i++)source.Emit(new(Text:"fixture"+i));
            Check(history.Snapshot().Count==201&&history.Snapshot().Any(c=>c.Id==id),"条数上限保护标签");
            var firstOrdinary=history.Snapshot().Last(c=>c.Tags.Count==0);
            history.SetTags(id,[]);Check(history.Snapshot().Count==201,"去掉最后一个标签暂不按上限淘汰");
            history.SetTags(firstOrdinary.Id,["B"]);
            source.Emit(new(Text:"another"));Check(history.Snapshot().All(c=>c.Id!=id),"下次复制再淘汰");
            source.Emit(captured);id=history.Snapshot()[0].Id;
            history.SetTags(id,["Alpha","B"]);history.SetTags(firstOrdinary.Id,[]);
            history.ClearUnpinned();Check(history.Snapshot().Single().Id==id,"清空保留标签");
            history.PasteAsync(id,false,1,default).GetAwaiter().GetResult();
            Check(Read(()=>Raw(13).SequenceEqual(originalBytes)&&Raw(Format("HTML Format")).SequenceEqual(formats.Html)&&Raw(Format("Rich Text Format")).SequenceEqual(formats.Rtf)),"三种格式原始字节往返");
            history.PasteAsync(id,true,1,default).GetAwaiter().GetResult();
            Check(Read(()=>Raw(13).SequenceEqual(originalBytes)&&!NativeClip.IsClipboardFormatAvailable(Format("HTML Format"))&&!NativeClip.IsClipboardFormatAvailable(Format("Rich Text Format"))),"纯文本写入");
            WindowsClipboardWriter.WriteFormats(original,ClipboardWriteMode.UserCopy,false,new(new byte[RichFormats.Limit],new byte[1]));
            var large=ReadContent();Check(large.Formats==null&&large.Text==original.Text,"两种格式合计超限");
            source.Emit(large with{Text="large"});Check(!history.Snapshot()[0].Rich,"超限只入文字");
        }
        var saved=disk.LoadIndex(index);Check(saved.Version==2&&saved.Tags.Length==2&&saved.Entries.Single(c=>c.Entry.Id==id).Entry.Rich,"索引和标签重载");
        using(var history=new ClipboardHistory(store,source,new IsolatedWriter(),new WindowsClipboardImages())){
            history.Ready.GetAwaiter().GetResult();var transfer=new DataStorage(store,()=>history,()=>{});
            foreach(var level in new[]{ClipboardExport.TextOnly,ClipboardExport.WithImages}){
                string archive=Path.Combine(root,level+".zip");transfer.ExportAsync(archive,new(true,false,level),null,default).GetAwaiter().GetResult();
                using(var zip=ZipFile.OpenRead(archive))Check(zip.Entries.Any(e=>e.Name.EndsWith(".rich"))==(level==ClipboardExport.WithImages),"分级导出");
                var dest=new SettingsStore(Path.Combine(root,"dest"+level),Defaults());dest.Save(dest.Current with{Clipboard=dest.Current.Clipboard with{PanelHeight=300}});
                var localHistory=new ClipboardHistory(dest,new FakeSource(),new FakeWriter(),new SwitchImages());localHistory.Ready.GetAwaiter().GetResult();
                localHistory.Capture(new(Text:"字"));localHistory.SetTags(localHistory.Snapshot()[0].Id,["alpha"]);localHistory.UpdateTag("alpha","alpha",6);localHistory.Dispose();
                var importer=new DataStorage(dest,()=>null,()=>{});importer.StageImportAsync(archive,new(true,false,true,DataImportMode.Merge),null,default).GetAwaiter().GetResult();
                DataStorage.ApplyPending(dest.DirectoryPath);dest.Load();
                var merged=disk.LoadIndex(Path.Combine(dest.DirectoryPath,"clipboard","history.json"));var item=merged.Entries.Single(c=>c.Entry.Text=="字");
                Check(merged.Tags.SequenceEqual([new ClipTag("alpha",6),new ClipTag("B",null)])&&item.Entry.Tags.SequenceEqual(["alpha","B"]),"同名标签合并保留本机色点");
                Check(dest.Current.Clipboard.PanelHeight==300&&dest.Current.Clipboard.KeyHints==ClipKeyHints.Always,"导入保留本机高度和导出提示设置");
                Check(item.Entry.Rich==(level==ClipboardExport.WithImages),"分级导入");
                if(item.FormatId!=null)Check(RichFormats.Read(Path.Combine(dest.DirectoryPath,"clipboard"),item.FormatId).Html.SequenceEqual(formats.Html),"导入格式原始字节");
            }
            var prior=history.Snapshot().Single(c=>c.Id==id);history.UpdateText(id,"https://example.test");
            var edited=history.Snapshot().Single(c=>c.Id==id);
            Check(edited.Kind==ClipKind.Link&&!edited.Rich&&edited.CopiedAt==prior.CopiedAt&&edited.Tags.SequenceEqual(prior.Tags),"编辑重判且保留元数据");
            history.DeleteTag("Alpha");Check(history.Snapshot().Single(c=>c.Id==id).Tags.SequenceEqual(["B"]),"全局删除标签");
            history.SetTags(id,[]);Check(history.Snapshot().Any(c=>c.Id==id),"移除最后标签暂留");
            history.ClearUnpinned();Check(history.Snapshot().Count==0&&history.Tags().Count==1,"空标签定义继续保存");
            string[] paths=[Path.Combine(root,"一.txt"),Path.Combine(root,"二.txt")];foreach(string p in paths)File.WriteAllText(p,"");
            source.Emit(new(Files:paths));var files=history.Snapshot()[0];
            void As(ClipEntry entry,ClipPasteAs kind)=>history.PasteAsAsync(entry.Id,kind,1,default).GetAwaiter().GetResult();
            As(files,ClipPasteAs.Paths);Check(ReadContent().Text==string.Join("\r\n",paths),"完整路径");
            As(files,ClipPasteAs.FileNames);Check(ReadContent().Text=="一.txt\r\n二.txt","文件名");
            source.Emit(new(Text:"#805874c9"));var color=history.Snapshot()[0];
            As(color,ClipPasteAs.Hex);Check(ReadContent().Text=="#5874C9","大写 RGB 十六进制");
            As(color,ClipPasteAs.Rgb);Check(ReadContent().Text=="rgb(88, 116, 201)","RGB 文字");
            source.Emit(new(Png:Png(31,19,1)));As(history.Snapshot()[0],ClipPasteAs.ImageFile);
            string png=ReadContent().Files!.Single();
            As(history.Snapshot()[0],ClipPasteAs.ImageFile);
            string second=ReadContent().Files!.Single();Check(File.Exists(png)&&png!=second,"临时文件重名加序号并保留上一个文件");
            Check(File.ReadAllBytes(png).SequenceEqual(File.ReadAllBytes(history.Snapshot()[0].ContentPath!)),"完整 PNG");
            Check(NativeClip.PaneClipSaveImage(png,null,Path.Combine(root,"thumb.png"),out int w,out int h)==1&&w==31&&h==19,"目标 PNG 可解码，尺寸一致");
            Check(Read(()=>BitConverter.ToInt32(Raw(Format("Preferred DropEffect")))==1),"文件首选复制");
            File.Delete(paths[0]);
            try{As(files,ClipPasteAs.Paths);Check(false,"缺失文件");}catch(FileNotFoundException){Check(true,"缺失文件有说明");}
            var image=history.Snapshot()[0];File.Delete(image.ContentPath!);
            try{As(image,ClipPasteAs.ImageFile);Check(false,"缺失图片");}catch(FileNotFoundException){Check(true,"缺失图片有说明");}
        }
        Check(!Directory.EnumerateFiles(Path.GetDirectoryName(index)!,"*.rich").Any(),"编辑后清理格式文件");
        var oldPath=Path.Combine(root,"legacy.json");File.WriteAllText(oldPath,"[{\"Entry\":{\"Id\":\"legacy\",\"Kind\":0,\"Text\":\"old\"},\"Fingerprint\":\"t:old\"}]");
        var old=disk.LoadIndex(oldPath);Check(old.Tags.Length==0&&old.Entries.Single().Entry.Tags.Count==0&&!old.Entries[0].Entry.Rich,"旧索引兼容");
    }
}
