using System.IO.Compression;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Pane.Services.Setup;

internal static class PayloadPatch
{
    internal sealed record Stamp(long Size, string Sha256)
    {
        public static Stamp Read(JsonElement node)
        {
            var size = node.GetProperty("size").GetInt64();
            var hash = node.GetProperty("sha256").GetString() ?? "";
            Require(size >= 0 && Regex.IsMatch(hash, @"\A[0-9a-fA-F]{64}\z"));
            return new(size, hash.ToLowerInvariant());
        }

        public void Verify(string file)
        {
            using var input = File.OpenRead(file);
            Require(input.Length == Size && Hash(input) == Sha256);
        }
    }

    internal static void Require(bool valid)
    {
        if (!valid) throw new InvalidDataException("增量更新数据不匹配");
    }

    internal static string Hash(Stream input) => Convert.ToHexStringLower(SHA256.HashData(input));
    internal static string Hash(byte[] data) => Convert.ToHexStringLower(SHA256.HashData(data));

    internal static string Relative(string? path)
    {
        Require(!string.IsNullOrEmpty(path));
        foreach (string part in path!.Split('/'))
        {
            Require(part.Length > 0 && part.IndexOfAny(Path.GetInvalidFileNameChars()) < 0 &&
                !part.EndsWith('.') && !part.EndsWith(' ') &&
                !Regex.IsMatch(part.Split('.')[0].TrimEnd(), @"\A(CON|CONIN\$|CONOUT\$|PRN|AUX|NUL|(?:COM|LPT)[0-9¹²³])\z",
                    RegexOptions.IgnoreCase | RegexOptions.CultureInvariant));
        }
        return path;
    }

    static void Header(JsonElement root)
    {
        Require(root.GetProperty("format").GetInt32() == 1 && root.GetProperty("product").GetString() == "Pane" &&
            root.GetProperty("arch").GetString() == "x64");
    }

    internal static Dictionary<string, Stamp> Manifest(byte[] bytes, string version)
    {
        using var document = JsonDocument.Parse(bytes);
        var root = document.RootElement;
        Header(root);
        Require(root.GetProperty("version").GetString() == version);
        var files = new Dictionary<string, Stamp>(StringComparer.OrdinalIgnoreCase);
        string? previous = null;
        foreach (var file in root.GetProperty("files").EnumerateArray())
        {
            string path = Relative(file.GetProperty("path").GetString());
            Require(string.CompareOrdinal(previous, path) < 0 && files.TryAdd(path, Stamp.Read(file)));
            previous = path;
        }
        return files;
    }

    internal static IEnumerable<string> Paths(string root)
    {
        SetupFiles.Check(root);
        foreach (var item in Directory.EnumerateFileSystemEntries(root))
        {
            SetupFiles.Check(item);
            if (Directory.Exists(item))
                foreach (var child in Paths(item)) yield return child;
            else yield return item;
        }
    }

    internal static void VerifyDirectory(string directory, Dictionary<string, Stamp> files)
    {
        int count = 0;
        foreach (var file in Paths(directory))
        {
            string path = Path.GetRelativePath(directory, file).Replace('\\', '/');
            Require(files.TryGetValue(path, out var stamp));
            stamp!.Verify(file);
            count++;
        }
        Require(count == files.Count);
    }

    static string Baseline(string directory, string path, Stamp stamp)
    {
        SetupFiles.Check(directory);
        foreach (var part in path.Split('/'))
        {
            directory = Path.Combine(directory, part);
            SetupFiles.Check(directory);
        }
        stamp.Verify(directory);
        return directory;
    }

    internal static void Apply(string baseline, string patch, byte[] manifest, string from, string to, string output,
        CancellationToken token = default)
    {
        using var zip = ZipFile.OpenRead(patch);
        var entries = zip.Entries.ToDictionary(e => Relative(e.FullName), StringComparer.OrdinalIgnoreCase);
        byte[] Read(string name)
        {
            using var stream = entries[name].Open();
            using var buffer = new MemoryStream();
            stream.CopyTo(buffer);
            return buffer.ToArray();
        }
        using var document = JsonDocument.Parse(Read("patch.json"));
        var root = document.RootElement;
        Header(root);
        Require(root.GetProperty("from").GetString() == from && root.GetProperty("to").GetString() == to);
        byte[] oldManifest = Read("base.json");
        Require(root.GetProperty("fromManifestSha256").GetString() == Hash(oldManifest) &&
            root.GetProperty("toManifestSha256").GetString() == Hash(manifest));
        var old = Manifest(oldManifest, from);
        var target = Manifest(manifest, to);
        var operations = root.GetProperty("ops").EnumerateArray().ToDictionary(
            op => Relative(op.GetProperty("path").GetString()), StringComparer.OrdinalIgnoreCase);
        var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "base.json", "patch.json" };
        foreach (string path in old.Keys.Where(path => !target.ContainsKey(path)))
            Require(operations.Remove(path, out var op) && op.GetProperty("op").GetString() == "delete");

        Require(!Directory.Exists(output));
        Directory.CreateDirectory(output);
        try
        {
            foreach (var (path, stamp) in target)
            {
                token.ThrowIfCancellationRequested();
                string file = Path.Combine(output, path);
                Directory.CreateDirectory(Path.GetDirectoryName(file)!);
                if (!operations.Remove(path, out var op))
                {
                    Require(old.TryGetValue(path, out var source) && source == stamp);
                    File.Copy(Baseline(baseline, path, stamp), file);
                    continue;
                }
                Require(Stamp.Read(op) == stamp);
                string entry = Relative(op.GetProperty("entry").GetString());
                Require(used.Add(entry));
                switch (op.GetProperty("op").GetString())
                {
                    case "replace":
                        Require(entries[entry].Length == stamp.Size);
                        entries[entry].ExtractToFile(file);
                        break;
                    case "delta":
                        var expected = Stamp.Read(op.GetProperty("base"));
                        Require(old.TryGetValue(path, out var sourceStamp) && sourceStamp == expected);
                        string sourceFile = Baseline(baseline, path, expected);
                        string delta = Path.Combine(Path.GetDirectoryName(output)!, Guid.NewGuid().ToString("N"));
                        try
                        {
                            entries[entry].ExtractToFile(delta);
                            if (!ApplyDeltaW(0, sourceFile, delta, file))
                                throw new IOException("无法应用文件差分", Marshal.GetLastWin32Error());
                        }
                        finally { File.Delete(delta); }
                        break;
                    default: throw new InvalidDataException();
                }
            }
            Require(operations.Count == 0 && used.Count == entries.Count);
            VerifyDirectory(output, target);
        }
        catch { SetupFiles.Delete(output); throw; }
    }

    [DllImport("msdelta.dll", CharSet = CharSet.Unicode, ExactSpelling = true, SetLastError = true)]
    static extern bool ApplyDeltaW(long flags, string source, string delta, string target);
}
