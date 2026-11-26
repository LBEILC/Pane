using Pane.Localization;
// 备忘录的界面接线（设计负责人，2026-11-25，docs/design.md「备忘录」）：热键、启动器命令「备忘录」和搜到的备忘录、「bw 内容」直接存、
// 剪贴板 / 分词的「存为备忘录」都从这里进。存储 App.Notes 由工程在「备忘录」能力启动时赋值（123），停用时为 null。
using Pane.Contracts;
using Pane.Views;
namespace Pane;
public sealed partial class App
{
    /// <summary>打开备忘录窗口：<paramref name="create"/> 时最上面新建一条空白；否则选中 <paramref name="noteId"/>（null 选第一条）。</summary>
    public void ShowMemo(string? noteId,bool create)
    {
        if(Notes is not { } store)return;
        Present(SecondaryId.Memo,Strings.Memo_Title,()=>{var v=new MemoView(store,secondaryActions!);v.ManageTags+=(_,_)=>OpenSettings("memo");return v;},v=>{var m=(MemoView)v;if(create)m.OpenNew();else m.OpenNote(noteId);});
    }

    // 备忘录热键：窗口开着且在前台就收起，否则打开并新建一条（同启动器、剪贴板热键再按收起）
    void ToggleMemo()
    {
        if(secondaries.TryGetValue(SecondaryId.Memo,out var panel)&&panel.Shown){
            var fg=Native.GetForegroundWindow();
            if(fg==panel.Input||fg==panel.Visual){panel.Hide();return;}
        }
        ShowMemo(null,create:true);
    }

    // 设计预览（只在演示模式，数据目录是截图脚本隔离的）：样例照原型 prototype/memo.html
    void DesignMemo(bool create)
    {
        if(!demo||Notes is not { } store)return;
        if(store.All.Count==0){
            foreach(var text in new[]{ // 不翻译：演示数据
                "买：牛奶、鸡蛋、咖啡豆",
                "## 读书笔记\n《设计心理学》第 3 章：知识既在头脑里，也在世界里。\n好的设计把需要记住的东西放进世界里。",
                "Wi-Fi 密码\n办公室：`pane-2026-office`\n会议室：问前台",
                "周五评审要带的\n- [x] 原型截图\n- [ ] 手测清单\n- [ ] **Pane 云**的流量数据\n会议链接 https://meet.example.com/abc",
                "常用地址\n上海市浦东新区张江路 123 号 4 栋 501 室\n邮编 201203，电话 138 0013 8000"})
                store.Add(text);
            if(store.All.FirstOrDefault(n=>n.Title=="常用地址") is { } pin)store.SetPinned(pin.Id,true); // 不翻译：演示数据
            // 标签（144）：照原型的示例
            foreach(var (name,color) in new (string,int?)[]{("工作",1),("生活",3),("待办",4),("读书",null),("灵感",6)})store.CreateTag(name,color); // 不翻译：演示数据
            foreach(var n in store.All){
                string[] tags=n.Title switch{"常用地址"=>["生活"],"周五评审要带的"=>["工作","待办"],"Wi-Fi 密码"=>["工作"],"读书笔记"=>["读书","灵感"],_=>n.Title.StartsWith("买",StringComparison.Ordinal)?["生活","待办"]:[]}; // 不翻译：演示数据
                if(tags.Length>0)store.SetTags(n.Id,tags);
            }
        }
        var second=store.All.FirstOrDefault(n=>n.Title.StartsWith("周五",StringComparison.Ordinal))?.Id; // 不翻译：演示数据
        if(create)ShowMemo(null,create:true);else ShowMemo(second,create:false);
    }

    /// <summary>把一段文字直接存成一条新备忘录（启动器「bw 内容」、剪贴板 / 分词的「存为备忘录」），屏幕中间提示一句「已存为备忘录」。空白文字不存。</summary>
    public bool SaveToMemo(string text)=>SaveToMemo(text,[]);

    /// <summary>剪贴板「存为备忘录」（144）：带上那条的标签；备忘录里没有的按剪贴板标签的色点新建，同名的直接用。</summary>
    public bool SaveToMemo(string text,IReadOnlyList<string> tags)
    {
        if(Notes is not { } store||string.IsNullOrWhiteSpace(text))return false;
        try{
            if(tags.Count==0)store.Add(text.Trim('\r','\n'));
            else{
                var colors=new Dictionary<string,int?>(StringComparer.OrdinalIgnoreCase);
                try{foreach(var t in ClipboardHistory.Tags())colors[t.Name]=t.Color;}catch(Exception){}
                store.Add(text.Trim('\r','\n'),tags,colors);
            }
        }
        catch(Exception ex){System.Diagnostics.Trace.WriteLine(ex);return false;}
        gestures?.Hint(new(GestureHintKind.Notice,[],CommandName:Strings.Memo_Saved));
        return true;
    }
}
