using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using Pane.Contracts;
using Pane.Services.Ai;
using Pane.Services.Cloud;
using Pane.Settings;

internal static partial class Program
{
    // 参数是本机从源码构建的开发服务和它的套餐文件。整个服务（含 SQLite 和块）只使用本次临时目录。
    static async Task Live(string binary,string plans,string? baseline=null,string? current=null,bool clipboard=false,string? perf=null,bool localSettingsOnly=false)
    {
        string root = Directory.CreateTempSubdirectory("Pane-live-112-").FullName;
        string db = Path.Combine(root,"test.db");
        using var socket = new TcpListener(IPAddress.Loopback,0);
        socket.Start();
        int port = ((IPEndPoint)socket.LocalEndpoint).Port;
        socket.Stop();
        var start = new ProcessStartInfo(Path.GetFullPath(binary)) { WorkingDirectory=root, UseShellExecute=false, CreateNoWindow=true };
        start.ArgumentList.Add("serve");
        foreach(var (name,value) in new Dictionary<string,string> {
            ["DEV_MODE"]="true",["LISTEN_ADDR"]="127.0.0.1:"+port,["PLANS_FILE"]=Path.GetFullPath(plans),
            ["DB_PATH"]=db,["BLOB_DIR"]=Path.Combine(root,"blobs"),["PANE_MIN_VERSION"]="0.1.0",
            ["PANE_PEPPER"]="pane-local-pepper",["AFDIAN_TOKEN"]="",["BACKUP_ENABLED"]="false" })
            start.Environment[name]=value;
        using var server=Process.Start(start)!;
        Environment.SetEnvironmentVariable("PANE_CLOUD_BASE",$"http://127.0.0.1:{port}/api/v1");
        try
        {
            using var probe=new HttpClient();
            await Until(async () => {
                try { using var r=await probe.GetAsync($"http://127.0.0.1:{port}/"); return true; }
                catch(HttpRequestException) {return false;}
            },"本机服务启动");
            string Sql(string sql,bool query=false)
            {
                var run=new ProcessStartInfo("node"){UseShellExecute=false,CreateNoWindow=true,RedirectStandardOutput=true};
                foreach(var arg in new[]{"--no-warnings","-e",
                    "const db=new(require('node:sqlite').DatabaseSync)(process.argv[1]);db.exec('PRAGMA busy_timeout=5000');if(process.argv[3]==='q')console.log(Object.values(db.prepare(process.argv[2]).get())[0]);else db.exec(process.argv[2]);db.close()",
                    db,sql,query?"q":"x"}) run.ArgumentList.Add(arg);
                using var p=Process.Start(run)!; var output=p.StandardOutput.ReadToEnd(); p.WaitForExit();
                if(p.ExitCode!=0)throw new InvalidOperationException("临时 SQLite 操作失败");
                return output.Trim();
            }
            void Fixture(string owner,string dev,string path)
            {
                string token=owner+"-"+dev;
                var me=Me.DeepClone(); me["id"]=owner; me["avatar"]=null;
                byte[] key=SHA256.HashData(Encoding.UTF8.GetBytes(owner));
                string hash=CloudKeys.Encode(SHA256.HashData(Encoding.UTF8.GetBytes("pane-local-pepper"+token)));
                Sql($"""
                    INSERT OR IGNORE INTO pane_users(id,email,nickname,created_at,salt,iterations,auth_salt,verifier,wrapped,terms,privacy,consent_at,cross_border)
                    VALUES('{owner}','{owner}@example.test','sample',1,'',600000,x'00',x'00','','1','1',1,1);
                    INSERT OR IGNORE INTO pane_devices VALUES('{owner}','{dev}','fixture',1,1);
                    INSERT OR REPLACE INTO pane_sessions(hash,uid,device,renewed,expires) VALUES('{hash}','{owner}','{dev}',unixepoch(),unixepoch()+3600);
                    INSERT INTO pane_periods(uid,start,tier,base_tier,granted,source) SELECT '{owner}',1,'year','year',1,'test'
                    WHERE NOT EXISTS(SELECT 1 FROM pane_periods WHERE uid='{owner}');
                    """);
                var secret=new SecretStore(Path.Combine(path,"cloud"));
                secret.Write("device.dpapi",dev);
                secret.Write("session.dpapi",new JsonObject{["token"]=token,["id"]=owner,["me"]=me,["key"]=CloudKeys.Encode(key)}.ToJsonString());
            }
            SettingsStore Store(string dev,string owner)
            {
                string dir=Path.Combine(root,dev); var s=new SettingsStore(dir,Defaults());s.Load();
                s.Save(s.Current with{Modules=new(){["cloud"]=true},SyncOptions=new(Enabled:true)});
                Fixture(owner,dev,dir);return s;
            }
            if(clipboard)
            {
                if(perf!=null)
                {
                    var sample=Store("perf","clipperf");
                    foreach(bool enabled in new[]{false,true})
                    {
                        var flags=new[]{"launcher","clipboard","gestures","super-panel","ai","folder-jump","rename","power-timer","words","cloud"}.ToDictionary(id=>id,id=>id is "cloud" or "clipboard");
                        sample.Save(sample.Current with{Modules=flags,SyncOptions=new(Enabled:true,Usage:false,Clipboard:enabled),Update=new(false),General=sample.Current.General with{ShowTrayIcon=false}});
                        await Measure(perf,enabled?"clipboard":"settings",sample.DirectoryPath);
                    }
                }
                else await LiveClipboard(Store,Sql);
                return;
            }
            if (baseline != null && current != null)
            {
                var sample=Store("perf","perf");
                foreach(var label in new[]{"baseline","off","on","off","on","baseline","on","baseline","off"})
                {
                    var flags=new[]{"launcher","clipboard","gestures","super-panel","ai","folder-jump","rename","power-timer","words","cloud"}.ToDictionary(id=>id,id=>id=="cloud");
                    sample.Save(sample.Current with{Modules=flags,SyncOptions=new(Enabled:label=="on"),Update=new(false),General=sample.Current.General with{ShowTrayIcon=false}});
                    await Measure(label=="baseline"?baseline:current,label,sample.DirectoryPath);
                }
                return;
            }
            var a=Store("a","first"); var b=Store("b","first");
            foreach (var store in new[]{a,b})
                store.Update(s=>s with { Modules=new(s.Modules!) { ["gestures"]=true } });
            var handler=new Faults();
            await using var aa=new AccountService(a.DirectoryPath,"1.0.0",handler);
            await using var ab=new AccountService(b.DirectoryPath,"1.0.0");
            await using var sa=new SyncService(a,aa); await Ready(sa);
            string legacyId=SyncCrypto.Id(aa.DataKey,"settings","Modules.gestures");
            // 直接写旧格式日志，模拟升级前离线时已经入队的手势开关。
            File.WriteAllText(PrepareFile(b.DirectoryPath),new JsonObject {
                ["Settings"]=new JsonObject { ["Modules.gestures"]=true },
                ["Records"]=new JsonObject { [legacyId]=new JsonObject {
                    ["Category"]="settings", ["Dirty"]=true,
                    ["Body"]=new JsonObject { ["v"]=1, ["k"]="Modules.gestures", ["val"]=true, ["at"]=10 }
                } }
            }.ToJsonString());
            await using var sb=new SyncService(b,ab); await Ready(sb);
            string Seq()=>Sql("SELECT seq FROM pane_sync_state WHERE uid='first'",true);
            string sequence=Seq();
            int expected=SyncData.Leaves(a.Current).Count+2;
            Check(Sql("SELECT COUNT(*) FROM pane_records WHERE uid='first'",true)==expected.ToString(),"首次记录数");
            a.Update(s=>s with { Modules=new(s.Modules!) { ["gestures"]=false }, Update=s.Update with { Source=UpdateSource.MirrorChyan } });
            await sa.SyncNowAsync(default);
            await sb.SyncNowAsync(default);
            Check(Seq()==sequence && b.Current.Modules!["gestures"] && b.Current.Update.Source==UpdateSource.GitHub,"能力开关和更新源仅在 A 改变");
            string sourceId=SyncCrypto.Id(aa.DataKey,"settings","Update.Source");
            Check(Sql($"SELECT COUNT(*) FROM pane_records WHERE id IN ('{legacyId}','{sourceId}')",true)=="0","首次同步和旧队列均未上传本机设置");
            var oldWrite=await aa.SyncRequest("sync",new(){["cursor"]=long.Parse(sequence),["changes"]=new JsonArray(
                Packet("settings",legacyId,0,"""{"v":1,"k":"Modules.gestures","val":false,"at":1}"""))},aa.SyncToken!,default);
            await sb.SyncNowAsync(default);
            await sa.SyncNowAsync(default);
            Check(Seq()==oldWrite["seq"]!.ToString() && b.Current.Modules!["gestures"],"收到旧版手势记录后不应用也不回传");
            sequence=Seq();
            a.Update(s=>s with {General=s.General with{StartWithWindows=true}, Launcher=s.Launcher with{Position=new("test",100,200)}});
            await sa.SyncNowAsync(default).WaitAsync(TimeSpan.FromSeconds(10));
            Check(Seq()==sequence && !b.Current.General.StartWithWindows,"本机设置不上传");
            foreach(var path in new[]{"General.StartWithWindows","Launcher.Position","Modules.cloud","SuperPanel.DisabledPrograms","SyncOptions.Enabled"})
            {
                string id=SyncCrypto.Id(aa.DataKey,"settings",path);
                Check(Sql($"SELECT COUNT(*) FROM pane_records WHERE id='{id}'",true)=="0","派生 id "+path);
            }
            var timer=Stopwatch.StartNew();
            a.Update(s=>s with {General=s.General with{PanelRadius=28}, Launcher=s.Launcher with{CapsuleAvatar=true,FounderRing=false}});
            await Until(()=>Task.FromResult(b.Current.General.PanelRadius==28 && b.Current.Launcher is {CapsuleAvatar:true,FounderRing:false}),"长轮询写回设置和启动器头像选项");
            Console.WriteLine($"settings latency={timer.ElapsedMilliseconds}ms");
            if (localSettingsOnly)
            {
                Console.WriteLine($"PASS local settings assertions={checks}");
                return;
            }
            sequence=Seq();await Task.Delay(2400);Check(Seq()==sequence,"设置写回无回环");

            handler.Block=new(TaskCreationOptions.RunContinuationsAsynchronously);
            a.Update(s=>s with{General=s.General with{PanelRadius=36,MotionSpeed=2}});
            var held=sa.SyncNowAsync(default);
            await Until(()=>Task.FromResult(handler.Holding),"等待并发提交");
            b.Update(s=>s with{General=s.General with{PanelRadius=34,ShowTrayIcon=false}});
            await sb.SyncNowAsync(default);
            handler.Block.SetResult(); await held;
            await Until(()=>Task.FromResult(a.Current.General.PanelRadius==34 && !a.Current.General.ShowTrayIcon && b.Current.General.MotionSpeed==2),"同项冲突和不同项合并");
            Check(handler.Conflicts>0,"收到服务端冲突");

            void Usage(SettingsStore store,int n,string extra)
            {
                File.WriteAllText(Path.Combine(store.DirectoryPath,"launcher-usage.json"),new JsonObject{["app"]=new JsonObject{["Count"]=n}}.ToJsonString());
                File.WriteAllText(Path.Combine(store.DirectoryPath,"terminal-history.json"),new JsonArray("same",extra).ToJsonString());
                store.NotifyUsageChanged();
            }
            await Until(()=>Task.FromResult(sa.State==SyncState.Idle && sb.State==SyncState.Idle),"等待两端完成设置合并");
            sequence=Seq();
            Usage(a,1,"left");Usage(a,2,"left");Usage(b,3,"right");
            await Task.Delay(2200);Check(Seq()==sequence,"连续使用未逐次上传");
            await Until(()=>Task.FromResult(a.OtherCounts.GetValueOrDefault("app")==3 && b.OtherCounts.GetValueOrDefault("app")==2),"30 秒合并上传",seconds:40);
            await Until(()=>Task.FromResult(a.OtherCounts.GetValueOrDefault("app")==3 && b.OtherCounts.GetValueOrDefault("app")==2),"使用合计");
            Check((int)SyncData.Usage(a.DirectoryPath)["counts"]!["app"]!+a.OtherCounts["app"]==5 &&
                new[]{"same","left"}.Concat(a.OtherCommands).Distinct().Count()==3,"原始次数和历史去重");

            string known=SyncCrypto.Id(aa.DataKey,"settings","General.PanelRadius");
            var knownRow=JsonNode.Parse(Sql($"SELECT json_object('base',seq,'data',data) FROM pane_records WHERE uid='first' AND id='{known}'",true))!;
            var content=JsonNode.Parse(SyncCrypto.Decrypt(aa.DataKey,"settings",known,(string)knownRow["data"]!))!;
            content["extra"]="future";content["at"]=DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            string future=SyncCrypto.Id(aa.DataKey,"settings","Future.Option"), opaque=SyncCrypto.Id(aa.DataKey,"cliptags","sample");
            JsonObject Packet(string c,string id,long basis,string json)=>new(){["c"]=c,["id"]=id,["base"]=basis,["data"]=SyncCrypto.Encrypt(aa.DataKey,c,id,json)};
            await aa.SyncRequest("sync",new(){["cursor"]=long.Parse(Seq()),["changes"]=new JsonArray(
                Packet("settings",known,(long)knownRow["base"]!,content.ToJsonString()),
                Packet("settings",future,0,"""{"v":1,"k":"Future.Option","val":"x","at":1}"""),
                Packet("cliptags",opaque,0,"""{"v":1,"new":true}"""))},aa.SyncToken!,default);
            await sa.SyncNowAsync(default);a.Update(s=>s with{General=s.General with{PanelRadius=30}});await sa.SyncNowAsync(default);
            string data=Sql($"SELECT data FROM pane_records WHERE uid='first' AND id='{known}'",true);
            Check(JsonNode.Parse(SyncCrypto.Decrypt(aa.DataKey,"settings",known,data))!["extra"]!.GetValue<string>()=="future","未知字段保留");
            var journal=SyncJournal.Read(Path.Combine(a.DirectoryPath,"cloud","sync.json"));
            Check((string?)journal.Records[future].Body?["val"]=="x" && journal.Records[opaque].Unknown!=null,"未知路径和类别保留");
            // 超过一页的未知类别，用同一真实账号的协议接口分批写入。
            for(int page=0;page<2;page++)
            {
                var many=new JsonArray();
                for(int i=0;i<260;i++)
                {
                    string id=SyncCrypto.Id(aa.DataKey,"cliptags",$"page-{page}-{i}");
                    ((IList<JsonNode?>)many).Add(Packet("cliptags",id,0,"""{"v":1}"""));
                }
                await aa.SyncRequest("sync",new(){["cursor"]=long.Parse(Seq()),["changes"]=many},aa.SyncToken!,default);
            }
            Sql("UPDATE pane_sync_state SET floor=seq+1,seq=seq+1 WHERE uid='first'");
            int initial=handler.Initial;
            await sa.SyncNowAsync(default);Check(handler.Initial>=initial+2,"游标过期重新分页");
            handler.Fail=true;
            var retry=sa.SyncNowAsync(default);
            await Until(()=>Task.FromResult(sa.State==SyncState.Error),"断网状态");
            timer.Restart();handler.Fail=false;await retry.WaitAsync(TimeSpan.FromSeconds(12));
            Check(timer.Elapsed.TotalSeconds>4 && sa.State==SyncState.Idle,"退避恢复");
            await sa.ApplyAsync(new(),default); await sb.ApplyAsync(new(),default);

            foreach(var mode in Enum.GetValues<SyncMergeChoice>())
            {
                string dir=Path.Combine(root,"switch"+mode);var s=new SettingsStore(dir,Defaults());s.Load();
                File.Copy(Path.Combine(a.DirectoryPath,"cloud","sync.json"),PrepareFile(dir),true);
                s.Save(s.Current with{Modules=new(){["cloud"]=true},General=s.Current.General with{PanelRadius=43},SyncOptions=new(Enabled:true)});
                string owner="next"+mode;Fixture(owner,"a",dir);
                await using var acct=new AccountService(dir,"1.0.0");
                string id=SyncCrypto.Id(acct.DataKey,"settings","General.PanelRadius");
                var pack=Packet("settings",id,0,"{}");
                pack["data"]=SyncCrypto.Encrypt(acct.DataKey,"settings",id,"""{"v":1,"k":"General.PanelRadius","val":55,"at":1}""");
                await acct.SyncRequest("sync",new(){["changes"]=new JsonArray(pack)},acct.SyncToken!,default);
                await using var sync=new SyncService(s,acct);
                await Until(()=>Task.FromResult(sync.PauseReason==SyncPauseReason.NeedsMergeChoice),"换账号暂停");
                await sync.ResolveMergeAsync(mode,default);await Ready(sync);
                double wanted=mode==SyncMergeChoice.UseCloud?55:43;
                Check(s.Current.General.PanelRadius==wanted,"换账号 "+mode);
                var cipher=Sql($"SELECT data FROM pane_records WHERE uid='{owner}' AND id='{id}'",true);
                Check((double)JsonNode.Parse(SyncCrypto.Decrypt(acct.DataKey,"settings",id,cipher))!["val"]! == wanted,"换账号云端 "+mode);
            }
            var legacy=Store("old","first");
            await using var old=new AccountService(legacy.DirectoryPath,"0.0.1");
            await using var outdated=new SyncService(legacy,old);
            await outdated.ApplyAsync(new(Enabled:true),default);
            await Until(()=>Task.FromResult(outdated.PauseReason==SyncPauseReason.ClientOutdated),"426");
            await outdated.ApplyAsync(new(),default);
            Sql("DELETE FROM pane_periods WHERE uid='first'");
            await sb.ApplyAsync(new(Enabled:true),default);
            await Until(()=>Task.FromResult(sb.PauseReason==SyncPauseReason.NoSubscription),"403");
            Sql("DELETE FROM pane_sessions WHERE uid='first'");
            await sa.ApplyAsync(new(Enabled:true),default);
            await Until(()=>Task.FromResult(sa.PauseReason==SyncPauseReason.NeedsSignIn && aa.State==AccountState.NeedsSignIn),"401");
            Console.WriteLine($"PASS live sync assertions={checks}");
        }
        finally
        {
            if(!server.HasExited)server.Kill();await server.WaitForExitAsync();
            // Windows 进程刚退出时 SQLite 文件句柄可能仍在释放，等本次夹具目录可删除。
            for(int attempt=0;;attempt++)
            {
                try { Directory.Delete(root,true); break; }
                catch(IOException) when(attempt<20) { await Task.Delay(250); }
            }
        }
    }
    static async Task Measure(string executable,string label,string data)
    {
        var exe=Path.GetFullPath(executable);
        bool development=false;
        for(var dir=new DirectoryInfo(Path.GetDirectoryName(exe)!); dir!=null; dir=dir.Parent)
            if(File.Exists(Path.Combine(dir.FullName,"Pane.App.csproj"))) development=true;
        if(!development)throw new InvalidOperationException("只测工程目录内的开发构建");
        var info=new ProcessStartInfo(exe){UseShellExecute=false,CreateNoWindow=true,WindowStyle=ProcessWindowStyle.Hidden};
        info.Environment["PANE_DATA_DIR"]=data;
        info.Environment["PANE_INSTANCE"]=Guid.NewGuid().ToString("N");
        info.Environment["PANE_OBSERVE"]="1";info.Environment["PANE_VERIFY"]="1";info.Environment["PANE_RESULT"]="";
        info.ArgumentList.Add("--autostart");
        using var app=Process.Start(info)!;
        try
        {
            if(label is "on" or "clipboard" or "settings")
                await Until(()=>Task.FromResult(File.Exists(Path.Combine(data,"cloud","sync.json")) && SyncJournal.Read(Path.Combine(data,"cloud","sync.json")) is {LastSuccess:not null} journal && journal.ClipboardEnabled==(label=="clipboard")),"AOT 首次同步");
            await Task.Delay(5000);
            app.Refresh(); var cpu=app.TotalProcessorTime;var timer=Stopwatch.StartNew();
            var samples=new List<double>();
            for(int i=0;i<5;i++){await Task.Delay(1000);app.Refresh();samples.Add(app.WorkingSet64/1048576.0);}
            var used=app.TotalProcessorTime-cpu;
            Console.WriteLine($"idle {label} median={samples.Order().ElementAt(2):F2} MiB cpu={used.TotalSeconds/timer.Elapsed.TotalSeconds*100:F2}% of one core");
        }
        finally
        {
            info.ArgumentList.Clear();info.ArgumentList.Add("--exit");
            using var stop=Process.Start(info)!;
            await stop.WaitForExitAsync();
            if(!app.WaitForExit(5000)){app.Kill();await app.WaitForExitAsync();throw new InvalidOperationException("测试实例退出超时");}
            Check(app.ExitCode==0 && stop.ExitCode==0,"隔离实例正常退出");
        }
    }
    static string PrepareFile(string root) => Path.Combine(Directory.CreateDirectory(Path.Combine(root,"cloud")).FullName,"sync.json");
    static Task Ready(SyncService s)=>Until(()=>Task.FromResult(s.State==SyncState.Idle),"同步空闲",()=>s.State+" "+s.ErrorCode+" "+s.PauseReason);
    static async Task Until(Func<Task<bool>> condition,string name,Func<string>? detail=null,int seconds=15)
    {
        var t=Stopwatch.StartNew();
        while(!await condition())
        {
            if(t.Elapsed.TotalSeconds>seconds)throw new InvalidOperationException(name+" "+detail?.Invoke());
            await Task.Delay(25);
        }
        Check(true,name);
    }
    sealed class Faults : DelegatingHandler
    {
        internal volatile bool Fail,Holding;
        internal int Conflicts,Initial;
        internal TaskCompletionSource? Block;
        internal Faults() : base(new HttpClientHandler{UseProxy=false}) {}
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage req,CancellationToken ct)
        {
            if(Fail)throw new HttpRequestException("模拟断网");
            if(req.Method==HttpMethod.Post && req.Content!=null)
            {
                var body=JsonNode.Parse(await req.Content.ReadAsStringAsync(ct))!;
                if((bool?)body["initial"]==true)Interlocked.Increment(ref Initial);
                if(Block is {} block){Holding=true;await block.Task.WaitAsync(ct);}
            }
            var r=await base.SendAsync(req,ct);
            if(req.Method==HttpMethod.Post && r.IsSuccessStatusCode)
                Interlocked.Add(ref Conflicts,JsonNode.Parse(await r.Content.ReadAsStringAsync(ct))?["conflicts"]?.AsArray().Count??0);
            return r;
        }
    }
}
