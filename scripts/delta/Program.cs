using System.IO.Compression;
using System.Runtime.InteropServices;
using System.Text.Json.Nodes;
using Pane.Services.Setup;

internal static class Program
{
    static JsonObject Header() => new() { ["format"] = 1, ["product"] = "Pane", ["arch"] = "x64" };

    static byte[] Manifest(string directory, string version)
    {
        var files = new JsonArray();
        foreach (var path in PayloadPatch.Paths(directory).Select(p => Path.GetRelativePath(directory, p).Replace('\\', '/')).Order(StringComparer.Ordinal))
        {
            using var input = File.OpenRead(Path.Combine(directory, path));
            files.Add(new JsonObject { ["path"] = path, ["size"] = input.Length, ["sha256"] = PayloadPatch.Hash(input) });
        }
        var manifest = Header();
        manifest["version"] = version;
        manifest["files"] = files;
        var bytes = System.Text.Encoding.UTF8.GetBytes(manifest.ToJsonString());
        PayloadPatch.Manifest(bytes, version);
        return bytes;
    }

    static void Write(ZipArchive zip, string name, byte[] bytes, CompressionLevel level)
    {
        using var output = zip.CreateEntry(name, level).Open();
        output.Write(bytes);
    }

    static void Build(string baseline, string target, string from, string to, string manifestFile, string patch)
    {
        byte[] baseManifest = Manifest(baseline, from), newManifest = File.ReadAllBytes(manifestFile);
        var old = PayloadPatch.Manifest(baseManifest, from);
        var files = PayloadPatch.Manifest(newManifest, to);
        string work = Directory.CreateTempSubdirectory("Pane-Patch-").FullName;
        string draft = patch + "." + Guid.NewGuid().ToString("N");
        try
        {
            using (var zip = ZipFile.Open(draft, ZipArchiveMode.Create))
            {
                Write(zip, "base.json", baseManifest, CompressionLevel.Optimal);
                var ops = new JsonArray();
                foreach (var (path, stamp) in files)
                {
                    if (old.TryGetValue(path, out var source) && source == stamp) continue;
                    var op = new JsonObject { ["path"] = path, ["size"] = stamp.Size, ["sha256"] = stamp.Sha256 };
                    var file = Path.Combine(target, path);
                    string delta = Path.Combine(work, "diff");
                    bool useDelta = false;
                    if (source != null)
                    {
                        File.Delete(delta);
                        if (!CreateDeltaW(15, 0, 0, Path.Combine(baseline, path), file, null, null, default, 0, 0, delta))
                            throw new IOException("无法生成差分：" + path, Marshal.GetLastWin32Error());
                        using var compressed = new MemoryStream();
                        using (var deflate = new DeflateStream(compressed, CompressionLevel.Optimal, true))
                        using (var input = File.OpenRead(file)) input.CopyTo(deflate);
                        useDelta = new FileInfo(delta).Length < compressed.Length;
                    }
                    string entry = (useDelta ? "d/" : "f/") + ops.Count;
                    op["op"] = useDelta ? "delta" : "replace";
                    op["entry"] = entry;
                    if (useDelta) op["base"] = new JsonObject { ["size"] = source!.Size, ["sha256"] = source.Sha256 };
                    zip.CreateEntryFromFile(useDelta ? delta : file, entry, useDelta ? CompressionLevel.NoCompression : CompressionLevel.Optimal);
                    ops.Add(op);
                }
                foreach (var path in old.Keys.Where(path => !files.ContainsKey(path)))
                    ops.Add(new JsonObject { ["path"] = path, ["op"] = "delete" });
                var metadata = Header();
                metadata["from"] = from;
                metadata["to"] = to;
                metadata["fromManifestSha256"] = PayloadPatch.Hash(baseManifest);
                metadata["toManifestSha256"] = PayloadPatch.Hash(newManifest);
                metadata["ops"] = ops;
                Write(zip, "patch.json", System.Text.Encoding.UTF8.GetBytes(metadata.ToJsonString()), CompressionLevel.Optimal);
            }
            PayloadPatch.Apply(baseline, draft, newManifest, from, to, Path.Combine(work, "verified"));
            File.Move(draft, patch, true);
            Console.WriteLine($"{from} -> {to}: {new FileInfo(patch).Length} bytes, {files.Count} files verified");
        }
        finally { File.Delete(draft); SetupFiles.Delete(work); }
    }

    // 旧安装包是原生 PE，按中央目录偏移定位内嵌 ZIP；只读取安装包字节。
    static void Extract(string installer, string output)
    {
        var bytes = File.ReadAllBytes(installer);
        for (int end = bytes.Length - 22; end >= 0; end--)
        {
            if (bytes[end] != 80 || BitConverter.ToUInt32(bytes, end) != 0x06054b50) continue;
            long start = end - (long)BitConverter.ToUInt32(bytes, end + 12) - BitConverter.ToUInt32(bytes, end + 16);
            int length = end + 22 + BitConverter.ToUInt16(bytes, end + 20);
            if (start < 0 || start >= end || length > bytes.Length || BitConverter.ToUInt32(bytes, (int)start) != 0x04034b50) continue;
            using var zip = new ZipArchive(new MemoryStream(bytes, (int)start, length - (int)start, false));
            if (zip.GetEntry("Pane.App.exe") == null) continue;
            var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var entry in zip.Entries)
            {
                // Windows PowerShell 打出的历史 ZIP 也会用反斜杠存放子目录。
                string name = entry.FullName.Replace('\\', '/');
                var path = PayloadPatch.Relative(name.TrimEnd('/'));
                PayloadPatch.Require(paths.Add(path));
                if (name.EndsWith('/')) continue;
                string file = Path.Combine(output, path);
                Directory.CreateDirectory(Path.GetDirectoryName(file)!);
                entry.ExtractToFile(file);
            }
            return;
        }
        throw new InvalidDataException("没有找到内嵌载荷");
    }

    static void Main(string[] args)
    {
        switch (args)
        {
            case ["manifest", var root, var version, var output]: File.WriteAllBytes(output, Manifest(root, version)); break;
            case ["patch", var baseline, var target, var from, var to, var manifest, var patch]:
                Build(baseline, target, from, to, manifest, patch); break;
            case ["extract", var installer, var destination]:
                PayloadPatch.Require(!Directory.Exists(destination));
                Directory.CreateDirectory(destination);
                try { Extract(installer, destination); }
                catch { SetupFiles.Delete(destination); throw; }
                break;
            default: throw new ArgumentException("无效的补丁工具参数");
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    struct DeltaInput { public nint Data; public nuint Size; public int Editable; }
    [DllImport("msdelta.dll", CharSet = CharSet.Unicode, ExactSpelling = true, SetLastError = true)]
    static extern bool CreateDeltaW(long types, long set, long reset, string source, string target, string? sourceOptions,
        string? targetOptions, DeltaInput options, nint time, uint hashAlgorithm, string delta);
}

// 与安装外壳共用文件操作，工具不访问应用设置。
namespace Pane.Settings { internal static class SettingsStore { public static string ProductionDirectory => throw new NotSupportedException(); } }
