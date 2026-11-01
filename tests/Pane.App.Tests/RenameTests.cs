using Pane.Contracts;
using Pane.Services;
using Pane.Services.Rename;
using Pane.Settings;
using System.Diagnostics;
static class RenameTests
{
    static int checks;
    static void Check(bool ok,string name){checks++;if(!ok)throw new Exception("rename: "+name);}
    public static async Task<int> Run(PaneSettings defaults){
        var date=new DateTime(2024,2,3,14,5,6);string Transform(string name,params RenameRule[] rules)=>BatchRenamer.Transform(name,false,date,date.AddDays(1),2,rules);
        void Rule(string expected,RenameRule rule,string name="Alpha Beta.txt")=>Check(Transform(name,rule)==expected,rule.ToString());
        Rule("a Beta.txt",new RenameRule(RenameRuleKind.Replace){Find="ALPHA",Text="a"});Rule("Alpha Beta.txt",new RenameRule(RenameRuleKind.Replace){Find="ALPHA",Text="a",MatchCase=true});
        Rule("Beta-Alpha.txt",new RenameRule(RenameRuleKind.Replace){Find="(Alpha) (Beta)",Text="$2-$1",Regex=true,MatchCase=true});
        Rule("!Alpha Beta.txt",new RenameRule(RenameRuleKind.Insert){Text="!",Position=RenamePosition.Start});Rule("Alpha Beta!.txt",new RenameRule(RenameRuleKind.Insert){Text="!"});
        Rule("A!lpha Beta.txt",new RenameRule(RenameRuleKind.Insert){Text="!",Position=RenamePosition.Index,Index=2});Rule("Alpha Bet!a.txt",new RenameRule(RenameRuleKind.Insert){Text="!",Position=RenamePosition.Index,Index=2,FromEnd=true});
        Rule("Alpha Beta!.txt",new RenameRule(RenameRuleKind.Insert){Text="!",Position=RenamePosition.Index,Index=0,FromEnd=true});Rule("!Alpha Beta.txt",new RenameRule(RenameRuleKind.Insert){Text="!",Position=RenamePosition.Index,Index=-1});
        Rule("Alpha Bet!a.txt",new RenameRule(RenameRuleKind.Insert){Text="!",Position=RenamePosition.Index,Index=999});
        Rule("A!lpha Beta.txt",new RenameRule(RenameRuleKind.Insert){Text="!",Position=RenamePosition.Index,Index=999,FromEnd=true});
        Rule("005-Alpha Beta.txt",new RenameRule(RenameRuleKind.Number){Start=1,Step=2,Digits=3,Separator="-",Position=RenamePosition.Start});Rule("Alpha Beta_06.txt",new RenameRule(RenameRuleKind.Number){Start=10,Step=-2,Digits=2,Separator="_"});
        Rule("Aha Beta.txt",new RenameRule(RenameRuleKind.Remove){Index=2,Count=2});Rule("Alpha Ba.txt",new RenameRule(RenameRuleKind.Remove){Index=2,Count=2,FromEnd=true});
        Rule(".txt",new RenameRule(RenameRuleKind.Remove){Index=1,Count=int.MaxValue});Rule(".txt",new RenameRule(RenameRuleKind.Remove){Index=1,Count=int.MaxValue,FromEnd=true});
        Rule("Alpha Bet.txt",new RenameRule(RenameRuleKind.Remove){Index=999,Count=5});Rule("lpha Beta.txt",new RenameRule(RenameRuleKind.Remove){Index=999,Count=5,FromEnd=true});Rule("Alpha Beta.txt",new RenameRule(RenameRuleKind.Remove){Count=-1});
        Rule("alpha beta.txt",new RenameRule(RenameRuleKind.Case));Rule("ALPHA BETA.TXT",new RenameRule(RenameRuleKind.Case){Case=RenameCase.Upper,IncludeExtension=true});Rule("Hello World.txt",new RenameRule(RenameRuleKind.Case){Case=RenameCase.Title},"hELLO wORLD.txt");
        Rule("Alpha Beta.png",new RenameRule(RenameRuleKind.Extension){Text="png"});Rule("Alpha Beta",new RenameRule(RenameRuleKind.Extension));
        Rule("20240203_Alpha Beta.txt",new RenameRule(RenameRuleKind.Date){DateSource=RenameDateSource.Created,DateFormat="yyyyMMdd",Separator="_",Position=RenamePosition.Start});Rule("Alpha Beta2024-02-04.txt",new RenameRule(RenameRuleKind.Date));
        Rule("Alpha Beta.txt",new RenameRule(RenameRuleKind.Insert){Enabled=false,Text="!"});
        Check(Transform("a.txt",new RenameRule(RenameRuleKind.Insert){Text="b"},new RenameRule(RenameRuleKind.Replace){Find="ab",Text="c"})=="c.txt","order");
        Check(BatchRenamer.Transform("Dir.Name",true,date,date,0,[new RenameRule(RenameRuleKind.Extension){Text="jpg"},new RenameRule(RenameRuleKind.Case){Case=RenameCase.Upper}])=="DIR.NAME","folder no extension");
        string root=Path.Combine(AppContext.BaseDirectory,"results","rename-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(root);
        try{
            string FileAt(string name){var p=Path.Combine(root,name);File.WriteAllText(p,name);return p;}
            var a=FileAt("a.txt");var b=FileAt("b.txt");var c=FileAt("c.txt");var service=new BatchRenamer();
            async Task Issue(RenameIssue expected,params RenameRule[] rules){var rows=await new BatchRenamer().PreviewAsync([a],rules,default);Check(rows[0].Issue==expected,"issue "+expected);}
            await Issue(RenameIssue.Unchanged);await Issue(RenameIssue.Empty,new RenameRule(RenameRuleKind.Remove){Count=99},new RenameRule(RenameRuleKind.Extension));
            foreach(var invalid in new[]{"CON","nul.txt","LPT1","COM²","a?","a/","a\\","a:","a*","a\"","a<","a>","a|","a ","a."})Check(BatchRenamer.NameIssue(invalid,Path.Combine(root,invalid))==RenameIssue.Invalid,"invalid "+invalid);
            await Issue(RenameIssue.TooLong,new RenameRule(RenameRuleKind.Insert){Text=new string('a',256)});Check(BatchRenamer.NameIssue("a",new string('a',32767))==RenameIssue.TooLong,"long path");
            await Issue(RenameIssue.Exists,new RenameRule(RenameRuleKind.Replace){Find="a",Text="b"});await Issue(RenameIssue.BadPattern,new RenameRule(RenameRuleKind.Replace){Regex=true,Find="["});
            var duplicates=await service.PreviewAsync([a,b],[new RenameRule(RenameRuleKind.Replace){Regex=true,Find=".+",Text="same"}],default);Check(duplicates.All(p=>p.Issue==RenameIssue.Duplicate),"duplicates");
            Check((await service.PreviewAsync([Path.Combine(root,"gone")],[],default))[0].Issue==RenameIssue.Missing,"missing");
            foreach(var names in new[]{new[]{"b.txt","a.txt","c.txt"},new[]{"b.txt","c.txt","a.txt"},new[]{"A.txt","b.txt","c.txt"}}){
                string[] paths=[a,b,c];var plan=paths.Select((p,i)=>new RenamePreview(p,names[i],Path.GetFileName(p)==names[i]?RenameIssue.Unchanged:RenameIssue.None)).ToArray();var result=await service.ApplyAsync(plan,default);
                Check(result.Failed.Count==0&&result.UndoId!=null,"apply swap cycle case");for(int i=0;i<3;i++)Check(File.ReadAllText(Path.Combine(root,names[i]))==Path.GetFileName(paths[i]),"contents retained");
                Check(Directory.GetFiles(root).Any(p=>Path.GetFileName(p)==names[0]),"actual casing");var undone=await service.UndoAsync(result.UndoId!,default);Check(undone.Failed.Count==0&&File.ReadAllText(a)=="a.txt"&&File.ReadAllText(b)=="b.txt","undo swap cycle case");
            }
            string parent=Path.Combine(root,"parent.old");Directory.CreateDirectory(parent);string child=Path.Combine(parent,"child.txt");File.WriteAllText(child,"child");
            var nested=await service.ApplyAsync([new(parent,"parent.new",RenameIssue.None),new(child,"new.txt",RenameIssue.None)],default);Check(nested.Renamed==2&&File.ReadAllText(Path.Combine(root,"parent.new","new.txt"))=="child","parent child");Check((await service.UndoAsync(nested.UndoId!,default)).Failed.Count==0&&File.Exists(child),"nested undo");
            using(var held=new FileStream(a,FileMode.Open,FileAccess.Read,FileShare.None)){
                var blockedCycle=await service.ApplyAsync([new(a,"b.txt",RenameIssue.None),new(b,"a.txt",RenameIssue.None)],default);Check(blockedCycle.Renamed==0&&blockedCycle.Failed.Count==2&&File.Exists(b),"blocked cycle rolled back");
                var partial=await service.ApplyAsync([new(a,"held.txt",RenameIssue.None),new(b,"free.txt",RenameIssue.None)],default);Check(partial.Renamed==1&&partial.Failed.Count==1,"independent failure");Check((await service.UndoAsync(partial.UndoId!,default)).Failed.Count==0,"partial undo");
            }
            string prefixed=FileAt(".pane-rename-user.txt");var prefixResult=await service.ApplyAsync([new(prefixed,"prefix-renamed.txt",RenameIssue.None)],default);Check((await service.UndoAsync(prefixResult.UndoId!,default)).Renamed==1&&File.Exists(prefixed),"undo counts original names independently of temporary prefix");
            var cache=new BatchRenamer();await cache.PreviewAsync([c],[],default);File.Delete(c);Check((await cache.PreviewAsync([c],[new RenameRule(RenameRuleKind.Insert){Text="!"}],default))[0].Issue==RenameIssue.None,"cached metadata");Check((await cache.ApplyAsync([new(c,"new.txt",RenameIssue.None)],default)).Failed.Count==1,"apply rechecks missing");
            var paths500=Enumerable.Range(0,500).Select(i=>FileAt($"perf{i:D4}.txt")).ToArray();await service.PreviewAsync(paths500,[],default);var times=new List<double>();
            for(int i=0;i<25;i++){var timer=Stopwatch.StartNew();await service.PreviewAsync(paths500,[new RenameRule(RenameRuleKind.Number){Start=i,Digits=4}],default);times.Add(timer.Elapsed.TotalMilliseconds);}times.Sort();Console.WriteLine($"rename preview files=500 samples=25 medianMs={times[12]:F3} maxMs={times[^1]:F3}");Check(times[^1]<20,"preview budget");
            var store=new SettingsStore(Path.Combine(root,"settings"),defaults);var badRegex=new RenameRule(RenameRuleKind.Replace){Regex=true,Find="["};store.Save(defaults with{Rename=new(){LastRules=[badRegex],Presets=[new("test",[badRegex])]}});store.Load();Check(store.Current.Rename.LastRules[0]==badRegex&&store.Current.Rename.Presets[0].Rules[0]==badRegex,"settings roundtrip allows bad regex");
            foreach(var settings in new[]{new RenameSettings{LastRules=Enumerable.Repeat(badRegex,21).ToArray()},new RenameSettings{Presets=Enumerable.Range(0,31).Select(i=>new RenamePreset(i.ToString(),[])).ToArray()},new RenameSettings{Presets=[new("test",Enumerable.Repeat(badRegex,21).ToArray())]}}){try{store.Save(defaults with{Rename=settings});Check(false,"settings limit");}catch(ArgumentException){checks++;}}
            store.Save(defaults with{Rename=new(){LastRules=Enumerable.Repeat(badRegex,20).ToArray(),Presets=Enumerable.Range(0,30).Select(i=>new RenamePreset(i.ToString(),[badRegex])).ToArray()}});Check(store.Current.Rename.Presets.Length==30,"settings boundary");
            var shell=new FakeShell{Paths=[a,b]};var selection=new FileSelection(shell);Check((await selection.ReadAsync(123,default)).SequenceEqual([a,b]),"shell order and background handle");Check(shell.Window==123,"exact window");shell.Edit=true;Check(selection.Read(123,true).Count==0&&shell.Reads==1,"edit skips shell");Check((await selection.ReadAsync(123,default)).Count==2,"launcher ignores edit preference");
            var platform=new TextPlatform();shell.Edit=false;var reader=new SelectionReader(platform,()=>true,files:selection);var selected=await reader.ReadAsync(default);Check(selected.Text==null&&selected.Failure==SelectionFailure.None&&selected.Files!.Count==2&&platform.DirectCalls==0,"files bypass uia clipboard");shell.Edit=true;Check((await reader.ReadAsync(default)).Text=="text"&&platform.DirectCalls==1,"edit reads text");shell.Edit=false;shell.Paths=[];Check((await reader.ReadAsync(default)).Text=="text","empty selection reads text");
        }finally{Directory.Delete(root,true);}
        using(var process=Process.GetCurrentProcess()){
            Thread.Sleep(100);process.Refresh();var before=process.TotalProcessorTime;Thread.Sleep(1000);process.Refresh();double baseline=(process.TotalProcessorTime-before).TotalMilliseconds;
            var idleShell=new FakeShell();var idleSelection=new FileSelection(idleShell);var idleRenamer=new BatchRenamer();int threads=process.Threads.Count;before=process.TotalProcessorTime;
            Thread.Sleep(1000);process.Refresh();double enabled=(process.TotalProcessorTime-before).TotalMilliseconds;
            Check(idleShell.Reads==0,"idle never enters shell");Console.WriteLine($"rename idle baselineCpuMs={baseline:F3} enabledCpuMs={enabled:F3} threads={threads}->{process.Threads.Count} shellReads={idleShell.Reads} wallMs=1000 each");GC.KeepAlive(idleSelection);GC.KeepAlive(idleRenamer);
        }
        Console.WriteLine($"PASS rename assertions={checks}; realClipboard=0");return checks;
    }
    sealed class FakeShell:IFileSelectionPlatform{public bool Edit;public string[] Paths=[];public int Reads;public nint Window;public bool Editing(nint window)=>Edit;public IReadOnlyList<string> Read(nint window){Window=window;Reads++;return Paths;}}
    sealed class TextPlatform:ISelectionPlatform{public int DirectCalls;public nint Foreground=>123;public uint Sequence=>0;public SelectionResult Direct(nint w){DirectCalls++;return new("text",w,SelectionFailure.None);}public object Snapshot()=>throw new Exception("clipboard");public void Copy(nint w)=>throw new Exception("clipboard");public string? ReadText()=>throw new Exception("clipboard");public void Restore(object s)=>throw new Exception("clipboard");public void Release(object s)=>throw new Exception("clipboard");}
}
