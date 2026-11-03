// 批量重命名（设计负责人定义，工程实现，040）：规则叠加、实时预览、一次执行、整批撤销。
// 入口：超级面板选中文件时的「批量重命名」、启动器文件附件的动作和「批量重命名」命令；窗口是设计做的 RenameView。
namespace Pane.Contracts;

/// <summary>
/// Replace：把 Find 换成 Text（Regex 时 Text 里可用 $1）；Insert：在 Position 插入 Text；
/// Number：在 Position 插入序号（Start 起、每次加 Step、补零到 Digits 位，序号和名字之间加 Separator）；
/// Remove：从第 Index 个字起往后删 Count 个（FromEnd 时从倒数第 Index 个字起往前删）；Case：改大小写；
/// Extension：扩展名换成 Text（不带点；空 = 去掉扩展名）；Date：在 Position 插入文件日期（DateSource、DateFormat，后面加 Separator）。
/// </summary>
public enum RenameRuleKind { Replace, Insert, Number, Remove, Case, Extension, Date }
/// <summary>Index：插在第 Index 个字前面（FromEnd 时插在倒数第 Index 个字后面，所以 1 = 末尾）。Index 从 1 数（2026-11-01 定）。</summary>
public enum RenamePosition { Start, End, Index }
/// <summary>Title：每个词首字母大写，其余小写。</summary>
public enum RenameCase { Lower, Upper, Title }
public enum RenameDateSource { Modified, Created }

/// <summary>
/// 一条规则。为了存进设置简单，所有种类共用一个扁平记录，每种只看自己用得到的字段。
/// 除 Extension 和 Case（IncludeExtension 时）外，规则只作用在不含扩展名的名字上；文件夹没有扩展名。
/// 规则按列表顺序依次作用，前一条的结果是后一条的输入；Enabled=false 的跳过。
/// </summary>
public sealed record RenameRule(RenameRuleKind Kind)
{
    public bool Enabled { get; init; } = true;
    public string Find { get; init; } = "";
    public string Text { get; init; } = "";
    public bool Regex { get; init; }
    public bool MatchCase { get; init; }
    public RenamePosition Position { get; init; } = RenamePosition.End;
    /// <summary>从 1 数，见 <see cref="RenamePosition.Index"/> 和 Remove；小于 1 按 1 算，超出名字长度按名字长度算。</summary>
    public int Index { get; init; } = 1;
    public bool FromEnd { get; init; }
    public int Count { get; init; } = 1;
    public int Start { get; init; } = 1;
    public int Step { get; init; } = 1;
    public int Digits { get; init; } = 1;
    public string Separator { get; init; } = "";
    public RenameCase Case { get; init; } = RenameCase.Lower;
    public bool IncludeExtension { get; init; }
    public RenameDateSource DateSource { get; init; } = RenameDateSource.Modified;
    public string DateFormat { get; init; } = "yyyy-MM-dd";
}

/// <summary>
/// Unchanged：新名字和原名一样（不算错，执行时跳过）；Empty：新名字是空的；Invalid：含 \ / : * ? " &lt; &gt; | 或以空格、点结尾，或是 CON 这类保留名；
/// TooLong：完整路径超长；Duplicate：和这批里另一个的新名字相同；Exists：目标位置已经有一个不在这批里的同名文件；
/// Missing：原文件已经不在了；BadPattern：正则写错了（整批都标这个）。除 None、Unchanged 外，有任何一项就不能执行。
/// </summary>
public enum RenameIssue { None, Unchanged, Empty, Invalid, TooLong, Duplicate, Exists, Missing, BadPattern }

public sealed record RenamePreview(string Path, string NewName, RenameIssue Issue);
public sealed record RenameFailure(string Path, string Reason);
/// <param name="UndoId">能撤销时给，传给 <see cref="IBatchRenamer.UndoAsync"/>；一个也没改成时为 null。</param>
public sealed record RenameResult(int Renamed, IReadOnlyList<RenameFailure> Failed, string? UndoId);

/// <summary>
/// 一个文件夹里能拿来批量重命名的文件（052，issue 11）。Direct：直接在这个文件夹里的；Nested：各层子文件夹里的。
/// 都只列文件、不列文件夹；按资源管理器的顺序排（同一文件夹内按名字自然排序，Direct 在前，Nested 按子文件夹路径再按名字）。
/// 两项加起来最多 <see cref="IBatchRenamer.MaxFolderFiles"/> 个，超了截断并设 Truncated。
/// </summary>
public sealed record RenameFolderListing(IReadOnlyList<string> Direct, IReadOnlyList<string> Nested, bool Truncated);

public interface IBatchRenamer
{
    /// <summary>
    /// 按 <paramref name="paths"/> 的顺序（序号就按这个顺序编）算出每一项的新名字和问题。界面在规则每次变化后调用（已防抖），
    /// 要快：同一批文件第二次起不再碰磁盘（日期、存在性第一次取完缓存），500 个文件在 20 ms 内。
    /// </summary>
    Task<IReadOnlyList<RenamePreview>> PreviewAsync(IReadOnlyList<string> paths, IReadOnlyList<RenameRule> rules, CancellationToken cancellationToken);

    /// <summary>
    /// 执行一份没有问题的预览（有问题的项界面不会传进来；Unchanged 跳过）。互换名字（a→b、b→a）要能正确完成。
    /// 单个失败（被占用、没权限）不影响其他项，记进 Failed；不弹系统对话框，不提权。
    /// </summary>
    Task<RenameResult> ApplyAsync(IReadOnlyList<RenamePreview> plan, CancellationToken cancellationToken);

    /// <summary>把那一次执行改过的名字全部改回去（只撤销还能撤销的；改回去失败的记进 Failed）。Pane 退出后不保证还能撤销。</summary>
    Task<RenameResult> UndoAsync(string undoId, CancellationToken cancellationToken);

    /// <summary>「添加文件夹」一次最多列这么多个文件（052）。</summary>
    const int MaxFolderFiles = 2000;

    /// <summary>
    /// 列出文件夹里的文件（052）：跳过隐藏和系统文件、不进隐藏 / 系统子文件夹、不跟随联接点和符号链接；没权限的子文件夹跳过不报错。
    /// 文件夹本身打不开时抛异常（界面显示原因）。默认实现只为了在工程实现前能编译。
    /// </summary>
    Task<RenameFolderListing> ListFolderAsync(string folder, CancellationToken cancellationToken) =>
        Task.FromResult(new RenameFolderListing([], [], false));
}
