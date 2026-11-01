using System.Runtime.InteropServices;
using Pane.Contracts;
namespace Pane.Services;
internal interface IFileSelectionPlatform
{
    bool Editing(nint window);
    IReadOnlyList<string> Read(nint window);
}
internal sealed class FileSelection(IFileSelectionPlatform? platform=null) : IFileSelection
{
    readonly IFileSelectionPlatform shell=platform??new WindowsFileSelectionPlatform();
    internal IReadOnlyList<string> Read(nint window,bool textFirst=false)=>textFirst&&shell.Editing(window)?[]:shell.Read(window);
    public Task<IReadOnlyList<string>> ReadAsync(nint window,CancellationToken cancellationToken)=>Sta.Run(()=>{cancellationToken.ThrowIfCancellationRequested();var result=Read(window);cancellationToken.ThrowIfCancellationRequested();return result;});
}
internal sealed class WindowsFileSelectionPlatform : IFileSelectionPlatform
{
    public bool Editing(nint window)=>PaneFileSelectionEditing(window)!=0;
    public IReadOnlyList<string> Read(nint window){
        var buffer=PaneFileSelectionRead(window);if(buffer==0)return [];
        try{var paths=new List<string>();nint cursor=buffer;while(Marshal.ReadInt16(cursor)!=0){string path=Marshal.PtrToStringUni(cursor)!;paths.Add(path);cursor+=(path.Length+1)*2;}return paths;}
        finally{Marshal.FreeCoTaskMem(buffer);}
    }
    [DllImport("Pane.Native.dll")] static extern nint PaneFileSelectionRead(nint window);
    [DllImport("Pane.Native.dll")] static extern int PaneFileSelectionEditing(nint window);
}
