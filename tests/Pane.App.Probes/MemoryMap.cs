using System;
using System.ComponentModel;
using System.Runtime.InteropServices;

// 在测量进程中枚举目标页，避免把分类缓冲区计入 Pane 的工作集。
public static class PaneMemoryMap
{
    [StructLayout(LayoutKind.Sequential)] struct Region
    {
        public ulong Base, AllocationBase;
        public uint AllocationProtect, Alignment;
        public ulong Size;
        public uint State, Protect, Type, Padding;
    }
    [StructLayout(LayoutKind.Sequential)] struct Page { public ulong Address, Flags; }
    [DllImport("kernel32.dll", SetLastError=true)] static extern IntPtr OpenProcess(uint access, bool inherit, int id);
    [DllImport("kernel32.dll")] static extern bool CloseHandle(IntPtr handle);
    [DllImport("kernel32.dll")] static extern UIntPtr VirtualQueryEx(IntPtr process, UIntPtr address, out Region region, UIntPtr size);
    [DllImport("psapi.dll", SetLastError=true)] static extern bool QueryWorkingSetEx(IntPtr process, [In,Out] Page[] pages, int bytes);
    public sealed class Result
    {
        public double PrivateCommitMiB, MappedCommitMiB, ImageCommitMiB;
        public double PrivateResidentMiB, MappedResidentMiB, ImageResidentMiB, SharedResidentMiB;
    }
    public static Result Read(int id)
    {
        var process=OpenProcess(0x410,false,id);
        if(process==IntPtr.Zero)throw new Win32Exception();
        try {
            var result=new Result();var pages=new Page[4096];
            for(ulong address=0; ; ) {
                Region region;
                if(VirtualQueryEx(process,(UIntPtr)address,out region,(UIntPtr)48)==UIntPtr.Zero)break;
                if(region.State==0x1000) {
                    double commit=region.Size/1048576.0;
                    if(region.Type==0x20000)result.PrivateCommitMiB+=commit;
                    else if(region.Type==0x40000)result.MappedCommitMiB+=commit;
                    else if(region.Type==0x1000000)result.ImageCommitMiB+=commit;
                    for(ulong offset=0;offset<region.Size;) {
                        int count=(int)Math.Min(4096,(region.Size-offset)/4096);
                        if(count==0)break;
                        for(int i=0;i<count;i++)pages[i]=new Page{Address=region.Base+offset+(ulong)i*4096};
                        if(!QueryWorkingSetEx(process,pages,count*16))throw new Win32Exception();
                        for(int i=0;i<count;i++)if((pages[i].Flags&1)!=0) {
                            const double page=4096.0/1048576;
                            if(region.Type==0x20000)result.PrivateResidentMiB+=page;
                            else if(region.Type==0x40000)result.MappedResidentMiB+=page;
                            else if(region.Type==0x1000000)result.ImageResidentMiB+=page;
                            if((pages[i].Flags&0x8000)!=0)result.SharedResidentMiB+=page;
                        }
                        offset+=(ulong)count*4096;
                    }
                }
                ulong next=region.Base+region.Size;if(next<=address)break;address=next;
            }
            return result;
        } finally {CloseHandle(process);}
    }
}
