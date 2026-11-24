namespace Pane.Services.Cloud;

internal sealed partial class AccountService
{
    internal async Task<byte[]> Blob(string id,string kind,byte[]? bytes,string token,CancellationToken ct)
    {
        if(id.Length!=22 || id.Any(c=>!char.IsAsciiLetterOrDigit(c) && c!='-' && c!='_'))throw new InvalidDataException();
        using var request=Request(bytes==null?HttpMethod.Get:HttpMethod.Put,"blobs/"+id,false,token);
        if(bytes!=null)
        {
            request.Headers.Add("X-Pane-Blob",kind);
            request.Content=new ByteArrayContent(bytes);
            request.Content.Headers.ContentType=new("application/octet-stream");
        }
        using var response=await http.SendAsync(request,ct);
        if(!response.IsSuccessStatusCode)throw await Error(response,ct);
        return bytes==null?await response.Content.ReadAsByteArrayAsync(ct):[];
    }
}
