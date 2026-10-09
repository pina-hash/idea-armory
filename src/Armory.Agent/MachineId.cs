using System.Security.Cryptography;
using System.Text;
using Microsoft.Win32;

namespace Armory.Agent;

// Which computer an incident came from, beyond its name: two lab computers imaged alike both
// report IDEA-06 (docs/agent/TELEMETRY.md, "The incident file"). Windows' own install id
// (HKLM\SOFTWARE\Microsoft\Cryptography, MachineGuid) is the same for a computer across
// accounts and reinstalls of Armory, and is never sent as it is: only the first 16 hex
// characters of its SHA-256, under Armory's own prefix, which tells computers apart and names
// nothing else. Null where there is no such id (another operating system, a locked-down key).
internal static class MachineId
{
    private static readonly Lazy<string?> Here = new(() => OperatingSystem.IsWindows() ? FromGuid(ReadWindows()) : null);

    internal static string? Current => Here.Value;

    // The id for a MachineGuid value, or null for none.
    internal static string? FromGuid(string? machineGuid)
    {
        var value = machineGuid?.Trim().ToLowerInvariant();
        if (string.IsNullOrEmpty(value)) return null;
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes("armory-machine\n" + value));
        return Convert.ToHexStringLower(hash)[..16];
    }

    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    private static string? ReadWindows()
    {
        try
        {
            // The 64-bit view: the value lives there, whichever way Armory itself runs.
            using var machine = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
            using var key = machine.OpenSubKey(@"SOFTWARE\Microsoft\Cryptography");
            return key?.GetValue("MachineGuid") as string;
        }
        catch (Exception error) when (error is System.Security.SecurityException or UnauthorizedAccessException or IOException) { return null; }
    }
}
