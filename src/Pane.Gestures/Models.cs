using System.Threading.Channels;
using System.Text.Json.Serialization;
namespace Pane.Gestures;

[Flags] public enum GestureButton { None = 0, Right = 1, Middle = 2, X1 = 4, X2 = 8 }  // 与 WGestures 编号一致

public enum GestureDirection { Up = 0, UpRight, Right, DownRight, Down, DownLeft, Left, UpLeft }

public enum GestureModifier { None = 0, WheelUp = 1, WheelDown = 2, MiddleClick = 4, LeftClick = 8, X1Click = 32, X2Click = 64 }

public sealed record GesturePattern(
    GestureButton Button,                      // 单个触发键
    IReadOnlyList<GestureDirection> Directions, // 可以为空
    GestureModifier Modifier);

public sealed record GestureBinding(
    string Name,              // 命令名，提示框里显示
    GesturePattern Pattern,
    GestureCommand Command,
    bool ExecuteOnModifier);  // 修饰动作发生时立即执行，可连续触发

[Flags] public enum KeyModifiers { None = 0, Ctrl = 1, Alt = 2, Shift = 4, Win = 8 }
public readonly record struct KeyChord(KeyModifiers Modifiers, ushort VirtualKey);

[JsonPolymorphic(TypeDiscriminatorPropertyName = "$command")]
[JsonDerivedType(typeof(HotkeyCommand), "Hotkey")]
[JsonDerivedType(typeof(SendTextCommand), "SendText")]
[JsonDerivedType(typeof(WebSearchCommand), "WebSearch")]
[JsonDerivedType(typeof(RunCommand), "Run")]
[JsonDerivedType(typeof(WindowCommand), "Window")]
[JsonDerivedType(typeof(VolumeCommand), "Volume")]
[JsonDerivedType(typeof(TaskSwitchCommand), "TaskSwitch")]
[JsonDerivedType(typeof(OpenCommand), "Open")]
[JsonDerivedType(typeof(PauseGesturesCommand), "PauseGestures")]
[JsonDerivedType(typeof(DoNothingCommand), "DoNothing")]
public abstract record GestureCommand;
public sealed record HotkeyCommand(IReadOnlyList<KeyChord> Chords) : GestureCommand;       // 多个即连续按键
public sealed record SendTextCommand(string Sequence) : GestureCommand;                    // SendKeys 语法
public sealed record WebSearchCommand(string UrlTemplate) : GestureCommand;                // 含 {query}
public sealed record RunCommand(string CommandLine, bool InExplorerFolder, bool ShowWindow) : GestureCommand;
public sealed record WindowCommand(WindowAction Action) : GestureCommand;
public enum WindowAction { MaximizeRestore, Minimize, Close, ToggleTopmost, SnapLeft, SnapRight }
public sealed record VolumeCommand(VolumeAction Action, int Step) : GestureCommand;
public enum VolumeAction { Up, Down, ToggleMute, WheelAdjust }                              // WheelAdjust 为修饰感知
public sealed record TaskSwitchCommand : GestureCommand;                                    // 修饰感知
public sealed record OpenCommand(string Target) : GestureCommand;                           // 程序、文件或网址
public sealed record PauseGesturesCommand : GestureCommand;
public sealed record DoNothingCommand : GestureCommand;

public enum AppRuleMode { InheritAndOverride, Disabled }
public sealed record AppRule(
    string ExePath,
    string? WindowClass,      // 可选，区分同名进程（如 dllhost.exe 的图片查看器）
    AppRuleMode Mode,
    IReadOnlyList<GestureBinding> Bindings);

public sealed record GestureSettings(
    GestureButton EnabledButtons,
    bool PauseInFullscreen,
    bool ShowTrail,
    bool FadeTrail,
    bool ShowCommandName,
    int? StationaryTimeoutMs,                         // null = 关
    KeyChord? PauseHotkey,
    IReadOnlyDictionary<GestureButton, string> TrailColors,
    bool AllowDiagonalMultiStroke = false); // 默认兼容 WGestures

public sealed record GestureConfig(
    int Version,
    GestureSettings Settings,
    IReadOnlyList<GestureBinding> Global,
    IReadOnlyList<AppRule> Apps);

// 界面用的事件
public enum TrailPhase { Begin, Move, End }
public readonly record struct TrailSample(GestureButton Button, int PhysicalX, int PhysicalY, TrailPhase Phase);
public sealed record GestureProgress(GestureButton Button, IReadOnlyList<GestureDirection> Directions,
    GestureModifier Modifier, string? CommandName);   // 画的过程中实时更新；没匹配时 CommandName 为 null
public enum GestureOutcomeKind { Executed, NotRecognized, ReplayedClick, Failed }
public sealed record GestureOutcome(GestureOutcomeKind Kind, string? CommandName, string? FailureReason);
public sealed record SuperPanelRequest(int PhysicalX, int PhysicalY, GestureButton Button);
public enum GestureServiceState { Running, Paused, PausedByFullscreen, WGesturesRunning, HookFailed }

public interface IGestureService : IAsyncDisposable
{
    GestureConfig Config { get; }
    void Apply(GestureConfig config);                 // 立即生效
    GestureServiceState State { get; }
    event EventHandler? StateChanged;
    void SetPaused(bool paused);
    ChannelReader<TrailSample> Trail { get; }         // 有界通道，物理坐标；界面负责 DPI 换算和绘制
    event EventHandler<GestureProgress>? Progress;
    event EventHandler<GestureOutcome>? Finished;
    event EventHandler<SuperPanelRequest>? SuperPanelRequested;
    event EventHandler<string>? PauseHotkeyUnavailable; // 参数是热键的显示文字
}

public sealed record UnsupportedItem(string Where, string GestureText, string Reason);
public sealed record ImportPreview(
    string SourceDirectory, string SourceVersion,
    int GlobalCount, int AppRuleCount, int DisabledAppCount,
    IReadOnlyList<UnsupportedItem> Unsupported,
    bool HasCustomTrailColors);

public interface IWGesturesImporter
{
    /// <summary>
    /// null = 自动查找；找不到返回 null。043 起参数也可以是文件：<c>gestures.wg2</c> / <c>gestures.wg</c>（1～3 版格式）或 WGestures 导出的 <c>.wgb</c>。
    /// <see cref="ImportPreview.SourceDirectory"/> 存的就是这个参数（自动查找时是找到的数据目录）。
    /// </summary>
    ImportPreview? Preview(string? directory = null);
    GestureConfig Import(ImportPreview preview, bool keepTrailColors);
    /// <summary>最近一次自动查找找过的位置（043）：没找到时界面列给用户看。</summary>
    IReadOnlyList<string> SearchedLocations => [];
}
