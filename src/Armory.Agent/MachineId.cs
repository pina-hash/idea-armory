using System.Net.NetworkInformation;
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
// The id tells computers apart in an incident and, since 0.5.1, on the website's Team, Files and
// Checked out views (each team heartbeat carries it as the computer key, idea-app 0310). Nothing
// about a sign-in or a check out depends on it, and nothing anywhere depends on the computer's
// name being unique (docs/agent/INSTALL.md, "Computers that share a name").
internal static class MachineId
{
    private static readonly Lazy<string?> Here = new(() => OperatingSystem.IsWindows() ? FromParts(ReadWindows(), ReadHardware()) : null);

    internal static string? Current => Here.Value;

    // The id for a MachineGuid value, or null for none.
    internal static string? FromGuid(string? machineGuid) => FromParts(machineGuid, null);

    // 0.4.3: a lab image copied to many computers without Sysprep copies its MachineGuid too, so
    // the id also takes this computer's own network hardware address (Hardware) when there is
    // one; with none it is exactly the id FromGuid always gave. Never sent as it is: only the hash.
    internal static string? FromParts(string? machineGuid, string? hardwareAddress)
    {
        var value = machineGuid?.Trim().ToLowerInvariant();
        if (string.IsNullOrEmpty(value)) return null;
        var hardware = hardwareAddress?.Trim().ToLowerInvariant();
        var text = "armory-machine\n" + value + (string.IsNullOrEmpty(hardware) ? "" : "\n" + hardware);
        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(text)))[..16];
    }

    // One network adapter, as Hardware reads it.
    internal sealed record NetworkAdapter(NetworkInterfaceType Kind, string Description, string Address);

    // The computer's own wired address, or its wireless one when it has no wired adapter: the
    // lowest, so the answer is the same at every start. Virtual, tunnel, Bluetooth and
    // all-zero addresses are not the computer's own and are left out. Null when there is none.
    internal static string? Hardware(IEnumerable<NetworkAdapter> adapters)
    {
        string[] virtualWords = ["virtual", "hyper-v", "vmware", "virtualbox", "vpn", "tap-", "bluetooth", "loopback", "wan miniport", "tunnel"];
        var own = adapters
            .Where(a => a.Kind is NetworkInterfaceType.Ethernet or NetworkInterfaceType.GigabitEthernet or NetworkInterfaceType.Wireless80211)
            .Where(a => a.Address.Length == 12 && a.Address.Any(c => c != '0') && !virtualWords.Any(w => a.Description.Contains(w, StringComparison.OrdinalIgnoreCase)))
            .ToList();
        var wired = own.Where(a => a.Kind != NetworkInterfaceType.Wireless80211).Select(a => a.Address.ToLowerInvariant()).Order(StringComparer.Ordinal).FirstOrDefault();
        return wired ?? own.Select(a => a.Address.ToLowerInvariant()).Order(StringComparer.Ordinal).FirstOrDefault();
    }

    private static string? ReadHardware()
    {
        try { return Hardware(NetworkInterface.GetAllNetworkInterfaces().Select(Adapter).ToList()); }
        catch (NetworkInformationException) { return null; }
    }

    private static NetworkAdapter Adapter(NetworkInterface adapter)
    {
        try { return new NetworkAdapter(adapter.NetworkInterfaceType, adapter.Description ?? "", adapter.GetPhysicalAddress().ToString()); }
        catch (NetworkInformationException) { return new NetworkAdapter(NetworkInterfaceType.Unknown, "", ""); }
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
