using System.IO.Compression;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using Pane.Contracts;
using Pane.Services.Ai;
using Pane.Services.Clipboard;
using Pane.Services.Cloud;
using Pane.Services.Modules;
using Pane.Settings;
using static Pane.Services.Cloud.CloudKeys;

internal static partial class Program
{
    static int checks;
    static void Check(bool ok, string label)
    {
        if (!ok) throw new InvalidOperationException(label);
        checks++;
    }
    static void Vectors()
    {
        foreach (var item in JsonNode.Parse(File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory,"kdf-vectors.json")))!.AsArray())
        {
            var v = item!;
            string Field(string k) => (string)v[k]!;
            var master = Master(Field("password"), Decode(Field("salt")), (int)v["kdf_iterations"]!);
            Check(Encode(master)==Field("master"),"master");
            Check(Encode(Expand(master,"pane-auth-v1"))==Field("auth_key"),"auth");
            var wrap=Expand(master,"pane-wrap-v1");
            var key=Decode(Field("data_key"));
            Check(Encode(wrap)==Field("wrap_key"),"wrap");
            Check(Wrap(wrap,key,nonce:Decode(Field("nonce")))==Field("wrapped_key"),"wrapped");
            Check(Unwrap(wrap,Field("wrapped_key")).SequenceEqual(key),"unwrap");
            Check(CheckKey(key)==Field("key_check"),"check");
            if(v["recovery"] is not {} r) continue;
            string display=(string)r["code_display"]!;
            var code=Decode((string)r["code_bytes"]!);
            Check(Display(code)==display,"recovery display");
            Check(Parse(" " + display.ToLowerInvariant().Replace('0','o').Replace('1','l').Replace("-"," \t-"))!.SequenceEqual(code),"normalize");
            var rk=Expand(code,"pane-recovery-v1");
            Check(Wrap(rk,key,true,Decode((string)r["nonce"]!))==(string)r["recovery_wrapped_key"]!,"recovery wrap");
            Check(Unwrap(rk,(string)r["recovery_wrapped_key"]!,true).SequenceEqual(key),"recovery unwrap");
        }
        foreach (var v in JsonNode.Parse(File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory,"sync-vectors.json")))!.AsArray())
        {
            string S(string k) => (string)v![k]!;
            byte[] B(string k) => Decode(S(k));
            var key = B("data_key");
            var c = S("c");
            var id = SyncCrypto.Id(key,c,S("natural_key"));
            Check(id == S("id"),"同步记录 id");
            Check(Encode(SyncCrypto.Seal(key,$"pane-rec-v1\0{c}\0{id}",B("deflate"),B("record_nonce"))) == S("data"),"记录密文");
            Check(SyncCrypto.Decrypt(key,c,id,S("data")) == S("plaintext_json"),"记录解压");
            Check(SyncCrypto.Decrypt(key,c,id,SyncCrypto.Encrypt(key,c,id,S("plaintext_json"))) == S("plaintext_json"),"记录往返");
            Check(Encode(SyncCrypto.EncryptBlob(key,S("blob_id"),B("blob_plaintext"),B("blob_nonce"))) == S("blob_ciphertext"),"块密文");
            Check(SyncCrypto.DecryptBlob(key,S("blob_id"),B("blob_ciphertext")).SequenceEqual(B("blob_plaintext")),"块明文");
        }
        static string CheckKey(byte[] key) => CloudKeys.Check(key);
    }
    sealed class Fake : HttpMessageHandler
    {
        internal readonly Queue<(string Verb,string Path,Func<HttpRequestMessage,JsonObject,HttpResponseMessage> Reply)> Steps=new();
        internal int Calls;
        internal TaskCompletionSource? BlockNext;
        internal void Expect(string verb,string path,Func<HttpRequestMessage,JsonObject,HttpResponseMessage> reply) => Steps.Enqueue((verb,path,reply));
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage req,CancellationToken ct)
        {
            Calls++;
            var step=Steps.Dequeue();
            Check(req.Method.Method==step.Verb && req.RequestUri!.AbsolutePath=="/api/v1/"+step.Path,"request "+step.Path);
            Check(req.Headers.GetValues("X-Pane-Client").Single()=="pane/0.1.110","version");
            var body=req.Content==null ? new JsonObject() : JsonNode.Parse(await req.Content.ReadAsStringAsync(ct))!.AsObject();
            if (BlockNext is {} started)
            {
                BlockNext = null;
                started.SetResult();
                await Task.Delay(Timeout.Infinite, ct);
            }
            return step.Reply(req,body);
        }
    }
    static HttpResponseMessage Json(JsonNode node,int status=200) => new((HttpStatusCode)status){Content=new StringContent(node.ToJsonString())};
    static HttpResponseMessage Empty() => new(HttpStatusCode.NoContent);
    static readonly JsonObject Me=JsonNode.Parse("""
        {"id":"u_test","email":"demo@example.com","nickname":"演示","created_at":1,"badges":[],
         "devices":{"count":1,"limit":5},"recovery":null,"avatar":{"etag":"\"avatar1\""},
         "subscription":{"status":"none","tier":null,"lifetime":false,"trial_available":true}}
        """)!.AsObject();
    static PaneSettings Defaults()=>new(1,new(false,true,ThemeMode.Dark,"#FF5874C9",20,1,new(.2,.2,1,1,1,0),new(.2,.2,1,1,1,0)),new(new(KeyModifiers.Ctrl,32)),new(new(KeyModifiers.Ctrl,86)));

    static async Task Integration(string root)
    {
        var fake=new Fake();
        var store=new SettingsStore(root,Defaults());
        store.Load();
        int created=0;
        var module=new CloudModule(() => { created++; return new AccountService(root,"0.1.110",fake,()=>[TrialHardware.Hash(null),TrialHardware.Hash("ffffffff-ffff-ffff-ffff-ffffffffffff")]); },store);
        var host=new ModuleHost([module],store,()=>false);
        await host.InitializeAsync(default);
        // 2026-11-24 开卖：Pane 云默认启用、在能力页显示；启用但没登录时一个请求都不发
        Check(created==1 && fake.Calls==0 && host.IsActive("cloud") && !host.Modules.Any(x=>x.Hidden),"默认启用、显示、未登录不联网");
        var settings=JsonNode.Parse(File.ReadAllText(Path.Combine(root,"settings.json")))!;
        settings["Modules"]!["cloud"]=true;
        settings["Launcher"]!.AsObject().Remove("CapsuleAvatar");
        settings["Launcher"]!.AsObject().Remove("FounderRing");
        File.WriteAllText(Path.Combine(root,"settings.json"),settings.ToJsonString());
        store.Load();
        Check(store.Current.Launcher is {CapsuleAvatar:false,FounderRing:true},"旧设置的头像选项默认值");
        store.Update(s=>s with {Launcher=s.Launcher with{CapsuleAvatar=true,FounderRing=false}});
        var reopened = new SettingsStore(root,Defaults());
        reopened.Load();
        Check(reopened.Current.Launcher is {CapsuleAvatar:true,FounderRing:false},"重新打开头像设置");
        await host.InitializeAsync(default);
        var account=module.Account!;
        Check(host.IsActive("cloud") && created>=1 && account.State==AccountState.SignedOut,"重新初始化后仍启用、未登录");
        Check(!(await account.RefreshAsync(default)).Ok && !(await account.GetPlansAsync(default)).Ok && fake.Calls==0,"signed out offline");
        await module.Sync!.ApplyAsync(new(Enabled:true),default);
        await module.Sync.SyncNowAsync(default);
        Check(fake.Calls == 0,"未登录时开启同步零调用");
        await module.Sync.ApplyAsync(new(),default);
        fake.Expect("POST","auth/code",(req,b)=>Json(new JsonObject{["error"]="rate_limited",["retry_after"]=60,["attempts_left"]=3},429));
        Check(await account.SendCodeAsync("demo@example.com",CodePurpose.Reset,default) is { Error:"rate_limited",RetryAfter:60,AttemptsLeft:3 },"error metadata");

        const string email="demo@example.com", pass="fixture-pass", token="fixture-session-110";
        string salt="",wrapped="",keyCheck="",device="",auth="",recovery="",newPassword=pass;
        const int iterations=600000;
        var secrets=new SecretStore(Path.Combine(root,"cloud"));
        JsonObject Session()=>JsonNode.Parse(secrets.Read("session.dpapi")!)!.AsObject();
        HttpResponseMessage SessionReply()=>Json(new JsonObject{["token"]=token,["wrapped_key"]=wrapped,["me"]=Me.DeepClone()});
        HttpResponseMessage NewKeys(HttpRequestMessage req,JsonObject body)
        {
            salt=(string)body["salt"]!; wrapped=(string)body["wrapped_key"]!; auth=(string)body["auth_key"]!;
            var m=Master(newPassword,Decode(salt),iterations);
            var data=Unwrap(Expand(m,"pane-wrap-v1"),wrapped);
            Check(Encode(Expand(m,"pane-auth-v1"))==auth && CloudKeys.Check(data)==(string?)body["key_check"],"new credentials");
            var id=(string)body["device"]!["id"]!;
            Check(Decode(id).Length==16 && (device=="" || id==device),"stable device");
            device=id;
            return SessionReply();
        }
        void Avatar(bool cached)
        {
            fake.Expect("GET","me/avatar",(req,b)=>{
                Check(req.Headers.Authorization?.Parameter==token && req.Headers.IfNoneMatch.Any()==cached,"avatar conditional");
                if(cached) return new(HttpStatusCode.NotModified);
                var response=new HttpResponseMessage(HttpStatusCode.OK){Content=new ByteArrayContent([1,2,3])};
                response.Headers.ETag=new("\"avatar1\"");return response;
            });
        }
        void Keys(string path,string verb)=>fake.Expect(verb,path,(req,b)=>Json(new JsonObject{["salt"]=salt,["kdf_iterations"]=iterations,["wrapped_key"]=wrapped}));
        fake.Expect("POST","auth/register",(req,b)=>{
            Check(b["consents"]!["cross_border"]!.GetValue<bool>() && (string?)b["code"]=="123456","registration");
            keyCheck=(string)b["key_check"]!; return NewKeys(req,b);
        });
        Avatar(false);
        Check((await account.RegisterAsync(email,"123456",pass,"演示",new("terms","privacy",true),default)).Ok,"registered");
        Check(File.ReadAllBytes(account.Profile!.AvatarPath!).SequenceEqual(new byte[]{1,2,3}),"avatar stored");
        var key=(string)Session()["key"]!;
        Check((string?)Session()["token"]==token && (string?)Session()["id"]=="u_test" && secrets.Read("device.dpapi")==device,"DPAPI roundtrip");
        int before = fake.Calls;
        await module.Sync.SyncNowAsync(default);
        Check(fake.Calls == before,"登录后同步关闭零调用");
        fake.Expect("PATCH","me",(req,b)=>{
            var me=Me.DeepClone(); me["badges"]=JsonNode.Parse("""[{"id":"founder","number":7,"granted_at":1,"public":true,"hidden":true}]""");
            Check((bool)b["founder_public"]!,"公开昵称参数"); return Json(me);
        });
        Check((await account.SetFounderPublicAsync(true,default)).Ok && account.Profile!.Badges.Single() is {Public:true,Hidden:true},"铭牌");
        fake.Expect("GET","member/group",(r,b)=>Json(JsonNode.Parse("""{"number":"100000000","code":"K7M2-9QXA","url":null}""")!));
        Check((await account.GetMemberGroupAsync(default)).Value == new CloudMemberGroup("100000000",null,"K7M2-9QXA"),"群资料");
        foreach(var err in new[]{"subscription_required","not_configured","not_founder"})
        {
            bool founder=err=="not_founder";
            fake.Expect(founder?"PATCH":"GET",founder?"me":"member/group",(r,b)=>Json(new JsonObject{["error"]=err},err=="not_configured"?404:403));
            CloudResult result=founder?await account.SetFounderPublicAsync(false,default):await account.GetMemberGroupAsync(default);
            Check(result.Error==err,"账号错误 "+err);
        }
        var zip=Path.Combine(root,"export.zip");
        await new DataStorage(store,()=>null,()=>{}).ExportAsync(zip,new(true,true,ClipboardExport.WithImages),null,default);
        using(var archive=ZipFile.OpenRead(zip))
        foreach(var entry in archive.Entries)
        {
            using var reader=new StreamReader(entry.Open());
            var contents=await reader.ReadToEndAsync();
            Check(!entry.FullName.Contains("dpapi") && !new[]{token,key,device}.Any(contents.Contains),"export");
        }
        fake.Expect("GET","me",(req,b)=>Json(Me));
        Check((await account.RefreshAsync(default)).Ok,"no avatar refetch within day");
        string avatar=account.Profile!.AvatarPath!;
        var originalTime=File.GetLastWriteTimeUtc(avatar);
        File.SetLastWriteTimeUtc(avatar+".etag",DateTime.UtcNow.AddDays(-2));
        fake.Expect("GET","me",(req,b)=>Json(Me)); Avatar(true);
        Check((await account.RefreshAsync(default)).Ok && File.GetLastWriteTimeUtc(avatar)==originalTime,"304 keeps file");

        fake.Expect("PUT","account/recovery",(req,b)=>{recovery=(string)b["recovery_wrapped_key"]!;Check((string?)b["key_check"]==keyCheck,"recovery check");return Empty();});
        string display=(await account.CreateRecoveryCodeAsync(default)).Value!;
        Check(Encode(Unwrap(Expand(Parse(display)!,"pane-recovery-v1"),recovery,true))==key,"recovery packaging");
        Keys("account/keys","GET");
        fake.Expect("POST","account/password",(req,b)=>{
            Check((string?)b["old_auth_key"]==auth,"old auth");
            var m=Master("updated-password",Decode((string)b["salt"]!),iterations);
            Check(Encode(Unwrap(Expand(m,"pane-wrap-v1"),(string)b["wrapped_key"]!))==key,"password preserves key");return Empty();
        });
        Check((await account.ChangePasswordAsync(pass,"updated-password",default)).Ok,"change password");
        fake.Expect("GET","me",(req,b)=>Json(new JsonObject{["error"]="unauthorized"},401));
        Check((await account.RefreshAsync(default)).Error=="unauthorized" && account.State==AccountState.NeedsSignIn &&
            account.LastEmail==email && (string?)Session()["key"]==key && Session()["token"]==null,"expired session");

        newPassword="reset-password";
        void Reset(bool fresh)
        {
            fake.Expect("POST","auth/reset",(req,b)=>{
                Check(((bool?)b["fresh"]==true)==fresh && ((string?)b["key_check"]==keyCheck)!=fresh,"reset mode");
                return NewKeys(req,b);
            });
            Avatar(true);
        }
        Reset(false);
        Check((await account.ResetPasswordAsync(email,"123456",newPassword,ResetMode.LocalKey,null,default)).Ok,"local reset");
        Check((await account.ResetPasswordAsync(email,"123456",newPassword,ResetMode.RecoveryCode,"xyz",default)).Error=="bad_recovery","invalid format offline");
        void FetchRecovery()=>fake.Expect("POST","auth/reset/recovery",(req,b)=>Json(new JsonObject{["recovery_wrapped_key"]=recovery}));
        FetchRecovery();
        Check((await account.ResetPasswordAsync(email,"123456",newPassword,ResetMode.RecoveryCode,Display(new byte[20]),default)).Error=="bad_recovery","wrong recovery no reset");
        FetchRecovery();Reset(false);
        Check((await account.ResetPasswordAsync(email,"123456",newPassword,ResetMode.RecoveryCode,display,default)).Ok,"recovery reset");
        Reset(true);
        Check((await account.ResetPasswordAsync(email,"123456",newPassword,ResetMode.Fresh,null,default)).Ok && (string?)Session()["key"]!=key,"fresh reset");
        fake.Expect("DELETE","account/recovery",(req,b)=>Empty());
        Check((await account.DeleteRecoveryCodeAsync(default)).Ok,"remove recovery");

        fake.Expect("POST","trial",(req,b)=>{Check(b["hw"]!.AsArray().Count==2 && b["hw"]!.AsArray().All(x=>x==null),"invalid hardware null");return Json(Me["subscription"]!);});
        Check((await account.ClaimTrialAsync(default)).Ok,"trial");
        const string guid="059f448e-d365-4300-937d-3eb219dabb34";
        Check(TrialHardware.Hash(guid)==Encode(SHA256.HashData(Encoding.UTF8.GetBytes("pane-trial-v1:"+guid))) && TrialHardware.Hash("03000200-0400-0500-0006-000700080009")==null,"hardware digest");

        void Order()=>fake.Expect("GET","orders/o",(req,b)=>Json(JsonNode.Parse("""{"order_no":"o","plan_id":"pane_year","status":"paid","amount":"69.90","created_at":1,"paid_at":2}""")!));
        Order();fake.Expect("GET","me",(req,b)=>Json(Me));Order();
        Check((await account.GetOrderAsync("o",default)).Value?.Status==CloudOrderStatus.Paid && (await account.GetOrderAsync("o",default)).Ok,"refresh once when paid");
        fake.Expect("POST","auth/logout",(req,b)=>Empty());
        Check((await account.SignOutAsync(default)).Ok && !account.HasLocalKey && Session()["token"]==null && account.LastEmail==email,"sign out");

        Keys("auth/prelogin","POST");
        fake.Expect("POST","auth/login",(req,b)=>{
            Check((string?)b["auth_key"]==auth && (string?)b["device"]!["id"]==device,"login credentials");
            return Json(JsonNode.Parse("""{"error":"device_limit","pending_token":"ticket","devices":[{"id":"other","name":"笔记本","current":false,"first_seen_at":1,"last_seen_at":2}]}""")!,409);
        });
        var limit=await account.SignInAsync(email,newPassword,default);
        Check(limit.Error=="device_limit" && limit.Value?.Devices.Single().Id=="other","device limit");
        fake.Expect("DELETE","devices/other",(req,b)=>{Check(req.Headers.Authorization?.Parameter=="ticket","remove pending");return Empty();});
        fake.Expect("POST","auth/login",(req,b)=>{Check(req.Headers.Authorization?.Parameter=="ticket","login pending");return SessionReply();});
        Avatar(true);
        Check((await account.RemoveDeviceAndSignInAsync("other",default)).Ok && account.HasLocalKey,"login retry");
        fake.Expect("POST","auth/reset",(req,b)=>{
            NewKeys(req,b);
            return Json(JsonNode.Parse("""{"error":"device_limit","pending_token":"reset-ticket","devices":[{"id":"other","name":"设备","current":false,"first_seen_at":1,"last_seen_at":2}]}""")!,409);
        });
        var resetLimit=await account.ResetPasswordAsync(email,"123456",newPassword,ResetMode.Fresh,null,default);
        Check(resetLimit.Value?.Devices.Single().Id=="other" && resetLimit.Error=="device_limit","重设后设备上限");
        fake.Expect("DELETE","devices/other",(r,b)=>{Check(r.Headers.Authorization?.Parameter=="reset-ticket","重设临时凭据");return Empty();});
        fake.Expect("POST","auth/login",(r,b)=>SessionReply());
        Avatar(true);
        Check((await account.RemoveDeviceAndSignInAsync("other",default)).Ok,"重设后继续登录");
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        fake.BlockNext = started;
        fake.Expect("GET","me",(req,b)=>throw new Exception("request should be cancelled"));
        var inflight = account.RefreshAsync(default);
        await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await host.ApplyAsync(host.Preview("cloud",false),default);
        try { await inflight; throw new Exception("ongoing request"); } catch(OperationCanceledException){}
        Check(module.Account==null && Session()["token"]!=null && Session()["key"]!=null && fake.Steps.Count==0,"disabled preserves session");
        int calls=fake.Calls;
        try { await account.RefreshAsync(default); throw new Exception("stale service"); } catch(OperationCanceledException){}
        Check(fake.Calls==calls,"disabled zero calls");
        await host.ApplyAsync(host.Preview("cloud",true),default);
        Check(module.Account is { State:AccountState.SignedIn, HasLocalKey:true, Profile.Email:email } && fake.Calls==calls,"resume offline");
        await host.ApplyAsync(host.Preview("cloud",false),default);
        Console.WriteLine($"HTTP calls={calls}, disabled=0, signed-out idle=0");
    }
    static async Task Main(string[] args)
    {
        if (args is ["--updates"])
        {
            try { await CloudUpdates(); }
            catch (Exception error) { Console.Error.WriteLine(error); Environment.ExitCode = 1; }
            return;
        }
        if(args is ["--clipboard",var binary,var prices,..var rest]){await Live(binary,prices,clipboard:true,perf:rest.FirstOrDefault());return;}
        if (args is ["--perf",var exe,var priceFile,var baseline,var current]) { await Live(exe,priceFile,baseline,current); return; }
        if (args is ["--sync", var server, var plans]) { await Live(server,plans); return; }
        if (args is ["--local-settings", var localServer, var localPlans]) { await Live(localServer,localPlans,localSettingsOnly:true); return; }
        Vectors();
        QrTests.Run();
        var root=Path.Combine(Path.GetTempPath(),"Pane-110-"+Guid.NewGuid().ToString("N"));
        try { await Integration(root); Console.WriteLine($"PASS assertions={checks}"); }
        finally { if(Directory.Exists(root)) Directory.Delete(root,true); }
    }
}
