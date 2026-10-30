using System.Collections.Concurrent;
using System.Diagnostics;
using Pane.Gestures;

static partial class Program
{
    static void QueueTests()
    {
        var hotkey=Binding("滚轮",[],GestureModifier.WheelUp,true,new HotkeyCommand([new(KeyModifiers.Ctrl,9)]));
        var request=new CommandRequest(hotkey,Target,CommandPhase.Execute,GestureModifier.WheelUp);
        using(var executor=new CommandExecutor(new Fake())){
            var times=new List<double>();
            for(int i=0;i<20;i++){var watch=Stopwatch.StartNew();executor.Execute(hotkey.Command,Target);times.Add(watch.Elapsed.TotalMilliseconds);}
            Console.WriteLine($"QUEUE executorWithoutArtificialDelay meanMs={times.Average():F2} minMs={times.Min():F2} maxMs={times.Max():F2}; nativeCalls=False");
        }
        foreach(var (interval,count) in new[]{(5,50),(100,10)})
        {
            var queue=new CommandQueue();var fake=new Fake{TabDelay=30};
            var starts=new ConcurrentQueue<long>();var times=new List<double>();
            var consumer=Task.Run(()=>{using var executor=new CommandExecutor(fake);foreach(var r in queue.GetConsumingEnumerable()){
                starts.Enqueue(Stopwatch.GetTimestamp());var watch=Stopwatch.StartNew();executor.Execute(r.Binding.Command,r.Target);times.Add(watch.Elapsed.TotalMilliseconds);
            }});
            long last=0;
            for(int i=0;i<count;i++){last=Stopwatch.GetTimestamp();queue.Add(request);if(i+1<count)Thread.Sleep(interval);}
            queue.CompleteAdding();consumer.GetAwaiter().GetResult();
            int after=starts.Count(t=>t>last);
            Check(interval==5?starts.Count<25:starts.Count==10,$"滚轮 {interval}ms 执行数量");
            Check(after<=1,$"滚轮 {interval}ms 停止后至多一次");
            Console.WriteLine($"QUEUE interval={interval}ms sent={count} executed={starts.Count} afterLast={after} executionMeanMs={times.Average():F2} maxMs={times.Max():F2}");
        }
        // 确定性检查方向、点击/松开边界与跨会话顺序，不依赖线程调度。
        var q=new CommandQueue();
        var begin=request with{Phase=CommandPhase.BeginAware};var end=request with{Phase=CommandPhase.EndAware};
        var click=request with{Modifier=GestureModifier.MiddleClick};
        var release=request with{Binding=hotkey with{ExecuteOnModifier=false}};
        q.Add(begin);q.Add(request);q.Add(click);q.Add(request with{Modifier=GestureModifier.WheelDown});q.Add(release);q.Add(end);
        q.CompleteAdding();var rows=q.GetConsumingEnumerable().ToArray();
        Check(rows.SequenceEqual([begin,click,request with{Modifier=GestureModifier.WheelDown},release,end]),"反向取最新且点击、松开、开始结束保序");
        q=new();q.Add(begin);q.Add(request);q.Add(end);q.Add(begin);q.Add(request with{Modifier=GestureModifier.WheelDown});q.Add(end);q.CompleteAdding();
        Check(q.GetConsumingEnumerable().Select(r=>r.Phase).SequenceEqual([CommandPhase.BeginAware,CommandPhase.EndAware,CommandPhase.BeginAware,CommandPhase.Execute,CommandPhase.EndAware]),"跨手势替换不越过上次 EndAware");
        q=new();for(int i=0;i<600;i++){q.Add(begin);q.Add(end);}q.CompleteAdding();
        Check(q.GetConsumingEnumerable().Count()==1200,"超出旧容量仍保留全部会话边界");
        var volume=request with{Binding=hotkey with{Command=new VolumeCommand(VolumeAction.WheelAdjust,2)},Phase=CommandPhase.ModifyAware};
        q=new();q.Add(volume with{Phase=CommandPhase.BeginAware});for(int i=0;i<50;i++)q.Add(volume);
        q.Add(volume with{Modifier=GestureModifier.WheelDown});q.Add(volume with{Phase=CommandPhase.EndAware});q.CompleteAdding();
        rows=q.GetConsumingEnumerable().ToArray();
        Check(rows.Length==3&&rows[1].Modifier==GestureModifier.WheelDown,"音量共享单等待项及最新方向规则");
        var volumeFake=new Fake();using(var executor=new CommandExecutor(volumeFake))executor.ModifyAware(rows[1].Binding.Command,rows[1].Modifier);
        Check(volumeFake.Log.SequenceEqual(["down:174","up:174","down:174","up:174"]),"合并音量执行最新向下动作");

        var taskBinding=hotkey with{Command=new TaskSwitchCommand()};
        q=new();var active=q;var taskFake=new Fake{TabDelay=30};taskFake.Held.Add(163);
        var phases=new List<CommandPhase>();
        var taskConsumer=Task.Run(()=>{using var executor=new CommandExecutor(taskFake);foreach(var r in active.GetConsumingEnumerable()){
            phases.Add(r.Phase);
            switch(r.Phase){case CommandPhase.BeginAware:executor.BeginAware(r.Binding.Command);break;case CommandPhase.ModifyAware:executor.ModifyAware(r.Binding.Command,r.Modifier);break;case CommandPhase.EndAware:executor.EndAware();break;}
        }});
        var config=GestureConfigStore.Empty with{Global=[taskBinding]};
        for(int session=0;session<2;session++){
            var s=new GestureSession(config,GestureButton.Right,Target,96);s.Command+=q.Add;
            for(int i=0;i<50;i++){s.Modify(i==49?GestureModifier.WheelDown:GestureModifier.WheelUp);Thread.Sleep(5);}s.End();
        }
        q.CompleteAdding();taskConsumer.GetAwaiter().GetResult();
        Check(phases.Count(p=>p==CommandPhase.EndAware)==2,"快速任务切换 EndAware 都执行");
        Check(phases.IndexOf(CommandPhase.EndAware)<phases.LastIndexOf(CommandPhase.BeginAware)&&phases[^1]==CommandPhase.EndAware,"两次任务切换严格保序");
        Check(taskFake.Log.TakeLast(2).SequenceEqual(["up:164","down:163"]),"快速任务切换恢复原修饰键");
        Console.WriteLine($"QUEUE taskSwitch modifications={phases.Count(p=>p==CommandPhase.ModifyAware)} end=2 restored=True");

        // 空闲队列的托管常驻成本：同进程比较旧实现与新实现，避免 GUI/系统噪声。
        const int samples=10000;
        var old=new object[samples];long before=GC.GetTotalMemory(true);
        for(int i=0;i<samples;i++)old[i]=new BlockingCollection<CommandRequest>(512);
        long oldBytes=GC.GetTotalMemory(true)-before;GC.KeepAlive(old);
        var current=new object[samples];before=GC.GetTotalMemory(true);
        for(int i=0;i<samples;i++)current[i]=new CommandQueue();
        long newBytes=GC.GetTotalMemory(true)-before;GC.KeepAlive(current);GC.KeepAlive(old);
        Check(newBytes<=oldBytes,"空闲队列内存不增加");
        Console.WriteLine($"QUEUE idleBytesPerInstance old={oldBytes/samples} new={newBytes/samples}");
        foreach(BlockingCollection<CommandRequest> item in old)item.Dispose();
    }
}
