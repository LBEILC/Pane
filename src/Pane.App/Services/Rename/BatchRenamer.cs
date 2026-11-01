using System.Globalization;
using System.Text.RegularExpressions;
using Pane.Contracts;
namespace Pane.Services.Rename;

internal sealed class BatchRenamer : IBatchRenamer
{
    sealed record Metadata(bool Exists,bool Directory,DateTime Created,DateTime Modified);
    sealed record Move(string Before,string After,bool RestoresOriginal=false);
    readonly SemaphoreSlim gate=new(1);
    string[] batch=[];
    Metadata[] metadata=[];
    HashSet<string> occupied=new(StringComparer.OrdinalIgnoreCase);
    readonly Dictionary<string,List<Move>> undo=[];
    static readonly StringComparer Paths=StringComparer.OrdinalIgnoreCase;
    static bool Exists(string path)=>File.Exists(path)||Directory.Exists(path);
    static string Target(string path,string name)=>Path.Combine(Path.GetDirectoryName(path)!,name);
    public async Task<IReadOnlyList<RenamePreview>> PreviewAsync(IReadOnlyList<string> paths,IReadOnlyList<RenameRule> rules,CancellationToken cancellationToken)
    {
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try{return await Task.Run(()=>Preview(paths,rules,cancellationToken),cancellationToken).ConfigureAwait(false);}
        finally{gate.Release();}
    }
    IReadOnlyList<RenamePreview> Preview(IReadOnlyList<string> paths,IReadOnlyList<RenameRule> rules,CancellationToken token)
    {
        if(!batch.SequenceEqual(paths,StringComparer.Ordinal)){
            var info=new List<Metadata>();var entries=new HashSet<string>(Paths);
            foreach(var path in paths){token.ThrowIfCancellationRequested();bool dir=Directory.Exists(path);FileSystemInfo file=dir?new DirectoryInfo(path):new FileInfo(path);file.Refresh();info.Add(new(file.Exists,dir,file.CreationTime,file.LastWriteTime));}
            foreach(var parent in paths.Select(Path.GetDirectoryName).Distinct(Paths)){
                token.ThrowIfCancellationRequested();try{if(parent!=null)entries.UnionWith(Directory.EnumerateFileSystemEntries(parent));}catch(IOException){}catch(UnauthorizedAccessException){}
            }
            batch=paths.ToArray();metadata=info.ToArray();occupied=entries;
        }
        var active=rules.Where(r=>r.Enabled).ToArray();var regex=new Dictionary<RenameRule,Regex>();
        try{foreach(var rule in active.Where(r=>r.Kind==RenameRuleKind.Replace&&r.Regex))regex[rule]=new Regex(rule.Find,RegexOptions.CultureInvariant|(rule.MatchCase?RegexOptions.None:RegexOptions.IgnoreCase),TimeSpan.FromMilliseconds(100));}
        catch(ArgumentException){return paths.Select(p=>new RenamePreview(p,Path.GetFileName(p),RenameIssue.BadPattern)).ToArray();}
        var result=new RenamePreview[paths.Count];var sources=new HashSet<string>(paths,Paths);var targets=new Dictionary<string,List<int>>(Paths);
        for(int i=0;i<paths.Count;i++){
            token.ThrowIfCancellationRequested();string path=paths[i],original=Path.GetFileName(path),name=original;var m=metadata[i];RenameIssue issue;
            try{name=Transform(original,m.Directory,m.Created,m.Modified,i,active,regex);issue=NameIssue(name,Target(path,name));}
            // TODO(design): 契约尚无日期格式错误或正则超时的问题类型，暂按 BadPattern 阻止整批执行。
            catch(Exception e) when(e is ArgumentException or RegexMatchTimeoutException or FormatException){return paths.Select(p=>new RenamePreview(p,Path.GetFileName(p),RenameIssue.BadPattern)).ToArray();}
            string target=Target(path,name);
            if(!m.Exists)issue=RenameIssue.Missing;
            else if(issue==RenameIssue.None&&occupied.Contains(target)&&!sources.Contains(target))issue=RenameIssue.Exists;
            else if(issue==RenameIssue.None&&name==original)issue=RenameIssue.Unchanged;
            result[i]=new(path,name,issue);
            if(!targets.TryGetValue(target,out var list))targets[target]=list=[];list.Add(i);
        }
        foreach(var list in targets.Values.Where(v=>v.Count>1))foreach(int i in list)if(result[i].Issue is RenameIssue.None or RenameIssue.Unchanged)result[i]=result[i] with{Issue=RenameIssue.Duplicate};
        return result;
    }
    internal static string Transform(string name,bool directory,DateTime created,DateTime modified,int ordinal,IReadOnlyList<RenameRule> rules,Dictionary<RenameRule,Regex>? patterns=null)
    {
        string ext=directory?"":Path.GetExtension(name),stem=ext.Length==0?name:name[..^ext.Length];
        foreach(var r in rules){
            if(!r.Enabled)continue;
            switch(r.Kind){
                case RenameRuleKind.Replace:
                    if(r.Regex){var rx=patterns?.GetValueOrDefault(r)??new Regex(r.Find,RegexOptions.CultureInvariant|(r.MatchCase?RegexOptions.None:RegexOptions.IgnoreCase),TimeSpan.FromMilliseconds(100));stem=rx.Replace(stem,r.Text);}
                    else if(r.Find.Length>0)stem=stem.Replace(r.Find,r.Text,r.MatchCase?StringComparison.Ordinal:StringComparison.OrdinalIgnoreCase);break;
                case RenameRuleKind.Insert:stem=Insert(stem,r.Text,r);break;
                case RenameRuleKind.Number:
                    // TODO(design): 契约未限定 Digits；正常界面最多 9 位，外部设置暂限制为 1～99 位。
                    string number=((long)r.Start+(long)ordinal*r.Step).ToString("D"+Math.Clamp(r.Digits,1,99),CultureInfo.InvariantCulture);
                    stem=Insert(stem,r.Position==RenamePosition.Start?number+r.Separator:r.Separator+number,r);break;
                case RenameRuleKind.Date:stem=Insert(stem,(r.DateSource==RenameDateSource.Created?created:modified).ToString(r.DateFormat,CultureInfo.InvariantCulture)+r.Separator,r);break;
                case RenameRuleKind.Remove:
                    if(stem.Length==0)break;int index=Math.Clamp(r.Index,1,stem.Length),count=Math.Max(0,r.Count);
                    int start=r.FromEnd?Math.Max(0,stem.Length-index-count+1):index-1;
                    int length=r.FromEnd?Math.Min(count,stem.Length-index+1):Math.Min(count,stem.Length-start);
                    stem=stem.Remove(start,length);break;
                case RenameRuleKind.Case:stem=ChangeCase(stem,r.Case);if(r.IncludeExtension)ext=ChangeCase(ext,r.Case);break;
                case RenameRuleKind.Extension:if(!directory)ext=r.Text.Length==0?"":"."+r.Text;break;
            }
        }
        return stem+ext;
    }
    static string ChangeCase(string value,RenameCase kind)=>kind switch{RenameCase.Upper=>value.ToUpperInvariant(),RenameCase.Title=>CultureInfo.InvariantCulture.TextInfo.ToTitleCase(value.ToLowerInvariant()),_=>value.ToLowerInvariant()};
    static string Insert(string value,string text,RenameRule r){int index=Math.Clamp(r.Index,1,Math.Max(1,value.Length));int at=r.Position switch{RenamePosition.Start=>0,RenamePosition.End=>value.Length,_=>value.Length==0?0:r.FromEnd?value.Length-index+1:index-1};return value.Insert(at,text);}
    internal static RenameIssue NameIssue(string name,string path){
        if(name.Length==0)return RenameIssue.Empty;
        if(name.IndexOfAny(Path.GetInvalidFileNameChars())>=0||name.EndsWith(' ')||name.EndsWith('.'))return RenameIssue.Invalid;
        string first=name.Split('.')[0].TrimEnd(' ').ToUpperInvariant();
        if(first is "CON" or "PRN" or "AUX" or "NUL" or "CONIN$" or "CONOUT$"||Regex.IsMatch(first,@"^(COM|LPT)[1-9¹²³]$",RegexOptions.CultureInvariant))return RenameIssue.Invalid;
        // Windows 长路径上限包含结束符；单个文件名组件受卷的 255 字符限制。
        return name.Length>255||path.Length>=32767?RenameIssue.TooLong:RenameIssue.None;
    }
    public async Task<RenameResult> ApplyAsync(IReadOnlyList<RenamePreview> plan,CancellationToken cancellationToken){
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try{return await Task.Run(()=>Apply(plan,cancellationToken),cancellationToken).ConfigureAwait(false);}finally{gate.Release();}
    }
    RenameResult Apply(IReadOnlyList<RenamePreview> plan,CancellationToken token){
        token.ThrowIfCancellationRequested();var failed=new List<RenameFailure>();var journal=new List<Move>();int renamed=0;
        // 每层先暂存再落位；子项先完成。互相依赖的一组整体回退，失败不波及独立文件。
        foreach(var layer in plan.Where(p=>p.Issue!=RenameIssue.Unchanged).GroupBy(p=>p.Path.Count(c=>c==Path.DirectorySeparatorChar)).OrderByDescending(g=>g.Key)){
            var items=layer.ToArray();var parent=Enumerable.Range(0,items.Length).ToArray();
            int Root(int i){while(parent[i]!=i){parent[i]=parent[parent[i]];i=parent[i];}return i;}
            var sources=new Dictionary<string,int>(Paths);
            for(int i=0;i<items.Length;i++)sources.TryAdd(items[i].Path,i);
            for(int i=0;i<items.Length;i++)if(sources.TryGetValue(Target(items[i].Path,items[i].NewName),out int other))parent[Root(i)]=Root(other);
            foreach(var component in Enumerable.Range(0,items.Length).GroupBy(Root)){
                var staged=new List<(RenamePreview Item,string Temp,string Target)>();var changes=new List<Move>();
                string? error=null;string? failedPath=null;
                foreach(int index in component){
                    var p=items[index];var target=Target(p.Path,p.NewName);
                    if(token.IsCancellationRequested){error="操作已取消";failedPath=p.Path;break;}
                    if(p.Issue!=RenameIssue.None||NameIssue(p.NewName,target)!=RenameIssue.None){error="预览存在问题";failedPath=p.Path;break;}
                    string temp=Target(p.Path,".pane-rename-"+Guid.NewGuid().ToString("N"));
                    try{MovePath(p.Path,temp);staged.Add((p,temp,target));changes.Add(new(p.Path,temp,true));}
                    catch(Exception e) when(IsFileError(e)){error=e.Message;failedPath=p.Path;break;}
                }
                if(error==null)foreach(var s in staged){
                    try{MovePath(s.Temp,s.Target);changes.Add(new(s.Temp,s.Target));}
                    catch(Exception e) when(IsFileError(e)){error=e.Message;failedPath=s.Item.Path;break;}
                }
                if(error==null){journal.AddRange(changes);renamed+=staged.Count;continue;}
                foreach(int index in component)failed.Add(new(items[index].Path,items[index].Path==failedPath?error:"关联文件重命名失败，已回退"));
                // 先把已落位项退回临时名称，再恢复原名；即使回退受阻也保留日志以便重试撤销。
                for(int i=changes.Count-1;i>=0;i--){var move=changes[i];
                    try{MovePath(move.After,move.Before);changes.RemoveAt(i);}
                    catch(Exception e) when(IsFileError(e)){failed.Add(new(move.After,e.Message));}
                }
                journal.AddRange(changes);
            }
        }
        batch=[];string? id=journal.Count==0?null:Guid.NewGuid().ToString("N");if(id!=null)undo[id]=journal;return new(renamed,failed,id);
    }
    static bool IsFileError(Exception e)=>e is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException;
    static void MovePath(string from,string to){if(Directory.Exists(from))Directory.Move(from,to);else File.Move(from,to);}
    public async Task<RenameResult> UndoAsync(string undoId,CancellationToken cancellationToken){
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try{return await Task.Run(()=>{
            cancellationToken.ThrowIfCancellationRequested();if(!undo.TryGetValue(undoId,out var journal))return new RenameResult(0,[],null);
            var failures=new List<RenameFailure>();int restored=0;
            for(int i=journal.Count-1;i>=0;i--){var move=journal[i];try{MovePath(move.After,move.Before);journal.RemoveAt(i);if(move.RestoresOriginal)restored++;}catch(Exception e) when(IsFileError(e)){failures.Add(new(move.After,e.Message));}}
            batch=[];if(journal.Count==0)undo.Remove(undoId);return new RenameResult(restored,failures,journal.Count>0?undoId:null);
        },cancellationToken).ConfigureAwait(false);}finally{gate.Release();}
    }
}
