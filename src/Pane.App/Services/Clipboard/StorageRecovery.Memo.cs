using Pane.Contracts;
using Pane.Services.Memo;

namespace Pane.Settings;

internal static partial class StorageRecovery
{
    static partial void RecoverLegacyNotes(string data)
    {
        string source = Path.Combine(data,"clipboard",NoteDisk.DirectoryName);
        if (!Directory.Exists(source)) return;
        string target = NoteDisk.FolderFor(data,null);
        var sourceData = NoteDisk.ScanIndex(source);
        var incoming = sourceData.Rows;
        if (!Directory.Exists(target))
        {
            Directory.Move(source,target);
            return;
        }

        var targetData = NoteDisk.ScanIndex(target);
        var merged = targetData.Rows.ToDictionary(row => row.Id);
        var occupied = Directory.EnumerateFileSystemEntries(target).Select(Path.GetFileName)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (occupied.Count == 0)
        {
            Directory.Delete(target);
            Directory.Move(source,target);
            return;
        }
        var moves = new List<StorageMoveFile>();
        // 被较新版本取代的正文暂存在事务目录，提交后随日志清理，失败时仍能还原。
        void Retire(string path) => moves.Add(new(path,Path.Combine(Root(data),Guid.NewGuid().ToString("N"))));
        foreach (var row in incoming)
        {
            string body = Path.Combine(source,row.FileName);
            if (merged.TryGetValue(row.Id,out var prior))
            {
                if (prior.Modified >= row.Modified)
                {
                    Retire(body);
                    continue;
                }
                Retire(Path.Combine(target,prior.FileName));
            }
            string name = row.FileName;
            if (occupied.Contains(name))
                name = NoteDisk.FileName(new Note(row.Id,File.ReadAllText(body),row.Pinned,row.Created,row.Modified,row.From),
                    occupied.Contains);
            occupied.Add(name);
            moves.Add(new(body,Path.Combine(target,name)));
            merged[row.Id] = row with { FileName = name };
        }

        string sourceIndex = Path.Combine(source,NoteDisk.Index);
        if (moves.Count > 0 || File.Exists(sourceIndex))
        {
            Begin(data,moves.ToArray(),sourceIndex,Path.Combine(target,NoteDisk.Index));
            try
            {
                foreach (var move in moves) File.Move(move.Source,move.Target);
                NoteDisk.SaveIndex(target,NoteDisk.MergeIndex(merged.Values,targetData,sourceData));
                File.Delete(sourceIndex);
                Complete(data);
            }
            catch { Recover(data); throw; }
        }
        if (!Directory.EnumerateFileSystemEntries(source).Any()) Directory.Delete(source);
    }
}
