using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;
using Pane.Services;
using Pane.Contracts;

internal static class Program
{
    [STAThread] static void Main(){ApplicationConfiguration.Initialize();Application.Run(new Fixture());}
    sealed class Fixture:Form
    {
        readonly WebView2 web=new(){Dock=DockStyle.Fill};
        readonly string directory=Path.Combine(AppContext.BaseDirectory,"results",Guid.NewGuid().ToString("N"));
        readonly System.Windows.Forms.Timer timeout=new(){Interval=30000};
        protected override bool ShowWithoutActivation=>true;
        protected override CreateParams CreateParams{get{var p=base.CreateParams;p.ExStyle|=0x08000080;return p;}}
        public Fixture(){Text="Pane 019 test fixture - Esc exits";Width=400;Height=160;ShowInTaskbar=false;Controls.Add(web);KeyPreview=true;KeyDown+=(_,e)=>{if(e.KeyCode==Keys.Escape)Close();};timeout.Tick+=(_,_)=>{Environment.ExitCode=2;Close();};Shown+=Run;}
        async void Run(object? sender,EventArgs args){
            Directory.CreateDirectory(directory);timeout.Start();var rows=new List<string>();
            try{
                var environment=await CoreWebView2Environment.CreateAsync(null,Path.Combine(directory,"web-profile"));
                await web.EnsureCoreWebView2Async(environment);
                var loaded=new TaskCompletionSource();web.NavigationCompleted+=(_,e)=>{if(e.IsSuccess)loaded.TrySetResult();else loaded.TrySetException(new Exception("navigation: "+e.WebErrorStatus));};
                web.NavigateToString("<html><body><p id='sample' tabindex='0'>Pane fake selection 019</p></body></html>");await loaded.Task;
                nint window=Handle,element=web.Handle;int success=0;
                var platform=new FixturePlatform(window,element);var reader=new SelectionReader(platform,()=>false,new SelectionLog(directory));
                for(int i=1;i<=20;i++){
                    await web.ExecuteScriptAsync("const p=document.getElementById('sample');p.focus();const r=document.createRange();r.selectNodeContents(p);const s=window.getSelection();s.removeAllRanges();s.addRange(r);");
                    var watch=Stopwatch.StartNew();var sample=await reader.ReadAsync(default);bool match=sample.Text=="Pane fake selection 019";
                    if(sample.Failure==SelectionFailure.None&&match)success++;
                    rows.Add($"run={i} result={sample.Failure} match={match} path=UIA ms={watch.Elapsed.TotalMilliseconds:F2} {platform.Detail}");
                    await Task.Delay(25);
                }
                rows.Add($"success={success}/20; systemClipboard=0; input=0; foregroundChanges=0; runtime={web.CoreWebView2.Environment.BrowserVersionString}");Environment.ExitCode=success==20?0:1;
            }catch(Exception e){rows.Add(e.ToString());Environment.ExitCode=1;}
            finally{File.WriteAllLines(Path.Combine(AppContext.BaseDirectory,"results","latest.txt"),rows);timeout.Stop();web.Dispose();Close();}
        }
        protected override void Dispose(bool disposing){if(disposing){timeout.Dispose();web.Dispose();}base.Dispose(disposing);}
    }
    sealed class FixturePlatform(nint window,nint element):ISelectionPlatform
    {
        public string Detail="";public string Diagnostics=>Detail;public nint Foreground=>window;public uint Sequence=>0;
        public string Target(nint target)=>"Pane.Selection.WebView.Tests.exe class=fixture";
        public SelectionResult Direct(nint target){var text=new StringBuilder(1024);int result=Native.PaneSelectionFixture(target,element,text,text.Capacity);Detail=Marshal.PtrToStringUni(Native.PaneSelectionDiagnostics())??"";return new(result==0?text.ToString():null,target,(SelectionFailure)result);}
        public object Snapshot()=>throw new InvalidOperationException("禁止系统剪贴板");
        public void Copy(nint target)=>throw new InvalidOperationException("禁止真实输入");
        public string? ReadText()=>throw new InvalidOperationException("禁止系统剪贴板");
        public void Restore(object snapshot)=>throw new InvalidOperationException("禁止系统剪贴板");
        public void Release(object snapshot)=>throw new InvalidOperationException("禁止系统剪贴板");
    }
    static class Native
    {
        [DllImport("Pane.Native.dll",CharSet=CharSet.Unicode)] internal static extern int PaneSelectionFixture(nint window,nint element,StringBuilder text,int capacity);
        [DllImport("Pane.Native.dll")] internal static extern nint PaneSelectionDiagnostics();
    }
}
