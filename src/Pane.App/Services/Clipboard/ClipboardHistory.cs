using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using Pane.Contracts;
using Pane.Settings;
namespace Pane.Services.Clipboard;

internal sealed record ClipboardContent(string? Text=null, string[]? Files=null, byte[]? Png=null,
    string Program="", string Source="", string? Icon=null, bool Exclude=false,
    int? History=null, int? Cloud=null, bool Own=false);
internal interface IClipboardSource : IDisposable
{
    event Action<ClipboardContent>? Captured;
    void Start();
    void Stop();
}
internal interface IClipboardWriter
{
    Task PasteAsync(ClipEntry entry,bool plainText,nint target,CancellationToken token);
}
internal interface IClipboardImages
{
    (int Width,int Height) Save(byte[] png,string full,string thumbnail);
    (int Width,int Height)? Thumbnail(string path,string thumbnail)=>null;
}
internal sealed record StoredClip(ClipEntry Entry,string Fingerprint);
[JsonSerializable(typeof(StoredClip[]))]
internal partial class ClipboardJson:JsonSerializerContext { }

internal static partial class ClipboardClassifier
{
    [GeneratedRegex(@"^#(?:[0-9a-fA-F]{3}|[0-9a-fA-F]{6}|[0-9a-fA-F]{8})$")]
    private static partial Regex Hex();
    [GeneratedRegex(@"^(rgb|rgba)\(\s*(\d{1,3})\s*,\s*(\d{1,3})\s*,\s*(\d{1,3})(?:\s*,\s*(0|1|0?\.\d+|1\.0+))?\s*\)$",RegexOptions.IgnoreCase)]
    private static partial Regex Rgb();
    [GeneratedRegex(@"\b(class|public|private|return|function|const|let|var|if|for|while|def|import)\b")]
    private static partial Regex Keyword();
    public static ClipKind Classify(string value)
    {
        string s=value.Trim();
        if(!s.Any(char.IsWhiteSpace)&&Uri.TryCreate(s,UriKind.Absolute,out var uri)&&uri.Scheme is "http" or "https"&&uri.Host.Length>0)return ClipKind.Link;
        var m=Rgb().Match(s);
        if(Hex().IsMatch(s)||m.Success&&Enumerable.Range(2,3).All(i=>int.Parse(m.Groups[i].Value)<=255)&&m.Groups[5].Success==m.Groups[1].Value.Equals("rgba",StringComparison.OrdinalIgnoreCase))return ClipKind.Color;
        // 宁可少判：多行、缩进、关键字，再加成对花括号和分号，或 Python 冒号块。
        if(s.Contains('\n')&&value.Split('\n').Any(l=>l.StartsWith("    ")||l.StartsWith('\t'))&&Keyword().IsMatch(s)&&
            (s.Contains('{')&&s.Contains('}')&&s.Contains(';')||s.Split('\n')[0].TrimEnd().EndsWith(':')))return ClipKind.Code;
        return ClipKind.Text;
    }
    public static bool Allowed(ClipboardContent c,ClipboardSettings s)=>!c.Own&&
        !(s.HonorExcludeMarker&&(c.Exclude||c.History==0||c.Cloud==0))&&
        !s.ExcludedPrograms.Contains(c.Program,StringComparer.OrdinalIgnoreCase);
}

internal sealed class ClipboardHistory:IClipboardHistory,IDisposable
{
    readonly object sync=new();
    readonly SettingsStore settings;
    readonly IClipboardSource source;
    readonly IClipboardWriter writer;
    readonly IClipboardImages images;
    readonly string directory,index;
    readonly List<StoredClip> entries=[];
    readonly AutoResetEvent signal=new(false);
    readonly ManualResetEvent shutdown=new(false);
    readonly Thread persistence;
    readonly ClipboardLog log;
    readonly IClipboardStorage storage;
    readonly HashSet<string> pendingThumbnails=new(StringComparer.OrdinalIgnoreCase);
    readonly List<Task> thumbnailTasks=[];
    string? storageProblem;
    public string? StorageProblem {get{lock(sync)return storageProblem;}}
    bool stopping,dirty;
    readonly object lifecycle=new();
    bool listening;
    public int StorageFailures {get;private set;}
    public Task Ready {get;}
    public event EventHandler? Changed;
    public ClipboardHistory(SettingsStore settings,IClipboardSource source,IClipboardWriter writer,IClipboardImages images,IClipboardStorage? storage=null)
    {
        this.settings=settings;this.source=source;this.writer=writer;this.images=images;
        directory=Path.Combine(settings.DirectoryPath,"clipboard");index=Path.Combine(directory,"history.json");
        this.storage=storage??new ClipboardStorage();log=new(settings.DirectoryPath);
        var ready=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);Ready=ready.Task;
        persistence=new Thread(()=>{
            try{
                Directory.CreateDirectory(directory);
                var loaded=this.storage.Load(index);
                lock(sync)entries.AddRange(loaded);
                log.Write("loaded",count:loaded.Length);
                try{Cleanup(loaded,true);}catch(Exception e){log.Write("cleanup-failed",e,"startup-cleanup");}
                source.Captured+=Capture;settings.Changed+=SettingsChanged;SettingsChanged(this,EventArgs.Empty);
                ready.SetResult();
            }catch(Exception e){log.Write("load-failed",e,"load");ready.SetException(e);return;}
            PersistLoop();
        }){IsBackground=true,Name="Pane clipboard storage"};
        persistence.Start();
    }
    void SettingsChanged(object? sender,EventArgs e)
    {
        // 停止监听会等待回调退出，不可持有回调所需的历史锁。
        lock(lifecycle){
            bool enabled=settings.Current.Clipboard.Enabled;
            if(enabled!=listening){if(enabled)source.Start();else source.Stop();listening=enabled;}
            if(enabled)lock(sync){int count=entries.Count;Trim();if(entries.Count!=count)Schedule();}
        }
        Changed?.Invoke(this,EventArgs.Empty);
    }
    public IReadOnlyList<ClipEntry> Snapshot(){lock(sync)return entries.Select(e=>e.Entry).OrderByDescending(e=>e.Pinned).ThenByDescending(e=>e.CopiedAt).ToArray();}
    internal void Capture(ClipboardContent content)
    {
        var s=settings.Current.Clipboard;if(!s.Enabled||!ClipboardClassifier.Allowed(content,s))return;
        ClipKind kind;string fingerprint;
        if(content.Files is {Length:>0}){if(!s.RecordFiles)return;kind=ClipKind.Files;fingerprint="f:"+Hash(Encoding.UTF8.GetBytes(string.Join('\0',content.Files)));}
        else if(content.Png is {Length:>0}){if(!s.RecordImages)return;kind=ClipKind.Image;fingerprint="i:"+Hash(content.Png);}
        else if(!string.IsNullOrEmpty(content.Text)){if(!s.RecordText)return;kind=ClipboardClassifier.Classify(content.Text);fingerprint="t:"+Hash(Encoding.UTF8.GetBytes(content.Text));}
        else return;
        lock(sync){
            if(stopping||!settings.Current.Clipboard.Enabled)return;
            var now=DateTimeOffset.UtcNow;var latest=entries.MaxBy(e=>e.Entry.CopiedAt);
            if(s.Dedupe&&latest?.Fingerprint==fingerprint){entries[entries.IndexOf(latest)]=latest with{Entry=latest.Entry with{CopiedAt=now}};}
            else {
                string id=Guid.NewGuid().ToString("N");string? full=null,thumb=null;int? width=null,height=null;
                if(kind==ClipKind.Image){full=Path.Combine(directory,id+".png");thumb=Path.Combine(directory,id+".thumb.png");var size=images.Save(content.Png!,full,thumb);width=size.Width;height=size.Height;}
                entries.Add(new(new(id,kind,kind==ClipKind.Image?null:content.Text,kind==ClipKind.Files?content.Files?.ToArray():null,
                    thumb==null?null:new(thumb),width,height,content.Source,content.Icon==null?null:new("app",content.Icon),now,false,full),fingerprint));
                Trim();
                if(kind==ClipKind.Files){
                    string thumbnail=Path.Combine(directory,id+".thumb.png");
                    pendingThumbnails.Add(thumbnail);
                    thumbnailTasks.RemoveAll(t=>t.IsCompleted);
                    thumbnailTasks.Add(Task.Run(()=>CreateFileThumbnail(id,content.Files!.ToArray(),thumbnail)));
                }
            }
            Schedule();
        }
        Changed?.Invoke(this,EventArgs.Empty);
    }
    static string Hash(byte[] bytes)=>Convert.ToHexString(SHA256.HashData(bytes));
    void Trim(){var remove=entries.Where(e=>!e.Entry.Pinned).OrderByDescending(e=>e.Entry.CopiedAt).Skip(settings.Current.Clipboard.Limit).ToHashSet();entries.RemoveAll(remove.Contains);}
    void Schedule(){dirty=true;signal.Set();}
    public void SetPinned(string id,bool pinned)=>Change(id,e=>e with{Pinned=pinned});
    void Change(string id,Func<ClipEntry,ClipEntry> update){lock(sync){int i=entries.FindIndex(e=>e.Entry.Id==id);if(i<0)return;entries[i]=entries[i] with{Entry=update(entries[i].Entry)};Trim();Schedule();}Changed?.Invoke(this,EventArgs.Empty);}
    public void Delete(string id){lock(sync){entries.RemoveAll(e=>e.Entry.Id==id);Schedule();}Changed?.Invoke(this,EventArgs.Empty);}
    public void ClearUnpinned(){lock(sync){entries.RemoveAll(e=>!e.Entry.Pinned);Schedule();}Changed?.Invoke(this,EventArgs.Empty);}
    public async Task PasteAsync(string id,bool plainText,nint targetWindow,CancellationToken cancellationToken)
    {
        await Ready.ConfigureAwait(false);ClipEntry entry;lock(sync)entry=entries.First(e=>e.Entry.Id==id).Entry;
        await writer.PasteAsync(entry,plainText,targetWindow,cancellationToken).ConfigureAwait(false);
        if(settings.Current.Clipboard.MoveToTopAfterPaste)Change(id,e=>e with{CopiedAt=DateTimeOffset.UtcNow});
    }
    void CreateFileThumbnail(string id,string[] files,string thumbnail)
    {
        try{
            foreach(string path in files){
                var size=images.Thumbnail(path,thumbnail);if(size==null)continue;
                lock(sync){
                    int i=entries.FindIndex(e=>e.Entry.Id==id);
                    if(i>=0&&!stopping){entries[i]=entries[i] with{Entry=entries[i].Entry with{Thumbnail=new(thumbnail),ImageWidth=size.Value.Width,ImageHeight=size.Value.Height}};Schedule();}
                    else File.Delete(thumbnail);
                }
                NotifyChanged();break;
            }
        }catch(Exception e){log.Write("thumbnail-failed",e,"thumbnail");}
        finally{lock(sync)pendingThumbnails.Remove(thumbnail);}
    }
    void Cleanup(StoredClip[] snapshot,bool startup=false)
    {
        lock(sync){
            var keep=entries.Concat(snapshot).SelectMany(e=>new[]{e.Entry.ContentPath,e.Entry.Thumbnail?.Path,e.Entry.SourceIcon?.ImagePath})
                .Where(p=>p!=null).ToHashSet(StringComparer.OrdinalIgnoreCase);
            keep.UnionWith(pendingThumbnails);
            foreach(var path in Directory.EnumerateFiles(directory)){
                string extension=Path.GetExtension(path);
                if(extension.Equals(".png",StringComparison.OrdinalIgnoreCase)&&!keep.Contains(path)||startup&&extension is ".input" or ".tmp")File.Delete(path);
            }
        }
    }
    void NotifyChanged()
    {
        // 订阅方失败不能杀死保存线程，日志不记录异常消息中的用户内容。
        foreach(var handler in Changed?.GetInvocationList()??[])
            try{((EventHandler)handler)(this,EventArgs.Empty);}catch(Exception e){log.Write("notification-failed",e,"notify");}
    }
    void PersistLoop()
    {
        log.Write("loop-start");int failures=0;
        while(true){
            string step="wait";
            try{
                if(failures==0){signal.WaitOne();shutdown.WaitOne(200);}
                else shutdown.WaitOne(RetryDelay(failures));
                step="snapshot";
                StoredClip[]? snapshot;
                lock(sync){snapshot=dirty?entries.ToArray():null;dirty=false;}
                if(snapshot!=null){
                    storage.Save(index,snapshot,value=>step=value);
                    step="cleanup";Cleanup(snapshot);
                    if(failures>0)log.Write("save-recovered",count:failures);
                    failures=0;bool changed;
                    lock(sync){changed=storageProblem!=null;storageProblem=null;}
                    if(changed)NotifyChanged();
                }
            }catch(Exception e){
                failures++;lock(sync){StorageFailures++;dirty=true;storageProblem=Problem(e,failures);}
                log.Write("save-failed",e,step,failures);NotifyChanged();
            }
            lock(sync){
                // 退出时至多完成当前尝试；失败保留旧索引，避免退出无限等待。
                if(stopping&&(!dirty||failures>0))break;
                if(dirty&&failures==0)signal.Set();
            }
        }
        log.Write("loop-exit",count:failures);
    }
    internal static int RetryDelay(int failures)=>(int)Math.Min(10000,250*Math.Pow(2,Math.Min(failures-1,6)));
    static string Problem(Exception e,int failures)
    {
        // TODO(design): 审阅其他异常的保守提示，以及从首次失败开始显示的时机。
        string reason=e switch{UnauthorizedAccessException=>"没有写入权限",IOException when (e.HResult&0xffff) is 32 or 33=>"文件被占用",IOException=>"磁盘读写错误",_=>"程序出错（"+e.GetType().Name+"）"};   // 设计审（045）：未知异常带上类型名，用户截图反馈就能定位
        return $"保存失败：{reason}，已失败 {failures} 次";
    }
    public void Dispose()
    {
        try{Ready.GetAwaiter().GetResult();}finally{
            source.Captured-=Capture;settings.Changed-=SettingsChanged;source.Dispose();
            Task[] tasks;lock(sync){tasks=thumbnailTasks.ToArray();}
            Task.WhenAll(tasks).GetAwaiter().GetResult();
            lock(sync){stopping=true;signal.Set();shutdown.Set();}
            persistence.Join();signal.Dispose();shutdown.Dispose();
        }
    }
}