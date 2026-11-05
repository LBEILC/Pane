using System.Reflection;
using System.Runtime.InteropServices;
using Pane.Services;
using Pane.Services.Clipboard;
namespace Pane;
public sealed partial class App
{
    async Task PanelKeysProbe()
    {
        PanelKeyRouter? router = null;
        try
        {
            if (!demo || !controlsEnabled || clipboardSource != null || PanelKeys != null ||
                string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("PANE_INSTANCE")) ||
                string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("PANE_DATA_DIR")))
                throw new InvalidOperationException("按键探针只允许隔离演示实例");
            int checks = 0;
            void Check(bool ok, string why) { if (!ok) throw new InvalidOperationException(why); checks++; }
            using (var higher = new PanelKeyRouter(new HigherPanelTarget()))
                Check(!higher.Begin(1, true) && !higher.Active && !PanelKeyRouter.HookInstalled, "完整性较高拒绝接管");
            router = new PanelKeyRouter();
            Check(!router.Begin(0, true), "空目标拒绝");
            Check(!router.Begin(-123, true), "无效目标拒绝");
            Check(!PanelKeyRouter.HookInstalled, "无效目标不启动钩子");
            nint target = panels["clipboard"].Input;
            Check(router.Begin(target, true) && router.Active && PanelKeyRouter.HookInstalled, "真实钩子开始");
            int uiThread = Environment.CurrentManagedThreadId, count = 0;
            bool ui = true;
            router.Pressed += (_, p) => { ui &= Environment.CurrentManagedThreadId == uiThread && p.Key == Contracts.PanelKey.Down; count++; };
            // 人工投递通知验证 UI 路由与代数；不伪称为真实按键输入。
            var flags = BindingFlags.Instance | BindingFlags.NonPublic;
            nint window = (nint)typeof(PanelKeyRouter).GetField("window", flags)!.GetValue(router)!;
            nuint generation = (nuint)typeof(PanelKeyRouter).GetField("generation", flags)!.GetValue(router)!;
            PanelProbePost(window, 0x8059, generation, 2);
            await Task.Delay(80);
            Check(count == 1 && ui, "Pressed 在创建窗口的 UI 线程触发");
            router.End(); router.End();
            Check(!router.Active && !PanelKeyRouter.HookInstalled, "重复 End 无害并卸载");
            PanelProbePost(window, 0x8059, generation, 2);
            await Task.Delay(80);
            Check(count == 1, "结束后旧通知不触发");
            Check(router.Begin(target, false) && router.Begin(target, true), "重复 Begin 重新开始");
            PanelProbePost(window, 0x8059, generation, 2);
            await Task.Delay(80);
            Check(count == 1, "新会话丢弃旧通知");
            router.Dispose(); router.Dispose();
            Check(!router.Active && !PanelKeyRouter.HookInstalled && !router.Begin(target, true), "Dispose 幂等且禁止重启");
            router = new PanelKeyRouter();
            Check(router.Begin(target, true), "新实例可重启");
            PanelKeys = router;
            File.WriteAllText(output + ".panelkeys-result", $"PASS assertions={checks};panelkeys=1;clipboard=0;input=0;awaitingExit=1");
        }
        catch (Exception e) { router?.Dispose(); File.WriteAllText(output + ".error", e.ToString()); }
    }
    [DllImport("user32.dll", EntryPoint = "PostMessageW")] static extern bool PanelProbePost(nint h, uint m, nuint w, nint l);
    // 只替换完整性判定；任何意外的输入、剪贴板或焦点操作都会使测试失败。
    sealed class HigherPanelTarget : IPastePlatform
    {
        public bool Exists(nint w) => true;
        public bool? Higher(nint w) => true;
        public nint Foreground => throw new NotSupportedException();
        public uint Process(nint w) => throw new NotSupportedException();
        public nint Root(nint w) => throw new NotSupportedException();
        public uint Thread(nint w) => throw new NotSupportedException();
        public bool Activate(nint w) => throw new NotSupportedException();
        public bool Attach(uint a, uint b, bool c) => throw new NotSupportedException();
        public void Top(nint w) => throw new NotSupportedException();
        public void Restore(nint w) => throw new NotSupportedException();
        public bool Physical(int k) => throw new NotSupportedException();
        public bool Logical(int k) => throw new NotSupportedException();
        public bool Key(ushort k, bool down) => throw new NotSupportedException();
        public string Class(nint w) => throw new NotSupportedException();
        public int Error => throw new NotSupportedException();
        public long Now => throw new NotSupportedException();
        public void Wait() => throw new NotSupportedException();
    }
}
