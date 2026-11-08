using System.Text.Json;
using System.Text.Json.Serialization;

namespace Pane.Settings;

internal sealed record StorageMoveFile(string Source,string Target);
internal sealed record StorageMoveJournal(bool SettingsExisted,bool HistoryExisted,StorageMoveFile[] Files);
[JsonSerializable(typeof(StorageMoveJournal))]
internal partial class StorageMoveJson:JsonSerializerContext { }

// 两个 JSON 无法用一次文件替换提交：持久化回滚记录，提交前崩溃则在读取设置前恢复。
internal static partial class StorageRecovery
{
    // 产品包含导入模块；仅链接设置代码的独立测试不需要该模块。
    static partial void RecoverImport(string data);
    public static void RecoverAll(string data){Recover(data);RecoverImport(data);}
    static string Root(string data)=>Path.Combine(data,".storage-move");
    static string History(string data)=>Path.Combine(data,"clipboard","history.json");
    public static void Begin(string data,StorageMoveFile[] files)
    {
        string root=Root(data);Directory.CreateDirectory(root);
        string settings=Path.Combine(data,"settings.json"),history=History(data);
        bool s=File.Exists(settings),h=File.Exists(history);
        if(s)File.Copy(settings,Path.Combine(root,"settings.bak"),true);
        if(h)File.Copy(history,Path.Combine(root,"history.bak"),true);
        string pending=Path.Combine(root,"pending.json");
        using(var stream=new FileStream(pending+".tmp",FileMode.Create,FileAccess.Write,FileShare.None)){
            JsonSerializer.Serialize(stream,new(s,h,files),StorageMoveJson.Default.StorageMoveJournal);stream.Flush(true);
        }
        File.Move(pending+".tmp",pending,true);
    }
    public static void Recover(string data)
    {
        string root=Root(data),pending=Path.Combine(root,"pending.json");
        if(!File.Exists(pending))return;
        var journal=JsonSerializer.Deserialize(File.ReadAllText(pending),StorageMoveJson.Default.StorageMoveJournal)??throw new InvalidDataException();
        // 先恢复原图；恢复失败则保留副本和日志，下一次仍能重试。
        foreach(var file in journal.Files)
            if(!File.Exists(file.Source)&&File.Exists(file.Target)){Directory.CreateDirectory(Path.GetDirectoryName(file.Source)!);File.Copy(file.Target,file.Source);}
        Restore(Path.Combine(root,"settings.bak"),Path.Combine(data,"settings.json"),journal.SettingsExisted);
        Restore(Path.Combine(root,"history.bak"),History(data),journal.HistoryExisted);
        foreach(var file in journal.Files)File.Delete(file.Target);
        Complete(data);
    }
    static void Restore(string backup,string destination,bool existed)
    {
        if(!existed){File.Delete(destination);return;}
        // 替换之前就失败时，原文件可能仍被其他程序以只读共享打开；不必再次替换相同内容。
        if(File.Exists(destination)&&File.ReadAllBytes(backup).SequenceEqual(File.ReadAllBytes(destination)))return;
        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
        File.Copy(backup,destination+".restore",true);File.Move(destination+".restore",destination,true);
    }
    public static void Complete(string data)
    {
        string root=Root(data);File.Delete(Path.Combine(root,"pending.json"));
        try{Directory.Delete(root,true);}catch(IOException){ }catch(UnauthorizedAccessException){ }
    }
}
