using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Armory.Core;

namespace Armory.Platform.Windows;

// How to tell Explorer that badges changed (docs/agent/EXPLORER.md), decided without Win32:
// each changed item when there are few, each folder that holds them when there are many, the
// vault root when even the folders are too many. Explorer then asks the handlers again for the
// items it shows.
public enum ShellChangeKind { Nothing, Items, Folders, Root }

public sealed record ShellChangePlan(ShellChangeKind Kind, IReadOnlyList<string> Paths)
{
    public const int MaxItems = 256;
    public const int MaxFolders = 256;

    public static readonly ShellChangePlan Nothing = new(ShellChangeKind.Nothing, []);

    // The plan between two publications for one vault root. Paths come back as full Windows
    // paths ("C:\IDEA\Armory\Robot 2027\Plate.SLDPRT"), each once, sorted.
    public static ShellChangePlan For(string vaultRoot, IEnumerable<BadgeEntry> previous, IEnumerable<BadgeEntry> next)
    {
        var before = Index(previous);
        var after = Index(next);
        var changed = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (path, state) in after)
            if (!before.TryGetValue(path, out var was) || was != state) changed.Add(path);
        foreach (var path in before.Keys)
            if (!after.ContainsKey(path)) changed.Add(path);
        if (changed.Count == 0) return Nothing;
        var root = vaultRoot.Replace('/', '\\').TrimEnd('\\');
        string Full(string relative) => relative.Length == 0 ? root : root + "\\" + relative.Replace('/', '\\');
        if (changed.Count <= MaxItems) return new(ShellChangeKind.Items, changed.Select(Full).ToArray());
        var folders = new SortedSet<string>(changed.Select(Parent), StringComparer.OrdinalIgnoreCase);
        if (folders.Count <= MaxFolders) return new(ShellChangeKind.Folders, folders.Select(Full).ToArray());
        return new(ShellChangeKind.Root, [root]);
    }

    // The folder that holds a vault-relative path; "" is the vault root.
    private static string Parent(string path)
    {
        var slash = path.LastIndexOf('/');
        return slash > 0 ? path[..slash] : "";
    }

    private static Dictionary<string, BadgeState> Index(IEnumerable<BadgeEntry> entries)
    {
        var index = new Dictionary<string, BadgeState>(StringComparer.OrdinalIgnoreCase);
        foreach (var (raw, state) in entries)
        {
            var path = raw.Replace('\\', '/').Trim('/');
            if (path.Length == 0 || state == BadgeState.None) continue;
            if (!index.TryGetValue(path, out var had) || had < state) index[path] = state;
        }
        return index;
    }
}

// SHChangeNotify, the Win32 half. SHChangeNotify can wait on Explorer, so call Send off the
// window and engine threads.
[SupportedOSPlatform("windows")]
public static class ShellNotify
{
    private const int UpdateDirectory = 0x00001000;    // SHCNE_UPDATEDIR
    private const int UpdateItem = 0x00002000;         // SHCNE_UPDATEITEM
    private const int AssociationChanged = 0x08000000; // SHCNE_ASSOCCHANGED
    private const uint IdList = 0x0000;                // SHCNF_IDLIST
    private const uint PathW = 0x0005;                 // SHCNF_PATHW
    private const uint FlushNoWait = 0x3000;           // SHCNF_FLUSHNOWAIT

    // One notice per path of the plan; the last one flushes without waiting.
    public static void Send(ShellChangePlan plan)
    {
        if (plan.Kind == ShellChangeKind.Nothing) return;
        var change = plan.Kind == ShellChangeKind.Items ? UpdateItem : UpdateDirectory;
        for (var i = 0; i < plan.Paths.Count; i++)
            SHChangeNotify(change, PathW | (i == plan.Paths.Count - 1 ? FlushNoWait : 0), plan.Paths[i], IntPtr.Zero);
    }

    // After the right-click menu's registry keys change (ShellVerbs), so Explorer reads them again.
    public static void AssociationsChanged() => SHChangeNotify(AssociationChanged, IdList, IntPtr.Zero, IntPtr.Zero);

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern void SHChangeNotify(int eventId, uint flags, string item1, IntPtr item2);

    [DllImport("shell32.dll")]
    private static extern void SHChangeNotify(int eventId, uint flags, IntPtr item1, IntPtr item2);
}
