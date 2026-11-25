using Pane.Contracts;
using Pane.Services.Memo;
using Pane.Settings;

static partial class Program
{
    static async Task MemoFolder136Tests()
    {
        string root = TestResults.NewDirectory("136-");
        string data = Path.Combine(root,"data"), current = Path.Combine(data,"备忘录");
        string legacy = Path.Combine(data,"clipboard","备忘录");
        Directory.CreateDirectory(legacy);
        foreach (string text in new[] { "甲","乙","丙" }) File.WriteAllText(Path.Combine(legacy,text+".md"),text);
        var original = NoteDisk.Scan(legacy);
        NoteDisk.SaveIndex(legacy,original);
        var settings = new SettingsStore(data,Defaults());
        settings.Load();
        void CheckContents(string folder)
        {
            var actual = NoteDisk.Scan(folder).OrderBy(row => row.Id);
            Check(actual.SequenceEqual(original.OrderBy(row => row.Id)) &&
                original.All(row => File.ReadAllText(Path.Combine(folder,row.FileName)) == Path.GetFileNameWithoutExtension(row.FileName)),
                "三条正文和索引一致");
        }
        CheckContents(current);
        Check(!Directory.Exists(legacy),"启动搬空旧目录");
        var storage = StorageService(settings);
        string conflictParent = Path.Combine(root,"冲突目标");
        string collision = Path.Combine(conflictParent,"Pane","备忘录");
        Directory.CreateDirectory(collision);
        var duplicate = original[0];
        var renamed = duplicate with { FileName = "另一文件名.md" };
        File.Copy(Path.Combine(current,duplicate.FileName),Path.Combine(collision,renamed.FileName));
        NoteDisk.SaveIndex(collision,[renamed]);
        await StorageFails(() => storage.MoveLargeFilesAsync(conflictParent,null,default),"迁移遇相同 id 停止");
        CheckContents(current);
        Check(storage.LargeFiles.IsDefault && NoteDisk.Scan(collision).Single() == renamed,"冲突保留双方备忘录");
        foreach (string? parent in new[] { Path.Combine(root,"第一处"),Path.Combine(root,"第二处"),null })
        {
            string previous = storage.CurrentMemoFolder;
            await storage.MoveLargeFilesAsync(parent,null,default);
            string expected = parent == null ? current : Path.Combine(parent,"Pane","备忘录");
            Check(storage.CurrentMemoFolder == expected && !Directory.EnumerateFiles(previous).Any(),"迁移到指定位置并清空来源");
            CheckContents(expected);
            var usage = (await storage.MeasureAsync(default)).Items;
            Check(usage.Single(item => item.Category == StorageCategory.Memo) is { Files:4 } memo && memo.Folder == expected &&
                usage.Single(item => item.Category == StorageCategory.Clipboard).Files == 1,"备忘录正文与索引独立计量");
        }

        Directory.CreateDirectory(legacy);
        // 同时覆盖旧目录和新目录各赢一次，并故意使用相同的正文文件名。
        var conflicts = original.Take(2).Select((row,i) =>
        {
            var file = new FileInfo(Path.Combine(legacy,row.FileName));
            File.WriteAllText(file.FullName,"旧目录版本");
            return row with { Modified = row.Modified.AddHours(i == 0 ? 1 : -1), Size = file.Length, LastWrite = file.LastWriteTimeUtc };
        }).ToArray();
        NoteDisk.SaveIndex(legacy,conflicts);
        settings.Load();
        var merged = NoteDisk.Scan(current).ToDictionary(row => row.Id);
        Check(merged.Count == 3 && !Directory.Exists(legacy),"合并清理旧文件并保留无冲突记录");
        foreach (var row in new[] { conflicts[0],original[1],original[2] })
        {
            var winner = merged[row.Id];
            string expected = row == conflicts[0] ? "旧目录版本" : Path.GetFileNameWithoutExtension(row.FileName);
            Check(winner.Modified == row.Modified && File.ReadAllText(Path.Combine(current,winner.FileName)) == expected,"同 id 保留较新的正文和修改时间");
        }
        Console.WriteLine("PASS 136 默认目录、三向迁移、占用统计、启动搬家和冲突合并");
    }
}
