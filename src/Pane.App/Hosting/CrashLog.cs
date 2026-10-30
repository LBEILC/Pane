using System.Reflection;
namespace Pane.Hosting;
internal sealed class CrashLog(string dataDirectory)
{
    string DirectoryPath=>Path.Combine(dataDirectory,"logs");
    public void Write(Exception exception){
        try {
            Directory.CreateDirectory(DirectoryPath);
            // 异常 Message 可能带剪贴板或查询正文；仅保存已知固定消息，其余脱敏。
            string message=exception.Message is "剪贴板历史初始化失败" or "托盘创建失败"?exception.Message:"异常消息已脱敏";
            string version=typeof(CrashLog).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion??"unknown";
            File.WriteAllText(Path.Combine(DirectoryPath,"crash-"+DateTime.UtcNow.ToString("yyyyMMdd-HHmmss-fffffff")+".txt"),$"UTC: {DateTime.UtcNow:O}\nVersion: {version}\nType: {exception.GetType().FullName}\nMessage: {message}\nHResult: {exception.HResult}\nStack: {exception.StackTrace}\n");
            foreach(var file in new DirectoryInfo(DirectoryPath).GetFiles("crash-*.txt").OrderByDescending(f=>f.Name).Skip(10))file.Delete();
        }catch(IOException){}catch(UnauthorizedAccessException){}
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
