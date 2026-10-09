namespace Armory.Core;

// The badge File Explorer shows on an item in the vault (docs/agent/EXPLORER.md). Explorer
// shows one overlay per icon, so four badges answer what a student asks at a glance: is
// something wrong, can I edit it, is someone else on it, is it up to date. The byte value is
// the state stored in the badge table (format 1) and also its strength: when two claims meet
// on one path, the larger value wins.
public enum BadgeState : byte
{
    None = 0,
    // Up to date, and not checked out by anyone.
    Synced = 1,
    // Checked out by someone else, or by you on another computer.
    Locked = 2,
    // Checked out by you on this computer, changed or not, or new here and not in Armory yet.
    Mine = 3,
    // Can't be uploaded, can't be read, changed without a check out, or a kept copy.
    Attention = 4,
}

// A file's status and who has it checked out, in Core's words. They map one to one to the
// engine's FileStatuses and CheckoutStates names (BadgeFacts.FromNames).
public enum BadgeFileStatus { Synced, Changed, Uploading, Downloading, Waiting, NewerWaiting, KeptCopy, NotInArmory, NotOnThisComputer }
public enum BadgeCheckout { Available, Mine, Other, MyOtherComputer }

// What the engine knows of one file in the vault. Path is vault-relative, either separator
// ("Robot 2027/Arm/Plate.SLDPRT"). InArmory: the file has a file id (it is in Armory).
// CantSend: Armory refused to upload it (a "can't be uploaded" or "shares a name" notice,
// dismissed or not). CantRead: Armory could not read it.
public sealed record BadgeFacts(string Path, BadgeFileStatus Status, BadgeCheckout Checkout, bool InArmory, bool CantSend, bool CantRead)
{
    // From the engine's names ("synced", "keptCopy", "myOtherComputer", ...). An unknown name
    // throws, so a status added later can never lose its badge without a test failing.
    public static BadgeFacts FromNames(string path, string status, string checkout, bool inArmory, bool cantSend, bool cantRead) =>
        new(path, StatusOf(status), CheckoutOf(checkout), inArmory, cantSend, cantRead);

    public static BadgeFileStatus StatusOf(string status) => status switch
    {
        "synced" => BadgeFileStatus.Synced,
        "changed" => BadgeFileStatus.Changed,
        "uploading" => BadgeFileStatus.Uploading,
        "downloading" => BadgeFileStatus.Downloading,
        "waiting" => BadgeFileStatus.Waiting,
        "newerWaiting" => BadgeFileStatus.NewerWaiting,
        "keptCopy" => BadgeFileStatus.KeptCopy,
        "notInArmory" => BadgeFileStatus.NotInArmory,
        "notOnThisComputer" => BadgeFileStatus.NotOnThisComputer,
        _ => throw new ArgumentException($"Unknown file status \"{status}\".", nameof(status)),
    };

    public static BadgeCheckout CheckoutOf(string checkout) => checkout switch
    {
        "available" => BadgeCheckout.Available,
        "mine" => BadgeCheckout.Mine,
        "other" => BadgeCheckout.Other,
        "myOtherComputer" => BadgeCheckout.MyOtherComputer,
        _ => throw new ArgumentException($"Unknown check out state \"{checkout}\".", nameof(checkout)),
    };
}

// One badged path in the vault (vault-relative, either separator) and its badge.
public readonly record struct BadgeEntry(string Path, BadgeState State);

public static class BadgeRules
{
    // The badge of one file. Attention beats Mine beats Locked beats Synced.
    public static BadgeState For(BadgeFacts facts)
    {
        // Nothing on this computer to put a badge on.
        if (facts.Status == BadgeFileStatus.NotOnThisComputer) return BadgeState.None;
        if (facts.CantSend || facts.CantRead || facts.Status == BadgeFileStatus.KeptCopy) return BadgeState.Attention;
        // A change you can make is always on a file you have checked out here; any other
        // change was made without a check out.
        if (facts.Status == BadgeFileStatus.Changed && facts.Checkout != BadgeCheckout.Mine) return BadgeState.Attention;
        if (facts.Checkout == BadgeCheckout.Mine) return BadgeState.Mine;
        // New here, on its way into Armory.
        if (!facts.InArmory && facts.Status is BadgeFileStatus.Waiting or BadgeFileStatus.Uploading) return BadgeState.Mine;
        if (facts.Checkout is BadgeCheckout.Other or BadgeCheckout.MyOtherComputer) return BadgeState.Locked;
        if (facts.InArmory && facts.Status == BadgeFileStatus.Synced && facts.Checkout == BadgeCheckout.Available) return BadgeState.Synced;
        return BadgeState.None;
    }

    // The files' badges plus their folders': every folder below the vault root carries the
    // strongest Attention or Mine found anywhere under it. Locked and Synced do not climb
    // (someone else's file deep in a folder is nothing to act on), and the vault root itself
    // never has a badge. Paths come back with '/' separators, sorted, one entry per path
    // (ignoring case, the strongest kept); files without a badge are left out.
    public static IReadOnlyList<BadgeEntry> WithFolders(IEnumerable<BadgeEntry> files)
    {
        var badges = new Dictionary<string, BadgeState>(StringComparer.OrdinalIgnoreCase);
        void Claim(string path, BadgeState state)
        {
            if (!badges.TryGetValue(path, out var had) || had < state) badges[path] = state;
        }
        foreach (var (raw, state) in files)
        {
            if (state == BadgeState.None) continue;
            var path = Normalize(raw);
            if (path.Length == 0) continue;
            Claim(path, state);
            if (state is not (BadgeState.Attention or BadgeState.Mine)) continue;
            for (var slash = path.LastIndexOf('/'); slash > 0; slash = path.LastIndexOf('/', slash - 1))
                Claim(path[..slash], state);
        }
        return badges.OrderBy(b => b.Key, StringComparer.Ordinal).Select(b => new BadgeEntry(b.Key, b.Value)).ToArray();
    }

    // Every badged path for the engine's facts: For on each file, then WithFolders.
    public static IReadOnlyList<BadgeEntry> Entries(IEnumerable<BadgeFacts> facts) =>
        WithFolders(facts.Select(f => new BadgeEntry(f.Path, For(f))));

    private static string Normalize(string path) => path.Replace('\\', '/').Trim('/');
}
