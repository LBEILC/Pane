using System.Runtime.InteropServices;
using System.Text;
using Pane.Services.Clipboard;
static partial class Program
{
    static void HiddenPasteTests(string output)
    {
        var desktop=new StringBuilder(256);
        if(!GetUserObjectInformationW(GetThreadDesktop(GetCurrentThreadId()),2,desktop,512,out _)||!desktop.ToString().StartsWith("Pane027-"))throw new InvalidOperationException("必须在隐藏测试桌面运行");
        var wc=new WindowClass{Proc=Marshal.GetFunctionPointerForDelegate(WindowProc),Name="Pane039Fixture"}; RegisterClassW(ref wc);
        int count=0;var windows=new List<nint>();
        void Assert(bool ok,string name){if(!ok)throw new Exception(name);count++;}
        try{
            nint owner=CreateWindowExW(0,"Pane039Fixture","fixture owner",0x10cf0000,0,0,200,150,0,0,0,0);windows.Add(owner);
            nint popup=CreateWindowExW(0,"Pane039Fixture","fixture popup",0x90000000,0,0,100,100,owner,0,0,0);windows.Add(popup);
            nint other=CreateWindowExW(0,"Pane039Fixture","fixture other",0x10cf0000,0,0,200,150,0,0,0,0);windows.Add(other);
            Assert(windows.All(w=>w!=0),"create windows");
            var native=new WindowsPastePlatform();var transfer=new PasteTransfer(native);
            bool popupActivated=native.Activate(popup);
            nint observed=native.Foreground;
            Assert(native.Root(owner)==native.Root(popup)&&transfer.Equivalent(owner,popup),"owned popup accepted");
            native.Activate(other);
            Assert(other!=owner&&native.Root(other)!=native.Root(owner)&&transfer.Equivalent(owner,other),"same process top level accepted");
            Assert(native.Higher(owner)==false,"equal integrity confirmed");
            using var ready=new ManualResetEventSlim();uint workerId=0;nint workerWindow=0;
            var worker=new Thread(()=>{workerId=GetCurrentThreadId();workerWindow=CreateWindowExW(0,"Pane039Fixture","fixture worker",0x10cf0000,0,0,100,100,0,0,0,0);ready.Set();while(GetMessageW(out var msg,0,0,0)>0){TranslateMessage(ref msg);DispatchMessageW(ref msg);}DestroyWindow(workerWindow);});
            worker.Start();ready.Wait();
            bool focusResult=false;
            try{
                Assert(workerWindow!=0,"other thread window");
                bool attached=native.Attach(workerId,GetCurrentThreadId(),true);
                Assert(attached,"native attach");
                try{native.Activate(owner);native.Top(owner);}finally{Assert(native.Attach(workerId,GetCurrentThreadId(),false),"native detach");}
                focusResult=new PasteTransfer(new FirstActivationFails(native)).Focus(owner,default);
                Assert(observed==0?!focusResult:focusResult,"foreground confirmation reflects desktop availability");
            }finally{PostThreadMessageW(workerId,0x12,0,0);worker.Join();}
            ShowWindow(owner,6);native.Restore(owner);
            File.WriteAllText(output,$"PASS hidden assertions={count}; oldExactRejected=2 (real HWND equivalence); activationReturned={popupActivated}; foreground={observed}; focusConfirmed={focusResult}; keyboardInput=0; clipboardRead=0; clipboardWrite=0; windowsDestroyed=1");        }catch(Exception e){File.WriteAllText(output,e.ToString());throw;}
        finally{foreach(var w in windows.AsEnumerable().Reverse())if(w!=0)DestroyWindow(w);}
    }
    sealed class FirstActivationFails(WindowsPastePlatform p):IPastePlatform
    {
        bool first=true;public bool Attached,Detached;
        public nint Foreground=>p.Foreground;public bool Exists(nint w)=>p.Exists(w);public uint Process(nint w)=>p.Process(w);public nint Root(nint w)=>p.Root(w);public uint Thread(nint w)=>p.Thread(w);
        public bool Activate(nint w){if(first){first=false;return false;}return p.Activate(w);}
        public bool Attach(uint a,uint b,bool on){bool ok=p.Attach(a,b,on);if(on)Attached=ok;else Detached=ok;return ok;}
        public void Top(nint w)=>p.Top(w);public void Restore(nint w)=>p.Restore(w);public bool Physical(int k)=>p.Physical(k);public bool Logical(int k)=>p.Logical(k);public bool Key(ushort k,bool d)=>throw new Exception("禁止测试输入");public bool? Higher(nint w)=>p.Higher(w);public string Class(nint w)=>p.Class(w);public int Error=>p.Error;public long Now=>p.Now;public void Wait()=>p.Wait();
    }
    static readonly WndProc WindowProc=DefWindowProcW;
    [UnmanagedFunctionPointer(CallingConvention.Winapi)] delegate nint WndProc(nint w,uint m,nuint wp,nint lp);
    [StructLayout(LayoutKind.Sequential,CharSet=CharSet.Unicode)] struct WindowClass {public uint Style;public nint Proc;public int ClassExtra,WindowExtra;public nint Instance,Icon,Cursor,Background;public string? Menu,Name;}
    [DllImport("user32.dll",CharSet=CharSet.Unicode)] static extern ushort RegisterClassW(ref WindowClass wc);
    [DllImport("user32.dll")] static extern nint DefWindowProcW(nint w,uint m,nuint wp,nint lp);
    [StructLayout(LayoutKind.Sequential)] struct Message {public nint Hwnd;public uint Id;public nuint W;public nint L;public uint Time;public int X,Y;public uint Private;}
    [DllImport("user32.dll",CharSet=CharSet.Unicode)] static extern nint CreateWindowExW(uint ex,string cls,string name,uint style,int x,int y,int width,int height,nint parent,nint menu,nint instance,nint param);
    [DllImport("user32.dll")] static extern bool DestroyWindow(nint window);
    [DllImport("user32.dll")] static extern bool ShowWindow(nint window,int show);
    [DllImport("user32.dll")] static extern nint GetThreadDesktop(uint thread);
    [DllImport("kernel32.dll")] static extern uint GetCurrentThreadId();
    [DllImport("user32.dll",CharSet=CharSet.Unicode)] static extern bool GetUserObjectInformationW(nint handle,int kind,StringBuilder value,int size,out int needed);
    [DllImport("user32.dll")] static extern int GetMessageW(out Message message,nint window,uint min,uint max);
    [DllImport("user32.dll")] static extern bool TranslateMessage(ref Message message);
    [DllImport("user32.dll")] static extern nint DispatchMessageW(ref Message message);
    [DllImport("user32.dll")] static extern bool PostThreadMessageW(uint thread,uint message,nuint w,nint l);
}
