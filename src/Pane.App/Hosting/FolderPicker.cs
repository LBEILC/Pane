using System.Runtime.InteropServices;

namespace Pane.Hosting;

internal static class FolderPicker
{
    delegate int Pick(out nint path);

    public static Task<string?> PickFolderAsync(nint owner, string? initial, string? title) =>
        OnSta(() => ReadPath((out nint path) => PickPaneFolder(owner, initial, title, out path)));

    public static Task<string?> PickFileAsync(nint owner, (string Name, string Pattern)[]? filters,
        string? initial = null, string? title = null) =>
        WithFilters(filters, (strings, count) => ReadPath((out nint path) =>
            PickPaneFile(owner, initial, title, strings, count, out path)));

    public static Task<IReadOnlyList<string>> PickFilesAsync(nint owner, (string Name, string Pattern)[]? filters,
        string? initial = null, string? title = null) =>
        WithFilters<IReadOnlyList<string>>(filters, (strings, count) =>
        {
            int result = PickPaneFiles(owner, initial, title, strings, count, out nint paths, out uint length);
            try
            {
                Marshal.ThrowExceptionForHR(result);
                if (length == 0) return Array.Empty<string>();
                var values = new string[checked((int)length)];
                nint current = paths;
                for (int i = 0; i < values.Length; i++, current += IntPtr.Size)
                    values[i] = Marshal.PtrToStringUni(Marshal.ReadIntPtr(current))!;
                return values;
            }
            finally
            {
                nint current = paths;
                for (uint i = 0; i < length; i++, current += IntPtr.Size)
                    Marshal.FreeCoTaskMem(Marshal.ReadIntPtr(current));
                Marshal.FreeCoTaskMem(paths);
            }
        });

    public static Task<string?> PickSaveAsync(nint owner, string suggestedName, string defaultExtension,
        (string Name, string Pattern)[]? filters, string? initial = null, string? title = null) =>
        WithFilters(filters, (strings, count) => ReadPath((out nint path) =>
            PickPaneSave(owner, suggestedName, defaultExtension, initial, title, strings, count, out path)));

    static string? ReadPath(Pick pick)
    {
        nint path = 0;
        try
        {
            Marshal.ThrowExceptionForHR(pick(out path));
            return Marshal.PtrToStringUni(path);
        }
        finally { Marshal.FreeCoTaskMem(path); }
    }

    static Task<T> WithFilters<T>((string Name, string Pattern)[]? filters, Func<nint[], uint, T> pick)
    {
        var groups = filters?.ToArray() ?? [];
        return OnSta(() =>
        {
            // COMDLG_FILTERSPEC 由两个相邻的宽字符指针组成。
            var strings = new nint[checked(groups.Length * 2)];
            try
            {
                for (int i = 0; i < groups.Length; i++)
                {
                    strings[i * 2] = Marshal.StringToCoTaskMemUni(groups[i].Name);
                    strings[i * 2 + 1] = Marshal.StringToCoTaskMemUni(groups[i].Pattern);
                }
                return pick(strings, (uint)groups.Length);
            }
            finally { foreach (nint value in strings) Marshal.FreeCoTaskMem(value); }
        });
    }

    static Task<T> OnSta<T>(Func<T> pick)
    {
        var completion = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            try { completion.SetResult(pick()); }
            catch (Exception error) { completion.SetException(error); }
        }) { IsBackground = true, Name = "Pane.FolderPicker" };
        // 系统选择框自行处理模态消息；等待期间界面线程继续处理消息。
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        return completion.Task;
    }

    [DllImport("Pane.Native.dll", ExactSpelling = true, CharSet = CharSet.Unicode)]
    static extern int PickPaneFolder(nint owner, string? initial, string? title, out nint path);

    [DllImport("Pane.Native.dll", ExactSpelling = true, CharSet = CharSet.Unicode)]
    static extern int PickPaneFile(nint owner, string? initial, string? title,
        [In] nint[] filters, uint count, out nint path);

    [DllImport("Pane.Native.dll", ExactSpelling = true, CharSet = CharSet.Unicode)]
    static extern int PickPaneFiles(nint owner, string? initial, string? title,
        [In] nint[] filters, uint count, out nint paths, out uint length);

    [DllImport("Pane.Native.dll", ExactSpelling = true, CharSet = CharSet.Unicode)]
    static extern int PickPaneSave(nint owner, string suggestedName, string defaultExtension,
        string? initial, string? title, [In] nint[] filters, uint count, out nint path);
}
