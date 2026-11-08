using Pane.Contracts;
using Pane.Services;
using Pane.Services.Clipboard;
using Pane.Settings;

static partial class Program
{
    static async Task Capture080Tests()
    {
        int before=checks;
        string root=TestResults.NewDirectory();Directory.CreateDirectory(root);
        string logPath=Path.Combine(root,"logs","clipboard.log");
        var log=new ClipboardLog(root);var options=Defaults().Clipboard;
        var platform=new CaptureFake();var entries=new List<ClipboardContent>();var delays=new List<int>();
        Task Run(Action action){action();return Task.CompletedTask;}
        Task Delay(int ms,CancellationToken token){delays.Add(ms);return Task.CompletedTask;}
        ClipboardCapture Capture(Func<int,CancellationToken,Task>? wait=null)=>new(platform,()=>options,log,entries.Add,Run,wait??Delay);
        void Reason(string reason)=>Check(File.ReadAllLines(logPath).Last().Contains("step="+reason),reason);
        platform.Value=platform.Value with{Own=true};await Capture().Capture(1,default);
        Check(entries.Count==0,"自身标记不记录");Reason("own-marker");
        platform.Value=platform.Value with{Own=false};await Capture().Capture(1,default);
        Check(entries.Count==1&&entries[0].Source=="Pane"&&entries[0].Program=="Pane.exe","主动复制记录 Pane 来源");
        entries.Clear();platform.Attempts=0;platform.Failures=2;await Capture().Capture(1,default);
        Check(entries.Count==1&&platform.Attempts==3&&delays.SequenceEqual([50,150]),"第三次成功且按退避重试");
        entries.Clear();delays.Clear();platform.Attempts=0;platform.Failures=int.MaxValue;
        await Capture().Capture(1,default);Check(entries.Count==0&&delays.SequenceEqual(ClipboardCapture.Delays),"重试有界");Reason("open-failed");
        var waiting=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using(var cancel=new CancellationTokenSource()){
            var old=Capture((_,token)=>{waiting.SetResult();return Task.Delay(Timeout.Infinite,token);}).Capture(1,cancel.Token);
            await waiting.Task;Check(!old.IsCompleted,"退避异步返回，不阻塞通知");
            platform.Sequence=2;cancel.Cancel();await old;Reason("sequence-changed");
            platform.Failures=0;platform.Value=platform.Value with{Text="new fixture"};await Capture().Capture(2,default);
            Check(entries.Count==1&&entries[0].Text=="new fixture","放弃旧项并读取新项");
        }
        await Capture().Capture(1,default);Reason("sequence-changed");
        foreach(var metadata in new[]{platform.Value with{Exclude=true},platform.Value with{History=0},platform.Value with{Cloud=0}}){
            platform.Value=metadata;await Capture().Capture(2,default);Reason("exclude-marker");
            platform.Value=new(Text:"private fixture",Program:"Pane.exe",Source:"Pane");
        }
        options=options with{ExcludedPrograms=["Pane.exe"]};await Capture().Capture(2,default);Reason("excluded-program");options=Defaults().Clipboard;
        foreach(string kind in new[]{"text","files","image"}){
            platform.Kind=kind;options=options with{RecordText=false,RecordFiles=false,RecordImages=false};await Capture().Capture(2,default);Reason("type-disabled");
        }
        options=Defaults().Clipboard;platform.Kind="";await Capture().Capture(2,default);Reason("unknown-format");
        platform.Kind="text";platform.Throw=true;await Capture().Capture(2,default);Reason("error");
        Check(platform.Opens==platform.Closes,"所有已打开的剪贴板均关闭");
        string lines=File.ReadAllText(logPath);Check(!lines.Contains("private fixture")&&!lines.Contains("new fixture"),"日志没有内容和异常消息");
        Check(File.ReadAllLines(logPath).All(l=>DateTimeOffset.TryParse(l.Split(' ')[0],out _)&&l.Contains("program=Pane.exe")),"日志含时间与程序名");
        for(int i=0;i<5000;i++)log.Skip("unknown-format",new string('x',260)+"\r\ninjection");
        Check(new FileInfo(logPath).Length<=ClipboardLog.Limit&&new FileInfo(logPath+".1").Length<=ClipboardLog.Limit,"跳过日志轮转限量");
        Check(!File.ReadAllText(logPath).Contains("injection"),"来源名称限长");

        var modes=new List<ClipboardWriteMode>();string clipboard="old fixture";int writes=0;
        var actions=new SelectionActions((text,mode)=>{clipboard=text;modes.Add(mode);writes++;},()=>new PasteFake());
        await actions.CopyAsync("copy fixture",default);
        Check(modes.Single()==ClipboardWriteMode.UserCopy&&clipboard=="copy fixture","超级面板复制明确使用主动复制");
        await actions.ReplaceSelectionAsync("replacement fixture",1,default);
        Check(writes==2&&clipboard=="replacement fixture"&&modes.Last()==ClipboardWriteMode.UserCopy,"替换只写一次，留下替换文字且进入历史");
        var entry=new ClipEntry("fixture",ClipKind.Text,"history fixture",null,null,null,null,"Pane",null,DateTimeOffset.Now,false);
        await new WindowsClipboardWriter((_,mode,_)=>modes.Add(mode),()=>new PasteFake()).PasteAsync(entry,false,1,default);
        Check(modes.Last()==ClipboardWriteMode.HistoryPaste,"历史粘贴明确带自身标记");
        await new ClipboardAttachmentActions(new UnusedAttachment(),(_,mode,_)=>modes.Add(mode)).RunAsync("copy-image",entry with{Kind=ClipKind.Image},default);
        Check(modes.Last()==ClipboardWriteMode.UserCopy,"图片附件复制明确使用主动复制");
        Console.WriteLine($"PASS capture080 assertions={checks-before};systemClipboard=0");
    }
    sealed class UnusedAttachment:ILauncherAttachmentActions
    {
        public Task RunAsync(string actionId,ClipEntry entry,CancellationToken token)=>throw new Exception("不应转发图片复制");
    }
    sealed class CaptureFake:IClipboardCapturePlatform
    {
        public uint Sequence {get;set;}=1;
        public int Attempts,Failures,Opens,Closes;
        public bool Throw;
        public string Program=>"Pane.exe";
        public string Kind {get;set;}="text";
        public ClipboardContent Value=new(Text:"private fixture",Program:"Pane.exe",Source:"Pane");
        public bool Open(){if(++Attempts<=Failures)return false;Opens++;return true;}
        public void Close()=>Closes++;
        public ClipboardContent Metadata()=>Value;
        public ClipboardContent Read(ClipboardContent metadata)=>Throw?throw new IOException("private fixture"):metadata;
    }
}
