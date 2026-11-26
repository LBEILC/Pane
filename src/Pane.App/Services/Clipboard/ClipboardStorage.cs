using System.Text.Json;
namespace Pane.Services.Clipboard;

// 服务内部的存储替身入口，不改变界面契约。
internal interface IClipboardStorage
{
    ClipboardIndex LoadIndex(string index);
    void SaveIndex(string index,ClipboardIndex state,Action<string> step);
}
internal sealed class ClipboardStorage:IClipboardStorage
{
    public StoredClip[] Load(string index)=>LoadIndex(index).Entries;
    public ClipboardIndex LoadIndex(string index)
    {
        // File.Exists 会将部分访问失败当成不存在；不能因此把原图误当孤立文件清理。
        try{
            using var file=File.OpenRead(index);
            using var doc=JsonDocument.Parse(file);
            ClipboardIndex? state;
            if(doc.RootElement.ValueKind==JsonValueKind.Array){
                var legacy=doc.RootElement.Deserialize(ClipboardJson.Default.StoredClipArray)??throw new InvalidDataException();
                state = new(2,[],legacy.Select(c=>c with{FormatId=null,Entry=c.Entry with{Tags=[],Rich=false}}).ToArray());
            }
            else state=doc.RootElement.Deserialize(ClipboardJson.Default.ClipboardIndex);
            if (state is not {Version:2,Entries:not null,Tags:not null}) throw new InvalidDataException("历史索引版本或内容无效");
            string folder = Path.GetDirectoryName(index)!;
            for (int i = 0; i < state.Entries.Length; i++)
            {
                var clip = state.Entries[i];
                var entry = clip.Entry;
                if (!Guid.TryParseExact(entry.Id, "N", out _)) continue;
                var full = MovedImage(folder, entry.ContentPath, entry.Id + ".png");
                var thumb = MovedImage(folder, entry.Thumbnail?.Path, entry.Id + ".thumb.png");
                if (full != entry.ContentPath || thumb != entry.Thumbnail?.Path)
                    state.Entries[i] = clip with { Entry = entry with { ContentPath = full, Thumbnail = thumb == null ? null : new(thumb) } };
            }
            return state;
        }catch(FileNotFoundException){return new(2,[],[]);}catch(DirectoryNotFoundException){return new(2,[],[]);}
    }
    // 旧索引记绝对路径。便携目录搬家后，先认回索引旁同一条记录的图片，避免启动清理将其误删。
    static string? MovedImage(string folder, string? path, string name)
    {
        if (path == null || !Path.IsPathFullyQualified(path) ||
            !string.Equals(Path.GetFileName(path),name,StringComparison.OrdinalIgnoreCase)) return path;
        string local = Path.Combine(folder,name);
        return !string.Equals(local,path,StringComparison.OrdinalIgnoreCase) && File.Exists(local) ? local : path;
    }

    public void Save(string index,StoredClip[] entries,Action<string> step)=>SaveIndex(index,new(2,[],entries),step);
    public void SaveIndex(string index,ClipboardIndex state,Action<string> step)
    {
        step("create-directory");Directory.CreateDirectory(Path.GetDirectoryName(index)!);
        string temp=index+".tmp";step("open-temp");
        using(var file=new FileStream(temp,FileMode.Create,FileAccess.Write,FileShare.None)){
            step("serialize");JsonSerializer.Serialize(file,state,ClipboardJson.Default.ClipboardIndex);
            step("flush");file.Flush(true);
        }
        step("replace-index");File.Move(temp,index,true);
    }
}
internal sealed class ClipboardLog(string directory)
{
    static readonly object gate=new();
    internal const int Limit=256*1024;
    public void Skip(string reason,string program)=>Write("capture-skipped",step:reason,program:program);
    public void Write(string name,Exception? error=null,string? step=null,int? count=null,string? program=null)
    {
        try{lock(gate){
            string path=Path.Combine(directory,"logs","clipboard.log");
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            // 不写 Message、StackTrace、路径或输入；事件和步骤仅由内部常量传入。
            string line=$"{DateTimeOffset.UtcNow:O} event={name}"+
                (program==null?"":$" program={CleanProgram(program)}")+(step==null?"":$" step={step}")+(count==null?"":$" count={count}")+
                (error==null?"":$" type={error.GetType().Name}"+(name=="capture-failed"?"":$" hresult=0x{error.HResult:X8}"))+Environment.NewLine;
            if(File.Exists(path)&&new FileInfo(path).Length+System.Text.Encoding.UTF8.GetByteCount(line)>Limit)File.Move(path,path+".1",true);
            File.AppendAllText(path,line);
        }}catch(Exception){/* 日志介质失败不能中止监听或保存。 */}
    }
    static string CleanProgram(string value)=>new string(value.Take(260).Select(c=>char.IsControl(c)||c==' '?'_':c).ToArray());
}
