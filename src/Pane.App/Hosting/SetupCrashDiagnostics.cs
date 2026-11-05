using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Pane.Services.Setup;

namespace Pane.Hosting;

// 安装专用诊断：在 XAML 之前安装，记录后仍交给系统处理，不吞异常或继续损坏的进程。
internal static unsafe class SetupCrashDiagnostics
{
    static bool active;
    static nint handler;
    static int recording;
    public static void Install(string[] args)
    {
        active = args.FirstOrDefault() is "--setup" or "--uninstall-worker";
        if (!active) return;
        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
        {
            if (e.ExceptionObject is Exception error) UninstallChain.Log("worker-crash", "failed", error, details: "source=AppDomain");
        };
        handler = AddVectoredExceptionHandler(1, &NativeException);
        if (handler == 0) UninstallChain.Log("worker-crash-hook", "failed", new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error()));
    }
    public static void Attach(Microsoft.UI.Xaml.Application app)
    {
        if (!active) return;
        app.UnhandledException += (_, e) => UninstallChain.Log("worker-crash", "failed", e.Exception, details: "source=XAML; message=" + e.Message);
    }
    static bool Read<T>(nint address, out T value) where T : unmanaged
    {
        value = default;
        fixed (T* target = &value) return ReadProcessMemory(new nint(-1), address, target, (nuint)sizeof(T), out var read) && read == (nuint)sizeof(T);
    }
    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    static int NativeException(nint pointers)
    {
        // EXCEPTION_POINTERS/EXCEPTION_RECORD：本产品只发布 x64；所有外部地址用 ReadProcessMemory 校验。
        if (!Read(pointers, out nint record) || !Read(record, out uint code) || code is not (0xC000027B or 0x80131620)) return 0;
        if (Interlocked.Exchange(ref recording, 1) != 0) return 0;
        try
        {
            UninstallChain.Log("worker-crash", "failed", new ExternalException("WinRT/native exception", unchecked((int)code)), details: "source=VEH; first-chance=true");
            if (code == 0xC000027B && Read(record + 24, out uint count) && count >= 2 &&
                Read(record + 32, out nuint number) && Read(record + 40, out nint entries))
            {
                for (int i = 0; i < (int)Math.Min(number, (nuint)16); i++)
                {
                    if (!Read(entries + i * 8, out nint entry) || !Read(entry, out uint size) || size < 32 ||
                        !Read(entry + 4, out uint signature) || signature is not (0x53453031 or 0x53453032) ||
                        !Read(entry + 8, out int hr) || !Read(entry + 12, out uint form)) continue;
                    string message = "Stowed exception";
                    if ((form & 3) == 2 && Read(entry + 16, out nint text))
                    {
                        var chars = new List<char>();
                        for (int j = 0; j < 1024 && Read(text + j * 2, out char c) && c != '\0'; j++) chars.Add(c);
                        if (chars.Count > 0) message = new string(chars.ToArray());
                    }
                    var trace = new System.Text.StringBuilder();
                    if ((form & 3) == 1 && size >= 40 && Read(entry + 24, out uint wordSize) && wordSize == 8 &&
                        Read(entry + 28, out uint words) && Read(entry + 32, out nint frames))
                        for (int frame = 0; frame < Math.Min(words, 128) && Read(frames + frame * 8, out ulong address); frame++) trace.Append($"0x{address:X16} ");
                    UninstallChain.Log("worker-crash", "failed", new ExternalException(message, hr), details: $"source=stowed; index={i}; form={form & 3}; nativeStack={trace}");
                }
            }
        }
        catch { /* 崩溃路径上不引发第二次异常。 */ }
        finally { Volatile.Write(ref recording, 0); }
        return 0; // EXCEPTION_CONTINUE_SEARCH
    }
    [DllImport("kernel32.dll", SetLastError = true)] static extern nint AddVectoredExceptionHandler(uint first, delegate* unmanaged[Stdcall]<nint, int> callback);
    [DllImport("kernel32.dll")] static extern bool ReadProcessMemory(nint process, nint address, void* buffer, nuint size, out nuint read);
}
