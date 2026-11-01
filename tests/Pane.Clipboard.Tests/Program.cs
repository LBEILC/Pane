using System.Buffers.Binary;
using System.IO.Compression;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using Pane.Contracts;
using Pane.Services.Clipboard;
using Pane.Settings;

static partial class Program
{
    static int checks;
    static void Check(bool value,string name){checks++;if(!value)throw new Exception(name);}
    static PaneSettings Defaults()=>new(1,new(false,true,ThemeMode.Dark,"#FF5874C9",20,1,new(.2,.2,1,1,1,0),new(.2,.2,1,1,1,0)),new(new(KeyModifiers.Ctrl,32)),new(new(KeyModifiers.Ctrl,86)));
    static async Task Main(string[] args)
    {
        if(args is ["--hidden-paste",var output]){HiddenPasteTests(output);return;}
        string native=Path.GetFullPath(args[0]);
        NativeLibrary.SetDllImportResolver(Assembly.GetExecutingAssembly(),(name,_,_)=>name=="Pane.Native.dll"?NativeLibrary.Load(native):0);
        if(args is [_,"production-fixture",var fixture]){
            string directory=Path.GetFullPath(fixture);if(Directory.Exists(directory))throw new InvalidOperationException("夹具目标已存在");
            Directory.CreateDirectory(directory);var store=new SettingsStore(directory,Defaults());
            store.Save(Defaults() with{Update=new(false)});
            File.WriteAllText(Path.Combine(directory,"045-fixture"),"synthetic");
            File.WriteAllBytes(Path.Combine(directory,"045-fixture.png"),Png(1280,720,45));return;
        }
        if(args.Length>1&&args[1]=="seed"){
            string directory=Path.GetFullPath(args[2]);if(Directory.Exists(Path.Combine(directory,"clipboard")))throw new InvalidOperationException("夹具目标已存在");
            var store=new SettingsStore(directory,Defaults());var source=new FakeSource();using var history=new ClipboardHistory(store,source,new FakeWriter(),new WindowsClipboardImages());await history.Ready;
            for(int i=0;i<450;i++)source.Emit(new(Text:$"fixture {i:D3} — 纯测试内容"));
            for(int i=0;i<50;i++)source.Emit(new(Png:Png(1280,720,i)));
            Check(history.Snapshot().Count==500&&history.Snapshot().Count(e=>e.Kind==ClipKind.Image)==50,"fixture count");
            Console.WriteLine("seed=500;images=50;systemClipboard=0");return;
        }
        foreach(var pair in new (string,ClipKind)[]{("hello",ClipKind.Text),(" https://example.test/a?q=1 ",ClipKind.Link),("http://example.test",ClipKind.Link),("https://a.test https://b.test",ClipKind.Text),("ftp://example.test",ClipKind.Text),("#abc",ClipKind.Color),("#ABCDEF",ClipKind.Color),("#88aabbcc",ClipKind.Color),("#abcd",ClipKind.Text),("#ggg",ClipKind.Text),("rgb(0, 255, 1)",ClipKind.Color),("rgba(0,1,2,.5)",ClipKind.Color),("rgba(0,1,2,1.0)",ClipKind.Color),("rgb(256,0,0)",ClipKind.Text),("rgb(0,0,0,1)",ClipKind.Text),("rgba(0,0,0)",ClipKind.Text),("rgba(0,0,0,2)",ClipKind.Text),("blue",ClipKind.Text),("public void X() {\n    return;\n}",ClipKind.Code),("def x():\n    return 1",ClipKind.Code),("购物清单\n    苹果;",ClipKind.Text),("text {\nwords; }",ClipKind.Text)})Check(ClipboardClassifier.Classify(pair.Item1)==pair.Item2,"classification");
        string root=Path.Combine(AppContext.BaseDirectory,"results",Guid.NewGuid().ToString("N"));var settings=new SettingsStore(root,Defaults());
        string json=JsonSerializer.Serialize(Defaults(),SettingsJson.Default.PaneSettings);
        using(var doc=JsonDocument.Parse(json)){
            string old="{"+string.Join(',',doc.RootElement.EnumerateObject().Select(p=>p.Name=="Clipboard"?"\"Clipboard\":{\"Hotkey\":{\"Modifiers\":\"Ctrl\",\"VirtualKey\":86}}":"\""+p.Name+"\":"+p.Value.GetRawText()))+"}";
            Directory.CreateDirectory(root);File.WriteAllText(Path.Combine(root,"settings.json"),old);bool corrupt=false;settings.CorruptFile+=(_,_)=>corrupt=true;settings.Load();
            var c=settings.Current.Clipboard;Check(!corrupt&&c.RecordText&&c.RecordImages&&c.RecordFiles&&c.Limit==500&&c.Dedupe&&c.MoveToTopAfterPaste&&c.HonorExcludeMarker&&c.ExcludedPrograms.Length==0,"old settings defaults");
        }
        foreach(var bad in new[]{settings.Current.Clipboard with{RecordText=false,RecordImages=false,RecordFiles=false},settings.Current.Clipboard with{Limit=3}}){try{settings.Save(settings.Current with{Clipboard=bad});Check(false,"invalid accepted");}catch(ArgumentException){Check(true,"invalid rejected");}}
        var source1=new FakeSource();var writer=new FakeWriter();var imageFiles=new WindowsClipboardImages();int events=0;string pinned,imageId;string? full;
        using(var history=new ClipboardHistory(settings,source1,writer,imageFiles)){
            await history.Ready;history.Changed+=(_,_)=>events++;
            foreach(var content in new[]{new ClipboardContent(Text:"secret",Exclude:true),new ClipboardContent(Text:"secret",History:0),new ClipboardContent(Text:"secret",Cloud:0),new ClipboardContent(Text:"secret",Own:true)})source1.Emit(content);
            Check(history.Snapshot().Count==0,"privacy excludes");
            settings.Save(settings.Current with{Clipboard=settings.Current.Clipboard with{HonorExcludeMarker=false,ExcludedPrograms=["BLOCKED.EXE"]}});
            source1.Emit(new(Text:"secret",Program:"blocked.exe"));Check(history.Snapshot().Count==0,"case insensitive exclusion");
            source1.Emit(new(Text:"accepted",Exclude:true,History:0,Cloud:0));Check(history.Snapshot().Count==1,"marker override");source1.Emit(new(Text:"own",Own:true));Check(history.Snapshot().Count==1,"own always excluded");
            settings.Save(settings.Current with{Clipboard=settings.Current.Clipboard with{HonorExcludeMarker=true}});
            source1.Emit(new(Text:"repeat"));var duplicate=history.Snapshot()[0];source1.Emit(new(Text:"repeat"));Check(history.Snapshot().Count==2&&history.Snapshot()[0].Id==duplicate.Id&&history.Snapshot()[0].CopiedAt>=duplicate.CopiedAt,"dedupe time and identity");
            settings.Save(settings.Current with{Clipboard=settings.Current.Clipboard with{Dedupe=false}});source1.Emit(new(Text:"repeat"));Check(history.Snapshot().Count==3,"dedupe disabled");
            history.ClearUnpinned();settings.Save(settings.Current with{Clipboard=settings.Current.Clipboard with{Dedupe=true,Limit=200}});
            source1.Emit(new(Text:"pinned"));pinned=history.Snapshot()[0].Id;history.SetPinned(pinned,true);
            for(int i=0;i<205;i++)source1.Emit(new(Text:"text "+i));
            var snapshot=history.Snapshot();Check(snapshot.Count==201&&snapshot[0].Id==pinned&&snapshot[1].Text=="text 204"&&!snapshot.Any(e=>e.Text=="text 0"),"limit excludes pinned and trims oldest");
            history.ClearUnpinned();Check(history.Snapshot().Count==1&&history.Snapshot()[0].Pinned,"clear keeps pin");
            source1.Emit(new(Files:[@"C:\fixture\one.txt",@"C:\fixture\two.txt"],Text:"ignored"));Check(history.Snapshot().First(e=>!e.Pinned).Kind==ClipKind.Files,"files priority");
            source1.Emit(new(Png:Png(640,360,1)));var image=history.Snapshot().First(e=>e.Kind==ClipKind.Image);imageId=image.Id;full=image.ContentPath;
            Check(image.ImageWidth==640&&image.ImageHeight==360&&File.Exists(full)&&File.Exists(image.Thumbnail!.Path),"full image thumbnail");
            await history.PasteAsync(pinned,false,123,default);Check(writer.Entry?.Id==pinned&&writer.Target==123&&!writer.Plain,"paste delegates target");
            await history.PasteAsync(imageId,true,123,default);Check(writer.Plain,"plain paste flag");
            var before=history.Snapshot().First(e=>e.Id==imageId).CopiedAt;settings.Save(settings.Current with{Clipboard=settings.Current.Clipboard with{MoveToTopAfterPaste=false}});await history.PasteAsync(imageId,false,123,default);Check(history.Snapshot().First(e=>e.Id==imageId).CopiedAt==before,"paste move disabled");
            source1.Emit(new(Text:"newer"));settings.Save(settings.Current with{Clipboard=settings.Current.Clipboard with{MoveToTopAfterPaste=true}});await history.PasteAsync(imageId,false,123,default);Check(history.Snapshot()[1].Id==imageId,"paste moves after pins");
            writer.Fail=true;before=history.Snapshot().First(e=>e.Id==imageId).CopiedAt;try{await history.PasteAsync(imageId,false,123,default);Check(false,"failure expected");}catch(TimeoutException){Check(history.Snapshot().First(e=>e.Id==imageId).CopiedAt==before,"failed paste no reorder");}writer.Fail=false;
            int count=history.Snapshot().Count;settings.Save(settings.Current with{Clipboard=settings.Current.Clipboard with{RecordText=false,RecordFiles=false}});source1.Emit(new(Text:"off"));source1.Emit(new(Files:["off"]));Check(history.Snapshot().Count==count,"record switches");
            Check(events>0,"changed notification");
        }
        Check(source1.Disposed,"source disposed");
        using(var history=new ClipboardHistory(settings,new FakeSource(),writer,imageFiles)){
            await history.Ready;Check(history.Snapshot().Any(e=>e.Id==pinned&&e.Pinned)&&history.Snapshot().Any(e=>e.Id==imageId&&e.ContentPath==full)&&history.Snapshot().Any(e=>e.Files?.Count==2),"storage roundtrip metadata files images pins");
            history.Delete(imageId);Check(!history.Snapshot().Any(e=>e.Id==imageId),"delete");
        }
        Check(!File.Exists(full),"delete image after durable index");
        using(var history=new ClipboardHistory(settings,new FakeSource(),writer,imageFiles)){
            await history.Ready;history.SetPinned(pinned,false);history.ClearUnpinned();Check(history.Snapshot().Count==0,"unpin and clear");
        }
        settings.Load();Check(!settings.Current.Clipboard.RecordText&&!settings.Current.Clipboard.RecordFiles&&settings.Current.Clipboard.Limit==200&&settings.Current.Clipboard.ExcludedPrograms[0]=="BLOCKED.EXE","new settings roundtrip");
        using(var source=new WindowsClipboardSource(settings,true)){source.Captured+=_=>throw new Exception("observe read content");source.Start();await Task.Delay(150);Check(source.Registered,"native register");source.Dispose();Check(source.Removed,"native unregister and thread join");}
        await SwitchTests();
        await PersistenceTests();
        await FileThumbnailTests();
        PasteTests();
        Console.WriteLine($"PASS clipboard assertions={checks};systemClipboardRead=0;systemClipboardWrite=0;listenerStopped=1");
    }
    internal static byte[] Png(int width,int height,int seed)
    {
        using var output=new MemoryStream();output.Write(new byte[]{137,80,78,71,13,10,26,10});
        byte[] header=new byte[13];BinaryPrimitives.WriteInt32BigEndian(header,width);BinaryPrimitives.WriteInt32BigEndian(header.AsSpan(4),height);header[8]=8;header[9]=6;Chunk(output,"IHDR",header);
        using var compressed=new MemoryStream();using(var z=new ZLibStream(compressed,CompressionLevel.Fastest,true)){
            byte[] row=new byte[width*4+1];for(int y=0;y<height;y++){for(int x=0;x<width;x++){int p=1+x*4;row[p]=(byte)(x+seed*3);row[p+1]=(byte)(y+seed*7);row[p+2]=(byte)(x/8+y/8+seed);row[p+3]=255;}z.Write(row);}
        }
        Chunk(output,"IDAT",compressed.ToArray());Chunk(output,"IEND",[]);return output.ToArray();
    }
    static void Chunk(Stream output,string type,byte[] data){Span<byte> value=stackalloc byte[4];BinaryPrimitives.WriteInt32BigEndian(value,data.Length);output.Write(value);byte[] tag=Encoding.ASCII.GetBytes(type);output.Write(tag);output.Write(data);uint crc=uint.MaxValue;foreach(byte b in tag.Concat(data)){crc^=b;for(int i=0;i<8;i++)crc=(crc&1)!=0?(crc>>1)^0xedb88320:crc>>1;}BinaryPrimitives.WriteUInt32BigEndian(value,~crc);output.Write(value);}
    sealed class FakeSource:IClipboardSource{public bool Disposed;public event Action<ClipboardContent>? Captured;public void Start(){}public void Stop(){}public void Emit(ClipboardContent value)=>Captured?.Invoke(value);public void Dispose()=>Disposed=true;}
    sealed class FakeWriter:IClipboardWriter{public ClipEntry? Entry;public bool Plain,Fail;public nint Target;public Task PasteAsync(ClipEntry entry,bool plain,nint target,CancellationToken token){token.ThrowIfCancellationRequested();if(Fail)throw new TimeoutException();Entry=entry;Plain=plain;Target=target;return Task.CompletedTask;}}
}
