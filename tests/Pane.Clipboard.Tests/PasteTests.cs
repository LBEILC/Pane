using Pane.Services.Clipboard;
static partial class Program
{
    static void PasteTests()
    {
        var p=new PasteFake();var transfer=new PasteTransfer(p);
        Check(transfer.Equivalent(1,1),"exact foreground");
        p.SameRoot=true;Check(transfer.Equivalent(1,2),"root owner foreground");p.SameRoot=false;
        p.SameProcess=true;Check(transfer.Equivalent(1,2),"same process foreground");p.SameProcess=false;
        Check(!transfer.Equivalent(1,2)&&!transfer.Equivalent(0,0),"unrelated and null rejected");
        p.ActivateWorks=false;p.FallbackWorks=true;Check(transfer.Focus(1,default)&&p.Attaches.SequenceEqual([true,false])&&p.Tops==1,"fallback attach and detach");
        p=new(){ActivateWorks=false};transfer=new(p);Check(!transfer.Focus(1,default)&&p.Now==300,"bounded focus polling");
        p=new(){LogicalKeys=[91]};transfer=new(p);transfer.PrepareModifiers(default);Check(p.Keys.SequenceEqual([(91,false)]),"stale logical key released");
        p=new(){LogicalKeys=[91],PhysicalKeys=[91]};transfer=new(p);
        try{transfer.PrepareModifiers(default);Check(false,"held key timeout");}catch(TimeoutException){Check(p.Keys.Count==0,"physical key never released");}
        foreach(bool? higher in new bool?[]{false,null,true}){
            foreach(string stage in new[]{"target","write","focus","modifiers","confirm","input"}){
                p=new(){HigherValue=higher};string? line=null;transfer=new(p,s=>line=s);
                if(stage=="target")p.Alive=false;
                if(stage=="focus")p.ActivateWorks=false;
                if(stage=="modifiers")p.PhysicalKeys=[91];
                if(stage=="confirm")p.LoseOnLogical=true;
                if(stage=="input")p.KeyWorks=false;
                Exception? failure=null;
                try{transfer.Run(1,()=>{if(stage=="write")throw new TimeoutException();},default,0);}catch(Exception e){failure=e;}
                Type expected=stage switch{"target"=>typeof(ClipboardTargetClosedException),"write" or "modifiers"=>typeof(TimeoutException),"focus" or "confirm"=>higher==true?typeof(ClipboardPermissionException):typeof(ClipboardFocusException),_=>higher==true?typeof(ClipboardPermissionException):typeof(InvalidOperationException)};
                Check(failure?.GetType()==expected,$"{stage} higher={higher} exception");
                Check(line!=null&&line.Contains("step="+stage)&&line.Contains("lastError=")&&line.Contains("sameRootOwner=")&&line.Contains("elapsedMs="),"failure metadata");
            }
        }
        p=new();transfer=new(p);transfer.Run(1,()=>{},default,0);Check(p.Keys.SequenceEqual([(17,true),(86,true),(86,false),(17,false)]),"Ctrl V unchanged");
        for(int failedKey=1;failedKey<=4;failedKey++){
            p=new(){FailKeyAt=failedKey};transfer=new(p);
            try{transfer.Run(1,()=>{},default,0);Check(false,"key failure expected");}
            catch(InvalidOperationException){Check(p.Keys.Count==(failedKey==1?1:failedKey==2?3:4),"partial input releases accepted downs");}
        }
        p=new(){LogicalKeys=[91],KeyWorks=false};transfer=new(p);
        try{transfer.Run(1,()=>{},default,0);Check(false,"release failure");}catch(InvalidOperationException e){Check(e.GetType()==typeof(InvalidOperationException),"release failure not permission");}
        p=new(){Alive=false};transfer=new(p);using var cancel=new CancellationTokenSource();cancel.Cancel();
        try{transfer.Run(1,()=>{},cancel.Token,0);Check(false,"cancel");}catch(OperationCanceledException){Check(p.Keys.Count==0,"cancel no input");}
        p=new(){ActivateWorks=false,ThrowTop=true};transfer=new(p);
        try{transfer.Focus(1,default);Check(false,"top throws");}catch(InvalidOperationException){Check(p.Attaches.SequenceEqual([true,false]),"detach on failure");}
    }
    sealed class PasteFake:IPastePlatform
    {
        public nint Foreground {get;set;}=2;
        public bool Alive=true,SameRoot,SameProcess,ActivateWorks=true,FallbackWorks,KeyWorks=true,LoseOnLogical,ThrowTop;
        public bool? HigherValue=false;
        public HashSet<int> PhysicalKeys=[],LogicalKeys=[];
        public List<(int,bool)> Keys=[];public List<bool> Attaches=[];public int Tops,FailKeyAt;
        public bool Exists(nint w)=>Alive;
        public uint Process(nint w)=>SameProcess?1:(uint)w;
        public nint Root(nint w)=>SameRoot?1:w;
        public uint Thread(nint w)=>(uint)w;
        public bool Activate(nint w){if(ActivateWorks||FallbackWorks&&Attaches.Contains(true)){Foreground=w;return true;}return false;}
        public bool Attach(uint a,uint b,bool attach){Attaches.Add(attach);return true;}
        public void Top(nint w){Tops++;if(ThrowTop)throw new InvalidOperationException();}
        public void Restore(nint w){}
        public bool Physical(int k)=>PhysicalKeys.Contains(k);
        public bool Logical(int k){if(LoseOnLogical)Foreground=2;return LogicalKeys.Contains(k);}
        public bool Key(ushort k,bool down){Keys.Add((k,down));return KeyWorks&&Keys.Count!=FailKeyAt;}
        public bool? Higher(nint w)=>HigherValue;
        public string Class(nint w)=>"fixture";
        public int Error=>5;
        public long Now {get;private set;}
        public void Wait()=>Now+=10;
    }
}
