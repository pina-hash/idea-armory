using Armory.Agent.Engine.View;
using Armory.Core;

namespace Armory.Agent.Engine;

internal enum SaveBlockedReason { NotCheckedOut, OtherStudent, NotYourProject, OtherComputer, ProjectReadOnly }
internal sealed record SaveProtection(SaveBlockedReason Reason, bool CanCheckOut, string? Who = null);
internal sealed record BlockedSave(string Key, string Path, SaveProtection Protection, long LastAttempt);

// Only an explicit SolidWorks save event is evidence. ~$ is an open marker and .swp is
// unmeasured. Neither creation nor a refused write raises a
// reliable FileSystemWatcher save event. No disk, attribute, checkout or SolidWorks calls here.
internal sealed class SaveBlockedDetector(TimeProvider clock)
{
    internal static readonly TimeSpan Burst = TimeSpan.FromSeconds(3);
    internal const int Capacity = 200;
    private readonly Dictionary<string, BlockedSave> files = new(StringComparer.OrdinalIgnoreCase);
    private string? student;

    internal void ForStudent(string? email)
    {
        if (string.Equals(student, email, StringComparison.OrdinalIgnoreCase)) return;
        files.Clear();
        student = email;
    }

    internal bool Attempt(string path, SaveProtection? protection)
    {
        if (student is null || protection is null || !VaultPath.TryCreate(path, out var valid, out _)) return false;
        var now = clock.GetTimestamp();
        files.TryGetValue(valid.Value, out var old);
        var sameBurst = old is not null && clock.GetElapsedTime(old.LastAttempt, now) < Burst;
        if (old is null && files.Count >= Capacity) files.Remove(files.Values.MinBy(f => f.LastAttempt)!.Path);
        files[valid.Value] = new(sameBurst ? old!.Key : "saveBlocked:" + Guid.NewGuid().ToString("N"), valid.Value, protection, now);
        return !sameBurst || old!.Protection != protection;
    }

    internal void Forget(string path) => files.Remove(path);

    internal IReadOnlyList<NoticeGroupView> Cards(Func<string, SaveProtection?> current)
    {
        List<NoticeGroupView> cards = [];
        foreach (var file in files.Values.ToArray())
        {
            if (current(file.Path) is not { } protection) { files.Remove(file.Path); continue; }
            var name = file.Path[(file.Path.LastIndexOf('/') + 1)..];
            var reason = Words(protection);
            var next = protection.CanCheckOut ? "Check out here, then save again in SolidWorks."
                : "In SolidWorks, use Save As to save a copy in the folder this button opens.";
            cards.Add(new(file.Key, NoticeKinds.SaveBlocked, NoticeTones.Look, $"SolidWorks can't save {name}", reason + " " + next, 1,
                new(protection.CanCheckOut ? "Check out" : "Save a copy in your folder", BridgeMessages.SaveBlockedAction, [file.Key]),
                [new(null, file.Path, name, reason)]));
        }
        return cards;
    }

    internal static string Words(SaveProtection p) => p.Reason switch
    {
        SaveBlockedReason.NotCheckedOut => "Armory keeps this file read-only because you have not checked it out.",
        SaveBlockedReason.OtherStudent => $"This file belongs to {p.Who ?? "another student"} and opens read-only for you.",
        SaveBlockedReason.NotYourProject => "Armory keeps this file read-only because it is in a project you are not on.",
        SaveBlockedReason.OtherComputer => "Armory keeps this file read-only because you checked it out on another computer.",
        _ => "Armory keeps this file read-only because this project does not allow you to edit it.",
    };
}

public sealed partial class SyncEngine
{
    private SaveBlockedDetector? saveBlocked;
    private SaveBlockedDetector BlockedSaves
    {
        get
        {
            saveBlocked ??= new(deps.Clock);
            saveBlocked.ForStudent(deps.Sessions.Current?.Email);
            return saveBlocked;
        }
    }

    // Facts already known to the read-only policy. A filename or a temporary file never
    // establishes ownership. A held file can be new work, with no server id yet.
    private SaveProtection? ProtectionOf(string key)
    {
        if (deps.Sessions.Current is not { } session || !SameEmail(state.Email, session.Email)) return null;
        if (HeldRecord(key) is { } held) return new(SaveBlockedReason.OtherStudent, false, DisplayName(held.Email));
        if (!state.Files.TryGetValue(key, out var st) || st.Purged || st.FileId is null) return null;
        var knownRemote = remoteById.TryGetValue(st.FileId.Value, out var remote);
        if (!knownRemote && (st.AppliedOwnership is null || st.BaseHash is null)) return null;
        if (knownRemote && (remote.File.Deleted || remote.File.Current is null)) return null;
        var ownership = knownRemote ? OwnershipOf(remote.File.Lock) : KnownOwnership(st);
        if (ownership == LockOwnership.ThisDevice) return null;
        if (!state.Projects.TryGetValue(st.ProjectId, out var project)) return null;
        if (project.Departed || NotIn(session.Email, project.Id)) return new(SaveBlockedReason.NotYourProject, false);
        if (ownership == LockOwnership.OtherPerson)
            return new(SaveBlockedReason.OtherStudent, false, (knownRemote ? remote.File.Lock?.HolderEmail : st.Holder?.Email) is { } who ? DisplayName(who) : null);
        if (ownership == LockOwnership.MyOtherDevice) return new(SaveBlockedReason.OtherComputer, false);
        if (project.Archived || !project.Usable) return new(SaveBlockedReason.ProjectReadOnly, false);
        return new(SaveBlockedReason.NotCheckedOut, online == true);
    }

    private IReadOnlyList<NoticeGroupView> SaveBlockedCards() => BlockedSaves.Cards(ProtectionOf);

    private void SaveAttempted(VaultPath path)
    {
        if (!fs.IsProtectedReadOnly(path) || !BlockedSaves.Attempt(path.Value, ProtectionOf(path.Value))) return;
        // A server read or long action must not delay this explanation. Reuse the already
        // published view, replacing only our cards; don't build from a half-refreshed server.
        var cards = SaveBlockedCards();
        var next = View with
        {
            Notices = cards.Concat(View.Notices.Where(n => n.Kind != NoticeKinds.SaveBlocked)).ToArray(),
            Prompt = View.Prompt is { } prompt && cards.Any(c => c.Items[0].Path.Equals(prompt.Path, StringComparison.OrdinalIgnoreCase)) ? null : View.Prompt,
        };
        Volatile.Write(ref view, next);
        lastViewJson = BridgeMessages.ViewMessage(next);
        ViewChanged?.Invoke(next);
    }

    // The key is an in-memory capability for this student's current card. The host uses this
    // to recheck the click before checking out or opening a copy folder. No path comes from UI.
    public Task<NoticeActionView?> SaveBlockedActionAsync(string key) => engineThread.InvokeAsync(() =>
    {
        var card = SaveBlockedCards().FirstOrDefault(c => c.Key == key);
        return Task.FromResult(card is null ? null : new NoticeActionView(card.Action!.Label,
            ProtectionOf(card.Items[0].Path)!.CanCheckOut ? BridgeMessages.CheckOut : "copy", [card.Items[0].Path]));
    });
}
