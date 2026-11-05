using System.Diagnostics;
using System.Text;
using System.Text.Json;
using Pane.Contracts;
using Pane.Services.Setup;
using Pane.Services.Updates;
using Pane.Settings;

internal static class ReleaseNotes062Tests
{
    internal static async Task<int> Run(PaneSettings defaults)
    {
        int count=0;
        void Check(bool ok,string name){count++;if(!ok)throw new Exception("062: "+name);}
        string root=Path.Combine(Path.GetTempPath(),"Pane-062-"+Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var log=new List<string>();
            var catalog=new ReleaseNotesCatalog(log.Add);
            Check(catalog.All.Count>=1&&log.Count==0,"embedded catalog");
            Check(catalog.All.Zip(catalog.All.Skip(1)).All(p=>SetupFiles.CompareVersions(p.First.Version,p.Second.Version)>0),"descending numeric order");
            Check(catalog.Find("0.1.0366")==catalog.Find("0.1.366")&&catalog.Find("0.1.366")!=null,"numeric Find");
            Check(catalog.Find("invalid")==null,"invalid Find");
            Func<Stream> Data(string json)=>()=>new MemoryStream(Encoding.UTF8.GetBytes(json));
            var broken=new ReleaseNotesCatalog([("bad.json",Data("{")),("missing.json",()=>throw new IOException()),("mixed.json",Data("[{\"version\":\"0.1.2\",\"items\":[{\"kind\":\"bad\",\"text\":\"x\"}]},{\"version\":\"0.1.3\",\"items\":[{\"kind\":\"new\",\"text\":\"x\"}]}]"))],log.Add);
            Check(broken.All.Count==1&&log.Count==3,"bad entries skipped and logged");
            string repo=FindRepo(),source=Path.Combine(root,"notes"),output=Path.Combine(root,"generated");
            Directory.CreateDirectory(source);
            void Write(string name,string value)=>File.WriteAllText(Path.Combine(source,name),value);
            Write("old.json","{\"version\":\"0.1.1\",\"items\":[{\"kind\":\"fixed\",\"text\":\"旧修复\"}]}");
            Write("next.json","{\"items\":[]}");
            async Task<(int Code,string Text)> Build()
            {
                var start=new ProcessStartInfo("powershell.exe"){UseShellExecute=false,CreateNoWindow=true,RedirectStandardOutput=true,RedirectStandardError=true};
                foreach(var arg in new[]{"-NoProfile","-ExecutionPolicy","RemoteSigned","-File",Path.Combine(repo,"scripts","release-notes.ps1"),"-Source",source,"-Output",output,"-Version","0.1.2","-Date","2026-11-05"})start.ArgumentList.Add(arg);
                using var process=Process.Start(start)!;var stdout=process.StandardOutput.ReadToEndAsync();var stderr=process.StandardError.ReadToEndAsync();await process.WaitForExitAsync();return(process.ExitCode,await stdout+await stderr);
            }
            var firstBuild=await Build();Check(firstBuild.Code==0,"empty next build: "+firstBuild.Text);
            using(var json=JsonDocument.Parse(File.ReadAllText(Path.Combine(output,"catalog.json")) ))Check(json.RootElement.GetArrayLength()==1,"empty next omitted");
            string next="{\"items\":[{\"kind\":\"fixed\",\"text\":\"修复文字\"},{\"kind\":\"new\",\"text\":\"新增文字\",\"highlight\":true},{\"kind\":\"improved\",\"text\":\"改进文字\"}]}";
            Write("next.json",next);Check((await Build()).Code==0,"populated next build");
            var stamped=new ReleaseNotesCatalog([("generated",()=>File.OpenRead(Path.Combine(output,"catalog.json")))],log.Add).Find("0.1.2");
            Check(stamped?.Date==new DateOnly(2026,11,5)&&stamped.Items.Count==3,"version and date stamped");
            string md=File.ReadAllText(Path.Combine(output,"release-notes.md"));
            var parsed=ReleaseNotesCatalog.ParseMarkdown(md,"0.1.2",new DateTimeOffset(2026,11,5,0,0,0,TimeSpan.Zero));
            Check(parsed!=null&&parsed.Items.Select(i=>(i.Kind,i.Text)).SequenceEqual(stamped!.Items.OrderBy(i=>i.Kind).Select(i=>(i.Kind,i.Text))),"markdown roundtrip kind/text");
            Check(md.StartsWith("## 新增")&&md.IndexOf("## 改进")<md.IndexOf("## 修复"),"section order");
            Check(ReleaseNotesCatalog.ParseMarkdown("\n## 新增  \n\n* 新内容\n## 未知\n- 忽略\n## 修复\n- 修复内容","0.1.2",DateTimeOffset.Now)?.Items.Count==2,"tolerant old headings and unknown section");
            Check(ReleaseNotesCatalog.ParseMarkdown("没有标题\n- 历史说明","0.1.178",DateTimeOffset.Now)==null,"unstructured old body");
            foreach(var bad in new[]{next.Replace("fixed","invalid"),next.Replace("修复文字"," "),next.Replace("{\"items\"","{\"version\":\"0.1.2\",\"items\"")})
            {Write("next.json",bad);var result=await Build();Check(result.Code!=0&&result.Text.Contains("next.json"),"build rejects bad next with filename");}
            Write("next.json",next);Write("duplicate.json","{\"version\":\"0.1.01\",\"items\":[]}");
            var duplicate=await Build();Check(duplicate.Code!=0&&duplicate.Text.Contains(".json"),"numeric duplicate rejected");

            var store=new SettingsStore(Path.Combine(root,"settings"),defaults);store.Load();
            Check(store.Current.Update.SeenVersion==SettingsStore.CurrentVersion(),"fresh seen version");
            Check(!store.Current.Update.InstallOnStartup&&store.Current.Update.NewsShown==0&&store.Current.Launcher.Width==LauncherWidth.Standard&&store.Current.Launcher.AppRows==2&&store.Current.Launcher.FileRows==3,"new field defaults");
            string settingsFile=Path.Combine(store.DirectoryPath,"settings.json");
            var old=JsonSerializer.Serialize(defaults,SettingsJson.Default.PaneSettings);
            using(var doc=JsonDocument.Parse(old))old="{"+string.Join(",",doc.RootElement.EnumerateObject().Where(p=>p.Name!="Update").Select(p=>("\""+p.Name+"\"")+":"+p.Value.GetRawText()))+"}";
            File.WriteAllText(settingsFile,old);store.Load();Check(store.Current.Update.SeenVersion=="0","old missing update seen version");
            File.WriteAllText(settingsFile,old.TrimEnd('}')+",\"Update\":{\"AutoUpdate\":true}}");store.Load();Check(store.Current.Update.SeenVersion=="0","old update missing seen version");
            using(var doc=JsonDocument.Parse(old))
            {
                var launcher=doc.RootElement.GetProperty("Launcher");
                string legacyLauncher="{"+string.Join(",",launcher.EnumerateObject().Where(p=>p.Name is not ("Width" or "AppRows" or "FileRows")).Select(p=>"\""+p.Name+"\":"+p.Value.GetRawText()))+"}";
                File.WriteAllText(settingsFile,old.Replace(launcher.GetRawText(),legacyLauncher));store.Load();
                Check(store.Current.Launcher.Width==LauncherWidth.Standard&&store.Current.Launcher.AppRows==2&&store.Current.Launcher.FileRows==3,"old launcher missing new fields");
            }
            foreach(var invalid in new[]{defaults with{Launcher=defaults.Launcher with{Width=(LauncherWidth)99}},defaults with{Launcher=defaults.Launcher with{AppRows=0}},defaults with{Launcher=defaults.Launcher with{AppRows=5}},defaults with{Launcher=defaults.Launcher with{FileRows=4}},defaults with{Update=defaults.Update with{NewsShown=-1}}})
            {
                bool rejected=false;try{store.Save(invalid);}catch(ArgumentException){rejected=true;}Check(rejected,"save validation");
                File.WriteAllText(settingsFile,JsonSerializer.Serialize(invalid,SettingsJson.Default.PaneSettings));store.Load();Check(store.Current==defaults,"invalid load uses defaults");
            }
            await using var server=new UpdateServer();
            server.Body="## 新增  \n\n* 新内容\n## 未知\n- 忽略\n## 修复\n- 修复内容";
            int installs=0;
            string data=Path.Combine(root,"update");
            var updateStore=new SettingsStore(data,defaults);updateStore.Load();
            void Options(bool auto=true,bool startup=true,string? failed=null)=>updateStore.Save(updateStore.Current with{Update=new(auto,startup,AutoInstallFailedVersion:failed)});
            Options();
            UpdateService Service(Action<string>? install=null,string version="0.1.99")=>new(version,data,()=>updateStore.Current.Update.AutoUpdate,install??(_=>installs++),server.Url);
            using(var empty=Service())Check(!StartupUpdate.TryInstall(empty,updateStore)&&installs==0,"not downloaded normal startup");
            using(var download=Service()){
                await download.CheckAsync(default);
                Check(download.Latest?.Details is {Items.Count:2} details&&details.Version=="0.1.100"&&details.Date==new DateOnly(2026,10,30),"release body mapped to Details");
            }
            int requests=server.Requests;
            using(var ready=Service())Check(StartupUpdate.TryInstall(ready,updateStore)&&installs==1&&ready.Latest?.Details?.Items.Count==2,"cached startup installs with details");
            Check(server.Requests==requests,"startup offline");
            Check(updateStore.Current.Update.AutoInstallFailedVersion=="0.1.100","failure latch persisted before handoff");
            using(var blocked=Service())Check(!StartupUpdate.TryInstall(blocked,updateStore)&&blocked.State==UpdateState.Ready&&installs==1,"old version restart blocked and ready");
            Options(failed:"0.1.0100");using(var blocked=Service())Check(!StartupUpdate.TryInstall(blocked,updateStore),"numeric failed version equality");
            Options();using(var failed=Service(_=>throw new IOException()))Check(!StartupUpdate.TryInstall(failed,updateStore)&&updateStore.Current.Update.AutoInstallFailedVersion=="0.1.100","launch failure latch");
            var reload=new SettingsStore(data,defaults);reload.Load();Check(reload.Current.Update.AutoInstallFailedVersion=="0.1.100","failure survives reload");
            using(var newer=Service(version:"0.1.100"))Check(!StartupUpdate.TryInstall(newer,updateStore)&&updateStore.Current.Update.AutoInstallFailedVersion==null,"new version clears latch");
            Options(startup:false);using(var off=Service())Check(!StartupUpdate.TryInstall(off,updateStore),"startup toggle off");
            Options(auto:false);using(var off=Service())Check(!StartupUpdate.TryInstall(off,updateStore),"auto update off");
            Options();File.WriteAllText(Path.Combine(data,"updates","Pane-Setup-0.1.100.exe"),"bad");using(var bad=Service())Check(!StartupUpdate.TryInstall(bad,updateStore),"tampered cache not installed");
        }
        finally{SetupFiles.Delete(root);}
        Console.WriteLine($"PASS 062 assertions={count}; externalRequests=0; realInstall=0");return count;
    }
    static string FindRepo(){for(var d=new DirectoryInfo(AppContext.BaseDirectory);d!=null;d=d.Parent)if(File.Exists(Path.Combine(d.FullName,"scripts","release-notes.ps1")))return d.FullName;throw new DirectoryNotFoundException();}
}
