using System.Globalization;
using System.Text;
using Pane.Contracts;
using Pane.Localization;

namespace Pane.Services.Clipboard;

internal sealed partial class ClipboardHistory
{
    readonly List<ClipTag> tags=[];
    static readonly StringComparer Names=StringComparer.OrdinalIgnoreCase;
    public IReadOnlyList<ClipTag> Tags(){lock(sync)return tags.ToArray();}
    public void SetTags(string id,IReadOnlyList<string> names)
    {
        lock(sync){
            int i=entries.FindIndex(c=>c.Entry.Id==id);if(i<0)return;
            var selected=new List<string>();
            foreach(var name in names.Select(n=>n.Trim()).Where(n=>n.Length>0).Distinct(Names)){
                var tag=tags.Find(t=>Names.Equals(t.Name,name));
                if(tag==null){tag=new(name,null);tags.Add(tag);}
                TrackTag(tag);
                selected.Add(tag.Name);
            }
            var clip=entries[i];entries[i]=clip with{Replica=clip.Replica?.TaggedNow(),Entry=clip.Entry with{Tags=selected.ToArray()}};
            Schedule();
        }
        NotifyChanged();
    }
    public void UpdateTag(string name,string newName,int? color)
    {
        newName=newName.Trim();
        if(newName.Length==0||color is <1 or >6)throw new ArgumentException(Strings.Settings_Clipboard_TagInvalid);
        ModifyTag(name,new(newName,color));
    }
    public void DeleteTag(string name)=>ModifyTag(name,null);
    void ModifyTag(string name,ClipTag? replacement)
    {
        lock(sync){
            int index=tags.FindIndex(t=>Names.Equals(t.Name,name));if(index<0)return;
            if(replacement==null)tags.RemoveAt(index);
            else{
                int other=tags.FindIndex(t=>Names.Equals(t.Name,replacement.Name));
                if(other>=0&&other!=index)
                    throw new ArgumentException(Strings.Settings_Clipboard_TagRenameFailed);
                tags[index]=replacement;
            }
            TrackTagChange(name,replacement);
            for(int i=0;i<entries.Count;i++){
                var clip=entries[i];if(!clip.Entry.Tags.Contains(name,Names))continue;
                var names=clip.Entry.Tags.Select(n=>Names.Equals(n,name)?replacement?.Name:n).OfType<string>().ToArray();
                entries[i]=clip with{Replica=clip.Replica?.TaggedNow(),Entry=clip.Entry with{Tags=names}};
            }
            Schedule();
        }
        NotifyChanged();
    }
    public void UpdateText(string id,string text)
    {
        lock(sync){
            int i=entries.FindIndex(c=>c.Entry.Id==id);if(i<0)return;
            var clip=entries[i];
            if(clip.Entry.Kind is ClipKind.Files or ClipKind.Image)throw new NotSupportedException();
            if(string.IsNullOrEmpty(text))return;
            string hash=Hash(Encoding.UTF8.GetBytes(text));
            entries[i]=new(clip.Entry with{Text=text,Kind=ClipboardClassifier.Classify(text),Rich=false},"t:"+hash,null,clip.Replica?.Edited(),hash);
            Schedule();
        }
        NotifyChanged();
    }
    static ClipEntry PasteRepresentation(ClipEntry entry,ClipPasteAs kind)
    {
        string text;
        switch(kind){
            case ClipPasteAs.Paths or ClipPasteAs.FileNames when entry.Kind==ClipKind.Files:
                var files=entry.Files??[];
                foreach(string path in files)if(!File.Exists(path)&&!Directory.Exists(path))throw new FileNotFoundException(Strings.Common_FileNotFound,path);
                text=string.Join("\r\n",kind==ClipPasteAs.Paths?files:files.Select(Path.GetFileName));break;
            case ClipPasteAs.ImageFile when entry.Kind==ClipKind.Image:
                if(!File.Exists(entry.ContentPath))throw new FileNotFoundException(Strings.Clipboard_ImageMissing);
                string directory=Path.Combine(Path.GetTempPath(),"Pane","ClipboardImages");
                Directory.CreateDirectory(directory);
                foreach(var old in new DirectoryInfo(directory).EnumerateFiles("*.png")){
                    try{if(old.LastWriteTimeUtc<DateTime.UtcNow.AddHours(-1))old.Delete();}
                    catch(IOException){}catch(UnauthorizedAccessException){}
                }
                string name=Path.GetFileNameWithoutExtension(string.Format(Strings.Clipboard_ImageFileName,entry.CopiedAt.LocalDateTime));
                for(int i=0;;i++){
                    string path=Path.Combine(directory,(i==0?name:$"{name} ({i})")+".png");
                    try{using var target=new FileStream(path,FileMode.CreateNew,FileAccess.Write,FileShare.None);
                        using var source=File.OpenRead(entry.ContentPath);source.CopyTo(target);}
                    catch(IOException e) when((e.HResult&0xffff) is 80 or 183){continue;}
                    return entry with{Kind=ClipKind.Files,Files=[path],Text=null};
                }
            case ClipPasteAs.Hex or ClipPasteAs.Rgb when entry.Kind==ClipKind.Color:
                string value=entry.Text!.Trim();int rgb;
                if(value.StartsWith('#')){
                    string hex=value[1..];
                    if(hex.Length==3)hex=string.Concat(hex.Select(c=>new string(c,2)));
                    rgb=int.Parse(hex[^6..],NumberStyles.HexNumber,CultureInfo.InvariantCulture);
                }else{
                    var parts=value[(value.IndexOf('(')+1)..^1].Split(',');
                    rgb=(int.Parse(parts[0])<<16)|(int.Parse(parts[1])<<8)|int.Parse(parts[2]);
                }
                text=kind==ClipPasteAs.Hex?$"#{rgb:X6}":FormattableString.Invariant($"rgb({rgb>>16}, {(rgb>>8)&255}, {rgb&255})");break;
            default:throw new NotSupportedException();
        }
        return entry with{Kind=ClipKind.Text,Text=text,Files=null,Rich=false};
    }
}
