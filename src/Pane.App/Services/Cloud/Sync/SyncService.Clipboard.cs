using Pane.Contracts;
using Pane.Services.Clipboard;

namespace Pane.Services.Cloud;

internal sealed partial class SyncService
{
    readonly SemaphoreSlim thumbnails=new(3);
    readonly Dictionary<(string Id,bool Thumbnail),Task<CloudResult>> fetching=[];
    string Token=>activeToken??throw new OperationCanceledException();
    async Task<byte[]> Download(string id,string kind,byte[] key,CancellationToken ct)=>
        SyncCrypto.DecryptBlob(key,id,await account.Blob(id,kind,null,Token,ct));
    public long? OriginalSize(string clipId)=>clipboard.History?.Lookup(clipId)?.Replica?.Size;
    public void EnsureThumbnail(string clipId)=>_ = Fetch(clipId,true);
    public Task<CloudResult> FetchOriginalAsync(string clipId,CancellationToken ct)=>Fetch(clipId,false).WaitAsync(ct);
    Task<CloudResult> Fetch(string id,bool thumbnail)
    {
        lock(gate)
        {
            var history=clipboard.History;
            var clip=history?.Lookup(id);
            if(clip==null)return Task.FromResult(new CloudResult("blob_expired"));
            if(clip.Entry.Kind!=ClipKind.Image || (thumbnail?clip.Entry.Thumbnail?.Path:clip.Entry.ContentPath)!=null)
                return Task.FromResult(new CloudResult());
            if(run==null || !Options.Clipboard || clip.Replica==null || clip.Replica.Owner!=account.SyncIdentity().Owner)
                return Task.FromResult(new CloudResult("network"));
            string? blob=thumbnail?clip.Replica.Thumb:clip.Replica.Orig;
            if(blob==null || !thumbnail && clip.Entry.Original==ClipOriginal.Expired)return Task.FromResult(new CloudResult("blob_expired"));
            if(!thumbnail && clip.Entry.Original==ClipOriginal.QuotaExceeded && clip.Replica.QuotaMonth==clipboard.Month)
                return Task.FromResult(new CloudResult("quota_exceeded"));
            var pair=(id,thumbnail);
            if(fetching.TryGetValue(pair,out var pending))return pending;
            var ct=run.Token;var key=account.DataKey;
            return fetching[pair]=Task.Run(async ()=>
            {
                bool acquired=false;
                try
                {
                    if(thumbnail){await thumbnails.WaitAsync(ct);acquired=true;}
                    else history!.SetDownload(clip,false,null,ClipOriginal.Downloading);
                    var data=await Download(blob,thumbnail?"thumb":"orig",key,ct);
                    ct.ThrowIfCancellationRequested();history!.SetDownload(clip,thumbnail,data,ClipOriginal.Local);
                    return new CloudResult();
                }
                catch(Exception ex)
                {
                    var code=(ex as AccountService.Failure)?.Code??"network";
                    if(!thumbnail)history!.SetDownload(clip,false,null,code switch{"blob_expired"=>ClipOriginal.Expired,"quota_exceeded"=>ClipOriginal.QuotaExceeded,_=>ClipOriginal.Remote});
                    return new CloudResult(code);
                }
                finally
                {
                    if(acquired)thumbnails.Release();
                    System.Security.Cryptography.CryptographicOperations.ZeroMemory(key);
                    lock(gate)fetching.Remove(pair);
                }
            });
        }
    }
}
