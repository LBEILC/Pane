using System.Runtime.InteropServices;
using Pane.Contracts;
namespace Pane.Services.Launcher;

internal sealed record EverythingPage(IReadOnlyList<FileHit> Items,int Total);
internal interface IEverythingIpc
{
    Task<EverythingPage> QueryAsync(string query,int offset,int count,int sort,CancellationToken token);
}
internal sealed class EverythingIpc:IEverythingIpc
{
    [UnmanagedFunctionPointer(CallingConvention.Cdecl,CharSet=CharSet.Unicode)]
    delegate void Row([MarshalAs(UnmanagedType.LPWStr)]string name,[MarshalAs(UnmanagedType.LPWStr)]string folder,int isFolder,long size,long modified);
    [DllImport("Pane.Native.dll",CharSet=CharSet.Unicode,CallingConvention=CallingConvention.Cdecl)]
    static extern int PaneEverythingPage(string query,int offset,int count,int sort,Row row,out int total);
    public async Task<EverythingPage> QueryAsync(string query,int offset,int count,int sort,CancellationToken token)
    {
        var work=Task.Run(()=>{
            token.ThrowIfCancellationRequested();
            var items=new List<FileHit>();
            Row row=(name,folder,isFolder,size,date)=>{
                DateTimeOffset? modified=null;
                if(date>0&&date<=2650467743999999999)modified=DateTimeOffset.FromFileTime(date);
                items.Add(new(Path.Combine(folder,name),name,FileSearch.Category(name,isFolder!=0),isFolder!=0||size<0?null:size,modified));
            };
            int result=PaneEverythingPage(query,offset,count,sort,row,out int total);GC.KeepAlive(row);
            // TODO(design): 确认 Everything 不可用时的两条提示文案。
            if(result!=1)throw new FileSearchUnavailableException(result==0?"Everything 没有运行，打开它之后就能搜文件":"连不上 Everything（需要 1.4 或更新的版本），可以在 设置 → 启动器 里重新检测");
            return new EverythingPage(items,total);
        },token);
        return await work.WaitAsync(token).ConfigureAwait(false);
    }
}
internal sealed class FileSearch(IEverythingIpc ipc,ILauncherExecutor executor):IFileSearch
{
    internal static readonly IReadOnlyDictionary<FileCategory,string> Extensions=new Dictionary<FileCategory,string>{
        [FileCategory.Document]="doc docx odt rtf wps pages",
        [FileCategory.Spreadsheet]="xls xlsx xlsm csv ods et numbers",
        [FileCategory.Presentation]="ppt pptx odp dps key",
        [FileCategory.Pdf]="pdf",
        [FileCategory.Text]="txt md markdown log json xml yaml yml toml ini cfg conf c cpp h hpp cs js mjs ts tsx jsx py java go rs rb php html htm css scss sql sh ps1 bat cmd",
        [FileCategory.Image]="jpg jpeg png gif bmp webp svg ico tif tiff heic avif psd",
        [FileCategory.Video]="mp4 mkv mov avi wmv flv webm m4v m2ts",
        [FileCategory.Audio]="mp3 wav flac aac m4a ogg wma opus",
        [FileCategory.Archive]="zip rar 7z tar gz bz2 xz tgz iso cab",
        [FileCategory.Program]="exe msi lnk appx msix"
    };
    static readonly Dictionary<string,FileCategory> categories=Extensions.SelectMany(p=>p.Value.Split(' ').Select(e=>new KeyValuePair<string,FileCategory>("."+e,p.Key))).ToDictionary(StringComparer.OrdinalIgnoreCase);
    internal static FileCategory Category(string name,bool folder)=>folder?FileCategory.Folder:categories.GetValueOrDefault(Path.GetExtension(name),FileCategory.All);
    internal static string Filter(string text,FileCategory category)=>category switch{
        FileCategory.All=>text,
        FileCategory.Folder=>"<"+text+"> folder:",
        _=>"<"+text+"> file: ext:"+Extensions[category].Replace(' ',';')
    };
    // 和启动器文件结果共用名字的直接匹配规则；路径命中不高亮文件名。
    internal static IReadOnlyList<TextSpan>? Highlights(string name,string query){
        int index=name.IndexOf(query,StringComparison.OrdinalIgnoreCase);
        return index<0?null:[new(index,query.Length)];
    }
    public async Task<FilePage> SearchAsync(FileQuery query,int offset,int count,CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ArgumentOutOfRangeException.ThrowIfNegative(offset);ArgumentOutOfRangeException.ThrowIfNegative(count);
        if(string.IsNullOrWhiteSpace(query.Text))return new([],0,null);
        var text=query.Text.Trim();
        var page=await ipc.QueryAsync(Filter(text,query.Category),offset,count,query.Sort==FileSort.Modified?14:1,cancellationToken).ConfigureAwait(false);
        IReadOnlyDictionary<FileCategory,int>? counts=null;
        if(offset==0){
            var pending=CountsAsync(text,query.Category,page.Total);
            if(pending.IsCompletedSuccessfully)counts=pending.Result;
        }
        cancellationToken.ThrowIfCancellationRequested();
        return new(page.Items.Select(hit=>hit with{Highlights=Highlights(hit.Name,text)}).ToArray(),page.Total,counts);
    }
    readonly object countsLock=new();
    CancellationTokenSource? countsBudget;
    string? countsText;DateTime countsAt;Task<IReadOnlyDictionary<FileCategory,int>?>? countsTask;
    internal Task<IReadOnlyDictionary<FileCategory,int>?> CountsAsync(string text,FileCategory selected,int total)
    {
        lock(countsLock){
            if(countsText==text&&DateTime.UtcNow-countsAt<TimeSpan.FromSeconds(3)&&countsTask!=null)return countsTask;
            countsBudget?.Cancel();countsBudget=new CancellationTokenSource(TimeSpan.FromSeconds(2));
            countsText=text;countsAt=DateTime.UtcNow;return countsTask=CountCore(text,selected,total,countsBudget);
        }
    }
    async Task<IReadOnlyDictionary<FileCategory,int>?> CountCore(string text,FileCategory selected,int total,CancellationTokenSource budget)
    {
        // 数量异步计算，首屏不等待；只保留最近关键词三秒内的结果。
        
        try{
            var result=new Dictionary<FileCategory,int>();
            foreach(var category in Enum.GetValues<FileCategory>())
                result[category]=category==selected?total:(await ipc.QueryAsync(Filter(text,category),0,0,1,budget.Token).ConfigureAwait(false)).Total;
            return result;
        }catch(Exception e) when(e is OperationCanceledException or FileSearchUnavailableException){return null;}
        finally{lock(countsLock){if(ReferenceEquals(countsBudget,budget))countsBudget=null;}budget.Dispose();}
    }
    public Task<FilePreview> PreviewAsync(FileHit hit,CancellationToken token)=>FilePreviewReader.ReadAsync(hit,token);
    public Task OpenAsync(string path,CancellationToken token)=>executor.OpenAsync(path,false,token);
    public Task RevealAsync(string path,CancellationToken token)=>executor.RevealAsync(path,token);
}
