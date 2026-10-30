using Pane.Contracts;
using Pane.Services;
internal static class SelectionTests
{
    public static async Task<int> Run()
    {
        int checks=0;void Check(bool ok,string name){checks++;if(!ok)throw new Exception(name);}
        string directory=Path.Combine(AppContext.BaseDirectory,"results","selection-019");
        var log=new SelectionLog(directory);
        var skipped=new Platform();var result=await new SelectionReader(skipped,()=>true,log).ReadAsync(default);
        Check(result.Failure==SelectionFailure.None&&skipped.Copied&&skipped.Restored,"optional format skipped then restored");
        var required=new Platform{Unavailable=true};result=await new SelectionReader(required,()=>true,log).ReadAsync(default);
        Check(result.Failure==SelectionFailure.ClipboardUnavailable&&!required.Copied,"required format failure prevents copy");
        var busy=new Platform{Unavailable=true,Busy=true};result=await new SelectionReader(busy,()=>true,log).ReadAsync(default);
        Check(result.Failure==SelectionFailure.ClipboardUnavailable&&!busy.Copied&&busy.Attempts>1,"busy snapshot exhausts retry without copy");
        string path=Path.Combine(directory,"logs","selection.log");var lines=File.ReadAllLines(path);
        Check(lines.TakeLast(3).All(x=>x.Contains("result=")&&x.Contains("totalMs=")),"one complete record per read");
        Check(!File.ReadAllText(path).Contains(Platform.Secret),"log excludes selected and clipboard text");
        var restoreFailure=new Platform{RestoreFails=true};result=await new SelectionReader(restoreFailure,()=>true,log).ReadAsync(default);
        Check(result.Failure==SelectionFailure.Unsupported&&File.ReadLines(path).Last().Contains("restoreFailed=True"),"restore failure is reported and logged");
        Check(!ClipboardHistorySuppression.TryConsume(),"restore failure clears pending suppression");
        for(int i=0;i<305;i++)log.Write($"fixture={i}\r\nmetadata");
        lines=File.ReadAllLines(path);Check(lines.Length==300&&lines[0].Contains("fixture=5 "),"bounded 300 single-line records");
        Console.WriteLine($"PASS selection assertions={checks}; fake retry only; systemClipboard=0");return checks;
    }
    sealed class Platform:ISelectionPlatform
    {
        public const string Secret="PRIVATE_FAKE_SELECTION_019";
        public bool Unavailable,Busy,Copied,Restored,RestoreFails;public int Attempts;uint sequence;
        public nint Foreground=>123;public uint Sequence=>sequence;
        public string Diagnostics=>Busy?$"snapshotAttempts={Attempts}":"format=49152 tymed=2 hr=0x80040069 required=false";
        public SelectionResult Direct(nint window)=>new(null,window,SelectionFailure.Unsupported);
        public object Snapshot(){do{Attempts++;if(!Busy)break;Thread.Sleep(5);}while(Attempts<4);if(Unavailable)throw new InvalidOperationException();return Secret;}
        public void Copy(nint window){Copied=true;sequence++;}
        public string ReadText()=>Secret;
        public void Restore(object snapshot){if(RestoreFails)throw new InvalidOperationException(Secret);Restored=(string)snapshot==Secret;sequence++;}
        public void Release(object snapshot){}
    }
}
