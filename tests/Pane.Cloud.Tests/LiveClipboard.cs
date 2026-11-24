using System.Diagnostics;
using System.Security.Cryptography;
using Pane.Contracts;
using Pane.Services.Clipboard;
using Pane.Services.Cloud;
using Pane.Settings;

internal static partial class Program
{
    sealed class FixedTime(DateTimeOffset instant):TimeProvider { public override DateTimeOffset GetUtcNow()=>instant; }
    sealed class ClipFixture:IClipboardSource,IClipboardWriter
    {
        public event Action<ClipboardContent>? Captured;
        public int Writes;
        public void Start(){}
        public void Stop(){}
        public void Dispose(){}
        public void Copy(ClipboardContent c)=>Captured?.Invoke(c);
        public Task WriteReceived(ClipEntry e,RichFormats? formats,CancellationToken ct)
        {
            if(e.Kind==ClipKind.Image)Check(File.Exists(e.ContentPath),"自动写入时原图在本机");
            Copy(new(Text:e.Text,Png:e.ContentPath==null?null:File.ReadAllBytes(e.ContentPath),Own:true));
            Interlocked.Increment(ref Writes);return Task.CompletedTask;
        }
        public Task PasteAsync(ClipEntry e,bool plain,nint target,CancellationToken ct)
        {Check(File.Exists(e.ContentPath),"下载后原图可粘贴");return Task.CompletedTask;}
    }
    static async Task LiveClipboard(Func<string,string,SettingsStore> create,Func<string,bool,string> sql)
    {
        var a=create("a","clips");var b=create("b","clips");
        var fa=new ClipFixture();var fb=new ClipFixture();
        using var ha=new ClipboardHistory(a,fa,fa,new WindowsClipboardImages());using var hb=new ClipboardHistory(b,fb,fb,new WindowsClipboardImages());
        await Task.WhenAll(ha.Ready,hb.Ready);
        fa.Copy(new(Text:"before"));ha.SetPinned(ha.Snapshot()[0].Id,true);
        var faults=new Faults();
        await using var aa=new AccountService(a.DirectoryPath,"1.0",faults);await using var ab=new AccountService(b.DirectoryPath,"1.0");
        await using var sa=new SyncService(a,aa);await using var sb=new SyncService(b,ab);
        var options=new SyncOptions(Enabled:true,Usage:false,Settings:false,Clipboard:true,ExcludedApps:["private.exe"]);
        await sa.ApplyAsync(options,default);await sb.ApplyAsync(options,default);
        await Ready(sa);await Ready(sb);
        Task Wait(Func<bool> test,string msg)=>Until(()=>Task.FromResult(test()),msg,()=>sa.State+"/"+sa.ErrorCode+" "+sb.State+"/"+sb.ErrorCode);
        string Count(string condition)=>sql("SELECT COUNT(*) FROM pane_records WHERE uid='clips' AND "+condition,true);
        ClipEntry Find(ClipboardHistory h,string text)=>h.Snapshot().Single(c=>c.Text==text);
        var elapsed=Stopwatch.StartNew();fa.Copy(new(Text:"first",Source:"App"));
        await Wait(()=>hb.Snapshot().Any(c=>c.Text=="first" && c.SourceDevice=="fixture"),"文字与来源");
        Console.WriteLine($"clip latency={elapsed.ElapsedMilliseconds}ms");
        Check(elapsed.Elapsed.TotalSeconds<5 && Count("c='clip'")=="1","几秒内到达且旧固定条目未上传");
        Check((await ab.RenameDeviceAsync("a","Desk",default)).Ok,"设备改名");await sb.SyncNowAsync(default);
        Check(Find(hb,"first").SourceDevice=="Desk","显示当前设备名");
        fb.Copy(new(Text:"first"));await Wait(()=>ha.Snapshot().Count(c=>c.Text=="first")==2,"跨设备保留重复文字");
        string aid=ha.Snapshot().Single(c=>c.Text=="first" && c.SourceDevice==null).Id;
        string bid=hb.Snapshot().Single(c=>c.Text=="first" && c.SourceDevice!=null).Id;
        hb.SetTags(bid,["work"]);await Wait(()=>ha.Tags().Any(t=>t.Name=="work") && ha.Lookup(aid)!.Entry.Tags.Count==1,"标签和保留");
        ha.UpdateTag("work","sample",3);await Wait(()=>hb.Tags().Any(t=>t==new ClipTag("sample",3)),"标签改名改色");
        hb.DeleteTag("sample");await Wait(()=>ha.Tags().Count==0 && ha.Lookup(aid)!.Entry.Tags.Count==0,"删除标签");
        async Task Conflict(bool delete)
        {
            faults.Holding=false;faults.Block=new(TaskCreationOptions.RunContinuationsAsynchronously);
            ha.UpdateText(aid,"older"+delete);
            await Wait(()=>faults.Holding,"并发写入屏障");
            if(delete)hb.Delete(bid);else hb.UpdateText(bid,"newer");
            await sb.SyncNowAsync(default);faults.Block.SetResult();faults.Block=null;
            await Wait(()=>delete?ha.Lookup(aid)==null:ha.Lookup(aid)?.Entry.Text=="newer","删除优先或新编辑优先");
        }
        await Conflict(false);await Conflict(true);
        // WIC 将自制的 640 × 640 噪声位图转换为产品使用的 PNG，覆盖缩略图压缩分支。
        static byte[] NoiseBitmap(int side)
        {
            var pixels=RandomNumberGenerator.GetBytes(side*side*3);
            using var bitmap=new MemoryStream();
            using(var header=new BinaryWriter(bitmap,System.Text.Encoding.UTF8,true))
            {
                header.Write((ushort)0x4d42);header.Write(54+pixels.Length);header.Write(0);header.Write(54);
                header.Write(40);header.Write(side);header.Write(side);header.Write((ushort)1);header.Write((ushort)24);
                header.Write(new byte[24]);header.Write(pixels);
            }
            return bitmap.ToArray();
        }
        const int side=640;
        var png=NoiseBitmap(side);
        int serial=0;
        async Task<(ClipEntry A,ClipEntry B)> Image(byte[]? bitmapBytes=null)
        {
            string source="image"+(++serial);
            fa.Copy(new(Png:[..(bitmapBytes ?? png),(byte)serial],Source:source));
            await Wait(()=>hb.Snapshot().Any(c=>c.Source==source),"图片元数据");
            return(ha.Snapshot().Single(c=>c.Source==source),hb.Snapshot().Single(c=>c.Source==source));
        }
        var pic=await Image();
        Check(pic.B.Thumbnail==null && pic.B.Original==ClipOriginal.Remote && pic.B.ImageWidth==side,"图片占位");
        sb.EnsureThumbnail(pic.B.Id);sb.EnsureThumbnail(pic.B.Id);
        await Wait(()=>File.Exists(hb.Lookup(pic.B.Id)?.Entry.Thumbnail?.Path),"按需缩略图");
        Check(new FileInfo(hb.Lookup(pic.B.Id)!.Entry.Thumbnail!.Path!).Length<=256*1024-29,"缩略图密文大小");
        Check((await sb.FetchOriginalAsync(pic.B.Id,default)).Ok && sb.OriginalSize(pic.B.Id)==new FileInfo(pic.A.ContentPath!).Length && File.ReadAllBytes(pic.A.ContentPath!).SequenceEqual(File.ReadAllBytes(hb.Lookup(pic.B.Id)!.Entry.ContentPath!)),"按需原图大小");
        await hb.PasteAsync(pic.B.Id,false,0,default);
        hb.SetPinned(pic.B.Id,true);await Wait(()=>ha.Lookup(pic.A.Id)!.Entry.Pinned,"远端固定");
        await Wait(()=>sql("SELECT COUNT(*) FROM pane_blobs WHERE kept=1 AND kind='orig'",true)=="1","原图进入长期保存");
        var exp=await Image();string orig=hb.Lookup(exp.B.Id)!.Replica!.Orig!;
        sql($"UPDATE pane_blobs SET t=t-691200 WHERE id='{orig}'",false);
        Check((await sb.FetchOriginalAsync(exp.B.Id,default)).Error=="blob_expired" && hb.Lookup(exp.B.Id)!.Entry.Original==ClipOriginal.Expired,"原图过期");
        hb.SetPinned(exp.B.Id,true);await Wait(()=>ha.Lookup(exp.A.Id)!.Entry.Pinned,"过期原图拒绝后重提交");
        Check(File.Exists(ha.Lookup(exp.A.Id)!.Entry.ContentPath),"本机过期原图仍在");
        var quota=await Image();sql("UPDATE pane_downloads SET bytes=3000000000 WHERE uid='clips'",false);
        Check((await sb.FetchOriginalAsync(quota.B.Id,default)).Error=="quota_exceeded" && hb.Lookup(quota.B.Id)!.Entry.Original==ClipOriginal.QuotaExceeded,"额度用完");
        sb.EnsureThumbnail(quota.B.Id);await Wait(()=>hb.Lookup(quota.B.Id)!.Entry.Thumbnail!=null,"额度用完仍有缩略图");
        fa.Copy(new(Text:"quota text"));await Wait(()=>hb.Snapshot().Any(c=>c.Text=="quota text"),"额度用完仍同步文字");
        sql("UPDATE pane_downloads SET bytes=0 WHERE uid='clips'",false);
        hb.CloudLink.Time=new FixedTime(DateTimeOffset.UtcNow.AddMonths(1));
        Check(hb.Snapshot().Single(e=>e.Id==quota.B.Id).Original==ClipOriginal.Remote && (await sb.FetchOriginalAsync(quota.B.Id,default)).Ok,"跨月恢复原图下载");
        hb.CloudLink.Time=TimeProvider.System;
        sql("UPDATE pane_users SET storage_bytes=1073741824 WHERE id='clips'",false);
        var full=await Image();Check(full.B.Original==ClipOriginal.NotSynced && hb.Lookup(full.B.Id)!.Replica!.Thumb!=null,"存储满仍上传缩略图");
        sql("UPDATE pane_users SET storage_bytes=0 WHERE id='clips'",false);
        var large=await Image(NoiseBitmap(1984));
        Check(new FileInfo(large.A.ContentPath!).Length > 11*1024*1024 &&
            large.B.Original==ClipOriginal.NotSynced && hb.Lookup(large.B.Id)!.Replica!.Thumb!=null,"11 MB 原图保留在来源设备，另一台只有缩略图");
        await sa.ApplyAsync(options with{ClipScope=ClipSyncScope.Text},default);await Ready(sa);
        fa.Copy(new(Png:png,Source:"omit"));fa.Copy(new(Text:"secret",Program:"PRIVATE.EXE"));fa.Copy(new(Files:["sample.txt"]));
        await sa.SyncNowAsync(default);
        Check(ha.Snapshot().Any(c=>c.Text=="secret") && !hb.Snapshot().Any(c=>c.Source=="omit"||c.Text=="secret"||c.Kind==ClipKind.Files),"范围、程序、文件");
        await sa.ApplyAsync(options with{Clipboard=false},default);await Ready(sa);
        string count=Count("c='clip'");fa.Copy(new(Text:"off"));await sa.SyncNowAsync(default);Check(Count("c='clip'")==count,"关闭保留云端");
        await sa.ApplyAsync(options,default);await Ready(sa);await sb.ApplyAsync(options with{WriteToSystemClipboard=true},default);await Ready(sb);
        fa.Copy(new(Text:"auto"));await Wait(()=>fb.Writes==1,"假剪贴板只收到一次");
        await Image();await Wait(()=>fb.Writes==2,"图片下完自动写入");
        Check(hb.Snapshot().Count(c=>c.Text=="auto")==1 && ha.Snapshot().Count(c=>c.Text=="auto")==1,"写入无回环");
        var rich=RandomNumberGenerator.GetBytes(100000);fa.Copy(new(Text:"rich",Formats:new(rich,[])));
        await Wait(()=>hb.Snapshot().Any(c=>c.Text=="rich"),"大块富文本");
        Check(hb.Formats(hb.Lookup(Find(hb,"rich").Id)!)!.Html.SequenceEqual(rich),"富文本按字节还原");
        ha.SetTags(Find(ha,"rich").Id,["keep"]);await Wait(()=>Find(hb,"rich").Tags.Count==1,"标签保留");
        string tombs=sql("SELECT seq FROM pane_sync_state WHERE uid='clips'",true);hb.ClearUnpinned();
        await Wait(()=>!ha.Snapshot().Any(c=>!c.Pinned && c.Tags.Count==0),"清除本机含未上传条目");
        Check(Find(ha,"rich").Tags.Count==1 && ha.Lookup(pic.A.Id)!.Entry.Pinned && Count("c='clip' AND deleted=1 AND seq>"+tombs)=="0","保留固定和标签、无新墓碑");
        await sb.ApplyAsync(options with{Clipboard=false},default);await Ready(sb);
        // 同一次捕获批次，避免测试造数据触发线上限频。
        lock(ha.StorageGate)for(int i=0;i<205;i++)fa.Copy(new(Text:"batch"+i));
        await sa.SyncNowAsync(default);
        var c=create("c","clips");var fc=new ClipFixture();using var hc=new ClipboardHistory(c,fc,fc,new WindowsClipboardImages());await hc.Ready;
        await using var ac=new AccountService(c.DirectoryPath,"1.0");await using var sc=new SyncService(c,ac);
        await sc.ApplyAsync(options with{WriteToSystemClipboard=true},default);await Ready(sc);
        Check(hc.Snapshot().Count(c=>!c.Pinned && c.Tags.Count==0)==200 && hc.Snapshot().Count(c=>c.Pinned||c.Tags.Count>0)==3 && fc.Writes==0,"首次 200 条和全部保留，无自动写入");
        Console.WriteLine($"PASS clipboard sync assertions={checks}");
    }
}
