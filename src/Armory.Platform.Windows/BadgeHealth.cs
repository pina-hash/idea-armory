using System.Diagnostics;
using System.Runtime.Versioning;
using System.Text.RegularExpressions;
using Microsoft.Win32;

namespace Armory.Platform.Windows;

// Whether File Explorer shows Armory's badges for the person signed in, and the one sentence
// Settings says about it (docs/agent/EXPLORER.md). Decide is pure; Read gathers the facts from
// the registry and this session's explorer.exe.
public enum BadgeHealthState { Off, On, AfterSignIn, Crowded, Partial, Broken }

// One overlay handler as Explorer reads its list: key name, CLSID, its DLL and whether that exists.
public sealed record OverlayIdentifier(string Name, string Clsid, string? Dll, bool DllExists);

// InstalledVersion, Format and InstalledAt: HKLM\SOFTWARE\IDEA Armory\Badges (the badges setup).
// Identifiers: HKLM ShellIconOverlayIdentifiers in the registry's order. Seen: the heartbeat
// ArmoryBadges.dll writes when this person's Explorer loads a badge (HKCU\Software\IDEA
// Armory\Badges\Seen<Badge>). ExplorerStarted: this session's explorer.exe, null without one.
public sealed record BadgeHealthFacts(string? InstalledVersion, string? Format, DateTimeOffset? InstalledAt,
    IReadOnlyList<OverlayIdentifier> Identifiers, IReadOnlyDictionary<string, DateTimeOffset> Seen, DateTimeOffset? ExplorerStarted, DateTimeOffset Now);

// State and Line for Settings; Detail is for the log only. Shown counts Armory's badges within
// Windows' limit; AppsAhead names the apps whose badges come first.
public sealed record BadgeHealthReport(BadgeHealthState State, string Line, string? Detail, int Shown, IReadOnlyList<string> AppsAhead)
{
    // The name the page uses: off, on, afterSignIn, crowded, partial or broken.
    public string Key => State switch
    {
        BadgeHealthState.Off => "off",
        BadgeHealthState.On => "on",
        BadgeHealthState.AfterSignIn => "afterSignIn",
        BadgeHealthState.Crowded => "crowded",
        BadgeHealthState.Partial => "partial",
        _ => "broken",
    };

    // Settings offers "Turn on" (the badges setup, as an administrator) for these two.
    public bool CanTurnOn => State is BadgeHealthState.Off or BadgeHealthState.Broken;
}

public static class BadgeHealth
{
    // Windows has 15 overlay slots and keeps 4: only the first 11 handlers in its list show.
    public const int WindowsLimit = 11;
    public const string MachineKey = @"SOFTWARE\IDEA Armory\Badges";
    public const string UserKey = @"Software\IDEA Armory\Badges";
    public const string IdentifiersKey = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Explorer\ShellIconOverlayIdentifiers";
    public const string Format = "1";
    // Explorer that has run this long without asking for a badge icon did not load the DLL.
    public static readonly TimeSpan LoadGrace = TimeSpan.FromMinutes(10);

    // Armory's four, in their key names' order (one leading space, like OneDrive's).
    public static readonly IReadOnlyList<(string Key, string Clsid, string Badge)> Badges =
    [
        (" IDEAArmory1Attention", "{E26E19F2-515F-472F-AD4F-1B0293728CE2}", "Attention"),
        (" IDEAArmory2Mine", "{DB040D16-C118-4CDA-B616-DF9340A8BC9F}", "Mine"),
        (" IDEAArmory3Locked", "{DF50E3A9-57B8-44AE-B690-257AFF283F97}", "Locked"),
        (" IDEAArmory4Synced", "{58F5F8D8-1041-43B9-B8DE-0EBEBCC29CF0}", "Synced"),
    ];

    public const string OffLine = "Armory's status isn't shown on file icons on this computer. Turning it on needs an administrator once.";
    public const string OnLine = "Armory's status shows on file icons.";
    public const string AfterSignInLine = "Armory's status shows on file icons after you sign out of Windows and back in.";
    public const string BrokenLine = "Armory's badges are installed, but a file is missing. Ask an administrator to turn them on again.";

    public static string CrowdedLine(int ahead, IReadOnlyList<string> apps) =>
        $"Windows isn't showing Armory's badges because {ahead} badges from other apps come first ({string.Join(", ", apps)}). Windows shows only {WindowsLimit}.";

    public static string PartialLine(int ahead, IReadOnlyList<string> apps) =>
        $"Windows shows only some of Armory's badges because {ahead} badges from other apps come first ({string.Join(", ", apps)}).";

    public static BadgeHealthReport Decide(BadgeHealthFacts facts)
    {
        var ours = new HashSet<string>(Badges.Select(b => b.Key), StringComparer.OrdinalIgnoreCase);
        var clsids = new HashSet<string>(Badges.Select(b => b.Clsid), StringComparer.OrdinalIgnoreCase);
        bool IsOurs(OverlayIdentifier i) => ours.Contains(i.Name) || clsids.Contains(i.Clsid.Trim());
        var list = facts.Identifiers;
        if (facts.InstalledVersion is null && !list.Any(IsOurs))
            return new(BadgeHealthState.Off, OffLine, null, 0, []);

        var problems = new List<string>();
        foreach (var (key, clsid, badge) in Badges)
        {
            var found = list.FirstOrDefault(i => string.Equals(i.Name, key, StringComparison.OrdinalIgnoreCase));
            if (found is null) problems.Add($"the {badge} badge is not registered");
            else if (!string.Equals(found.Clsid.Trim(), clsid, StringComparison.OrdinalIgnoreCase)) problems.Add($"the {badge} badge names another CLSID ({found.Clsid})");
            else if (found.Dll is null) problems.Add($"the {badge} badge has no DLL registered");
            else if (!found.DllExists) problems.Add($"the {badge} badge's DLL is missing ({found.Dll})");
        }
        if (facts.InstalledVersion is null) problems.Add("the badges setup's version is not recorded");
        if (facts.Format is not null && facts.Format != Format) problems.Add($"the badges installed read format {facts.Format}, and this Armory writes format {Format}");
        if (problems.Count > 0) return new(BadgeHealthState.Broken, BrokenLine, string.Join("; ", problems), 0, []);

        var first = list.ToList().FindIndex(i => IsOurs(i));
        var ahead = list.Take(first).Where(i => !IsOurs(i)).ToArray();
        var apps = AppNames(ahead.Select(i => i.Name));
        var shown = list.Select((identifier, index) => (identifier, index)).Count(p => IsOurs(p.identifier) && p.index < WindowsLimit);
        if (shown == 0) return new(BadgeHealthState.Crowded, CrowdedLine(ahead.Length, apps), null, 0, apps);
        if (shown < Badges.Count) return new(BadgeHealthState.Partial, PartialLine(ahead.Length, apps), null, shown, apps);

        if (facts.ExplorerStarted is not { } started)
            return new(BadgeHealthState.AfterSignIn, AfterSignInLine, "explorer.exe is not running in this session", shown, apps);
        if (facts.Seen.Values.Any(seen => seen >= started))
            return new(BadgeHealthState.On, OnLine, null, shown, apps);
        if (facts.InstalledAt is { } installed && installed > started)
            return new(BadgeHealthState.AfterSignIn, AfterSignInLine, "the badges were installed after this session's explorer.exe started", shown, apps);
        if (facts.Now - started < LoadGrace)
            return new(BadgeHealthState.On, OnLine, "explorer.exe has not asked for Armory's badge icons yet", shown, apps);
        return new(BadgeHealthState.Broken, BrokenLine, "explorer.exe did not ask for Armory's badge icons", shown, apps);
    }

    // The apps behind key names: " OneDrive1" is OneDrive, "  DropboxExt01" is Dropbox. Trimmed,
    // trailing digits and then a trailing Ext or Ico removed, each name once, in list order.
    public static IReadOnlyList<string> AppNames(IEnumerable<string> keyNames)
    {
        var names = new List<string>();
        foreach (var key in keyNames)
        {
            var name = Regex.Replace(Regex.Replace(key.Trim(), "[0-9]+$", ""), "(Ext|Ico)$", "").Trim();
            if (name.Length > 0 && !names.Contains(name, StringComparer.OrdinalIgnoreCase)) names.Add(name);
        }
        return names;
    }

    // The facts on this computer, for this Windows user and session.
    [SupportedOSPlatform("windows")]
    public static BadgeHealthFacts Read()
    {
        using var machine = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
        string? version = null, format = null;
        DateTimeOffset? installedAt = null;
        using (var badges = machine.OpenSubKey(MachineKey))
        {
            version = badges?.GetValue("Version") as string;
            format = badges?.GetValue("Format") as string;
            if (badges?.GetValue("InstalledAt") is long when && when > 0) installedAt = DateTimeOffset.FromFileTime(when).ToUniversalTime();
        }
        var identifiers = new List<OverlayIdentifier>();
        using (var list = machine.OpenSubKey(IdentifiersKey))
        using (var classes = machine.OpenSubKey(@"SOFTWARE\Classes\CLSID"))
        {
            foreach (var name in list?.GetSubKeyNames() ?? [])
            {
                using var entry = list!.OpenSubKey(name);
                var clsid = (entry?.GetValue(null) as string ?? "").Trim();
                string? dll = null;
                if (clsid.Length > 0)
                {
                    using var server = classes?.OpenSubKey(clsid + @"\InprocServer32");
                    if (server?.GetValue(null) is string registered && registered.Trim().Length > 0)
                        dll = Environment.ExpandEnvironmentVariables(registered.Trim().Trim('"'));
                }
                identifiers.Add(new(name, clsid, dll, dll is not null && File.Exists(dll)));
            }
        }
        var seen = new Dictionary<string, DateTimeOffset>(StringComparer.OrdinalIgnoreCase);
        using (var user = Registry.CurrentUser.OpenSubKey(UserKey))
            foreach (var (_, _, badge) in Badges)
                if (user?.GetValue("Seen" + badge) is long when && when > 0) seen[badge] = DateTimeOffset.FromFileTime(when).ToUniversalTime();
        return new(version, format, installedAt, identifiers, seen, ExplorerStarted(), DateTimeOffset.UtcNow);
    }

    [SupportedOSPlatform("windows")]
    public static BadgeHealthReport Check() => Decide(Read());

    // The earliest explorer.exe in this Windows session (the desktop), or null.
    [SupportedOSPlatform("windows")]
    private static DateTimeOffset? ExplorerStarted()
    {
        using var me = Process.GetCurrentProcess();
        var session = me.SessionId;
        DateTimeOffset? earliest = null;
        foreach (var process in Process.GetProcessesByName("explorer"))
        {
            using (process)
            {
                try
                {
                    if (process.SessionId != session) continue;
                    var started = new DateTimeOffset(process.StartTime.ToUniversalTime(), TimeSpan.Zero);
                    if (earliest is null || started < earliest) earliest = started;
                }
                catch (Exception error) when (error is InvalidOperationException or System.ComponentModel.Win32Exception or NotSupportedException) { }
            }
        }
        return earliest;
    }
}
