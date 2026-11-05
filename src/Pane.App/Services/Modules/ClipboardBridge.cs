using Pane.Contracts;
namespace Pane.Services.Modules;

// 视图保留这个稳定代理，模块重建后不会继续引用已释放的历史服务。
internal sealed class ClipboardBridge : IClipboardHistory
{
    IClipboardHistory? target;
    public void Attach(IClipboardHistory? value)
    {
        if (target != null) target.Changed -= OnChanged;
        target = value;
        if (target != null) target.Changed += OnChanged;
        OnChanged(this, EventArgs.Empty);
    }
    void OnChanged(object? sender, EventArgs args) => Changed?.Invoke(this, args);
    public event EventHandler? Changed;
    public string? StorageProblem => target?.StorageProblem;
    public IReadOnlyList<ClipEntry> Snapshot() => target?.Snapshot() ?? [];
    public Task PasteAsync(string id, bool plainText, nint window, CancellationToken token) =>
        target?.PasteAsync(id, plainText, window, token) ?? Task.CompletedTask;
    public void SetPinned(string id, bool pinned) => target?.SetPinned(id, pinned);
    public void Delete(string id) => target?.Delete(id);
    public void ClearUnpinned() => target?.ClearUnpinned();
}
