using System.Text;
namespace Pane.Services;

// 只接受阶段元数据；不传入文本、窗口标题或异常消息。
internal sealed class SelectionLog(string dataDirectory)
{
    static readonly object sync=new();
    public void Write(string metadata)
    {
        try{lock(sync){
            string path=Path.Combine(dataDirectory,"logs","selection.log");
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            var lines=File.Exists(path)?File.ReadLines(path).TakeLast(299).ToList():[];
            lines.Add(DateTimeOffset.Now.ToString("O")+" "+metadata.Replace('\r',' ').Replace('\n',' '));
            File.WriteAllLines(path,lines,new UTF8Encoding(false));
        }}catch(IOException){}catch(UnauthorizedAccessException){}
    }
}
