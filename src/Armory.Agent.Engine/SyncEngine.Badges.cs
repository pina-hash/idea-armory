using Armory.Agent.Engine.View;
using Armory.Core;

namespace Armory.Agent.Engine;

// What File Explorer's badges are made of (docs/agent/EXPLORER.md 2.1): one BadgeFacts per file
// on this computer, read on the engine thread between two steps of a pass. The host turns them
// into the badge table (BadgeRules.Entries, then BadgePublisher). Never built from the view:
// a dismissed notice leaves the view, but its file still needs a badge.
public sealed partial class SyncEngine
{
    public Task<IReadOnlyList<BadgeFacts>> BadgeFactsAsync() => engineThread.InvokeAsync(() => Task.FromResult(CurrentBadgeFacts()));

    private IReadOnlyList<BadgeFacts> CurrentBadgeFacts()
    {
        // Only files in a project of this account: a stray file elsewhere in the vault goes nowhere.
        var projects = new HashSet<string>(state.Projects.Values.Where(p => p.Usable).Select(p => p.Folder), StringComparer.OrdinalIgnoreCase);
        // Files Armory could not read in this pass or lately (a "stale marker" card is about a file
        // still open, not one Armory can't read).
        var now = deps.Clock.GetUtcNow();
        var unreadable = new HashSet<string>(notes.Where(n => n.Kind == NoticeKinds.CantRead && n.Title != StaleMarkerTitle && n.Path.Length > 0).Select(n => n.Path),
            StringComparer.OrdinalIgnoreCase);
        unreadable.UnionWith(state.Remembered.Where(n => n.Kind == NoticeKinds.CantRead && now - n.At < TimeSpan.FromMinutes(30)).Select(n => n.Path));
        List<BadgeFacts> facts = [];
        foreach (var (disk, file) in local)
        {
            // Where the file's record is: its own path, or where its folder goes back to.
            var record = HomeOf(disk) ?? disk;
            var slash = record.IndexOf('/', StringComparison.Ordinal);
            if (slash <= 0 || !projects.Contains(record[..slash])) continue;
            if (state.Files.TryGetValue(record, out var st) && st.Purged) st = null;
            string status, checkout;
            var inArmory = st?.FileId is not null;
            if (st?.FileId is { } id && remoteById.TryGetValue(id, out var known))
            {
                var remote = known.File;
                var ownership = OwnershipOf(remote.Lock);
                status = StatusOf(st, remote, file, ownership);
                checkout = remote.Deleted ? CheckoutStates.Available : CheckoutOf(remote.Lock).State;
                inArmory = !remote.Deleted;
            }
            // Offline since the start: as this computer last knew it.
            else if (st is { FileId: not null, BaseHash: not null })
            {
                var ownership = KnownOwnership(st);
                status = KnownStatus(st, file, ownership);
                checkout = KnownCheckout(st).State;
            }
            else
            {
                status = StatusOf(st, null, file, LockOwnership.Free);
                checkout = CheckoutStates.Available;
            }
            facts.Add(BadgeFacts.FromNames(disk, status, checkout, inArmory, st?.Refusal is not null,
                unreadable.Contains(record) || unreadable.Contains(disk)));
        }
        return facts;
    }
}
