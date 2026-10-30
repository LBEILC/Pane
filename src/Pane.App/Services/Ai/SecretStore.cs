using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using Pane.Contracts;

namespace Pane.Services.Ai;

// 直接调用当前用户 DPAPI，不新增 NuGet 依赖，不生成明文临时文件。
internal sealed class SecretStore(string directory)
{
    [StructLayout(LayoutKind.Sequential)] struct Blob { public int Size; public nint Data; }
    [DllImport("crypt32.dll",SetLastError=true)] static extern bool CryptProtectData(ref Blob input,nint description,nint entropy,nint reserved,nint prompt,uint flags,out Blob output);
    [DllImport("crypt32.dll",SetLastError=true)] static extern bool CryptUnprotectData(ref Blob input,nint description,nint entropy,nint reserved,nint prompt,uint flags,out Blob output);
    [DllImport("kernel32.dll")] static extern nint LocalFree(nint pointer);
    static byte[] Transform(byte[] bytes,bool protect)
    {
        var input=new Blob{Size=bytes.Length,Data=Marshal.AllocHGlobal(bytes.Length)};Blob output=default;
        try {
            Marshal.Copy(bytes,0,input.Data,bytes.Length);
            bool ok=protect?CryptProtectData(ref input,0,0,0,0,1,out output):CryptUnprotectData(ref input,0,0,0,0,1,out output);
            if(!ok)throw new AiCredentialException("无法读取或保存加密凭据，请重新配置");
            var result=new byte[output.Size];Marshal.Copy(output.Data,result,0,result.Length);return result;
        }finally{for(int i=0;i<input.Size;i++)Marshal.WriteByte(input.Data,i,0);Marshal.FreeHGlobal(input.Data);if(output.Data!=0){for(int i=0;i<output.Size;i++)Marshal.WriteByte(output.Data,i,0);LocalFree(output.Data);}}
    }
    internal string? Read(string name)
    {
        try{
            var path=Path.Combine(directory,name);if(!File.Exists(path))return null;
            byte[] plain=Transform(File.ReadAllBytes(path),false);
            try{return Encoding.UTF8.GetString(plain);}finally{CryptographicOperations.ZeroMemory(plain);}
        }catch(Exception e) when(e is IOException or UnauthorizedAccessException){throw new AiCredentialException("无法读取加密凭据，请检查数据目录");}
    }
    internal void Write(string name,string? value)
    {
        try {
            var path=Path.Combine(directory,name);if(value==null){File.Delete(path);return;}
            Directory.CreateDirectory(directory);byte[] plain=Encoding.UTF8.GetBytes(value);
            try{File.WriteAllBytes(path+".tmp",Transform(plain,true));File.Move(path+".tmp",path,true);}finally{CryptographicOperations.ZeroMemory(plain);}
        }catch(Exception e) when(e is IOException or UnauthorizedAccessException){throw new AiCredentialException("无法保存加密凭据，请检查数据目录");}
    }
}
