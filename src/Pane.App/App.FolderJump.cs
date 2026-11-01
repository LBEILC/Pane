// 文件对话框跳转菜单的宿主（设计负责人，038）：服务说要菜单（Ctrl+Shift+G，或 Ctrl+G 没有可直接跳的资源管理器），
// 就在对话框文件名框的左下角弹出 FolderJumpView；选了文件夹先收起菜单，再让服务把对话框跳过去。
using Pane.Contracts;
using Pane.Hosting;
using Pane.Views;
namespace Pane;
public sealed partial class App
{
    IFolderJump? folderJump;
    /// <summary>文件对话框跳转（038）。工程赋真实实现；赋值时接上菜单。演示模式用假服务。</summary>
    public IFolderJump? FolderJump
    {
        get=>folderJump;
        set{
            if(folderJump!=null)folderJump.MenuRequested-=OnFolderJumpMenu;
            folderJump=value;
            if(value!=null)value.MenuRequested+=OnFolderJumpMenu;
        }
    }
    FolderJumpView? jumpView;
    GlassPanel? jumpPanel;
    nint jumpDialog;

    void OnFolderJumpMenu(object? sender,FolderJumpRequest request)=>queue.TryEnqueue(()=>ShowFolderJump(request));

    void ShowFolderJump(FolderJumpRequest request)
    {
        if(closing||folderJump is not { } service)return;
        if(jumpPanel==null)
        {
            jumpView=new FolderJumpView();
            jumpView.Jump+=(_,path)=>_=JumpTo(path);
            jumpPanel=new GlassPanel("跳到文件夹",jumpView,jumpView,Glass){Placement=PanelPlacement.Tray};
            WatchFrames(jumpPanel);jumpPanel.Prepare();panels.Add("folder-jump",jumpPanel);
        }
        IReadOnlyList<FolderJumpEntry> entries;
        try{entries=service.Entries();}
        catch(Exception e){ReportFailure("列出文件夹",e);return;}
        jumpDialog=request.Dialog;
        jumpView!.Apply(entries);
        foreach(var other in panels.Values)if(other!=jumpPanel)other.Hide();
        current=jumpPanel;
        jumpPanel.Show(true,request.X,request.Y,!demo);
    }

    async Task JumpTo(string path)
    {
        if(folderJump is not { } service)return;
        try{await service.JumpAsync(jumpDialog,path,CancellationToken.None);}
        catch(Exception e){ReportFailure("跳到文件夹",e);}
    }
}
