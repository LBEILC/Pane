using System.IO.Compression;
using System.Text;
using Pane.Contracts;
using Pane.Services.Ai;
using Pane.Services.Clipboard;
using Pane.Settings;

static partial class Program
{
    static async Task MirrorExport097Tests()
    {
        string root = Path.Combine(Path.GetTempPath(), "Pane-mirror-export-" + Guid.NewGuid().ToString("N"));
        try
        {
            var settings = new SettingsStore(root, Defaults()); settings.Save(Defaults());
            const string secret = "fixture-097-export-cdk";
            new SecretStore(root).Write("mirror-cdk.bin", "{\"cdk\":\"" + secret + "\",\"expires\":null}");
            Check(!Encoding.UTF8.GetString(File.ReadAllBytes(Path.Combine(root, "mirror-cdk.bin"))).Contains(secret), "097 DPAPI ciphertext");
            Check(!File.ReadAllText(Path.Combine(root, "settings.json")).Contains(secret), "097 settings no CDK");
            IDataTransfer transfer = new DataStorage(settings, () => null, () => { });
            string zip = Path.Combine(root, "export.zip");
            await transfer.ExportAsync(zip, new(true, true, ClipboardExport.WithImages), null, default);
            using var archive = ZipFile.OpenRead(zip);
            Check(!archive.Entries.Any(e => e.FullName.Contains("mirror-cdk")), "097 export no secret file");
            foreach (var entry in archive.Entries)
            {
                using var reader = new StreamReader(entry.Open());
                Check(!(await reader.ReadToEndAsync()).Contains(secret), "097 export content no CDK");
            }
            Console.WriteLine($"PASS mirror export assertions={checks}; realData=0; systemClipboard=0");
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }
}
