$ErrorActionPreference='Stop'
$repo=(Resolve-Path "$PSScriptRoot/../..").Path
$out=New-Item -Type Directory -Force "$repo/.codex-runs/122/$([guid]::NewGuid().ToString('N'))"
$probe=Join-Path $out 'Window122.exe'
$guard=Get-Content -Raw -Encoding UTF8 "$repo/experiments/027-idle/HiddenDesktop.cs"
Add-Type -TypeDefinition ($guard + @'
public class Window122 {
 [STAThread] public static void Main(){
  HiddenDesktop027.Require();
  string path=System.Reflection.Assembly.GetExecutingAssembly().Location;
  using(var form=new System.Windows.Forms.Form()){
   var timer=new System.Windows.Forms.Timer{Interval=100};
   var end=DateTime.UtcNow.AddSeconds(15);
   timer.Tick+=(s,e)=>{
    if(System.IO.File.Exists(path+".exit")||DateTime.UtcNow>end){form.Close();return;}
    if(!System.IO.File.Exists(path+".ready")){
     form.WindowState=System.Windows.Forms.FormWindowState.Minimized;
     System.IO.File.WriteAllText(path+".tmp",form.Handle.ToInt64().ToString());
     System.IO.File.Move(path+".tmp",path+".ready");
    }
   };
   timer.Start();System.Windows.Forms.Application.Run(form);
  }
 }
}
'@) -ReferencedAssemblies System.Windows.Forms -OutputAssembly $probe -OutputType WindowsApplication
Add-Type -TypeDefinition $guard
$runner=(Resolve-Path "$PSScriptRoot/bin/Debug/net10.0-windows/Pane.Launcher.Tests.exe").Path
$code=[HiddenDesktop027]::Run($runner,@('--122-hidden',$probe),$out.FullName,25000)
Get-Content -LiteralPath "$probe.log" -Encoding utf8
if($code){throw "隐藏桌面验收失败 $code"}
