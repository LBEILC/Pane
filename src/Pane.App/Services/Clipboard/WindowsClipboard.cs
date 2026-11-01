using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using Pane.Contracts;
using Pane.Settings;
namespace Pane.Services.Clipboard;

internal sealed class WindowsClipboardSource(SettingsStore settings,bool observe):IClipboardSource
{
    NativeClip.Callback? callback;
    nint listener;
    uint lastSequence;
    public bool Registered {get;private set;}
    public bool Removed {get;private set;}=true;
    public int Notifications {get;private set;}
    public int Failures {get;private set;}
    public event Action<ClipboardContent>? Captured;
    public void Start(){if(listener!=0)return;lastSequence=SelectionNative.GetClipboardSequenceNumber();Removed=false;callback=OnChange;listener=NativeClip.PaneClipListen(callback);Registered=listener!=0;if(!Registered)throw new InvalidOperationException("剪贴板监听注册失败");}
    void OnChange(uint sequence)
    {
        Notifications++;
        // 观察模式在任何内容/来源 API 前返回，自测只统计系统通知。
        if(observe||!settings.Current.Clipboard.Enabled||sequence==lastSequence)return;
        lastSequence=sequence;
        if(ClipboardHistorySuppression.TryConsume(sequence))return;
        bool opened=false;
        try {
            for(int i=0;i<5&&!opened;i++){opened=NativeClip.OpenClipboard(0);if(!opened)Thread.Sleep(15);}
            if(!opened||SelectionNative.GetClipboardSequenceNumber()!=sequence)return;
            bool own=Has("Pane.OwnClipboard"),exclude=Has("ExcludeClipboardContentFromMonitorProcessing");
            int? history=Flag("CanIncludeInClipboardHistory"),cloud=Flag("CanUploadToCloudClipboard");
            var s=settings.Current.Clipboard;
            if(own||s.HonorExcludeMarker&&(exclude||history==0||cloud==0))return;
            string program="",display="",path="";
            NativeClip.GetWindowThreadProcessId(NativeClip.GetClipboardOwner(),out uint pid);
            if(pid!=0){try{using var process=Process.GetProcessById((int)pid);path=process.MainModule?.FileName??"";program=Path.GetFileName(path);display=FileVersionInfo.GetVersionInfo(path).FileDescription??program;}catch(Exception e) when(e is System.ComponentModel.Win32Exception or InvalidOperationException or ArgumentException){}}
            if(s.ExcludedPrograms.Contains(program,StringComparer.OrdinalIgnoreCase)||s.ExcludedPrograms.Length>0&&program.Length==0)return;
            string? text=null;string[]? files=null;byte[]? png=null;
            if(NativeClip.IsClipboardFormatAvailable(15)){
                if(!s.RecordFiles)return;var handle=NativeClip.GetClipboardData(15);uint count=NativeClip.DragQueryFileW(handle,uint.MaxValue,null,0);files=new string[count];
                for(uint i=0;i<count;i++){var name=new StringBuilder((int)NativeClip.DragQueryFileW(handle,i,null,0)+1);NativeClip.DragQueryFileW(handle,i,name,(uint)name.Capacity);files[i]=name.ToString();}
            }else if(Has("PNG")||NativeClip.IsClipboardFormatAvailable(17)||NativeClip.IsClipboardFormatAvailable(8)){
                if(!s.RecordImages)return;if(NativeClip.PaneClipImageAlloc(out var bytes,out int length)!=1)return;
                try{png=new byte[length];Marshal.Copy(bytes,png,0,length);}finally{Marshal.FreeCoTaskMem(bytes);}
            }else if(s.RecordText&&NativeClip.IsClipboardFormatAvailable(13)){
                var handle=NativeClip.GetClipboardData(13);var p=NativeClip.GlobalLock(handle);if(p!=0){try{int chars=checked((int)(NativeClip.GlobalSize(handle)/2));text=Marshal.PtrToStringUni(p,chars)?.TrimEnd('\0');}finally{NativeClip.GlobalUnlock(handle);}}
            }
            NativeClip.CloseClipboard();opened=false;
            if(!settings.Current.Clipboard.Enabled)return;
            string? icon=null;
            if(path.Length>0){string cache=Path.Combine(settings.DirectoryPath,"clipboard","icons");Directory.CreateDirectory(cache);string target=Path.Combine(cache,Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(path)))+".png");
                if(File.Exists(target)||NativeClip.PaneClipSourceIcon(path,target)==1)icon=target;
            }
            Captured?.Invoke(new(text,files,png,program,string.IsNullOrWhiteSpace(display)?program:display,icon,exclude,history,cloud,own));
        }catch(Exception e){Failures++;new ClipboardLog(settings.DirectoryPath).Write("capture-failed",e);}finally{if(opened)NativeClip.CloseClipboard();}
    }
    static bool Has(string name)=>NativeClip.IsClipboardFormatAvailable(NativeClip.RegisterClipboardFormatW(name));
    static int? Flag(string name){uint format=NativeClip.RegisterClipboardFormatW(name);if(!NativeClip.IsClipboardFormatAvailable(format))return null;var handle=NativeClip.GetClipboardData(format);if(NativeClip.GlobalSize(handle)<4)return 0;var p=NativeClip.GlobalLock(handle);if(p==0)return 0;try{return Marshal.ReadInt32(p);}finally{NativeClip.GlobalUnlock(handle);}}
    public void Dispose()=>Stop();
    public void Stop(){if(listener==0)return;Removed=NativeClip.PaneClipStop(listener,out int count)==1;Notifications=count;listener=0;Registered=false;GC.KeepAlive(callback);}
}
internal sealed class WindowsClipboardImages:IClipboardImages
{
    public (int Width,int Height)? Thumbnail(string path,string thumbnail)
    {
        string temporary=thumbnail+".tmp";
        try{
            if(!LocalImage(path))return null;
            var file=new FileInfo(path);if(!file.Exists||file.Length>50L*1024*1024)return null;
            if(NativeClip.PaneClipSaveImage(path,null,temporary,out int width,out int height)!=1)return null;
            File.Move(temporary,thumbnail,true);return(width,height);
        }catch(Exception){return null;}
        finally{try{File.Delete(temporary);}catch(Exception){}}
    }
    internal static bool LocalImage(string path)
    {
        // 拒绝 UNC、设备路径、映射网络盘和重解析点，避免后台解码访问远端。
        if(!Path.IsPathFullyQualified(path)||path.StartsWith(@"\\")||path.StartsWith("//"))return false;
        if(Path.GetExtension(path).ToLowerInvariant() is not (".png" or ".jpg" or ".jpeg" or ".gif" or ".bmp" or ".webp" or ".heic"))return false;
        string root=Path.GetPathRoot(path)!;
        if(new DriveInfo(root).DriveType==DriveType.Network)return false;
        for(string? part=path;part!=null&&part.Length>=root.Length;part=Path.GetDirectoryName(part))
            if((File.GetAttributes(part)&FileAttributes.ReparsePoint)!=0)return false;
        return true;
    }
    public (int Width,int Height) Save(byte[] png,string full,string thumbnail)
    {
        string input=full+".input",temporary=full+".tmp",small=thumbnail+".tmp";
        try{File.WriteAllBytes(input,png);if(NativeClip.PaneClipSaveImage(input,temporary,small,out int width,out int height)!=1)throw new InvalidDataException("图片解码失败");
            foreach(var path in new[]{temporary,small})using(var stream=new FileStream(path,FileMode.Open,FileAccess.Write,FileShare.None))stream.Flush(true);
            File.Move(temporary,full,true);File.Move(small,thumbnail,true);return(width,height);
        }finally{File.Delete(input);File.Delete(temporary);File.Delete(small);}
    }
}
internal sealed class WindowsClipboardWriter:IClipboardWriter
{
    public Task PasteAsync(ClipEntry entry,bool plainText,nint target,CancellationToken token)
    {
        long started=Environment.TickCount64; var platform=new Pane.Services.Clipboard.WindowsPastePlatform();
        return Sta.Run(()=>{new PasteTransfer(platform,PasteTransfer.WriteLog).Run(target,()=>Write(entry,plainText),token,started);return true;});
    }
    internal static void Write(ClipEntry entry,bool plainText=false)
    {
        string? files=null,image=null,text=entry.Text;
        // TODO(design): 图片没有文本时，纯文本粘贴保守地报告不支持；由设计决定是否禁用该入口。
        if(plainText){text=entry.Kind==ClipKind.Files?string.Join(Environment.NewLine,entry.Files??[]):entry.Text;if(text==null)throw new NotSupportedException("此条目没有纯文本");}
        else if(entry.Kind==ClipKind.Image){image=entry.ContentPath??throw new InvalidDataException("缺少完整图片");text=null;}
        else if(entry.Kind==ClipKind.Files){files=string.Join('\0',entry.Files??[])+"\0\0";text=null;}
        if(NativeClip.PaneClipWrite(text,image,files,files?.Length??0)!=1)throw new TimeoutException("剪贴板写入失败或正在被占用");
    }
}
internal sealed class ClipboardAttachmentActions(ILauncherAttachmentActions other):ILauncherAttachmentActions
{
    public Task RunAsync(string actionId,ClipEntry attachment,CancellationToken token)
    {
        if(attachment.Kind!=ClipKind.Image)return other.RunAsync(actionId,attachment,token);
        return Sta.Run(()=>{token.ThrowIfCancellationRequested();
            switch(actionId){
                case "copy-image":WindowsClipboardWriter.Write(attachment);break;
                case "save-image":if(attachment.ContentPath==null)throw new InvalidDataException("缺少完整图片");if(NativeClip.PaneClipSaveDialog(attachment.ContentPath,$"截图 {attachment.CopiedAt.LocalDateTime:yyyy-MM-dd HH-mm-ss}.png")==0)throw new IOException("图片保存失败");break;
                default:throw new NotSupportedException(actionId);
            }return true;
        });
    }
}
internal static class NativeClip
{
    const string Dll="Pane.Native.dll";
    [DllImport(Dll,CharSet=CharSet.Unicode)] internal static extern int PaneClipSourceIcon(string path,string output);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] internal delegate void Callback(uint sequence);
    [DllImport(Dll)] internal static extern nint PaneClipListen(Callback callback);
    [DllImport(Dll)] internal static extern int PaneClipStop(nint listener,out int count);
    [DllImport(Dll)] internal static extern int PaneClipImageAlloc(out nint output,out int length);
    [DllImport(Dll,CharSet=CharSet.Unicode)] internal static extern int PaneClipSaveImage(string input,string? full,string thumb,out int width,out int height);
    [DllImport(Dll,CharSet=CharSet.Unicode,SetLastError=true)] internal static extern int PaneClipWrite(string? text,string? image,string? files,int chars);
    [DllImport(Dll,CharSet=CharSet.Unicode)] internal static extern int PaneClipSaveDialog(string source,string name);
    [DllImport("user32.dll")] internal static extern bool OpenClipboard(nint owner);
    [DllImport("user32.dll")] internal static extern bool CloseClipboard();
    [DllImport("user32.dll")] internal static extern bool IsClipboardFormatAvailable(uint format);
    [DllImport("user32.dll")] internal static extern nint GetClipboardData(uint format);
    [DllImport("user32.dll")] internal static extern nint GetClipboardOwner();
    [DllImport("user32.dll")] internal static extern uint GetWindowThreadProcessId(nint window,out uint process);
    [DllImport("user32.dll",CharSet=CharSet.Unicode)] internal static extern uint RegisterClipboardFormatW(string name);
    [DllImport("user32.dll")] internal static extern bool IsWindow(nint window);
    [DllImport("kernel32.dll")] internal static extern nint GlobalLock(nint memory);
    [DllImport("kernel32.dll")] internal static extern bool GlobalUnlock(nint memory);
    [DllImport("kernel32.dll")] internal static extern nuint GlobalSize(nint memory);
    [DllImport("shell32.dll",CharSet=CharSet.Unicode)] internal static extern uint DragQueryFileW(nint drop,uint index,StringBuilder? path,uint size);
}
