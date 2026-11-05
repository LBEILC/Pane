using Microsoft.UI.Xaml;
using Pane.Contracts;

namespace Pane.Views;

// 界面按能力显隐联动入口（055，设计负责人）：指向一块没在运行的能力的入口不显示。
// 宿主还没建好能力目录时（启动早期）当作都在，免得一闪而过的空白。
static class ModuleGate
{
    public static bool On(string id) => ((App)Application.Current).Modules is not { } m || m.IsActive(id);
}
