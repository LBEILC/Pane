using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Win32;

namespace Pane.Services.Cloud;

internal static class TrialHardware
{
    [DllImport("kernel32.dll")]
    static extern uint GetSystemFirmwareTable(uint provider, uint table, [Out] byte[]? buffer, uint size);
    internal static string?[] Read() => [Hash(FirmwareId()), Hash(RegistryId())];
    internal static string? Hash(string? value)
    {
        if (!Guid.TryParse(value, out var id)) return null;
        var canonical = id.ToString("D");
        if (id == Guid.Empty || canonical is "ffffffff-ffff-ffff-ffff-ffffffffffff"
            or "03000200-0400-0500-0006-000700080009" or "00020003-0004-0005-0006-000700080009"
            or "12345678-1234-5678-1234-567812345678") return null;
        return CloudKeys.Encode(SHA256.HashData(Encoding.UTF8.GetBytes("pane-trial-v1:" + value)));
    }
    static string? RegistryId()
    {
        try
        {
            using var root = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
            using var key = root.OpenSubKey(@"SOFTWARE\Microsoft\Cryptography");
            return key?.GetValue("MachineGuid") as string;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or System.Security.SecurityException) { return null; }
    }
    static string? FirmwareId()
    {
        const uint rsmb = 0x52534D42;
        uint size = GetSystemFirmwareTable(rsmb, 0, null, 0);
        if (size is < 8 or > 16777216) return null;
        byte[] bytes = new byte[size];
        if (GetSystemFirmwareTable(rsmb, 0, bytes, size) != size) return null;
        for (int p = 8; p + 4 <= bytes.Length;)
        {
            int length = bytes[p + 1];
            if (length < 4 || p + length > bytes.Length || bytes[p] == 127) break;
            if (bytes[p] == 1 && length >= 24)
                return new Guid(bytes.AsSpan(p + 8, 16), bigEndian: bytes[1] < 2 || bytes[1] == 2 && bytes[2] < 6).ToString("D");
            p += length;
            while (p + 1 < bytes.Length && (bytes[p] != 0 || bytes[p+1] != 0)) p++;
            p += 2;
        }
        return null;
    }
}
