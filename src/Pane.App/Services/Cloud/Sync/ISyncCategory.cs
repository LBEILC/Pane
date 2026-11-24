using System.Text.Json.Nodes;
using Pane.Contracts;

namespace Pane.Services.Cloud;

// 扩展类别共享同一游标和原子状态文件。Receive 负责应用或合并冲突，Rejected 可修正队列后重试。
internal interface ISyncCategory
{
    string Name { get; }
    bool Enabled(SyncOptions options);
    void Capture(SyncJournal journal,byte[] key,string device);
    Task<JsonObject> Encode(string id,SyncRecord record,byte[] key,CancellationToken ct);
    Task Receive(SyncJournal journal,JsonObject wire,byte[] key,bool initial,CancellationToken ct);
    void Rejected(SyncJournal journal,JsonObject rejection);
}
