using System.Runtime.InteropServices;

namespace Pane.Services.Cloud;

// 复用已缩放到长边 640 的本机 PNG。细节过多时用系统 JPEG 编码器降低质量，保留尺寸。
internal static class ClipboardThumbnail
{
    const int Limit=256*1024-29;
    [StructLayout(LayoutKind.Sequential)]
    struct Startup {public uint Version;public nint Callback;public int SuppressThread,SuppressCodecs;}
    [StructLayout(LayoutKind.Sequential)]
    struct Parameter {public Guid Encoder;public uint Values,Type;public nint Value;}
    [StructLayout(LayoutKind.Sequential)]
    struct Parameters {public uint Count;public Parameter Quality;}
    [DllImport("gdiplus")] static extern int GdiplusStartup(out nuint token,ref Startup input,nint output);
    [DllImport("gdiplus")] static extern void GdiplusShutdown(nuint token);
    [DllImport("gdiplus",CharSet=CharSet.Unicode)] static extern int GdipLoadImageFromFile(string path,out nint image);
    [DllImport("gdiplus",CharSet=CharSet.Unicode)] static extern int GdipSaveImageToFile(nint image,string path,ref Guid codec,ref Parameters parameters);
    [DllImport("gdiplus")] static extern int GdipDisposeImage(nint image);
    internal static unsafe byte[] Read(string path)
    {
        if(new FileInfo(path).Length<=Limit)return File.ReadAllBytes(path);
        Startup startup=new(){Version=1};if(GdiplusStartup(out var token,ref startup,0)!=0)throw new IOException();
        nint image=0;string temp=Path.Combine(Path.GetTempPath(),Guid.NewGuid().ToString("N")+".jpg");
        try
        {
            if(GdipLoadImageFromFile(path,out image)!=0)throw new IOException();
            Guid codec=new("557cf401-1a04-11d3-9a73-0000f81ef32e");
            for(int quality=85;quality>=5;quality-=20)
            {
                Parameters parameters=new(){Count=1,Quality=new(){Encoder=new("1d5be4b5-fa4a-452d-9cdd-5db35105e7eb"),Values=1,Type=4,Value=(nint)(&quality)}};
                if(GdipSaveImageToFile(image,temp,ref codec,ref parameters)!=0)throw new IOException();
                if(new FileInfo(temp).Length<=Limit)return File.ReadAllBytes(temp);
                File.Delete(temp);
            }
            throw new InvalidDataException();
        }
        finally{if(image!=0)GdipDisposeImage(image);GdiplusShutdown(token);File.Delete(temp);}
    }
}
