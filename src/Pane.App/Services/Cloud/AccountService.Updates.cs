using System.Net.Http.Headers;
using Pane.Services.Updates;

namespace Pane.Services.Cloud;

internal sealed partial class AccountService : ICloudUpdateAccount
{
    public async Task<HttpResponseMessage> GetReleaseAsync(string path, long offset, CancellationToken token)
    {
        var bearer = SyncToken ?? throw new CloudUnavailable("signed_out");
        using var request = Request(HttpMethod.Get, path, false, bearer);
        if (offset > 0) request.Headers.Range = new RangeHeaderValue(offset, null);
        var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token).ConfigureAwait(false);
        if (response.IsSuccessStatusCode) return response;
        using (response)
        {
            var error = await Error(response, token).ConfigureAwait(false);
            if (error.Code == "unauthorized") await ExpireAsync(bearer, token).ConfigureAwait(false);
            throw new CloudUnavailable(error.Code switch
            {
                "unauthorized" => "signed_out",
                "subscription_required" => "subscription_required",
                "quota_insufficient" when error.Status == 409 => "low_quota",
                _ => "network"
            });
        }
    }
}
