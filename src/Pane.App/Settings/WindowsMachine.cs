using Microsoft.Win32;
using System.Security.Cryptography;
using System.Text;

namespace Pane.Settings;

internal static class WindowsMachine
{
    internal static string Key(string? identity = null) => Convert.ToHexString(
        SHA256.HashData(Encoding.UTF8.GetBytes(identity ?? RegistryId() ?? Environment.MachineName)));

    internal static string? RegistryId()
    {
        try
        {
            using var root = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
            using var key = root.OpenSubKey(@"SOFTWARE\Microsoft\Cryptography");
            return key?.GetValue("MachineGuid") as string;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or System.Security.SecurityException) { return null; }
    }
}
