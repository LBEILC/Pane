using System.Diagnostics;
using System.Runtime.InteropServices;

namespace Pane.Hosting;

internal static class NativeCrashEvents
{
    public static NativeCrashEvent? Find(CrashSession session, CancellationToken cancellation)
    {
        var watch = Stopwatch.StartNew();
        string start = DateTime.FromFileTimeUtc(session.CreationTime).ToString("O");
        nint query = EvtQuery(0, "Application",
            $"*[System[((EventID=1000 and Provider[@Name='Application Error']) or (EventID=1001 and Provider[@Name='Windows Error Reporting'])) and TimeCreated[@SystemTime>='{start}']]]", 0x201);
        if (query == 0) return null;
        try
        {
            nint[] events = new nint[1];
            while (!cancellation.IsCancellationRequested && watch.Elapsed < NativeCrashRecovery.QueryLimit)
            {
                uint remaining = (uint)Math.Max(1, (NativeCrashRecovery.QueryLimit - watch.Elapsed).TotalMilliseconds);
                if (!EvtNext(query, 1, events, remaining, 0, out uint returned) || returned == 0) break;
                try
                {
                    if (cancellation.IsCancellationRequested) break;
                    EvtRender(0, events[0], 1, 0, 0, out uint size, out _);
                    if (size == 0 || size > 1024 * 1024) continue;
                    nint buffer = Marshal.AllocHGlobal((int)size);
                    try
                    {
                        if (EvtRender(0, events[0], 1, size, buffer, out _, out _) &&
                            NativeCrashEvent.Match(Marshal.PtrToStringUni(buffer)!, session) is { } match) return match;
                    }
                    finally { Marshal.FreeHGlobal(buffer); }
                }
                finally { EvtClose(events[0]); }
            }
            return null;
        }
        finally { EvtClose(query); }
    }
    [DllImport("wevtapi.dll", CharSet = CharSet.Unicode)] static extern nint EvtQuery(nint session, string path, string query, uint flags);
    [DllImport("wevtapi.dll")] [return: MarshalAs(UnmanagedType.Bool)] static extern bool EvtNext(nint query, uint size, [Out] nint[] events, uint timeout, uint flags, out uint returned);
    [DllImport("wevtapi.dll")] [return: MarshalAs(UnmanagedType.Bool)] static extern bool EvtRender(nint context, nint fragment, uint flags, uint size, nint buffer, out uint used, out uint count);
    [DllImport("wevtapi.dll")] [return: MarshalAs(UnmanagedType.Bool)] static extern bool EvtClose(nint handle);
}
