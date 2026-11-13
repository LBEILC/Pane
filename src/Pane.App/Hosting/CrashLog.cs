using System.Reflection;
namespace Pane.Hosting;
internal sealed class CrashLog(string dataDirectory)
{
    static readonly object sync = new();
    string DirectoryPath=>Path.Combine(dataDirectory,"logs");
    public void Write(Exception exception){
        AutostartDiagnostics.StartupFailed(exception);
        try {
            Directory.CreateDirectory(DirectoryPath);
            // 异常 Message 可能带剪贴板或查询正文；仅保存已知固定消息，其余脱敏。
            string message=exception.Message is "剪贴板历史初始化失败" or "托盘创建失败"?exception.Message:"异常消息已脱敏"; // 不翻译：日志匹配
            string version=typeof(CrashLog).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion??"unknown";
            var session=NativeCrashRecovery.Current?.Session;
            WriteContent(DateTime.UtcNow,$"UTC: {DateTime.UtcNow:O}\nVersion: {version}\n"+(session==null?"":$"Session: {session.Identity}\n")+$"Type: {exception.GetType().FullName}\nMessage: {message}\nHResult: {exception.HResult}\nStack: {exception.StackTrace}\n");
            NativeCrashRecovery.Current?.Stop();
        }catch(IOException){}catch(UnauthorizedAccessException){}
    }
    internal bool HasSession(CrashSession session) => Directory.Exists(DirectoryPath) &&
        Directory.EnumerateFiles(DirectoryPath,"crash-*.txt").Any(file=>File.ReadLines(file).Contains($"Session: {session.Identity}"));
    internal void WriteNative(CrashSession session,NativeCrashEvent crash){
        lock(sync){
            if(HasSession(session))return;
            WriteContent(crash.Utc,crash.Format(session));
        }
    }
    void WriteContent(DateTime utc,string content){
        lock(sync){
            Directory.CreateDirectory(DirectoryPath);
            File.WriteAllText(Path.Combine(DirectoryPath,"crash-"+utc.ToString("yyyyMMdd-HHmmss-fffffff")+".txt"),content);
            foreach(var file in new DirectoryInfo(DirectoryPath).GetFiles("crash-*.txt").OrderByDescending(f=>f.Name).Skip(10))file.Delete();
        }
    }
    public string? Pending(){
        if(!Directory.Exists(DirectoryPath))return null;
        var latest=Directory.GetFiles(DirectoryPath,"crash-*.txt").OrderByDescending(Path.GetFileName).FirstOrDefault();
        string marker=Path.Combine(DirectoryPath,"notified");
        if(latest==null||File.Exists(marker)&&File.ReadAllText(marker)==Path.GetFileName(latest))return null;
        return latest;
    }
    public void MarkNotified(string path)=>File.WriteAllText(Path.Combine(DirectoryPath,"notified"),Path.GetFileName(path));
}
