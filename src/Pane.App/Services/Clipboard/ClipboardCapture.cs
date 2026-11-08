using Pane.Settings;
namespace Pane.Services.Clipboard;


internal interface IClipboardCapturePlatform
{
    uint Sequence { get; }
    bool Open();
    void Close();
    string Program { get; }
    ClipboardContent Metadata();
    string Kind { get; }
    ClipboardContent Read(ClipboardContent metadata);
    ClipboardContent Complete(ClipboardContent content)=>content;
}

// 每次读取在一个 STA 上完成；退避不占用原生通知线程，也不持有剪贴板。
internal sealed class ClipboardCapture(IClipboardCapturePlatform platform,Func<ClipboardSettings> settings,
    ClipboardLog log,Action<ClipboardContent> captured,Func<Action,Task> run,
    Func<int,CancellationToken,Task>? delay=null,Action? failed=null)
{
    internal static readonly int[] Delays=[50,150,400,1000];
    public async Task Capture(uint sequence,CancellationToken token,bool suppressed=false)
    {
        string program="";
        try{
            for(int attempt=0;;attempt++){
                token.ThrowIfCancellationRequested();
                bool retry=false;
                await run(()=>{
                    token.ThrowIfCancellationRequested();
                    program=platform.Program;
                    if(platform.Sequence!=sequence){log.Skip("sequence-changed",program);return;}
                    if(suppressed){log.Skip("own-marker",program);return;}
                    if(!settings().Enabled){log.Skip("type-disabled",program);return;}
                    if(!platform.Open()){retry=true;return;}
                    ClipboardContent? content=null;
                    try{
                        if(platform.Sequence!=sequence){log.Skip("sequence-changed",program);return;}
                        var metadata=platform.Metadata();program=metadata.Program;
                        var s=settings();
                        string? reason=metadata.Own?"own-marker":
                            s.HonorExcludeMarker&&(metadata.Exclude||metadata.History==0||metadata.Cloud==0)?"exclude-marker":
                            s.ExcludedPrograms.Contains(program,StringComparer.OrdinalIgnoreCase)||s.ExcludedPrograms.Length>0&&program.Length==0?"excluded-program":null;
                        string kind=platform.Kind;
                        reason??=kind switch{
                            "files" when !s.RecordFiles=>"type-disabled",
                            "image" when !s.RecordImages=>"type-disabled",
                            "text" when !s.RecordText=>"type-disabled",
                            ""=>"unknown-format",_=>null};
                        if(reason!=null){log.Skip(reason,program);return;}
                        content=platform.Read(metadata);
                        if(string.IsNullOrWhiteSpace(content.Text)&&content.Files is not {Length:>0}&&content.Png is not {Length:>0}){log.Skip("unknown-format",program);return;}
                    }finally{platform.Close();}
                    if(content!=null)content=platform.Complete(content);
                    token.ThrowIfCancellationRequested();
                    if(platform.Sequence!=sequence){log.Skip("sequence-changed",program);return;}
                    if(!settings().Enabled){log.Skip("type-disabled",program);return;}
                    if(content!=null)captured(content);
                }).ConfigureAwait(false);
                if(!retry)return;
                if(attempt==Delays.Length){log.Skip("open-failed",program);return;}
                await (delay??Task.Delay)(Delays[attempt],token).ConfigureAwait(false);
            }
        }catch(OperationCanceledException){log.Skip("sequence-changed",program);}
        catch(Exception){failed?.Invoke();log.Skip("error",program);}
    }
}
