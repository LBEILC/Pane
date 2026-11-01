// 二级窗口（设计负责人）：文件、翻译、AI 对话、批量重命名。docs/design.md「启动器排法与二级窗口」。
// 和设置窗口同一套宿主：失去焦点不关、有任务栏按钮、可拖边框改大小；打开时才创建，关掉就释放（关之前记下位置和大小，
// 这次运行里再打开放回原处）。每种只开一个，再展开时复用并带上新的输入。Esc 不关窗口（视图自己处理），Ctrl+W / 关闭按钮 / Alt+F4 关。
using Pane.Contracts;
using Pane.Hosting;
using Pane.Views;
namespace Pane;
public sealed partial class App
{
    /// <summary>文件窗口用的搜索服务。工程在 021 里赋真实实现（要在创建启动器之前赋值）；演示模式用假数据。</summary>
    public IFileSearch? Files {get;set;}
    /// <summary>批量重命名（040）：读资源管理器 / 桌面里选中的文件、改名。工程赋真实实现（在创建启动器之前）；演示模式用假服务。</summary>
    public IFileSelection? FileSelection {get;set;}
    public IBatchRenamer? Renamer {get;set;}
    /// <summary>网页搜索建议（037）：启动器用关键字搜网页时取。工程赋真实实现；演示模式用假服务。</summary>
    public IWebSuggestions? WebSuggestions {get;set;}
    /// <summary>终端命令历史（037）：设置页「清空」用。工程赋真实实现。</summary>
    public ITerminalHistory? TerminalHistory {get;set;}
    readonly Dictionary<string,GlassPanel> secondaries=[];
    readonly Dictionary<string,(int X,int Y,double W,double H)> secondaryBounds=[];
    ISelectionActions? secondaryActions;

    void WireSecondary(LauncherView launcher,ISelectionActions actions)
    {
        secondaryActions=actions;
        if(demo)Files??=new Pane.DesignData.FileSearchSample();
        if(demo){var rename=new Pane.DesignData.RenameSample();FileSelection??=rename;Renamer??=rename;WebSuggestions??=new Pane.DesignData.WebSuggestionsSample();}
        launcher.Files=Files;
        launcher.OpenFiles+=(q,focus)=>ShowFiles(q,focus);
        launcher.OpenTranslate+=text=>ShowTranslate(text);
        launcher.OpenChat+=(q,history)=>ShowChat(q,history);
        launcher.OpenRename+=(files,window)=>ShowRename(files,window);
    }

    public void ShowFiles(string query,string? focus)=>Present("文件",()=>new FilesView(Files,secondaryActions!),v=>((FilesView)v).Open(query,focus));
    public void ShowTranslate(string? text)=>Present("翻译",()=>{var v=new TranslateView(Ai,secondaryActions!);v.OpenSettings+=(_,_)=>Command("show-settings");return v;},v=>((TranslateView)v).Open(text));
    public void ShowChat(string? question,IReadOnlyList<AiChatMessage>? history)=>Present("AI 对话",()=>new ChatView(Ai,secondaryActions!),v=>((ChatView)v).Open(question,history));
    public void ShowChatDraft(string draft)=>Present("AI 对话",()=>new ChatView(Ai,secondaryActions!),v=>((ChatView)v).SetDraft(draft));

    /// <summary>
    /// 批量重命名：<paramref name="files"/> 给了就用它（超级面板读到的选中文件、启动器的文件附件）；
    /// 没给就读 <paramref name="window"/>（呼出启动器之前那个资源管理器窗口）里选中的文件。都没有也打开，窗口里可以添加或拖进来。
    /// </summary>
    public async void ShowRename(IReadOnlyList<string>? files,nint window)
    {
        if(closing||Renamer is not { } renamer)return;
        if(files is null&&window!=0&&FileSelection is { } selection)
        {
            try{files=await selection.ReadAsync(window,CancellationToken.None);}
            catch(Exception e){ReportFailure("读取选中的文件",e);}
        }
        Present("批量重命名",()=>new RenameView(renamer),v=>((RenameView)v).Open(files??[]));
    }

    // 超级面板：自动翻译 / 翻译结果按 Tab 到翻译窗口；「问 AI」打开对话窗口并带上选中文字；选中文件时「批量重命名」
    void WireSuper(SuperView super){super.OpenTranslate+=text=>ShowTranslate(text);super.OpenChat+=text=>ShowChatDraft(text);super.OpenRename+=files=>ShowRename(files,0);}

    void Present(string name,Func<Microsoft.UI.Xaml.Controls.UserControl> make,Action<IPanelView> open)
    {
        if(closing)return;
        if(!secondaries.TryGetValue(name,out var panel)){
            var view=make();var sv=(ISecondaryView)view;
            panel=new GlassPanel(name,view,sv,Glass,true,true,sv.MinimumSize){EscapeHides=false};
            WatchFrames(panel);
            if(secondaryBounds.TryGetValue(name,out var b)){panel.Width=b.W;panel.Height=b.H;panel.RememberedOrigin=(b.X,b.Y);}
            sv.TopmostChanged+=(_,on)=>panel.SetTopmost(on);
            var host=panel;
            host.Hidden+=(_,_)=>{
                secondaryBounds[name]=(host.Origin.X,host.Origin.Y,host.Width,host.Height);
                secondaries.Remove(name);if(current==host)current=null;host.Dispose();ReclaimAfterSettings();
            };
            panel.Prepare();secondaries[name]=panel;
        }
        // 从启动器 / 超级面板展开：那两个面板先收起
        foreach(var p in panels.Values)p.Hide();
        open(panel.View);
        current=panel;
        panel.BringToFront(activate:!demo&&Environment.GetEnvironmentVariable("PANE_VERIFY")!="1");
    }

    void HideSecondaries(){foreach(var w in secondaries.Values.ToArray())w.Hide();}
    void TickSecondaries(){foreach(var w in secondaries.Values.ToArray())w.Tick();}
    bool SecondariesSettled=>secondaries.Values.All(w=>w.Settled);

    // 设计预览：PANE_DESIGN_OPEN=files|translate|chat|rename 就绪后直接打开对应窗口（带上 PANE_DESIGN_QUERY；rename 的 QUERY=empty 打开空窗口）
    bool designOpened;
    void DesignOpen()
    {
        if(designOpened||!demo)return;designOpened=true;
        var q=Environment.GetEnvironmentVariable("PANE_DESIGN_QUERY");
        switch(Environment.GetEnvironmentVariable("PANE_DESIGN_OPEN")){
            case "files":ShowFiles(q??"报价",null);break;
            case "clipboard":Command("show-clipboard");break;
            case "translate":ShowTranslate(q??"Glassmorphism relies on background blur, translucency and a subtle highlight border.");break;
            case "tray":{var m=GlassPanel.MonitorFromPoint(new GlassPanel.Point(),1);var info=new GlassPanel.MonitorInfo{Size=System.Runtime.InteropServices.Marshal.SizeOf<GlassPanel.MonitorInfo>()};GlassPanel.GetMonitorInfoW(m,ref info);ShowTrayMenu($"{info.Monitor.Right-140},{info.Monitor.Bottom-24}");break;}/* 演示：模拟任务栏右下角的托盘图标 */
            case "folder-jump":/* 演示：屏幕中间偏左上，像对话框文件名框的左下角 */
            {
                FolderJump??=new Pane.DesignData.FolderJumpSample();
                var m=GlassPanel.MonitorFromPoint(new GlassPanel.Point(),1);var info=new GlassPanel.MonitorInfo{Size=System.Runtime.InteropServices.Marshal.SizeOf<GlassPanel.MonitorInfo>()};GlassPanel.GetMonitorInfoW(m,ref info);
                ShowFolderJump(new FolderJumpRequest(0,info.Monitor.Left+(info.Monitor.Right-info.Monitor.Left)/3,info.Monitor.Top+(info.Monitor.Bottom-info.Monitor.Top)/3));
                break;
            }
            case "rename":
                ShowRename(q=="empty"?[]:Pane.DesignData.RenameSample.Photos,0);
                if(q!="empty"&&secondaries.TryGetValue("批量重命名",out var rw))((RenameView)rw.View).UseRules(Pane.DesignData.RenameSample.SampleRules);
                break;
            case "chat":ShowChat(q,q==null?[new(AiChatRole.User,"玻璃上的文字怎么保持清晰"),new(AiChatRole.Assistant,"让玻璃上的文字保持清晰，主要靠三件事：背景要糊、底色要压、文字要够实。\n\n- 模糊半径不低于 30 像素，背后的细节糊掉，文字才不打架；\n- 深色主题加一层深色着色（约 40%），亮壁纸也压得住；\n- 正文用不透明的文字色，`主题色` 只用在小面积的强调上。")]:null);break;
        }
    }
}
