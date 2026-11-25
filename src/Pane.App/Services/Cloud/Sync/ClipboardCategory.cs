using System.IO.Compression;
using System.Text;
using System.Text.Json.Nodes;
using Pane.Contracts;
using Pane.Services.Clipboard;

namespace Pane.Services.Cloud;

internal sealed partial class SyncService
{
    readonly ClipboardCloudLink clipboard;
    bool ClipboardEnabled(SyncOptions o)=>o.Clipboard && clipboard.History!=null;
    void AcknowledgeClip(string id,SyncRecord r)
    {
        if(account.SyncIdentity().Owner is {} owner)
            clipboard.History?.Acknowledge(owner,id,r.Base,(string?)r.Body?["thumb"],(string?)r.Body?["orig"],(long?)r.Body?["size"]);
    }
    sealed class ClipboardCategory(SyncService engine,string name):ISyncCategory
    {
        public string Name=>name;
        public bool Enabled(SyncOptions options)=>engine.ClipboardEnabled(options);
        ClipboardHistory History=>engine.clipboard.History??throw new OperationCanceledException();
        string Owner=>engine.account.SyncIdentity().Owner??throw new OperationCanceledException();
        static long Time(JsonObject? body,string field)=>(long?)body?[field]??-1;
        static JsonArray Strings(IEnumerable<string> values)=>new(values.Select(x=>(JsonNode?)JsonValue.Create(x)).ToArray());
        static string[] Values(JsonNode? array)=>array?.AsArray().Select(x=>(string)x!).ToArray()??[];
        public void Capture(SyncJournal j,byte[] key,string device)
        {
            var (clips,ledger)=History.CloudSnapshot(Owner,name);
            if(name=="control")
            {
                if(!ledger.Clear.TryGetValue(Owner,out long before))return;
                string id=SyncCrypto.Id(key,name,"clip-clear");
                if(before<=Time(j.Records.GetValueOrDefault(id)?.Body,"before"))return;
                PruneCleared(j,before);
                var body=Local(j,id,name,device).Body!;
                body["before"]=before; body["at"]=before;
                return;
            }
            if(name=="cliptags")
            {
                foreach(var tag in ledger.Tags.Values.Where(t=>t.Owner==Owner))
                {
                    string id=SyncCrypto.Id(key,name,tag.Key);
                    var previous=j.Records.GetValueOrDefault(id);
                    if(previous?.Unknown!=null || tag.At<=Time(previous?.Body,"at") || previous!=null && previous.Body==null)continue;
                    var rec=Local(j,id,name,device);
                    if(tag.Name==null){rec.Body=null;rec.DeletedAt=tag.At;continue;}
                    var b=rec.Body!;
                    b["key"]=tag.Key;b["name"]=tag.Name;b["color"]=tag.Color;b["order"]=tag.Order;b["at"]=tag.At;
                }
                return;
            }
            foreach(var (id,owner) in ledger.Deleted)
                if(owner==Owner && (!j.Records.TryGetValue(id,out var rec)||rec.Body!=null))Local(j,id,name,device).Body=null;
            foreach(var c in clips)
            {
                var r=c.Replica!;var e=c.Entry;
                if(r.Origin==null && (e.Kind==ClipKind.Image && engine.Options.ClipScope==ClipSyncScope.Text || engine.Options.ExcludedApps?.Contains(r.Program??"",StringComparer.OrdinalIgnoreCase)==true))continue;
                CaptureClip(j,device,c,r.Id);
                foreach(string alias in r.Aliases??[])
                    if(j.Records.ContainsKey(alias))CaptureClip(j,device,c,alias);
            }
        }
        void CaptureClip(SyncJournal j,string device,StoredClip c,string id)
        {
            var r=c.Replica!;
            var e=c.Entry;
            bool alias=id!=r.Id;
            j.Records.TryGetValue(id,out var rec);
            if(rec?.Unknown!=null || rec!=null && rec.Body==null)return;
            var b=rec?.Body;
            bool edit=(r.LocalEdit??r.Edit)==r.Edit && r.Edit>Time(b,"at") && (!alias || b?["h"]?.ToString()!=c.SyncHash);
            bool pin=(r.LocalPin??r.Pin)==r.Pin && r.Pin>Time(b,"pinAt");
            bool tags=(r.LocalTags??r.Tags)==r.Tags && r.Tags>Time(b,"tagsAt");
            if(!(edit||pin||tags))return;
            var local=Local(j,id,name,device);
            if(rec==null)local.Base=r.Base;
            local.LocalProgram=r.Program;
            b=local.Body!;
            if(edit)
            {
                // TODO(design): 可同步字段使用白名单；来源只传显示名，exe、图标和文件路径留在本机。
                b["at"]=r.Edit;b["kind"]=e.Kind.ToString();
                if(!alias){b["origin"]=r.Origin??device;b["source"]=e.Source;}
                if(c.SyncHash!=null)b["h"]=c.SyncHash;
                if(!alias)b["copied"]=(r.CopiedAt??e.CopiedAt).ToUnixTimeMilliseconds();
                b["width"]=e.ImageWidth;b["height"]=e.ImageHeight;
                b["text"]=e.Text;b.Remove("richBlocks");
                var f=History.Formats(c);b["html"]=f==null?null:Convert.ToBase64String(f.Html);
                b["rtf"]=f==null?null:Convert.ToBase64String(f.Rtf);
            }
            if(pin){b["pinAt"]=r.Pin;b["pinned"]=e.Pinned;}
            if(tags){b["tagsAt"]=r.Tags;b["tags"]=Strings(e.Tags);}
        }
        static void PruneCleared(SyncJournal journal,long before)
        {
            foreach(var (id,r) in journal.Records.ToArray())
                if(r.Category=="clip" && r.Body!=null && Time(r.Body,"copied")<before && (bool?)r.Body["pinned"]!=true && Values(r.Body["tags"]).Length==0)
                    journal.Records.Remove(id);
        }
        async Task<string> Upload(byte[] key,string kind,byte[] bytes,CancellationToken ct)
        {
            string id=CloudKeys.Encode(Guid.NewGuid().ToByteArray());
            await engine.account.Blob(id,kind,SyncCrypto.EncryptBlob(key,id,bytes),engine.Token,ct);
            return id;
        }
        static byte[] Compress(JsonObject fields)
        {
            using var buffer=new MemoryStream();
            using(var zip=new DeflateStream(buffer,CompressionLevel.Fastest,true))zip.Write(Encoding.UTF8.GetBytes(fields.ToJsonString()));
            return buffer.ToArray();
        }
        public async Task<JsonObject> Encode(string id,SyncRecord record,byte[] key,CancellationToken ct)
        {
            var wire=new JsonObject{["c"]=name,["id"]=id,["base"]=record.Base};
            var b=record.Body;
            if(b==null||record.DeleteFirst){wire["deleted"]=true;return wire;}
            if(name=="control")wire["op"]="clip-clear";
            if(name=="clip")
            {
                if((string?)b["kind"]=="Image" && (!b.ContainsKey("thumb") || !b.ContainsKey("size") && !b.ContainsKey("orig")))
                {
                    var c=History.CloudLookup(Owner,id);
                    if(!b.ContainsKey("thumb") && c?.Entry.Thumbnail?.Path is {} path && File.Exists(path))
                    {
                        var bytes=ClipboardThumbnail.Read(path);
                        b["thumb"]=await Upload(key,"thumb",bytes,ct);
                    }
                    if(!b.ContainsKey("size") && c?.Entry.ContentPath is {} full && new FileInfo(full) is {Exists:true,Length:<=10485760} file)
                    {
                        try{b["orig"]=await Upload(key,"orig",await File.ReadAllBytesAsync(full,ct),ct);b["size"]=file.Length;}
                        catch(AccountService.Failure e) when(e.Code=="storage_full"){b["size"]=null;b.Remove("orig");}
                    }
                }
                if(!b.ContainsKey("richBlocks") && Compress(b).Length>48*1024)
                {
                    var payload=new JsonObject();
                    foreach(var field in new[]{"text","html","rtf"})payload[field]=b[field]?.DeepClone();
                    var bytes=Compress(payload);var blocks=new List<string>();
                    for(int pos=0;pos<bytes.Length;pos+=2400000)
                        blocks.Add(await Upload(key,"rich",bytes.AsMemory(pos,Math.Min(bytes.Length-pos,2400000)).ToArray(),ct));
                    foreach(var field in payload)b.Remove(field.Key);
                    b["richBlocks"]=Strings(blocks);
                }
                wire["keep"]=(bool?)b["pinned"]==true || Values(b["tags"]).Length>0;
                wire["blobs"]=Strings(new[]{(string?)b["thumb"],(string?)b["orig"]}.OfType<string>().Concat(Values(b["richBlocks"])));
            }
            wire["data"]=SyncCrypto.Encrypt(key,name,id,b.ToJsonString());
            return wire;
        }
        public async Task Receive(SyncJournal j,JsonObject wire,byte[] key,bool initial,CancellationToken ct)
        {
            string id=(string)wire["id"]!,device=(string)wire["dev"]!;
            long seq=(long)wire["seq"]!;
            j.Records.TryGetValue(id,out var local);
            if(!initial && local!=null && local.Base>=seq)return;
            var body=(bool?)wire["deleted"]==true?null:JsonNode.Parse(SyncCrypto.Decrypt(key,name,id,(string)wire["data"]!))!.AsObject();
            var remote=new SyncRecord{Category=name,Base=seq,Body=body,Device=device,LocalProgram=local?.LocalProgram};
            if(body!=null && (int?)body["v"]!=1){remote.Body=null;remote.Unknown=wire.DeepClone().AsObject();j.Records[id]=remote;return;}
            if(local?.Dirty==true)
            {
                long localAt=local.Body==null?local.DeletedAt:Time(local.Body,"at");
                long remoteAt=body==null?((long?)wire["t"]??0)*1000:Time(body,"at");
                if(name=="clip" && local.Body==null || name!="clip" && localAt>remoteAt)
                {
                    local.Base=seq;
                    if(body!=null && local.Body!=null)
                        foreach(var (field,value) in body)
                            if(!local.Body.ContainsKey(field))local.Body[field]=value?.DeepClone();
                    return;
                }
                if(body!=null && name=="clip")
                {
                    var merged=body.DeepClone().AsObject();
                    foreach(var (field,value) in local.Body!)
                    {
                        string clock=field switch{"pinned" or "pinAt"=>"pinAt","tags" or "tagsAt"=>"tagsAt",_=>"at"};
                        if(Time(local.Body,clock)>Time(body,clock) || !body.ContainsKey(field) && field is not ("text" or "html" or "rtf" or "richBlocks" or "orig" or "size" or "thumb"))
                            merged[field]=value?.DeepClone();
                    }
                    if(Time(local.Body,"at")>Time(body,"at"))
                        foreach(string field in new[]{"text","html","rtf","richBlocks"})
                            if(!local.Body.ContainsKey(field))merged.Remove(field);
                    remote.Dirty=!JsonNode.DeepEquals(merged,body);remote.Body=body=merged;
                }
            }
            if(name=="control" && body!=null && id==SyncCrypto.Id(key,name,"clip-clear"))
            {
                long before=(long)body["before"]!;
                History.CloudClear(Owner,before);
                PruneCleared(j,before);
            }
            if(name=="cliptags")
            {
                var keyName=(string?)(body??local?.Body)?["key"];
                if(keyName!=null && SyncCrypto.Id(key,name,keyName)==id)
                    History.ApplyTag(new(Owner,keyName,(string?)body?["name"],(int?)body?["color"],(int?)body?["order"]??0,(long?)body?["at"]??ClipReplica.Now()));
            }
            if(name=="clip")
            {
                if(body==null)History.CloudDelete(Owner,id);
                else await ApplyClip(body,key,id,device,seq,local==null && device!=engine.clipboardDevice,initial,ct);
            }
            j.Records[id]=remote;
        }
        async Task ApplyClip(JsonObject b,byte[] key,string id,string device,long basis,bool fresh,bool initial,CancellationToken ct)
        {
            if(!Enum.TryParse<ClipKind>((string?)b["kind"],out var kind)||kind==ClipKind.Files)return;
            string origin=(string?)b["origin"]??device;
            var fields=b;
            if(b["richBlocks"]!=null)
            {
                using var buf=new MemoryStream();
                foreach(string block in Values(b["richBlocks"]))buf.Write(await engine.Download(block,"rich",key,ct));
                buf.Position=0;using var zip=new DeflateStream(buf,CompressionMode.Decompress);
                fields=JsonNode.Parse(zip)!.AsObject();
            }
            RichFormats? formats=fields["html"]==null?null:new(Convert.FromBase64String((string)fields["html"]!),Convert.FromBase64String((string)fields["rtf"]!));
            string? orig=(string?)b["orig"];
            var entry=new ClipEntry("",kind,(string?)fields["text"],null,null,(int?)b["width"],(int?)b["height"],(string?)b["source"]??"",null,
                DateTimeOffset.FromUnixTimeMilliseconds((long)b["copied"]!),(bool?)b["pinned"]==true)
                {Tags=Values(b["tags"]),Rich=formats!=null,Original=kind!=ClipKind.Image?ClipOriginal.Local:orig!=null?ClipOriginal.Remote:ClipOriginal.NotSynced};
            fresh=fresh && History.CloudLookup(Owner,id)==null;
            var clip=History.CloudApply(entry,new(Owner,id,origin==engine.clipboardDevice?null:origin,Time(b,"at"),Time(b,"pinAt"),Time(b,"tagsAt"),(string?)b["thumb"],orig,(long?)b["size"],Base:basis,CopiedAt:entry.CopiedAt,
                ReceivedAt:fresh && origin!=engine.clipboardDevice ? (initial?entry.CopiedAt:History.CloudLink.Time.GetUtcNow()):null),formats,(string?)b["h"],fresh);
            if(initial || !fresh || !engine.Options.WriteToSystemClipboard || clip==null || origin!=device)return;
            if(kind==ClipKind.Image)
            {
                if(!(await engine.FetchOriginalAsync(clip.Entry.Id,ct)).Ok)return;
                clip=History.Lookup(clip.Entry.Id);
            }
            if(clip!=null)await History.WriteReceived(clip,ct);
        }
        public void Rejected(SyncJournal j,JsonObject rejection)
        {
            if(name!="clip" || (string?)rejection["error"] is not ("blob_expired" or "storage_full"))throw new InvalidDataException();
            string id=(string)rejection["id"]!;
            var b=j.Records[id].Body!;
            if(b["orig"]==null || !Values(rejection["blobs"]).Contains((string)b["orig"]!))throw new InvalidDataException();
            b.Remove("orig");b["size"]=null;
            var clip=History.CloudLookup(Owner,id);
            if(clip?.Entry is {Kind:ClipKind.Image,ContentPath:null})History.SetDownload(clip,false,null,ClipOriginal.Expired);
        }
    }
}
