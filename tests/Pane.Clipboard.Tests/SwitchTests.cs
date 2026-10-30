using System.Diagnostics;
using Pane.Contracts;
using Pane.Hosting;
using Pane.Services.Clipboard;
using Pane.Settings;

static partial class Program
{
    static async Task SwitchTests()
    {
        int before=checks;
        string root=Path.Combine(AppContext.BaseDirectory,"results",Guid.NewGuid().ToString("N"));
        var store=new SettingsStore(root,Defaults());var source=new SwitchSource();var images=new SwitchImages();
        string index=Path.Combine(root,"clipboard","history.json");
        using(var history=new ClipboardHistory(store,source,new FakeWriter(),images)){
            await history.Ready;
            var attachment=new LauncherClipboardHistory(history,store);
            Check(source.Starts==1&&source.Active,"enabled listens");
            source.Emit(new(Text:"before"));source.Emit(new(Png:[1,2,3]));
            await Task.Delay(550);
            byte[] bytes=File.ReadAllBytes(index);var stamp=File.GetLastWriteTimeUtc(index);
            var files=Directory.GetFiles(Path.GetDirectoryName(index)!).Order().ToArray();
            Check(attachment.Snapshot().Count==2,"enabled attachment");
            store.Save(store.Current with{Clipboard=store.Current.Clipboard with{Enabled=false}});
            Check(!source.Active&&source.Stops==1,"disabled removes listener immediately");
            source.Emit(new(Text:"disabled"));history.Capture(new(Png:[4,5,6]));
            Check(history.Snapshot().Count==2&&images.Saves==1,"disabled rejects late callback and image cache");
            Check(attachment.Snapshot().Count==0,"disabled auto attachment empty");
            store.Save(store.Current with{Clipboard=store.Current.Clipboard with{Limit=200}});
            await Task.Delay(550);
            Check(bytes.SequenceEqual(File.ReadAllBytes(index))&&stamp==File.GetLastWriteTimeUtc(index),"disable and settings leave history untouched");
            Check(files.SequenceEqual(Directory.GetFiles(Path.GetDirectoryName(index)!).Order()),"disabled creates no cache");
            store.Save(store.Current with{Clipboard=store.Current.Clipboard with{Enabled=true}});
            Check(source.Active&&source.Starts==2,"reenable registers listener");
            Check(history.Snapshot().Count==2,"reenable does not backfill");
            source.Emit(new(Text:"after"));Check(history.Snapshot().Count==3,"reenable records new content");
            store.Save(store.Current with{Clipboard=store.Current.Clipboard with{Enabled=false}});
            history.ClearUnpinned();Check(history.Snapshot().Count==0,"disabled clear remains available");
        }
        byte[] saved=File.ReadAllBytes(index);var savedStamp=File.GetLastWriteTimeUtc(index);
        var cold=new SwitchSource();
        using(var history=new ClipboardHistory(store,cold,new FakeWriter(),images)){
            await history.Ready;Check(cold.Starts==0,"disabled startup never listens");
        }
        Check(saved.SequenceEqual(File.ReadAllBytes(index))&&savedStamp==File.GetLastWriteTimeUtc(index),"disabled startup and shutdown preserve disk");

        var ids=new List<int>();var calls=new List<string>();
        void Register(int id,string name,Hotkey key){ids.Add(id);calls.Add("+"+id);}
        void Apply(bool enabled,bool suspended=false)=>ShellHotkeyBindings.Rebind(ids,id=>calls.Add("-"+id),Register,store.Current.Launcher.Hotkey,store.Current.Clipboard.Hotkey,enabled,suspended);
        Apply(true);Check(ids.SequenceEqual(new[]{1,2}),"hotkeys enabled");calls.Clear();
        Apply(false);Check(calls.Contains("-2")&&!calls.Contains("+2")&&ids.SequenceEqual(new[]{1}),"disabled unregisters id 2 without registration or conflict");
        calls.Clear();Apply(true);Check(calls.Contains("+2"),"reenable registers id 2");
        Apply(false,true);Check(ids.Count==0,"suspended disabled no hotkeys");
        var notices=new HashSet<string>();int opened=0;
        foreach(string entry in new[]{"single-instance","tray","launcher","winv"}){
            if(ClipboardAccess.Allow("show-clipboard",false,text=>notices.Add(text)))opened++;
            Check(opened==0,"disabled entry: "+entry);
        }
        Check(notices.SetEquals(new[]{ClipboardAccess.DisabledNotice}),"one existing NotifyOnce notification category");
        Check(ClipboardAccess.Allow("show-clipboard",true,_=>throw new Exception()),"reenable entry");
        Check(ClipboardAccess.Allow("show-super",false,_=>throw new Exception()),"super entry unaffected");
        Check(Enumerable.Range(0,6).Select(i=>ClipboardAccess.Cycle(i,false)).SequenceEqual(new[]{"launcher","super","launcher","super","launcher","super"}),"cycle skips clipboard");
        Check(ClipboardAccess.Cycle(1,true)=="clipboard","enabled cycle retains clipboard");
        Check(Defaults().Clipboard.Enabled,"default enabled");

        // 观察模式只接收通知，绝不读取内容。空闲各测一次，不启动产品或注册热键。
        using(var observe=new WindowsClipboardSource(store,true)){
            using var process=Process.GetCurrentProcess();
            observe.Start();await Task.Delay(200);process.Refresh();var cpu=process.TotalProcessorTime;int threads=process.Threads.Count;
            await Task.Delay(1000);process.Refresh();double on=(process.TotalProcessorTime-cpu).TotalMilliseconds;
            observe.Stop();await Task.Delay(200);process.Refresh();cpu=process.TotalProcessorTime;int offThreads=process.Threads.Count;
            await Task.Delay(1000);process.Refresh();double off=(process.TotalProcessorTime-cpu).TotalMilliseconds;
            Check(observe.Removed&&!observe.Registered,"idle measurement listener stopped");
            Console.WriteLine($"idle once: enabledCpuMs={on:F3};disabledCpuMs={off:F3};enabledThreads={threads};disabledThreads={offThreads};contentReads=0");
        }
        Console.WriteLine($"PASS clipboard switch assertions={checks-before};realHotkeys=0");
    }
    sealed class SwitchSource:IClipboardSource
    {
        public bool Active;public int Starts,Stops;
        public event Action<ClipboardContent>? Captured;
        public void Start(){Active=true;Starts++;}
        public void Stop(){Active=false;Stops++;}
        public void Emit(ClipboardContent value){if(Active)Captured?.Invoke(value);}
        public void Dispose()=>Stop();
    }
    sealed class SwitchImages:IClipboardImages
    {
        public int Saves;
        public (int Width,int Height) Save(byte[] png,string full,string thumbnail){Saves++;File.WriteAllBytes(full,png);File.WriteAllBytes(thumbnail,png);return(1,1);}
    }
}
