using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;

internal sealed class UpdateServer : IAsyncDisposable
{
    internal sealed record Reply(int Status, byte[] Body, string ContentType = "application/octet-stream", string? Location = null);
    public Func<string, Task<Reply?>>? Respond { get; set; }
    public System.Collections.Concurrent.ConcurrentQueue<string> Paths = new();
    readonly TcpListener listener = new(IPAddress.Loopback, 0);
    readonly CancellationTokenSource stop = new();
    readonly Task loop;
    readonly List<Task> clients = [];
    public Uri Url { get; }
    public byte[] Payload = new byte[512 * 1024];
    public string Version = "0.1.100";
    public string Body = "# 标题\n**更新内容**";
    public bool Draft { get; set; }
    public bool Preview { get; set; }
    public bool BadHash { get; set; }
    public bool Disconnect { get; set; }
    public bool BreakDownload { get; set; }
    public bool Slow { get; set; }
    public bool UserAgent, Credentials;
    public int Status = 200, Requests, Downloads;
    public UpdateServer() { listener.Start(); Url = new($"http://127.0.0.1:{((IPEndPoint)listener.LocalEndpoint).Port}/latest"); loop = Accept(); }
    async Task Accept()
    {
        try { while (!stop.IsCancellationRequested) { var client = await listener.AcceptTcpClientAsync(stop.Token); clients.Add(Serve(client)); } }
        catch (OperationCanceledException) { }
    }
    async Task Serve(TcpClient client)
    {
        using (client)
        try
        {
            using var stream = client.GetStream(); using var reader = new StreamReader(stream, leaveOpen: true);
            string first = await reader.ReadLineAsync(stop.Token) ?? "";
            string? line; while (!string.IsNullOrEmpty(line = await reader.ReadLineAsync(stop.Token)))
            { if (line.StartsWith("User-Agent: Pane/")) UserAgent = true; if (line.StartsWith("Authorization:") || line.StartsWith("Cookie:")) Credentials = true; }
            Interlocked.Increment(ref Requests);
            string path = first.Split(' ')[1]; Paths.Enqueue(path);
            if (Respond != null && await Respond(path) is {} reply)
            {
                byte[] head = Encoding.ASCII.GetBytes($"HTTP/1.1 {reply.Status} Test\r\nContent-Length: {reply.Body.Length}\r\nContent-Type: {reply.ContentType}\r\nConnection: close\r\n" + (reply.Location == null ? "" : "Location: " + reply.Location + "\r\n") + "\r\n");
                await stream.WriteAsync(head, stop.Token); await stream.WriteAsync(reply.Body, stop.Token); return;
            }
            if (Disconnect) return;
            string name = $"Pane-Setup-{Version}.exe", host = Url.GetLeftPart(UriPartial.Authority);
            byte[] body;
            bool package = first.Contains("/" + name + " ");
            if (package) { Interlocked.Increment(ref Downloads); body = Payload; }
            else if (first.Contains("/SHA256SUMS.txt ")) body = Encoding.UTF8.GetBytes((BadHash ? new string('0', 64) : Convert.ToHexString(SHA256.HashData(Payload))) + "  " + name + "\n");
            else body = Encoding.UTF8.GetBytes($$"""
                {"tag_name":"v{{Version}}","draft":{{Draft.ToString().ToLowerInvariant()}},"prerelease":{{Preview.ToString().ToLowerInvariant()}},"body":{{System.Text.Json.JsonSerializer.Serialize(Body, UpdateTestJson.Default.String)}},"published_at":"2026-10-30T00:00:00Z","html_url":"{{host}}/release","assets":[{"name":"{{name}}","size":{{Payload.Length}},"browser_download_url":"{{host}}/{{name}}"},{"name":"SHA256SUMS.txt","size":{{Encoding.UTF8.GetByteCount(Convert.ToHexString(SHA256.HashData(Payload)) + "  " + name + "\n")}},"browser_download_url":"{{host}}/SHA256SUMS.txt"}]}
                """);
            byte[] headers = Encoding.ASCII.GetBytes($"HTTP/1.1 {Status} Test\r\nContent-Length: {body.Length}\r\nConnection: close\r\nRetry-After: 3600\r\n\r\n");
            await stream.WriteAsync(headers, stop.Token);
            if (package && BreakDownload) { await stream.WriteAsync(body.AsMemory(0, 10), stop.Token); return; }
            if (package && Slow) { await stream.WriteAsync(body.AsMemory(0, 10), stop.Token); await Task.Delay(500, stop.Token); await stream.WriteAsync(body.AsMemory(10), stop.Token); }
            else await stream.WriteAsync(body, stop.Token);
        }
        catch (Exception e) when (e is IOException or OperationCanceledException or SocketException) { }
    }
    public async ValueTask DisposeAsync() { stop.Cancel(); listener.Stop(); await loop; await Task.WhenAll(clients); stop.Dispose(); }
}

[System.Text.Json.Serialization.JsonSerializable(typeof(string))]
internal partial class UpdateTestJson : System.Text.Json.Serialization.JsonSerializerContext { }
