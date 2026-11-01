using System.Buffers.Binary;
using System.Diagnostics;
using Pane.Contracts;
using Pane.Services.Clipboard;
using Pane.Settings;

static partial class Program
{
    static async Task FileThumbnailTests()
    {
        int before=checks;string root=TestRoot();Directory.CreateDirectory(root);
        string image=Path.Combine(root,"fixture.png"),second=Path.Combine(root,"second.png"),text=Path.Combine(root,"plain.txt"),bad=Path.Combine(root,"bad.jpg"),large=Path.Combine(root,"large.png");
        byte[] original=Png(1280,720,9);File.WriteAllBytes(image,original);File.WriteAllBytes(second,Png(900,1200,2));File.WriteAllText(text,"fixture");File.WriteAllText(bad,"invalid");
        using(var file=File.Create(large))file.SetLength(50L*1024*1024+1);
        var codec=new WindowsClipboardImages();
        foreach(string path in new[]{text,bad,large,@"\\server\share\fixture.png",@"//server/share/fixture.png"})Check(codec.Thumbnail(path,Path.Combine(root,"reject.png"))==null,"skip unsupported file");
        var source=new FakeSource();var gate=new GatedImages(codec);
        var settings=new SettingsStore(root,Defaults());string id;
        using(var history=new ClipboardHistory(settings,source,new FakeWriter(),gate)){
            await history.Ready;int changed=0;history.Changed+=(_,_)=>Interlocked.Increment(ref changed);
            var watch=Stopwatch.StartNew();source.Emit(new(Files:[image]));
            Check(watch.ElapsedMilliseconds<500,"capture does not wait for decoder");
            Check(gate.Started.Wait(5000)&&history.Snapshot()[0].Thumbnail==null,"thumbnail deferred");
            gate.Release.Set();await Until(()=>history.Snapshot()[0].Thumbnail!=null);var entry=history.Snapshot()[0];id=entry.Id;
            Check(entry.Kind==ClipKind.Files&&entry.ContentPath==null&&entry.Files![0]==image,"file semantics unchanged");
            Check(entry.ImageWidth==1280&&entry.ImageHeight==720,"file image dimensions");
            byte[] thumb=File.ReadAllBytes(entry.Thumbnail!.Path!);
            Check(BinaryPrimitives.ReadInt32BigEndian(thumb.AsSpan(16,4))==640&&BinaryPrimitives.ReadInt32BigEndian(thumb.AsSpan(20,4))==360,"640 thumbnail");
            await Until(()=>changed>=2);Check(changed>=2,"thumbnail notifies");
            source.Emit(new(Files:[text,bad,image,second]));await Until(()=>history.Snapshot()[0].Thumbnail!=null);
            Check(history.Snapshot()[0].ImageWidth==1280,"first decodable image in mixed files");
            source.Emit(new(Files:[text]));Check(history.Snapshot()[0].Thumbnail==null,"non-image file retained");
        }
        Check(File.ReadAllBytes(image).SequenceEqual(original),"original bytes unchanged");
        Check(Directory.GetFiles(Path.Combine(root,"clipboard"),"*.png").All(p=>p.EndsWith(".thumb.png")),"no full copy for files");
        using(var history=new ClipboardHistory(settings,new FakeSource(),new FakeWriter(),codec)){
            await history.Ready;Check(history.Snapshot().Single(e=>e.Id==id).Thumbnail is {} t&&File.Exists(t.Path),"file thumbnail survives reload cleanup");
        }
        Console.WriteLine($"PASS file thumbnails assertions={checks-before}");
    }
    sealed class GatedImages(IClipboardImages inner):IClipboardImages
    {
        public readonly ManualResetEventSlim Started=new(),Release=new();
        public (int Width,int Height) Save(byte[] png,string full,string thumbnail)=>inner.Save(png,full,thumbnail);
        public (int Width,int Height)? Thumbnail(string path,string thumbnail){Started.Set();if(!Release.Wait(10000))throw new TimeoutException();return inner.Thumbnail(path,thumbnail);}
    }
}
