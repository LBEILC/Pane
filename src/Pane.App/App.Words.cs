using Pane.Localization;
// 分词的界面接线（设计负责人，093）：分词窗口从剪贴板文字条目（Ctrl+B / 行尾按钮）、分词热键（工程接）打开。docs/design.md「分词」。
using Pane.Contracts;
using Pane.Views;
namespace Pane;
public sealed partial class App
{
    /// <summary>分词服务（093）。「分词」能力启动时由工程赋值（演示模式用 DesignData/TextSegmenterSample），停止时置 null。</summary>
    public ITextSegmenter? Words {get;set;}

    /// <summary>
    /// 打开分词窗口拆 <paramref name="text"/>。<paramref name="source"/> 是来源程序的显示名（剪贴板条目的来源、热键时选中文字所在的程序），不知道就 null；
    /// <paramref name="fromClipboard"/> 决定窗口头写「来自剪贴板」还是「选中的文字」。空白文字不打开。
    /// </summary>
    public void ShowWords(string text,string? source,bool fromClipboard)
    {
        if(Words is not { } segmenter||string.IsNullOrWhiteSpace(text))return;
        // 从剪贴板进入：剪贴板收起，窗口在剪贴板原来的位置打开；按热键：鼠标右下 12（同超级面板），约束在工作区内
        (int X,int Y) at;
        if(fromClipboard&&panels.TryGetValue("clipboard",out var clip)&&clip.Shown)at=clip.Origin;
        else{Native.GetCursorPos(out var p);at=(p.X+12,p.Y+12);}
        Present(SecondaryId.Words,Strings.Module_Words_Name,()=>{var v=new WordsView(segmenter,secondaryActions!);v.SendToLauncher+=(_,t)=>SendWordsToLauncher(t);return v;},
            v=>((WordsView)v).Open(text,source,fromClipboard),at);
    }

    // 交给启动器：先关分词窗口，再打开启动器带上这段文字（单行短文字填进输入框，别的当附件，同剪贴板交给启动器）
    void SendWordsToLauncher(string text)
    {
        if(secondaries.TryGetValue(SecondaryId.Words,out var w))w.Hide();
        if(!ModuleGate.On(ModuleIds.Launcher))return;
        Command("show-launcher");
        launcherView?.Use(new ClipEntry("words",ClipKind.Text,text,null,null,null,null,"",null,DateTimeOffset.Now,false));
    }

    /// <summary>分词热键读不到选中文字时（空选区、读不了、超时）调用：屏幕中间提示一句「没有读到选中的文字」，界面线程或后台线程都可以调。</summary>
    public void ReportWordsNoSelection()=>queue.TryEnqueue(()=>{if(!closing)gestures?.Hint(new(GestureHintKind.Notice,[],CommandName:Strings.Words_NoSelection));});
}
