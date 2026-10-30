using System.Buffers.Binary;
using System.Xml;
using System.Globalization;
using System.Text;
using Pane.Contracts;
namespace Pane.Services.Launcher;

internal static class FilePreviewReader
{
    static FilePreviewReader()=>Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
    internal static async Task<FilePreview> ReadAsync(FileHit hit,CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        using var budget=CancellationTokenSource.CreateLinkedTokenSource(token);budget.CancelAfter(200);
        var work=Task.Run(()=>Read(hit,budget.Token),budget.Token);
        try{var result=await work.WaitAsync(budget.Token).ConfigureAwait(false);token.ThrowIfCancellationRequested();return result;}
        catch(OperationCanceledException) when(!token.IsCancellationRequested){return new();}
    }
    static FilePreview Read(FileHit hit,CancellationToken token)
    {
        try{
            if(hit.Category==FileCategory.Folder){
                int count=0;foreach(var _ in Directory.EnumerateFileSystemEntries(hit.Path)){token.ThrowIfCancellationRequested();count++;}
                return new(FolderItems:count);
            }
            if(hit.Category is not (FileCategory.Text or FileCategory.Image))return new();
            using var stream=new FileStream(hit.Path,FileMode.Open,FileAccess.Read,FileShare.ReadWrite|FileShare.Delete,4096,FileOptions.SequentialScan);
            // 头部读取封顶 64KiB，绝不解码像素或读取完整大文件。
            var bytes=new byte[65536];int length=0;
            while(length<bytes.Length){token.ThrowIfCancellationRequested();int read=stream.Read(bytes,length,bytes.Length-length);if(read==0)break;length+=read;}
            token.ThrowIfCancellationRequested();
            if(hit.Category==FileCategory.Image)return Image(bytes.AsSpan(0,length));
            var text=Text(bytes.AsSpan(0,length),length==bytes.Length);
            if(text==null)return new();
            using var reader=new StringReader(text);var head=new StringBuilder();
            for(int i=0;i<40&&head.Length<4000;i++){
                var line=reader.ReadLine();if(line==null)break;
                if(i>0)head.Append('\n');
                head.Append(line.AsSpan(0,Math.Min(line.Length,4000-head.Length)));
            }
            return new(TextHead:head.ToString());
        }catch(Exception e) when(e is IOException or UnauthorizedAccessException or OperationCanceledException or ArgumentException){return new();}
    }
    static string? Text(ReadOnlySpan<byte> bytes,bool truncated)
    {
        Encoding encoding;int skip=0;
        if(bytes.StartsWith(new byte[]{0xff,0xfe})){encoding=new UnicodeEncoding(false,true,true);skip=2;}
        else if(bytes.StartsWith(new byte[]{0xfe,0xff})){encoding=new UnicodeEncoding(true,true,true);skip=2;}
        else if(bytes.StartsWith(new byte[]{0xef,0xbb,0xbf})){encoding=new UTF8Encoding(false,true);skip=3;}
        else{
            // 无 BOM 的 UTF-16：只把规律性的零字节识别为字节序。
            int even=0,odd=0;for(int i=0;i<Math.Min(bytes.Length,512);i++)if(bytes[i]==0){if(i%2==0)even++;else odd++;}
            if(odd>8&&even==0)encoding=new UnicodeEncoding(false,false,true);
            else if(even>8&&odd==0)encoding=new UnicodeEncoding(true,false,true);
            else encoding=new UTF8Encoding(false,true);
        }
        string value;
        try{value=Decode(encoding,bytes[skip..],truncated);}
        catch(DecoderFallbackException){
            if(encoding.CodePage!=65001||skip!=0)return null;
            try{value=Decode(Encoding.GetEncoding(936,EncoderFallback.ExceptionFallback,DecoderFallback.ExceptionFallback),bytes,truncated);}
            catch(DecoderFallbackException){return null;}
        }
        return value.Any(c=>char.IsControl(c)&&c is not ('\r' or '\n' or '\t'))?null:value;
    }
    static string Decode(Encoding encoding,ReadOnlySpan<byte> bytes,bool truncated){
        var chars=new char[encoding.GetMaxCharCount(bytes.Length)];
        var decoder=encoding.GetDecoder();int count=decoder.GetChars(bytes,chars,!truncated);return new(chars,0,count);
    }
    static FilePreview Image(ReadOnlySpan<byte> b)
    {
        int width=0,height=0;
        if(b.Length>=24&&b[..8].SequenceEqual(new byte[]{137,80,78,71,13,10,26,10})){width=BinaryPrimitives.ReadInt32BigEndian(b[16..]);height=BinaryPrimitives.ReadInt32BigEndian(b[20..]);}
        else if(b.Length>=10&&(b[..6].SequenceEqual("GIF87a"u8)||b[..6].SequenceEqual("GIF89a"u8))){width=BinaryPrimitives.ReadUInt16LittleEndian(b[6..]);height=BinaryPrimitives.ReadUInt16LittleEndian(b[8..]);}
        else if(b.Length>=26&&b[..2].SequenceEqual("BM"u8)&&BinaryPrimitives.ReadUInt32LittleEndian(b[14..])>=40){width=BinaryPrimitives.ReadInt32LittleEndian(b[18..]);int h=BinaryPrimitives.ReadInt32LittleEndian(b[22..]);height=h==int.MinValue?0:Math.Abs(h);}
        else if(b.Length>=8&&b[..4].SequenceEqual(new byte[]{0,0,1,0})){width=b[6]==0?256:b[6];height=b[7]==0?256:b[7];}
        else if(b.Length>=22&&b[..4].SequenceEqual("8BPS"u8)){height=BinaryPrimitives.ReadInt32BigEndian(b[14..]);width=BinaryPrimitives.ReadInt32BigEndian(b[18..]);}
        else if(b.Length>=30&&b[..4].SequenceEqual("RIFF"u8)&&b[8..12].SequenceEqual("WEBP"u8)&&b[12..16].SequenceEqual("VP8X"u8)){width=1+b[24]+(b[25]<<8)+(b[26]<<16);height=1+b[27]+(b[28]<<8)+(b[29]<<16);}
        else if(b.Length>=30&&b[..4].SequenceEqual("RIFF"u8)&&b[8..12].SequenceEqual("WEBP"u8)&&b[12..16].SequenceEqual("VP8 "u8)&&b[23..26].SequenceEqual(new byte[]{0x9d,1,0x2a})){width=BinaryPrimitives.ReadUInt16LittleEndian(b[26..])&0x3fff;height=BinaryPrimitives.ReadUInt16LittleEndian(b[28..])&0x3fff;}
        else if(b.Length>=25&&b[..4].SequenceEqual("RIFF"u8)&&b[8..12].SequenceEqual("WEBP"u8)&&b[12..16].SequenceEqual("VP8L"u8)&&b[20]==0x2f){uint bits=BinaryPrimitives.ReadUInt32LittleEndian(b[21..]);width=(int)(bits&0x3fff)+1;height=(int)((bits>>14)&0x3fff)+1;}
        else if(b.Length>=8&&(b[..4].SequenceEqual(new byte[]{73,73,42,0})||b[..4].SequenceEqual(new byte[]{77,77,0,42}))){
            bool little=b[0]==73;uint offset=U32(b[4..],little);
            if(offset<=(uint)b.Length-2){int p=(int)offset;int n=U16(b[p..],little);p+=2;
                for(int i=0;i<n&&p+12<=b.Length;i++,p+=12){int tag=U16(b[p..],little),type=U16(b[(p+2)..],little);if(U32(b[(p+4)..],little)!=1)continue;
                    uint value=type==3?U16(b[(p+8)..],little):type==4?U32(b[(p+8)..],little):0;
                    if(value<=int.MaxValue){if(tag==256)width=(int)value;if(tag==257)height=(int)value;}
                }
            }
        }
        else if(b.Length>=12&&b[4..8].SequenceEqual("ftyp"u8)){
            // ISO BMFF 的 ispe 属性包含宽高；只检查已读取头部，不加载媒体内容。
            int found=0;
            for(int p=4;p+16<=b.Length;p++)if(b.Slice(p,4).SequenceEqual("ispe"u8)&&BinaryPrimitives.ReadUInt32BigEndian(b[(p-4)..])>=20){
                width=BinaryPrimitives.ReadInt32BigEndian(b[(p+8)..]);height=BinaryPrimitives.ReadInt32BigEndian(b[(p+12)..]);found++;
            }
            if(found!=1){width=0;height=0;}
        }
        else if(b.Length>=2&&b[0]==255&&b[1]==216){
            int p=2;while(p+4<=b.Length){if(b[p++]!=255)break;byte marker=b[p++];if(marker==255){p--;continue;}if(marker is 0xd9 or 0xda)break;if(marker is 0xd8 or 0x01 or >=0xd0 and <=0xd7)continue;
                int size=BinaryPrimitives.ReadUInt16BigEndian(b[p..]);if(size<2||p+size>b.Length)break;
                if(marker is >=0xc0 and <=0xcf && marker is not (0xc4 or 0xc8 or 0xcc)&&size>=7){height=BinaryPrimitives.ReadUInt16BigEndian(b[(p+3)..]);width=BinaryPrimitives.ReadUInt16BigEndian(b[(p+5)..]);break;}p+=size;
            }
        }
        if(width==0&&b.Length>0){
            try{
                using var stream=new MemoryStream(b.ToArray());
                using var xml=XmlReader.Create(stream,new XmlReaderSettings{DtdProcessing=DtdProcessing.Prohibit,XmlResolver=null,MaxCharactersInDocument=65536});
                xml.MoveToContent();if(xml.LocalName=="svg"){
                    width=Dimension(xml.GetAttribute("width"));height=Dimension(xml.GetAttribute("height"));
                }
            }catch(XmlException){}
        }
        return width>0&&height>0?new(ImageWidth:width,ImageHeight:height):new();
    }

    static ushort U16(ReadOnlySpan<byte> b,bool little)=>little?BinaryPrimitives.ReadUInt16LittleEndian(b):BinaryPrimitives.ReadUInt16BigEndian(b);
    static uint U32(ReadOnlySpan<byte> b,bool little)=>little?BinaryPrimitives.ReadUInt32LittleEndian(b):BinaryPrimitives.ReadUInt32BigEndian(b);
    static int Dimension(string? value){
        if(value==null)return 0;if(value.EndsWith("px",StringComparison.Ordinal))value=value[..^2];
        return double.TryParse(value,NumberStyles.Float,CultureInfo.InvariantCulture,out double n)&&n>0&&n<=int.MaxValue?(int)Math.Ceiling(n):0;
    }
}