using System.Text.Json;
namespace Pane.Services.Clipboard;

// 服务内部的存储替身入口，不改变界面契约。
internal interface IClipboardStorage
{
    StoredClip[] Load(string index);
    void Save(string index,StoredClip[] entries,Action<string> step);
}
internal sealed class ClipboardStorage:IClipboardStorage
{
    public StoredClip[] Load(string index)
    {
        // File.Exists 会将部分访问失败当成不存在；不能因此把原图误当孤立文件清理。
        try{
            using var file=File.OpenRead(index);
            return JsonSerializer.Deserialize(file,ClipboardJson.Default.StoredClipArray)??throw new InvalidDataException("历史索引为空"); // 不翻译：日志异常
        }catch(FileNotFoundException){return [];}
    }
    public void Save(string index,StoredClip[] entries,Action<string> step)
    {
        step("create-directory");Directory.CreateDirectory(Path.GetDirectoryName(index)!);
        string temp=index+".tmp";step("open-temp");
        using(var file=new FileStream(temp,FileMode.Create,FileAccess.Write,FileShare.None)){
            step("serialize");JsonSerializer.Serialize(file,entries,ClipboardJson.Default.StoredClipArray);
            step("flush");file.Flush(true);
        }
        step("replace-index");File.Move(temp,index,true);
    }
}
internal sealed class ClipboardLog(string directory)
{
    static readonly object gate=new();
    internal const int Limit=256*1024;
    public void Write(string name,Exception? error=null,string? step=null,int? count=null)
    {
        try{lock(gate){
            string path=Path.Combine(directory,"logs","clipboard.log");
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            // 不写 Message、StackTrace、路径或输入；事件和步骤仅由内部常量传入。
            string line=$"{DateTimeOffset.UtcNow:O} event={name}"+
                (step==null?"":$" step={step}")+(count==null?"":$" count={count}")+
                (error==null?"":$" type={error.GetType().Name}"+(name=="capture-failed"?"":$" hresult=0x{error.HResult:X8}"))+Environment.NewLine;
            if(File.Exists(path)&&new FileInfo(path).Length+System.Text.Encoding.UTF8.GetByteCount(line)>Limit)File.Move(path,path+".1",true);
            File.AppendAllText(path,line);
        }}catch(Exception){/* 日志介质失败不能中止监听或保存。 */}
    }
}
