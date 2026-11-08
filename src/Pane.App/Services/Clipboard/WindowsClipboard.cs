using Pane.Localization;
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
    readonly object gate=new();
    CancellationTokenSource? pending;
    Task work=Task.CompletedTask;
    public bool Registered {get;private set;}
    public bool Removed {get;private set;}=true;
    public int Notifications {get;private set;}
    public int Failures {get;private set;}
    public event Action<ClipboardContent>? Captured;
    public void Start(){if(listener!=0)return;lastSequence=SelectionNative.GetClipboardSequenceNumber();Removed=false;callback=OnChange;listener=NativeClip.PaneClipListen(callback);Registered=listener!=0;if(!Registered)throw new InvalidOperationException("剪贴板监听注册失败");} // 不翻译：异常
    void OnChange(uint sequence)
    {
        Notifications++;
        // 观察模式不读取内容或来源。
        if(observe||sequence==lastSequence)return;
        lastSequence=sequence;
        lock(gate){
            pending?.Cancel();
            var cancellation=new CancellationTokenSource();pending=cancellation;
            bool suppressed=ClipboardHistorySuppression.TryConsume(sequence);
            Task previous=work;
            work=Task.Run(async()=>{
                try{
                    await previous.ConfigureAwait(false);
                    var capture=new ClipboardCapture(new WindowsClipboardCapture(settings),()=>settings.Current.Clipboard,
                        new ClipboardLog(settings.DirectoryPath),c=>Captured?.Invoke(c),
                        action=>Sta.Run(()=>{action();return true;}),failed:()=>Failures++);
                    await capture.Capture(sequence,cancellation.Token,suppressed).ConfigureAwait(false);
                }finally{lock(gate){if(pending==cancellation)pending=null;cancellation.Dispose();}}
            });
        }
    }
    public void Dispose()=>Stop();
    public void Stop(){
        if(listener==0)return;
        Removed=NativeClip.PaneClipStop(listener,out int count)==1;Notifications=count;listener=0;Registered=false;
        Task remaining;lock(gate){pending?.Cancel();remaining=work;}
        remaining.GetAwaiter().GetResult();GC.KeepAlive(callback);
    }
}
internal sealed class WindowsClipboardCapture(SettingsStore settings):IClipboardCapturePlatform
{
    string path="",display="";
    public uint Sequence=>SelectionNative.GetClipboardSequenceNumber();
    public bool Open()=>NativeClip.OpenClipboard(0);
    public void Close()=>NativeClip.CloseClipboard();
    public string Program {
        get{
            path="";display="";
            NativeClip.GetWindowThreadProcessId(NativeClip.GetClipboardOwner(),out uint pid);
            if(pid!=0)try{using var process=Process.GetProcessById((int)pid);path=process.MainModule?.FileName??"";display=FileVersionInfo.GetVersionInfo(path).FileDescription??"";}
            catch(Exception e) when(e is System.ComponentModel.Win32Exception or InvalidOperationException or ArgumentException){}
            return Path.GetFileName(path);
        }
    }
    public ClipboardContent Metadata(){
        string program=Program;
        // 写入方的临时 owner 窗口会销毁，单独的来源格式只负责归属，不抑制历史。
        if(Has("Pane.SourceClipboard")||Has("Pane.OwnClipboard")){program="Pane.exe";display="Pane";path=Environment.ProcessPath??"";}
        return new(Program:program,Source:string.IsNullOrWhiteSpace(display)?program:display,
            Own:Has("Pane.OwnClipboard"),Exclude:Has("ExcludeClipboardContentFromMonitorProcessing"),
            History:Flag("CanIncludeInClipboardHistory"),Cloud:Flag("CanUploadToCloudClipboard"));
    }
    public string Kind=>NativeClip.IsClipboardFormatAvailable(15)?"files":
        Has("PNG")||NativeClip.IsClipboardFormatAvailable(17)||NativeClip.IsClipboardFormatAvailable(8)?"image":
        NativeClip.IsClipboardFormatAvailable(13)?"text":"";
    public ClipboardContent Read(ClipboardContent metadata){
        string? text=null;string[]? files=null;byte[]? png=null;
        if(Kind=="files"){
            var handle=NativeClip.GetClipboardData(15);uint count=NativeClip.DragQueryFileW(handle,uint.MaxValue,null,0);files=new string[count];
            for(uint i=0;i<count;i++){var name=new StringBuilder((int)NativeClip.DragQueryFileW(handle,i,null,0)+1);NativeClip.DragQueryFileW(handle,i,name,(uint)name.Capacity);files[i]=name.ToString();}
        }else if(Kind=="image"){
            if(NativeClip.PaneClipImageAlloc(out var bytes,out int length)!=1)throw new InvalidDataException();
            try{png=new byte[length];Marshal.Copy(bytes,png,0,length);}finally{Marshal.FreeCoTaskMem(bytes);}
        }else if(Kind=="text"){
            var handle=NativeClip.GetClipboardData(13);var p=NativeClip.GlobalLock(handle);
            if(p==0)throw new InvalidDataException();
            try{int chars=checked((int)(NativeClip.GlobalSize(handle)/2));text=Marshal.PtrToStringUni(p,chars)?.TrimEnd('\0');}finally{NativeClip.GlobalUnlock(handle);}
        }
        return metadata with{Text=text,Files=files,Png=png};
    }
    public ClipboardContent Complete(ClipboardContent content){
        string? icon=null;
        if(path.Length>0){string cache=Path.Combine(settings.DirectoryPath,"clipboard","icons");Directory.CreateDirectory(cache);string target=Path.Combine(cache,Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(path)))+".png");
            if(File.Exists(target)||NativeClip.PaneClipSourceIcon(path,target)==1)icon=target;
        }
        return content with{Icon=icon};
    }
    static bool Has(string name)=>NativeClip.IsClipboardFormatAvailable(NativeClip.RegisterClipboardFormatW(name));
    static int? Flag(string name){uint format=NativeClip.RegisterClipboardFormatW(name);if(!NativeClip.IsClipboardFormatAvailable(format))return null;var handle=NativeClip.GetClipboardData(format);if(NativeClip.GlobalSize(handle)<4)return 0;var p=NativeClip.GlobalLock(handle);if(p==0)return 0;try{return Marshal.ReadInt32(p);}finally{NativeClip.GlobalUnlock(handle);}}
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
        try{File.WriteAllBytes(input,png);if(NativeClip.PaneClipSaveImage(input,temporary,small,out int width,out int height)!=1)throw new InvalidDataException("图片解码失败"); // 不翻译：异常
            foreach(var path in new[]{temporary,small})using(var stream=new FileStream(path,FileMode.Open,FileAccess.Write,FileShare.None))stream.Flush(true);
            File.Move(temporary,full,true);File.Move(small,thumbnail,true);return(width,height);
        }finally{File.Delete(input);File.Delete(temporary);File.Delete(small);}
    }
}
internal sealed class WindowsClipboardWriter(Action<ClipEntry,ClipboardWriteMode,bool>? write=null,Func<IPastePlatform>? paste=null):IClipboardWriter
{
    public Task PasteAsync(ClipEntry entry,bool plainText,nint target,CancellationToken token)
    {
        long started=Environment.TickCount64; var platform=paste?.Invoke()??new WindowsPastePlatform();
        return Sta.Run(()=>{new PasteTransfer(platform,PasteTransfer.WriteLog).Run(target,()=>(write??Write)(entry,ClipboardWriteMode.HistoryPaste,plainText),token,started);return true;});
    }
    internal static void Write(ClipEntry entry,ClipboardWriteMode mode,bool plainText=false)
    {
        string? files=null,image=null,text=entry.Text;
        // TODO(design): 图片没有文本时，纯文本粘贴保守地报告不支持；由设计决定是否禁用该入口。
        if(plainText){text=entry.Kind==ClipKind.Files?string.Join(Environment.NewLine,entry.Files??[]):entry.Text;if(text==null)throw new NotSupportedException(Strings.Clipboard_NoPlainText);}
        else if(entry.Kind==ClipKind.Image){image=entry.ContentPath??throw new InvalidDataException(Strings.Clipboard_ImageMissing);text=null;}
        else if(entry.Kind==ClipKind.Files){files=string.Join('\0',entry.Files??[])+"\0\0";text=null;}
        if(NativeClip.PaneClipWrite(text,image,files,files?.Length??0,mode)!=1)throw new TimeoutException(Strings.Clipboard_WriteFailed);
    }
}
internal sealed class ClipboardAttachmentActions(ILauncherAttachmentActions other,Action<ClipEntry,ClipboardWriteMode,bool>? write=null):ILauncherAttachmentActions
{
    public Task RunAsync(string actionId,ClipEntry attachment,CancellationToken token)
    {
        if(attachment.Kind!=ClipKind.Image)return other.RunAsync(actionId,attachment,token);
        return Sta.Run(()=>{token.ThrowIfCancellationRequested();
            switch(actionId){
                case "copy-image":(write??WindowsClipboardWriter.Write)(attachment,ClipboardWriteMode.UserCopy,false);break;
                case "save-image":if(attachment.ContentPath==null)throw new InvalidDataException(Strings.Clipboard_ImageMissing);if(NativeClip.PaneClipSaveDialog(attachment.ContentPath,string.Format(Strings.Clipboard_ImageFileName, (object?)(attachment.CopiedAt.LocalDateTime)))==0)throw new IOException(Strings.Clipboard_SaveImageFailed);break;
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
    [DllImport(Dll,CharSet=CharSet.Unicode,SetLastError=true)] internal static extern int PaneClipWrite(string? text,string? image,string? files,int chars,ClipboardWriteMode mode);
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
