using System.Text.RegularExpressions;
using Armory.Client;
using Armory.Core;

namespace Armory.Agent.Engine;

// The SolidWorks year of a file (docs/agent/ENGINE.md, "The SolidWorks year"). Two sources, for
// the exact bytes in question: the SolidWorks link's stamp (RecordReleaseStamp, kept by content
// hash in EngineState.ReleaseStamps) and the file reader (EngineDependencies.ReleaseReader),
// read once per content hash. Core's SavedReleaseRule combines them; when they disagree the
// year is unknown. The link also says which SolidWorks runs here (SolidWorksAttached), so the
// words say what this computer can do. B5: the team's versions the server never checked (Warn
// mode before the reader) are read from this computer's identical copies after each pass.
public sealed partial class SyncEngine
{
    private static readonly TimeSpan StampsKeptAfterCommit = TimeSpan.FromDays(30);
    private static readonly TimeSpan StampsKeptUncommitted = TimeSpan.FromDays(90);
    // How long a loop pass reads unchecked files (B5) before leaving the rest to the next pass.
    private static readonly TimeSpan ReleaseAuditSlice = TimeSpan.FromSeconds(2);
    // The reader's answer by content hash, for this start (a year is also kept on the file's record).
    private readonly Dictionary<string, SolidWorksRelease?> releases = new(StringComparer.Ordinal);
    // Content hashes whose stamp and reading disagreed, recorded once each.
    private readonly HashSet<string> releaseDisagreements = new(StringComparer.Ordinal);
    // The SolidWorks running on this computer as the link last said, and whether saving down
    // worked there; null while no link is attached.
    private (SolidWorksRevision Revision, bool SaveDownWorks)? solidWorks;

    // ---- What the SolidWorks link tells the engine -------------------------------------------------

    // The link stamped bytes it just saw SolidWorks save (research section 2). A stamp counts only
    // for exactly those bytes (its SHA-256). Fire and forget, like DismissNotice.
    public void RecordReleaseStamp(ReleaseStamp stamp) => _ = RecordReleaseStampAsync(stamp);

    // The same, done when it is kept (saved, unless a pass is running, which saves it). False: no
    // stamp (its hash is not a SHA-256).
    public Task<bool> RecordReleaseStampAsync(ReleaseStamp stamp) => engineThread.InvokeAsync(async () =>
    {
        if (stamp is null || !IsSha256(stamp.Sha256)) return false;
        var hash = stamp.Sha256.ToLowerInvariant();
        stamp = stamp with { Sha256 = hash };
        state.ReleaseStamps.TryGetValue(hash, out var existing);
        var merged = SavedReleaseRule.Merge(existing?.Stamp, stamp);
        state.ReleaseStamps[hash] = new StampRecord(merged, existing?.Committed);
        MarkDirty();
        RequestPublish();
        if (passGate.CurrentCount > 0) await SettleAsync();
        // A file held back as unknown may go now: the next pass decides with the stamp.
        wake.Release();
        return true;
    });

    // The link attached to SolidWorks: its RevisionNumber ("34.4.1"), and false once a save down
    // was asked for and SolidWorks did not do it (no license for it, research section 3.5).
    public void SolidWorksAttached(string revisionNumber, bool saveDownWorks = true) => engineThread.Enqueue(() =>
    {
        solidWorks = SolidWorksRevision.Parse(revisionNumber) is { } revision ? (revision, saveDownWorks) : null;
        RequestPublish();
    });

    public void SolidWorksDetached() => engineThread.Enqueue(() =>
    {
        solidWorks = null;
        RequestPublish();
    });

    // ---- Reading ------------------------------------------------------------------------------

    // The saved release of these bytes (hash), opened by open when they must be read: the stamp
    // and the reader combined. The reader runs off the engine thread; a stream that fails throws.
    private async Task<SolidWorksRelease?> ReleaseOfAsync(FileState? st, string hash, Func<Stream> open, CancellationToken ct)
    {
        var stamp = state.ReleaseStamps.GetValueOrDefault(hash)?.Stamp;
        var parsed = deps.ReleaseReader is null ? null : await ParsedReleaseAsync(st, hash, open, keep: false, ct);
        if (SavedReleaseRule.Disagree(stamp, parsed) && releaseDisagreements.Add(hash))
        {
            flight?.Note("release disagreement", $"{hash[..Math.Min(12, hash.Length)]}: stamp {stamp?.Year}, reader {parsed?.Year}");
            deps.Log?.Invoke($"release: the SolidWorks stamp says {stamp?.Year} and the file says {parsed?.Year} for {hash}; treated as unknown");
        }
        return SavedReleaseRule.Combine(stamp, parsed);
    }

    // What is known now, without reading anything (the view builds from this).
    private SolidWorksRelease? KnownRelease(FileState? st, string hash)
        => SavedReleaseRule.Combine(state.ReleaseStamps.GetValueOrDefault(hash)?.Stamp, CachedRelease(st, hash));

    private SolidWorksRelease? CachedRelease(FileState? st, string hash)
        => releases.TryGetValue(hash, out var known) ? known : st is { ReleaseYear: { } year } && st.ReleaseHash == hash ? new SolidWorksRelease(year) : null;

    // The reader's answer, read at most once per content hash this start. With keep, a year is
    // kept on the file's record too (the team's version, B5), so no later start reads it again;
    // an upload's bytes need not be (the server checks them).
    private async Task<SolidWorksRelease?> ParsedReleaseAsync(FileState? st, string hash, Func<Stream> open, bool keep, CancellationToken ct)
    {
        if (releases.TryGetValue(hash, out var known)) return known;
        if (st is { ReleaseYear: { } year } && st.ReleaseHash == hash) return releases[hash] = new SolidWorksRelease(year);
        var reader = deps.ReleaseReader!;
        SolidWorksRelease? release;
        await using (var stream = open())
            release = await Task.Run(async () => await reader.ReadAsync(stream, ct), ct);
        releases[hash] = release;
        if (keep && st is not null && release is { } found && (st.ReleaseHash != hash || st.ReleaseYear != found.Year))
        {
            st.ReleaseHash = hash;
            st.ReleaseYear = found.Year;
            MarkDirty();
        }
        return release;
    }

    // An upload's release: the bytes this file's capture kept (or the file itself).
    private Task<SolidWorksRelease?> ReadReleaseAsync(FileState st, VaultPath path, string hash, CancellationToken ct)
    {
        var snapshot = SnapshotFor(st, hash);
        return ReleaseOfAsync(st, hash, () => snapshot is not null ? deps.Snapshots.OpenRead(snapshot.Id) : fs.OpenRead(path), ct);
    }

    // ---- Stamps ---------------------------------------------------------------------------------

    // Those bytes reached the server from here: their stamp is kept 30 more days.
    private void StampCommitted(string? hash)
    {
        if (hash is null || !state.ReleaseStamps.TryGetValue(hash, out var record) || record.Committed is not null) return;
        state.ReleaseStamps[hash] = record with { Committed = deps.Clock.GetUtcNow() };
        MarkDirty();
    }

    private void PruneStamps()
    {
        if (state.ReleaseStamps.Count == 0) return;
        var now = deps.Clock.GetUtcNow();
        foreach (var (hash, record) in state.ReleaseStamps.ToArray())
            if (record.Committed is { } committed ? now - committed > StampsKeptAfterCommit : now - record.Stamp.At > StampsKeptUncommitted)
            {
                state.ReleaseStamps.Remove(hash);
                MarkDirty();
            }
    }

    private static bool IsSha256(string? hash) => hash is { Length: 64 } && Sha256Text().IsMatch(hash);

    [GeneratedRegex("^[0-9a-fA-F]{64}$")]
    private static partial Regex Sha256Text();

    // ---- B5: the team's versions saved before the reader -------------------------------------------

    // After a pass: every SolidWorks file whose current server version was never checked (Warn
    // mode before this reader) and whose copy here is those same bytes is read, once per content
    // hash. The view then shows its year, and a year above the project's pin is a notice. A loop
    // pass gives way to a window action and reads for at most ReleaseAuditSlice; the next pass
    // goes on from there. A window action's own pass does not read them.
    private async Task AuditReleasesAsync(CancellationToken ct)
    {
        if (deps.ReleaseReader is null) return;
        var started = deps.Clock.GetTimestamp();
        foreach (var (_, project, path, current) in UncheckedTeamVersions())
        {
            ct.ThrowIfCancellationRequested();
            if (Fenced(path.Value) || !TryLocal(path.Value, out var file) || file.Hash != current.Hash) continue;
            state.Files.TryGetValue(path.Value, out var st);
            if (releases.ContainsKey(current.Hash) || (st is { ReleaseYear: not null } && st.ReleaseHash == current.Hash)) continue;
            if (loopPass && (actionsWaiting > 0 || deps.Clock.GetElapsedTime(started) >= ReleaseAuditSlice)) return;
            try { await ParsedReleaseAsync(st, current.Hash, () => fs.OpenRead(file.Path), keep: true, ct); }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException)
            {
                // Open in another program, or gone since the scan: read on a later pass.
                deps.Log?.Invoke($"release: couldn't read {path.Value}: {error.Message}");
            }
        }
    }

    // The team's current SolidWorks versions the server never checked, with where they are here.
    private IEnumerable<(RemoteFile File, ProjectState Project, VaultPath Path, RemoteVersion Current)> UncheckedTeamVersions()
    {
        foreach (var (file, project, path) in remoteById.Values.ToArray())
            if (!file.Deleted && file.Current is { ReleaseChecked: not true } current && Reconciler.IsSolidWorks(path) && project.Usable && !project.Archived)
                yield return (file, project, path, current);
    }

    // The year the team's current version of a file was saved in, as this computer knows it: the
    // server's own check, else what was read here in those very bytes (B5: an identical copy;
    // the reading stays known by its hash after the copy here changes). Never reads.
    private int? TeamRelease(RemoteFile remote, VaultPath path, FileState? st)
    {
        if (remote.Deleted || remote.Current is not { } current || !Reconciler.IsSolidWorks(path)) return null;
        if (current.ReleaseChecked == true) return current.SavedRelease;
        return KnownRelease(st, current.Hash)?.Year;
    }

    // ---- Words --------------------------------------------------------------------------------

    // The refusal of a file saved in a SolidWorks newer than the project's pin (research section
    // 6): what this computer can do about it. A private draft never uploads; it waits here.
    internal static string NewerThanPinWords(int saved, int pin, string project, SolidWorksRevision? running, bool saveDownWorks)
    {
        var first = $"Saved in SolidWorks {saved}, and {project} uses SolidWorks {pin}.";
        var waits = $"{first} It stays on this computer only until it is saved in SolidWorks {pin}.";
        if (running is not { } sw || saved > sw.Year) return waits;
        var plan = SaveDown.Plan(sw, pin);
        if (plan.CanSave() && saveDownWorks)
            return $"{first} Open it in SolidWorks {sw.Year} and click Save: Armory saves it as {pin}, then it uploads by itself.";
        return plan switch
        {
            SaveDownPlan.Penultimate or SaveDownPlan.Antepenultimate => $"{waits} SolidWorks on this computer couldn't save it in {pin}. Ask a CAD lead or a mentor what to do.",
            SaveDownPlan.OldServicePack => $"{waits} Update SolidWorks {sw.Year} to Service Pack 3 or newer so Armory can save it in {pin}.",
            SaveDownPlan.TooFarApart => $"{waits} SolidWorks {sw.Year} can't save files as {pin}.",
            _ => waits,
        };
    }

    // The notice's detail for files in one project saved in a newer SolidWorks (B5, research
    // section 6), for what this computer is, and whether it needs the student here: yes on a
    // computer that can fix them, and while no SolidWorks link says what this computer is; news
    // on a computer the link says cannot.
    internal static (string Detail, bool NeedsYou) NewerThanPinNotice(int saved, int pin, SolidWorksRevision? running, bool saveDownWorks)
    {
        if (running is { } here && saved <= here.Year && SaveDown.Plan(here, pin).CanSave() && saveDownWorks)
            return ($"People on SolidWorks {pin} computers can't change these files. You can fix them here: 1. Check one out in Armory. " +
                $"2. Open it in SolidWorks {here.Year}. 3. Click Save. Armory saves it as SolidWorks {pin} for you. 4. Check it in.", true);
        if (running is { } pinned && pinned.Year == pin)
            return ($"You can open parts and assemblies to look (SolidWorks {pin} SP5 shows them as a future version), but you can't change them here, and drawings " +
                $"won't open. Someone with SolidWorks {saved} can fix them: check it out, open it, click Save, and check it in.", false);
        return ($"People on SolidWorks {pin} computers can look at these files but can't change them, and drawings won't open. Someone with SolidWorks {saved} " +
            $"can fix them: check it out in Armory, open it in SolidWorks {saved}, click Save so Armory saves it as SolidWorks {pin}, then check it in.", running is null);
    }
}
