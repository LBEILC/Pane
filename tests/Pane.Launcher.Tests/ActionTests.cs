using Pane.Contracts;
using Pane.Services.Launcher;
using Pane.Settings;

static partial class Program
{
    sealed class ActionPlatform:LauncherActionPlatform
    {
        internal int Reads,Resolves;internal string? Property;

        internal IReadOnlyList<UninstallEntry> Entries=[new("one",@"C:\Fixture",null,"MsiExec.exe /X{fixture}",false)];
        internal override ActionTarget? Resolve(string launch){Resolves++;return launch=="missing"?null:new(@"C:\Fixture\app.exe",["--profile","a b"]);}
        internal override IReadOnlyList<UninstallEntry> ReadUninstall(){Reads++;return Entries;}
        internal override Task PropertiesAsync(string path,CancellationToken token){Property=path;return Task.CompletedTask;}
    }
    sealed class ActionProcess:ILaunchProcess
    {
        internal LaunchRequest? Request;internal Exception? Failure;
        public Task StartAsync(LaunchRequest request,CancellationToken token){token.ThrowIfCancellationRequested();if(Failure!=null)throw Failure;Request=request;return Task.CompletedTask;}
    }
    static async Task ActionTests()
    {
        int start=checks;const string target=@"C:\Fixture\app.exe";
        var entry=new UninstallEntry("one",@"C:\Fixture",null,"uninstall.exe",false);
        Check(UninstallMatching.Match(target,[entry])==entry,"install location match");
        Check(UninstallMatching.Match(target,[entry with{Location=null,Icon="\"C:\\Fixture\\app.exe\",-1"}])!=null,"quoted display icon match");
        Check(UninstallMatching.Match(@"c:\fixture\sub\..\APP.exe",[entry])!=null,"normalized paths");
        Check(UninstallMatching.Match(@"C:\FixtureOther\app.exe",[entry])==null,"directory boundary");
        Check(UninstallMatching.Match(target,[entry,entry with{Id="two"}])==null,"ambiguous entries");
        foreach(var root in new[]{@"C:\",@"C:\Program Files",@"C:\Program Files (x86)",@"D:\Program Files",@"C:\Program Files\Common Files"})Check(UninstallMatching.Match(target,[entry with{Location=root,Icon=target}])==null,"broad directory rejected");
        Check(UninstallMatching.Match(target,[entry with{SystemComponent=true}])==null,"system component rejected");
        Check(UninstallMatching.Match(target,[entry with{Command=" "}])==null,"missing uninstall rejected");
        Check(UninstallMatching.Normalize(@"\\server\share\app.exe")==null,"remote target rejected");
        var platform=new ActionPlatform();var executor=new FakeExecutor();var process=new ActionProcess();var actions=new LauncherActions(platform,executor,process);
        var app=new LauncherItem("app:1",LauncherItemKind.App,LauncherGroups.Applications,"fixture",null,new("app"));
        var indexed=new IndexedApp(app.Id,new PinyinTable().Create("fixture"),"fixture.lnk",false,null);
        var resolved=actions.Target(app,indexed);
        Check(actions.Get(app,resolved).SequenceEqual(Enum.GetValues<LauncherItemAction>()),"ordered app actions");
        Check(actions.Target(app,indexed with{Packaged=true})==null,"store no actions");
        Check(actions.Target(app,indexed with{Launch="missing"})==null,"unresolved shortcut no actions");
        foreach(var kind in new[]{LauncherItemKind.Command,LauncherItemKind.Calculation,LauncherItemKind.Web,LauncherItemKind.Ai,LauncherItemKind.Action})Check(actions.Get(app with{Kind=kind},actions.Target(app with{Kind=kind},null)).Count==0,"non file no actions");
        foreach(var kind in new[]{LauncherItemKind.File,LauncherItemKind.Folder}){
            var file=app with{Kind=kind,Id="favorite:"+target};
            Check(actions.Get(file,actions.Target(file,null)).SequenceEqual([LauncherItemAction.OpenLocation,LauncherItemAction.CopyPath,LauncherItemAction.Properties]),"file actions");
        }
        Check(actions.Target(app with{Kind=LauncherItemKind.File,Id=@"file:\\server\share\file.txt"},null)?.Path==@"\\server\share\file.txt","network file path preserved");
        foreach(var action in Enum.GetValues<LauncherItemAction>())await actions.Run(app,resolved,action,default);
        Check(executor.Calls.SequenceEqual(["reveal:"+target,"copy:"+target]),"fake reveal and clipboard target");
        Check(platform.Property==target,"fake properties target");
        Check(process.Request is {FileName:"MsiExec.exe",Elevated:false}&&process.Request.Arguments.SequenceEqual(["/X{fixture}"]),"MSI untouched");
        await actions.Run(app,resolved,LauncherItemAction.RunAsAdmin,default);
        Check(process.Request is {Elevated:true}&&process.Request.FileName==target&&process.Request.Arguments.SequenceEqual(["--profile","a b"]),"shortcut arguments preserved");
        platform.Entries=[entry with{Command="\"C:\\Fixture\\uninstall tool.exe\" /remove \"a b\""}];
        await actions.Run(app,resolved,LauncherItemAction.Uninstall,default);
        Check(process.Request!.FileName==@"C:\Fixture\uninstall tool.exe"&&process.Request.Arguments.SequenceEqual(["/remove","a b"]),"quoted uninstall arguments");
        platform.Entries=[];
        Check(!actions.Get(app,resolved).Contains(LauncherItemAction.Uninstall),"no matching uninstall");
        try{await actions.Run(app,resolved,LauncherItemAction.Uninstall,default);Check(false,"removed registry entry");}catch(LauncherActionException){Check(true,"removed registry entry");}
        var store=new SettingsStore(Path.Combine(AppContext.BaseDirectory,"results","actions-"+Guid.NewGuid()),Defaults());
        using var service=new LauncherSearch(store,new FixtureIndex(new FakeCatalog()),new FakeFiles(),executor,process:process,actionPlatform:platform);await service.Indexing;
        int reads=platform.Reads,resolves=platform.Resolves;await service.SearchAsync("wx",default);
        Check(platform.Reads==reads&&platform.Resolves==resolves,"search never invokes actions");
        await service.ExecuteElevatedAsync(app,default);var elevated=process.Request;
        await service.RunAsync(app,LauncherItemAction.RunAsAdmin,default);
        Check(elevated!.FileName==process.Request!.FileName&&elevated.Arguments.SequenceEqual(process.Request.Arguments)&&process.Request.Elevated,"shared elevation implementation");
        process.Failure=new System.ComponentModel.Win32Exception(1223);await service.RunAsync(app,LauncherItemAction.RunAsAdmin,default);Check(true,"UAC cancellation silent");
        process.Failure=new IOException();try{await service.RunAsync(app,LauncherItemAction.RunAsAdmin,default);Check(false,"failure wrapping");}catch(LauncherActionException e){Check(e.Message==Pane.Localization.Strings.Launcher_ActionFailed,"localized failure");}
        using var cancel=new CancellationTokenSource();cancel.Cancel();
        try{await service.GetActionsAsync(app,cancel.Token);Check(false,"cancel get");}catch(OperationCanceledException){Check(true,"cancel get");}
        try{await service.RunAsync(app,LauncherItemAction.CopyPath,cancel.Token);Check(false,"cancel run");}catch(OperationCanceledException){Check(true,"cancel run");}
        var sample=new Pane.DesignData.LauncherSampleSearch();
        Check((await sample.GetActionsAsync(app with{Title="微信"},default)).Count==5,"sample full actions");
        Check((await sample.GetActionsAsync(app with{Title="Visual Studio Code"},default)).Count==4,"sample no uninstall");
        Check((await sample.GetActionsAsync(app with{Title="终端"},default)).Count==0,"sample packaged");
        Console.WriteLine($"PASS actions assertions={checks-start};realExecutions=0;clipboardWrites=0");
    }
    static void ActionProbe()
    {
        var platform=new WindowsLauncherActionPlatform();var entries=platform.ReadUninstall();
        var catalog=new WindowsAppCatalog(Path.Combine(AppContext.BaseDirectory,"results","actions-probe")).Build(new PinyinTable(),default);
        int matched=0,unmatched=0,packaged=0,unresolved=0;
        foreach(var app in catalog.Apps){
            if(app.Packaged){packaged++;unmatched++;continue;}
            var target=platform.Resolve(app.Launch);if(target==null){unresolved++;unmatched++;continue;}
            bool match=UninstallMatching.Match(target.Path,entries)!=null;
            if(match)matched++;else unmatched++;
            if(new[]{"Visual Studio Code","7-Zip File Manager","Microsoft Edge"}.Contains(app.Name.Name))Console.WriteLine($"example={app.Name.Name};matched={match}");
        }
        Console.WriteLine($"actions catalog matched={matched};unmatched={unmatched};packaged={packaged};unresolved={unresolved};registryRows={entries.Count};readOnly=True");
    }
}
