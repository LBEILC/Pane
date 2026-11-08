namespace Pane.Settings;

internal static partial class StorageRecovery
{
    static partial void RecoverImport(string data)=>Pane.Services.Clipboard.DataStorage.ApplyPending(data);
}
