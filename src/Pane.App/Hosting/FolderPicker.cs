using System.Runtime.InteropServices;

namespace Pane.Hosting;

internal static class FolderPicker
{
    public static Task<string?> PickFolderAsync(nint owner, string? initial, string? title)
    {
        var completion = new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            nint path = 0;
            try
            {
                int result = PickPaneFolder(owner, initial, title, out path);
                Marshal.ThrowExceptionForHR(result);
                completion.SetResult(path == 0 ? null : Marshal.PtrToStringUni(path));
            }
            catch (Exception error) { completion.SetException(error); }
            finally { Marshal.FreeCoTaskMem(path); }
        }) { IsBackground = true, Name = "Pane.FolderPicker" };
        // 系统选择框自行处理模态消息；等待期间界面线程继续处理消息。
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        return completion.Task;
    }

    [DllImport("Pane.Native.dll", ExactSpelling = true, CharSet = CharSet.Unicode)]
    static extern int PickPaneFolder(nint owner, string? initial, string? title, out nint path);
}
