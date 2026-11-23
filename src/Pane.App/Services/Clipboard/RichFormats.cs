namespace Pane.Services.Clipboard;

// 长度头后依次存 Win32 提供的原始字节；不解析 HTML 偏移或 RTF 编码。
internal sealed record RichFormats(byte[] Html,byte[] Rtf)
{
    internal const int Limit=2*1024*1024;
    internal static string PathFor(string directory,string id)
    {
        if(!Guid.TryParseExact(id,"N",out _))throw new InvalidDataException();
        return Path.Combine(directory,id+".rich");
    }
    internal string? Save(string directory)
    {
        long total=Html.LongLength+Rtf.LongLength;
        if(total==0||total>Limit)return null;
        string id=Guid.NewGuid().ToString("N");
        using var file=new FileStream(PathFor(directory,id),FileMode.CreateNew,FileAccess.Write,FileShare.None);
        using var writer=new BinaryWriter(file);
        writer.Write(Html.Length);writer.Write(Html);writer.Write(Rtf);file.Flush(true);
        return id;
    }
    internal static RichFormats Read(string directory,string id)
    {
        using var file=File.OpenRead(PathFor(directory,id));
        using var reader=new BinaryReader(file);
        long total=file.Length-sizeof(int);
        int html=reader.ReadInt32();
        if(total<=0||total>Limit||html<0||html>total)throw new InvalidDataException();
        return new(reader.ReadBytes(html),reader.ReadBytes((int)total-html));
    }
}
