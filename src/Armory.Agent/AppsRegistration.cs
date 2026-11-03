using System.Globalization;
using System.Xml.Linq;
using Microsoft.Win32;

namespace Armory.Agent;

// Velopack stores one HKCU Apps row per package ID. Another root can overwrite it before
// this root retires that copy, so restoring a snapshot alone cannot always recover the owner.
internal static class AppsRegistration
{
    private const string Root = @"Software\Microsoft\Windows\CurrentVersion\Uninstall";

    internal static void Repair(string exe, Action<string> log)
    {
        try { RepairCore(exe, log); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or System.Security.SecurityException or System.Xml.XmlException or ArgumentException)
        { log("deferred Apps registration repair: " + error.Message); }
    }

    private static bool SameLocation(RegistryKey? key, string install)
    {
        if (key?.GetValue("InstallLocation") is not string location) return false;
        return string.Equals(LinkForwarder.LongName(Path.TrimEndingDirectorySeparator(Path.GetFullPath(location))),
            LinkForwarder.LongName(Path.TrimEndingDirectorySeparator(install)), StringComparison.OrdinalIgnoreCase);
    }

    private static void RepairCore(string exe, Action<string> log)
    {
        var folder = Path.GetDirectoryName(exe)!;
        if (!Updates.IsVelopackCopy(exe, File.Exists))
        {
            if (!string.Equals(folder, ShellIdentity.InstallFolder(), StringComparison.OrdinalIgnoreCase)) return;
            var rows = LegacyInstall.UninstallKeys.Where(path =>
            {
                using var row = Registry.CurrentUser.OpenSubKey(path);
                return SameLocation(row, folder);
            }).ToArray();
            if (rows.Length < 2) return;
            var keep = LegacyInstall.UninstallKeys[File.Exists(Path.Combine(folder, "unins000.exe")) ? 1 : 0];
            foreach (var path in rows.Where(path => path != keep))
            {
                Registry.CurrentUser.DeleteSubKeyTree(path, false);
                log("removed duplicate Apps registration " + path);
            }
            return;
        }
        var install = Path.GetDirectoryName(folder)!;
        if (Path.TrimEndingDirectorySeparator(install) == Path.TrimEndingDirectorySeparator(Path.GetPathRoot(install)!) ||
            File.Exists(Path.Combine(install, ".msi-installed"))) return;
        var metadata = XDocument.Load(Path.Combine(folder, "sq.version")).Descendants().FirstOrDefault(e => e.Name.LocalName == "metadata");
        string? Field(string name) => metadata?.Elements().FirstOrDefault(e => e.Name.LocalName == name)?.Value;
        if (Field("id") != Updates.PackId || Updates.Release(Field("version")) is not { } version) return;

        bool Here(RegistryKey? key) => SameLocation(key, install);
        var machineOwner = false;
        foreach (var view in new[] { RegistryView.Registry64, RegistryView.Registry32 })
        {
            using var machine = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, view);
            using var row = machine.OpenSubKey(Root + "\\" + Updates.PackId);
            machineOwner |= Here(row);
        }
        using (var row = Registry.CurrentUser.OpenSubKey(Root + "\\" + Updates.PackId))
        {
            if (!Here(row) && !machineOwner)
            {
                using var owner = Registry.CurrentUser.CreateSubKey(Root + "\\" + Updates.PackId);
                var updater = Path.Combine(install, "Update.exe");
                foreach (var value in new Dictionary<string, string>
                {
                    ["DisplayName"] = ShellIdentity.DisplayName, ["DisplayVersion"] = version.ToString(),
                    ["Publisher"] = "IDEA, Don Bosco Tech", ["DisplayIcon"] = exe, ["InstallLocation"] = install,
                    ["InstallDate"] = Directory.GetCreationTime(install).ToString("yyyyMMdd", CultureInfo.InvariantCulture),
                    ["UninstallString"] = "\"" + updater + "\" --uninstall", ["QuietUninstallString"] = "\"" + updater + "\" --uninstall --silent",
                }) owner.SetValue(value.Key, value.Value, RegistryValueKind.String);
                owner.SetValue("NoModify", 1, RegistryValueKind.DWord);
                owner.SetValue("NoRepair", 1, RegistryValueKind.DWord);
                log("registered the surviving Apps entry at " + install);
            }
        }
        // Remove historical rows that refer to this updating root without uninstalling it.
        foreach (var hive in new[] { RegistryHive.CurrentUser, RegistryHive.LocalMachine })
        foreach (var view in new[] { RegistryView.Registry64, RegistryView.Registry32 })
        foreach (var key in LegacyInstall.UninstallKeys)
        {
            using var scope = RegistryKey.OpenBaseKey(hive, view);
            using var old = scope.OpenSubKey(key);
            if (!Here(old)) continue;
            try { scope.DeleteSubKeyTree(key, false); log("removed duplicate Apps registration " + hive + "/" + view + "/" + key); }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException or System.Security.SecurityException)
            { log("deferred duplicate Apps registration: " + error.Message); }
        }
    }
}
