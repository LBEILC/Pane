using System.Runtime.CompilerServices;
using Pane.Contracts;
using Pane.Localization;
using Pane.Settings;

namespace Pane.Services.Clipboard;

// 两个可独立启停的能力按同一个设置实例相遇，不共享账号或测试目录。
internal sealed class ClipboardCloudLink
{
    static readonly ConditionalWeakTable<SettingsStore, ClipboardCloudLink> links = new();
    internal static ClipboardCloudLink For(SettingsStore store) => links.GetOrCreateValue(store);
    internal TimeProvider Time {get;set;}=TimeProvider.System;
    internal int Month { get { var d=Time.GetUtcNow().ToOffset(TimeSpan.FromHours(8));return d.Year*100+d.Month; } }
    internal volatile ClipboardHistory? History;
    internal Func<string?>? Owner {get;set;}
    internal Func<string,string?>? DeviceName {get;set;}
    internal Action? Wake {get;set;}
}
internal sealed record ClipReplica(string Owner,string Id,string? Origin,long Edit,long Pin,long Tags,
    string? Thumb=null,string? Orig=null,long? Size=null,int QuotaMonth=0,long Base=0,string? Program=null,
    string[]? Aliases=null, DateTimeOffset? ReceivedAt=null, string? ReceivedHash=null, DateTimeOffset? CopiedAt=null, long? LocalEdit=null,long? LocalPin=null,long? LocalTags=null)
{
    internal static long Now(long previous=0) => Math.Max(DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),previous+1);
    internal static ClipReplica Create(string owner,string program,DateTimeOffset copiedAt) => new(owner,Convert.ToBase64String(Guid.NewGuid().ToByteArray()).TrimEnd('=').Replace('+','-').Replace('/','_'),null,Now(),0,0,Program:program,CopiedAt:copiedAt);
    internal bool Matches(string id)=>Id==id || Aliases?.Contains(id)==true;
    internal ClipReplica Copied(DateTimeOffset now)=>Edited() with {CopiedAt=now};
    internal ClipReplica Edited(){long at=Now(Edit);return this with {Edit=at,LocalEdit=at};}
    internal ClipReplica PinnedNow(){long at=Now(Pin);return this with {Pin=at,LocalPin=at};}
    internal ClipReplica TaggedNow(){long at=Now(Tags);return this with {Tags=at,LocalTags=at};}
}
internal sealed record ClipboardWrite(string EntryId,DateTimeOffset At);
internal sealed record ReplicaTag(string Owner,string Key,string? Name,int? Color,int Order,long At);
internal sealed class ClipLedger
{
    public Dictionary<string,string> Deleted {get;set;}=[];
    public Dictionary<string,long> Clear {get;set;}=[];
    public Dictionary<string,ReplicaTag> Tags {get;set;}=[];
    public Dictionary<string,ClipboardWrite> Writes {get;set;}=[];
    internal ClipLedger Copy()=>new(){Deleted=new(Deleted),Clear=new(Clear),Tags=new(Tags),Writes=new(Writes)};
}
internal sealed partial class ClipboardHistory
{
    ClipLedger ledger=new();
    static bool Within(DateTimeOffset a,DateTimeOffset b)=>(a-b).Duration()<=TimeSpan.FromMinutes(10);
    bool MoveEcho(string owner,string hash,DateTimeOffset now,string program)
    {
        var match=entries.Where(c=>c.Replica?.Owner==owner && c.SyncHash==hash &&
            c.Replica.ReceivedHash==hash && c.Replica.ReceivedAt is {} at && Within(now,at)).MaxBy(c=>c.Replica!.ReceivedAt);
        if (match==null && ledger.Writes.TryGetValue(owner+"/"+hash,out var write) && Within(now,write.At))
        {
            match=entries.Find(c=>c.Entry.Id==write.EntryId && c.SyncHash==hash);
            if(match==null)return true;
        }
        // TODO(design): 对端自动写入也可能折返到最初复制的电脑。这一端没有接收记录，
        // 暂限已确认上传且来源程序不同的副本；窗口内用户在另一程序主动复制也可能命中。
        if(match==null && settings.Current.SyncOptions.WriteToSystemClipboard && !string.IsNullOrEmpty(program))
            match=entries.Where(c=>c.SyncHash==hash && c.Replica is {Origin:null,Base:>0,CopiedAt:{} copied} r &&
                r.Owner==owner && Within(now,copied) && !Names.Equals(Path.GetFileName(program),r.Program))
                .MaxBy(c=>c.Replica!.CopiedAt);
        if(match==null)return false;
        entries[entries.IndexOf(match)]=match with{Entry=match.Entry with{CopiedAt=now}};
        return true;
    }
    void TrackDeletion(ClipReplica replica)
    {
        ledger.Deleted[replica.Id]=replica.Owner;
        foreach(string alias in replica.Aliases??[])ledger.Deleted[alias]=replica.Owner;
    }
    internal ClipboardCloudLink CloudLink=>ClipboardCloudLink.For(settings);
    string? ActiveOwner=>settings.Current.SyncOptions is {Enabled:true,Clipboard:true} ? CloudLink.Owner?.Invoke():null;
    string? CaptureOwner(string program,ClipKind kind)
    {
        var options=settings.Current.SyncOptions;
        return kind==ClipKind.Files || kind==ClipKind.Image && options.ClipScope==ClipSyncScope.Text ||
            options.ExcludedApps?.Contains(Path.GetFileName(program),StringComparer.OrdinalIgnoreCase)==true ? null:ActiveOwner;
    }
    ClipEntry DisplayEntry(StoredClip clip)
    {
        var entry=clip.Entry;
        if(clip.Replica is not {Origin:{} origin} replica)return entry;
        return entry with {SourceDevice=CloudLink.DeviceName?.Invoke(origin)??Strings.Clipboard_OtherDevice,
            Original=entry.Original==ClipOriginal.QuotaExceeded && replica.QuotaMonth!=CloudLink.Month ||
                entry.Original==ClipOriginal.Downloading && !downloads.Contains(entry.Id) ? ClipOriginal.Remote:entry.Original};
    }
    readonly HashSet<string> downloads=[];
    void TrackTag(ClipTag tag)
    {
        if(ActiveOwner is not {} owner || ledger.Tags.Values.Any(t=>t.Owner==owner && Names.Equals(t.Name,tag.Name)))return;
        string key=Guid.NewGuid().ToString("N");
        int order=ledger.Tags.Values.Where(t=>t.Owner==owner).Select(t=>t.Order).DefaultIfEmpty(-1).Max()+1;
        ledger.Tags[key]=new(owner,key,tag.Name,tag.Color,order,ClipReplica.Now());
    }
    void TrackTagChange(string name,ClipTag? replacement)
    {
        if(ActiveOwner is not {} owner)return;
        var tag=ledger.Tags.Values.FirstOrDefault(t=>t.Owner==owner && Names.Equals(t.Name,name));
        if(tag==null){if(replacement!=null)TrackTag(replacement);return;}
        ledger.Tags[tag.Key]=tag with{Name=replacement?.Name,Color=replacement?.Color,At=ClipReplica.Now(tag.At)};
    }
    internal void ApplyTag(ReplicaTag tag)
    {
        lock(sync)
        {
            ledger.Tags.TryGetValue(tag.Key,out var previous);
            if(previous!=null && previous.At>tag.At)return;
            if(previous?.Name is {} old)
            {
                tags.RemoveAll(t=>Names.Equals(t.Name,old));
                for(int i=0;i<entries.Count;i++)
                {
                    var c=entries[i];
                    if(c.Entry.Tags.Contains(old,Names))entries[i]=c with{Entry=c.Entry with{Tags=c.Entry.Tags.Select(n=>Names.Equals(n,old)?tag.Name:n).OfType<string>().ToArray()}};
                }
            }
            if(tag.Name!=null)
            {
                tags.RemoveAll(t=>Names.Equals(t.Name,tag.Name));
                tags.Insert(Math.Clamp(tag.Order,0,tags.Count),new(tag.Name,tag.Color));
            }
            ledger.Tags[tag.Key]=tag;
            var order=ledger.Tags.Values.Where(t=>t.Owner==tag.Owner && t.Name!=null)
                .GroupBy(t=>t.Name!,Names).ToDictionary(g=>g.Key,g=>g.Min(t=>t.Order),Names);
            var sorted=tags.OrderBy(t=>order.GetValueOrDefault(t.Name,int.MaxValue)).ThenBy(t=>t.Name,Names).ToArray();
            tags.Clear();tags.AddRange(sorted);
            Schedule(false);
        }
        NotifyChanged();
    }
    internal (StoredClip[] Clips,ClipLedger Ledger) CloudSnapshot(string owner,string category)
    {
        lock(sync)
        {
            if(category=="cliptags")
            {
                int count=ledger.Tags.Count;
                foreach(var tag in tags)TrackTag(tag);
                if(count!=ledger.Tags.Count)Schedule(false);
            }
            return (category=="clip"?entries.Where(c=>c.Replica?.Owner==owner).ToArray():[],ledger.Copy());
        }
    }
    internal StoredClip? CloudLookup(string owner,string id)
    {
        lock(sync)return entries.Find(c=>c.Replica?.Owner==owner && c.Replica.Matches(id));
    }
    internal void Acknowledge(string owner,string id,long basis,string? thumb,string? orig,long? size)
    {
        lock(sync)
        {
            int i=entries.FindIndex(c=>c.Replica?.Id==id && c.Replica.Owner==owner);
            if(i<0)return;
            var c=entries[i];
            entries[i]=c with{Replica=c.Replica! with{Base=basis,Thumb=thumb,Orig=orig,Size=size}};
            dirty=true;signal.Set();
        }
    }
    internal StoredClip? Lookup(string id)
    {
        lock(sync)return entries.Find(c=>c.Entry.Id==id);
    }
    internal RichFormats? Formats(StoredClip c)=>c.FormatId==null?null:RichFormats.Read(directory,c.FormatId);
    internal void CloudClear(string owner,long before)
    {
        lock(sync)
        {
            if(ledger.Clear.GetValueOrDefault(owner)>=before)return;
            ledger.Clear[owner]=before;
            entries.RemoveAll(c=>!c.Entry.Pinned && c.Entry.Tags.Count==0 && c.Entry.CopiedAt.ToUnixTimeMilliseconds()<before);
            Schedule(false);
        }
        NotifyChanged();
    }
    internal void CloudDelete(string owner,string cloudId)
    {
        lock(sync)
        {
            var clip=CloudLookup(owner,cloudId);
            if(clip?.Replica is {} r){TrackDeletion(r);entries.Remove(clip);}
            Schedule(false);
        }
        NotifyChanged();
    }
    static bool MatchesContent(StoredClip clip,ClipKind kind,string hash)
    {
        if(clip.Entry.Kind==ClipKind.Files || (clip.Entry.Kind==ClipKind.Image)!=(kind==ClipKind.Image))return false;
        if(clip.SyncHash!=null)return clip.SyncHash==hash;
        // 没有 h 的云记录保持独立。尚未开同步时保存的本机内容可以按原文或原图核对。
        if(clip.Replica!=null)return false;
        if(kind!=ClipKind.Image)return clip.Fingerprint=="t:"+hash;
        if(clip.Entry.ContentPath is not {} path)return false;
        try{return ClipboardPixels.Hash(File.ReadAllBytes(path))==hash;}
        catch(Exception e) when(e is IOException or UnauthorizedAccessException){return false;}
    }
    internal StoredClip? CloudApply(ClipEntry incoming,ClipReplica replica,RichFormats? formats,string? hash=null,bool newRemote=false)
    {
        StoredClip result;
        lock(sync)
        {
            if(stopping || ledger.Deleted.ContainsKey(replica.Id))return null;
            var index=entries.FindIndex(c=>c.Replica?.Owner==replica.Owner && c.Replica.Matches(replica.Id));
            if(index<0 && newRemote && hash!=null)
            {
                var candidate=entries.Where(c=>(c.Replica==null || c.Replica.Owner==replica.Owner) &&
                    Within(c.Replica?.CopiedAt??c.Entry.CopiedAt,incoming.CopiedAt) && MatchesContent(c,incoming.Kind,hash))
                    .MaxBy(c=>c.Entry.CopiedAt);
                if(candidate!=null)index=entries.IndexOf(candidate);
            }
            var prior=index<0?null:entries[index];
            bool alias=prior?.Replica is {} primary && primary.Id!=replica.Id;
            var received=replica.ReceivedAt??prior?.Replica?.ReceivedAt;
            string? receivedHash=replica.ReceivedAt!=null?hash:prior?.Replica?.ReceivedHash;
            if(index<0 && ledger.Clear.GetValueOrDefault(replica.Owner)>incoming.CopiedAt.ToUnixTimeMilliseconds() && !incoming.Pinned && incoming.Tags.Count==0)return null;
            var icon=SourceIcon(incoming.Source);
            var entry=incoming with{Id=prior?.Entry.Id??Guid.NewGuid().ToString("N"),SourceIcon=prior?.Entry.SourceIcon??icon,
                ContentPath=prior?.Entry.ContentPath,Thumbnail=prior?.Entry.Thumbnail};
            string? format=null;
            if(prior?.Replica is {} r)
            {
                if(r.Edit>replica.Edit || alias && newRemote){hash=prior.SyncHash;entry=entry with{Text=prior.Entry.Text,Kind=prior.Entry.Kind,Rich=prior.Entry.Rich,CopiedAt=prior.Entry.CopiedAt};format=prior.FormatId;replica=replica with{Edit=r.Edit};}
                if(r.Pin>replica.Pin){entry=entry with{Pinned=prior.Entry.Pinned};replica=replica with{Pin=r.Pin};}
                if(r.Tags>replica.Tags){entry=entry with{Tags=prior.Entry.Tags};replica=replica with{Tags=r.Tags};}
                if(entry.ContentPath!=null)entry=entry with{Original=ClipOriginal.Local};
                else if(replica.Orig!=null && replica.Orig==r.Orig)entry=entry with{Original=prior.Entry.Original};
                var localEdit=r.LocalEdit??r.Edit;
                var localPin=r.LocalPin??r.Pin;
                var localTags=r.LocalTags??r.Tags;
                if(alias)
                {
                    var aliases=r.Aliases??[];
                    if(!aliases.Contains(replica.Id))aliases=[..aliases,replica.Id];
                    entry=entry with{Source=prior.Entry.Source,SourceIcon=prior.Entry.SourceIcon,CopiedAt=prior.Entry.CopiedAt,Original=prior.Entry.Original};
                    replica=r with{Aliases=aliases,Edit=replica.Edit,Pin=replica.Pin,Tags=replica.Tags};
                }
                else
                {
                    if(replica.Edit==r.Edit && prior.Entry.CopiedAt>entry.CopiedAt)entry=entry with{CopiedAt=prior.Entry.CopiedAt};
                    replica=replica with{QuotaMonth=r.QuotaMonth,Program=r.Program,Aliases=r.Aliases};
                }
                replica=replica with{LocalEdit=localEdit,LocalPin=localPin,LocalTags=localTags};
            }
            if(prior!=null && prior.Replica==null)
            {
                entry=entry with{Source=prior.Entry.Source,Pinned=entry.Pinned||prior.Entry.Pinned,
                    Tags=entry.Tags.Concat(prior.Entry.Tags).Distinct(Names).ToArray(),
                    Original=entry.ContentPath!=null?ClipOriginal.Local:entry.Original};
            }
            if(entry.Rich && format==null)format=formats?.Save(directory);
            string fingerprint=prior?.Fingerprint??"cloud:"+replica.Id;
            if(fingerprint.StartsWith("t:",StringComparison.Ordinal) && entry.Text!=null)
                fingerprint="t:"+Hash(System.Text.Encoding.UTF8.GetBytes(entry.Text));
            result=new(entry,fingerprint,format,replica with{ReceivedAt=received,ReceivedHash=receivedHash},hash);
            if(index<0)entries.Add(result);else entries[index]=result;
            Schedule(false);
        }
        NotifyChanged();return result;
    }
    internal void SetDownload(StoredClip clip,bool thumbnail,byte[]? bytes,ClipOriginal state)
    {
        lock(sync)
        {
            int i=entries.FindIndex(c=>c.Entry.Id==clip.Entry.Id);
            if(i<0||stopping)return;
            var c=entries[i];var r=c.Replica!;
            if((thumbnail?r.Thumb:r.Orig)!=(thumbnail?clip.Replica!.Thumb:clip.Replica!.Orig))return;
            string? path=null;
            if(bytes!=null)
            {
                Directory.CreateDirectory(imageDirectory);
                path=Path.Combine(imageDirectory,c.Entry.Id+(thumbnail?".thumb.png":".png"));
                File.WriteAllBytes(path+".tmp",bytes);File.Move(path+".tmp",path,true);
            }
            if(!thumbnail){if(state==ClipOriginal.Downloading)downloads.Add(c.Entry.Id);else downloads.Remove(c.Entry.Id);}
            entries[i]=c with{Entry=thumbnail?c.Entry with{Thumbnail=new(path)}:c.Entry with{Original=state,ContentPath=path},
                Replica=r with{QuotaMonth=state==ClipOriginal.QuotaExceeded?CloudLink.Month:r.QuotaMonth}};
            Schedule(false);
        }
        NotifyChanged();
    }
    internal async Task WriteReceived(StoredClip c,CancellationToken ct)
    {
        try
        {
            await writer.WriteReceived(c.Entry,Formats(c),ct);
            if(c.SyncHash is {} hash && c.Replica is {} r)
                lock(sync)
                {
                    var now=CloudLink.Time.GetUtcNow();
                    foreach(var key in ledger.Writes.Where(p=>!Within(now,p.Value.At)).Select(p=>p.Key).ToArray())ledger.Writes.Remove(key);
                    ledger.Writes[r.Owner+"/"+hash]=new(c.Entry.Id,now);
                    Schedule(false);
                }
        }
        catch(Exception e) when(e is IOException or TimeoutException or UnauthorizedAccessException)
        {
            // 系统剪贴板被其他应用占用不影响已合入的历史和后续记录。
            log.Write("remote-write-failed",e,"write");
        }
    }
}
