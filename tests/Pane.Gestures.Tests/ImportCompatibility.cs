using System.IO.Compression;
using System.Text;
using Pane.Gestures;

static partial class Program
{
    static string LegacyGestures(int version)
    {
        string Intent(int button)
        {
            string gesture = $$$$"""{"GestureButton":{{{{button}}}},"Dirs":[0,2],"Modifier":0}""";
            string intent = $$$$"""{"Name":"测试手势","Gesture":{{{{gesture}}}},"Command":{"$type":"DoNothingCommand"}}""";
            return version == 1 ? $$$$"""{"Key":{{{{gesture}}}},"Value":{{{{intent}}}}}""" : intent;
        }
        string intents = string.Join(',', new[] { 1, 2, 4, 8 }.Select(b => Intent(b - (version == 3 ? 0 : 1))));
        return $$$$"""{"FileVersion":"{{{{version}}}}","Global":{"GestureIntents":[{{{{intents}}}}]},"Apps":{"demo":{"ExecutablePath":"C:\\fixture.exe","IsGesturingEnabled":true,"InheritGlobalGestures":true,"GestureIntents":[{{{{Intent(version == 3 ? 2 : 1)}}}}]},"disabled":{"ExecutablePath":"C:\\disabled.exe","IsGesturingEnabled":false,"GestureIntents":[]}}}""";
    }

    static void ImportCompatibility(string output)
    {
        int before = checks;
        string root = Path.GetFullPath(Path.Combine(output, "compatibility", Guid.NewGuid().ToString("N")));
        Directory.CreateDirectory(root);
        var importer = new WGesturesImporter(root);
        for (int version = 1; version <= 3; version++)
        {
            string directory = Path.Combine(root, "format" + version);
            Directory.CreateDirectory(directory);
            string file = Path.Combine(directory, version == 3 ? "gestures.wg2" : "gestures.wg");
            File.WriteAllText(file, LegacyGestures(version), new UTF8Encoding(version == 1));
            var preview = importer.Preview(file)!;
            var config = importer.Import(preview, true);
            Check(preview.SourceDirectory == file && preview.SourceVersion == version.ToString(), "explicit source and version");
            Check(preview.GlobalCount == 4 && preview.AppRuleCount == 2 && preview.DisabledAppCount == 1 && preview.Unsupported.Count == 0, "legacy counts");
            Check(config.Global.Select(b => (int)b.Pattern.Button).SequenceEqual([1, 2, 4, 8]), "legacy button conversion");
            Check(config.Global.All(b => b.Pattern.Directions.SequenceEqual([GestureDirection.Up, GestureDirection.Right]) && b.Command is DoNothingCommand), "legacy pattern and command");
            Check(config.Apps[0].ExePath == @"C:\fixture.exe" && config.Apps[0].Mode == AppRuleMode.InheritAndOverride
                && config.Apps[0].Bindings[0].Pattern.Button == GestureButton.Middle && config.Apps[1].Mode == AppRuleMode.Disabled, "legacy app conversion");
            Check(config.Settings == GestureConfigStore.Empty.Settings with { TrailColors = config.Settings.TrailColors }
                && !preview.HasCustomTrailColors, "missing plist uses defaults");
            Check(importer.Preview(directory)!.SourceDirectory == directory, "directory source retained");

            string archive = Path.Combine(root, "format" + version + ".wgb");
            WriteArchive(archive, (version == 1 ? "gestures.json" : Path.GetFileName(file), Encoding.UTF8.GetBytes(LegacyGestures(version))),
                ("config.plist", Encoding.UTF8.GetBytes("<plist><dict><key>GestureViewShowPath</key><false/></dict></plist>")));
            var packedPreview = importer.Preview(archive)!;
            var packed = importer.Import(packedPreview, true);
            Check(packedPreview.SourceDirectory == archive && packedPreview.SourceVersion == version.ToString() && packedPreview.Unsupported.Count == 0, "archive source version");
            Check(packed.Global.Count == 4 && packed.Apps.Count == 2 && !packed.Settings.ShowTrail, "archive settings and gestures");
            Check(packed.Global.Select(b => b.Pattern.Button).SequenceEqual(config.Global.Select(b => b.Pattern.Button)), "archive uses same conversion");
        }

        string local = Path.Combine(root, "local");
        var finder = new WGesturesImporter(local);
        Check(finder.Preview() is null && finder.SearchedLocations.Count == 2, "missing locations reported");
        string Put(string relative, int version, int age)
        {
            string file = Path.Combine(local, relative);
            Directory.CreateDirectory(Path.GetDirectoryName(file)!);
            File.WriteAllText(file, LegacyGestures(version));
            File.SetLastWriteTimeUtc(file, new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc).AddDays(age));
            return file;
        }
        Put(@"YingDev.com\WGestures\9.0.0.0\gestures.wg2", 3, 1);
        string newest = Put(@"YingDev.com\WGestures\1.0.0.0\gestures.wg", 2, 2);
        Check(finder.Preview()!.SourceDirectory == Path.GetDirectoryName(newest), "mtime beats version with wg only and no plist");
        string store = Put(@"Packages\Fixture.WGestures_test\LocalCache\Local\YingDev.com\WGestures\1.0\gestures.wg", 1, 3);
        Put(@"Packages\Other_test\LocalCache\Local\YingDev.com\WGestures\1.0\gestures.wg2", 3, 10);
        Check(finder.Preview()!.SourceDirectory == Path.GetDirectoryName(store), "store competes by mtime and other packages excluded");
        Check(finder.SearchedLocations.Contains(Path.GetDirectoryName(store)!) && !finder.SearchedLocations.Any(p => p.Contains("Other_test")), "searched concrete store locations");
        string latest = Put(@"Packages\Fixture.WGestures_test\LocalCache\Local\YingDev.com\WGestures\1.0\gestures.wg2", 3, 4);
        Check(finder.Preview()!.SourceVersion == "3", "two names in same directory select newest");
        Check(finder.Preview(store)!.SourceVersion == "1", "explicit older file not replaced");
        File.WriteAllText(latest, LegacyGestures(4));
        try { finder.Preview(latest); throw new Exception("unknown version accepted"); }
        catch (InvalidDataException e) { Check(e.Message.Contains(latest) && e.Message.Contains("4"), "unknown version identifies source"); }

        string noConfig = Path.Combine(root, "no-config.wgb");
        WriteArchive(noConfig, ("gestures.wg2", Encoding.UTF8.GetBytes(LegacyGestures(3))));
        Check(importer.Import(importer.Preview(noConfig)!, true).Settings.ShowTrail, "archive missing plist defaults");
        string invalid = Path.Combine(root, "invalid.wgb");
        foreach (byte[] bad in new byte[][] { [], [1], Encoding.UTF8.GetBytes("not an archive"), File.ReadAllBytes(noConfig)[..^4] })
        {
            File.WriteAllBytes(invalid, bad);
            BadArchive(importer, invalid);
        }
        WriteArchive(invalid, ("config.plist", Encoding.UTF8.GetBytes("<plist><dict/></plist>")));
        BadArchive(importer, invalid);
        WriteArchive(invalid, ("gestures.wg2", Encoding.UTF8.GetBytes("not json")));
        BadArchive(importer, invalid);
        WriteArchive(invalid, ("gestures.wg2", Encoding.UTF8.GetBytes(LegacyGestures(3))), ("config.plist", Encoding.UTF8.GetBytes("not xml")));
        BadArchive(importer, invalid);
        WriteArchive(invalid, ("gestures.wg2", Encoding.UTF8.GetBytes(LegacyGestures(3))), ("config.plist", Encoding.UTF8.GetBytes("<wrong/>")));
        BadArchive(importer, invalid);
        WriteArchive(invalid, ("gestures.wg2", Encoding.UTF8.GetBytes(LegacyGestures(4))));
        try { importer.Preview(invalid); throw new Exception("unknown archive version accepted"); }
        catch (InvalidDataException e) { Check(e.Message.Contains(invalid) && e.Message.Contains("gestures.wg2") && e.Message.Contains("版本 4"), "archive unknown version identifies inner file"); }
        Console.WriteLine($"PASS import compatibility assertions={checks - before}");
    }

    static void BadArchive(WGesturesImporter importer, string path)
    {
        try { importer.Preview(path); throw new Exception("bad archive accepted"); }
        catch (InvalidDataException e) { Check(e.Message.Contains("不是 WGestures 导出的文件"), "bad archive error"); }
    }

    // 按原作字段和 MS-NRBF 记录格式构造最小包，不执行第三方源码或 BinaryFormatter。
    static void WriteArchive(string path, params (string Name, byte[] Data)[] files)
    {
        using var output = new BinaryWriter(File.Create(path));
        foreach (var (name, data) in files)
        {
            using var payload = new MemoryStream();
            using (var compressed = new DeflateStream(payload, CompressionLevel.Optimal, true))
            using (var writer = new BinaryWriter(compressed, Encoding.UTF8, true))
            {
                Header(writer);
                Class(writer, "ArchiveFile", ["<Path>k__BackingField", "<Name>k__BackingField", "<OriginalFileName>k__BackingField", "<Created>k__BackingField", "<Modified>k__BackingField", "<Archived>k__BackingField", "<OriginalFileLength>k__BackingField", "<FileData>k__BackingField"], [1, 1, 1, 0, 0, 0, 0, 7], [13, 13, 13, 8, 2]);
                String(writer, 3, @"C:\fixture"); String(writer, 4, name); String(writer, 5, @"C:\fixture\" + name);
                writer.Write(0L); writer.Write(0L); writer.Write(0L); writer.Write(data.Length);
                writer.Write((byte)9); writer.Write(6);
                writer.Write((byte)15); writer.Write(6); writer.Write(data.Length); writer.Write((byte)2); writer.Write(data);
                writer.Write((byte)11);
            }
            using var index = new MemoryStream();
            using (var writer = new BinaryWriter(index, Encoding.UTF8, true))
            {
                Header(writer);
                Class(writer, "RawIndex", ["N", "L"], [1, 0], [8]);
                String(writer, 3, @"C:\fixture\" + name); writer.Write((int)payload.Length); writer.Write((byte)11);
            }
            output.Write((ushort)index.Length); output.Write(index.ToArray()); output.Write(payload.ToArray());
        }
    }
    static void Header(BinaryWriter writer)
    {
        writer.Write((byte)0); writer.Write(1); writer.Write(-1); writer.Write(1); writer.Write(0);
        writer.Write((byte)12); writer.Write(2); writer.Write("NativeMultiFileArchiveLib, Version=1.0.0.0, Culture=neutral, PublicKeyToken=null");
    }
    static void Class(BinaryWriter writer, string name, string[] fields, byte[] types, byte[] primitiveTypes)
    {
        writer.Write((byte)5); writer.Write(1); writer.Write("NativeMultiFileArchiveLib." + name); writer.Write(fields.Length);
        foreach (string field in fields) writer.Write(field);
        writer.Write(types); writer.Write(primitiveTypes); writer.Write(2);
    }
    static void String(BinaryWriter writer, int id, string value) { writer.Write((byte)6); writer.Write(id); writer.Write(value); }
}
