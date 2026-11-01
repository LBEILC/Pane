using Pane.Contracts;
namespace Pane.Services.Launcher;

internal sealed class ExtraCommandSource:EntrySource
{
    readonly SearchEntry[] entries;readonly ILauncherExecutor executor;
    public ExtraCommandSource(ILauncherExecutor executor,ILauncherEnvironment environment):base("命令",4,"command:")
    {
        this.executor=executor;var table=new PinyinTable();
        entries=(environment.HibernateAvailable?new[]{("logoff","注销","logoff sign out"),("hibernate","休眠","hibernate")}:new[]{("logoff","注销","logoff sign out")}).Select(c=>new SearchEntry("command:"+c.Item1,LauncherItemKind.Command,table.Create(c.Item2),c.Item3,c.Item1=="logoff"?"logout":"moon")).ToArray();
    }
    protected override SearchEntry[] Entries(SourceContext c)=>entries;
    public override Task<IReadOnlyList<LauncherItem>> SearchAsync(SourceContext c,CancellationToken token)
    {
        token.ThrowIfCancellationRequested();if(c.Query.Length==0)return Rows([]);
        return Rows(entries.Where(e=>e.Name.Match(c.Query)!=null||e.Subtitle.Contains(c.Query,StringComparison.OrdinalIgnoreCase)).Select(e=>new LauncherItem(e.Id,e.Kind,Group,e.Name.Name,"命令",new(e.Glyph))));
    }
    public override Task ExecuteAsync(LauncherItem item,SourceContext c,CancellationToken token)=>executor.CommandAsync(item.Id[8..],token);
}
