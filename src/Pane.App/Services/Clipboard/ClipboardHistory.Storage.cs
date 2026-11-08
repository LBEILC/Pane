using Pane.Contracts;

namespace Pane.Services.Clipboard;

internal sealed partial class ClipboardHistory
{
    string imageDirectory="";
    internal object StorageGate=>sync;
    internal StoredClip[] StorageSnapshot()=>entries.ToArray(); // 调用方持有 StorageGate。
    internal void StorageMoved(StoredClip[] moved,string target)
    {
        entries.Clear();entries.AddRange(moved);imageDirectory=target;dirty=false;
    }
    internal void StorageNotify()=>NotifyChanged();
    void TrimImages()
    {
        long megabytes=settings.Current.Clipboard.ImageLimitMegabytes;
        if(megabytes<=0)return;
        long limit=megabytes>long.MaxValue/(1024*1024)?long.MaxValue:megabytes*1024*1024;
        var images=entries.Where(e=>e.Entry.Kind==ClipKind.Image&&!e.Entry.Pinned).OrderBy(e=>e.Entry.CopiedAt)
            .Select(e=>(Clip:e,Bytes:FileBytes(e.Entry.ContentPath)+FileBytes(e.Entry.Thumbnail?.Path))).ToArray();
        long total=images.Sum(e=>e.Bytes);
        foreach(var item in images){if(total<=limit)break;entries.Remove(item.Clip);total-=item.Bytes;}
        // 原图由持久化线程在历史成功落盘后清理，避免索引失败时丢图。
    }
    static long FileBytes(string? path)
    {
        try{return path==null?0:new FileInfo(path).Length;}catch(IOException){return 0;}catch(UnauthorizedAccessException){return 0;}
    }
}
