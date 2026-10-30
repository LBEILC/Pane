using Pane.Services;
using Pane.Contracts;
using Pane.Settings;
using Pane.Hosting;
using System.Text.Json;
static class Program
{
 static int checks;
 static void Check(bool condition,string name){checks++;if(!condition)throw new Exception(name);}
 static async Task Main(string[] args){
  if(args.Length is 3 or 4 && args[0]=="--verify-release") { await ReleaseVerification.Run(args[1],args[2],args.Length==4 && args[3]=="--fake-server");return; }
  if(args is ["--fonts"]){try{await FontTests.Run();}catch(Exception e){Console.Error.WriteLine(e);Environment.ExitCode=1;}return;}
  if(args is ["--update-e2e",var oldPackage,var newPackage,var evidence]){await UpdateEndToEnd.Run(oldPackage,newPackage,evidence);return;}
  foreach(var failure in new[]{SelectionFailure.None,SelectionFailure.Empty,SelectionFailure.Unsupported,SelectionFailure.Elevated,SelectionFailure.Timeout}){
   var fake=new Fake{Failure=failure};var reader=new SelectionReader(fake,()=>false);var result=await reader.ReadAsync(default);Check(result.Failure==failure,"direct failure "+failure);Check(fake.Calls.SequenceEqual(["direct"]),"no clipboard");
  }
  {
   var fake=new Fake();var result=await new SelectionReader(fake,()=>true).ReadAsync(default);Check(result.Text=="selected"&&result.Failure==SelectionFailure.None,"fallback text");Check(fake.Calls.SequenceEqual(["direct","snapshot","copy","read","restore"]),"snapshot copy restore order");Check(fake.Data=="original-all-formats","restore snapshot");
  }
  {var fake=new Fake{Changes=false};var result=await new SelectionReader(fake,()=>true).ReadAsync(default);Check(result.Failure==SelectionFailure.Timeout,"copy timeout");Check(fake.Data=="original-all-formats"&&fake.Calls.Last()=="restore","restore on timeout");}
  {var fake=new Fake{Failure=SelectionFailure.Elevated};var result=await new SelectionReader(fake,()=>true).ReadAsync(default);Check(result.Failure==SelectionFailure.Elevated&&fake.Calls.Count==1,"elevated no clipboard");}
  {var fake=new Fake{SnapshotFails=true};var result=await new SelectionReader(fake,()=>true).ReadAsync(default);Check(result.Failure==SelectionFailure.ClipboardUnavailable&&!fake.Calls.Contains("copy"),"snapshot failure no copy");}
  {var fake=new Fake{DirectDelay=700};var watch=System.Diagnostics.Stopwatch.StartNew();var result=await new SelectionReader(fake,()=>true).ReadAsync(default);Check(result.Failure==SelectionFailure.Timeout&&watch.ElapsedMilliseconds<680,"600ms overall deadline");await Task.Delay(150);Check(fake.Calls.Count==1,"late UIA cannot copy");}
  {var fake=new Fake{Changes=false};using var cancel=new CancellationTokenSource(40);try{await new SelectionReader(fake,()=>true).ReadAsync(cancel.Token);throw new Exception("cancellation missing");}catch(OperationCanceledException){checks++;}await Task.Delay(30);Check(fake.Data=="original-all-formats","cancel restores");}
  ClipboardHistorySuppression.IgnoreNext(2);Check(ClipboardHistorySuppression.TryConsume()&&ClipboardHistorySuppression.TryConsume()&&!ClipboardHistorySuppression.TryConsume(),"ignore next N");
  ClipboardHistorySuppression.IgnoreSequence(42);ClipboardHistorySuppression.CancelPending();Check(ClipboardHistorySuppression.TryConsume(42)&&!ClipboardHistorySuppression.TryConsume(43),"delayed notification sequence");
  var hotkey=new Hotkey(KeyModifiers.Alt,32);Check(HotkeyProcessConflict.IsUTools(hotkey,["uTools"]),"utools present");Check(!HotkeyProcessConflict.IsUTools(hotkey,["other"]),"utools absent");Check(!HotkeyProcessConflict.IsUTools(new(KeyModifiers.Ctrl,32),["uTools"]),"other key no conflict");
  var settings=new PaneSettings(1,new(false,true,ThemeMode.Dark,"#FF5874C9",20,1,new(.2,.2,1,1,1,0),new(.2,.2,1,1,1,0)),new(hotkey),new(new(KeyModifiers.Ctrl,86)));
  string json=JsonSerializer.Serialize(settings,SettingsJson.Default.PaneSettings);using var document=JsonDocument.Parse(json);var old=string.Join(",",document.RootElement.EnumerateObject().Where(p=>p.Name!="SuperPanel").Select(p=>"\""+p.Name+"\":"+p.Value.GetRawText()));
  var loaded=JsonSerializer.Deserialize("{"+old+"}",SettingsJson.Default.PaneSettings)!;Check(loaded.SuperPanel.TriggerButton==SuperPanelButton.X1&&loaded.SuperPanel.HoldMilliseconds==400&&loaded.SuperPanel.ClipboardFallback,"old settings defaults");
  var dir=Path.Combine(AppContext.BaseDirectory,"results","settings");var store=new SettingsStore(dir,settings);store.Save(settings with{SuperPanel=new(true,SuperPanelButton.Middle,650,false,false){DisabledPrograms=["test.exe"]}});store.Load();Check(store.Current.SuperPanel.HoldMilliseconds==650&&store.Current.SuperPanel.TriggerButton==SuperPanelButton.Middle,"super settings roundtrip");
  checks+=await SelectionTests.Run();
  checks+=ReleaseTests.Run(settings);
  checks+=await SetupTests.Run(settings);
  checks+=await UpdateTests.Run();
  Console.WriteLine($"PASS app assertions={checks}; systemClipboard=0; input=0");
 }
 sealed class Fake:ISelectionPlatform {
  public List<string> Calls=[];public SelectionFailure Failure=SelectionFailure.Unsupported;public bool Changes=true,SnapshotFails;public int DirectDelay;public string Data="original-all-formats";uint sequence;
  public nint Foreground=>123;public uint Sequence=>sequence;
  public SelectionResult Direct(nint window){Calls.Add("direct");Thread.Sleep(DirectDelay);return new(Failure==SelectionFailure.None?"direct":null,window,Failure);}
  public object Snapshot(){Calls.Add("snapshot");if(SnapshotFails)throw new InvalidOperationException();return Data;}
  public void Copy(nint window){Calls.Add("copy");if(Changes){Data="selected";sequence++;}}
  public string ReadText(){Calls.Add("read");return Data;}
  public void Restore(object snapshot){Calls.Add("restore");Data=(string)snapshot;sequence++;}
  public void Release(object snapshot)=>Calls.Add("release");
 }
}
