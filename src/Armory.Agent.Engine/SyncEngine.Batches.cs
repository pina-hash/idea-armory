using System.Globalization;
using Armory.Agent.Engine.View;
using Armory.Client;
using Armory.Core;

namespace Armory.Agent.Engine;

// v0.3 batches (ARMORY.md, item 6): several check outs take their locks with armory_lock_files and
// several check ins, undos and adds let theirs go with armory_release_locks, at most 500 distinct
// files a call (ArmoryApi.Chunk). The answer is {total, succeeded, refused, results}: each file is
// applied on its own, so what landed is never reported as failed, and each refused file says why.
//
// Crash safety is the single-file path's: every file's own in-flight record (its own operation id)
// is saved, all together, before the call. The batch's operation id derives from those ids, so it
// is minted once for what the student asked and a resend (PostgrestClient resends 40P01 and 40001
// itself, up to 3 times, with the same body) answers from the server's receipt. A stop before the
// answers are applied re-sends each lock alone with its own id (taking a lock this device already
// holds answers true); a release in flight is dropped and decided again, as always. A site
// without the batch RPCs (404 PGRST202) gets the files one by one, and is asked again in an hour.
public sealed partial class SyncEngine
{
    private static readonly TimeSpan BatchesMissingRetry = TimeSpan.FromHours(1);
    private DateTimeOffset batchesMissingUntil = DateTimeOffset.MinValue;
    // Why each check out a batch refused was refused, in the window's words.
    private readonly Dictionary<FileState, string> checkOutRefusals = new(ReferenceEqualityComparer.Instance);

    private bool BatchesAvailable => deps.Clock.GetUtcNow() >= batchesMissingUntil;

    private void BatchesMissing(string rpc)
    {
        batchesMissingUntil = deps.Clock.GetUtcNow() + BatchesMissingRetry;
        deps.Log?.Invoke($"batches: the site has no {rpc}; files go one by one, and it is asked again in {BatchesMissingRetry.TotalMinutes:0} minutes");
    }

    // The check outs whose lock is due now, in batches.
    private async Task LockBatchAsync(List<(FileState State, VaultPath Path)> files, CancellationToken ct)
    {
        var device = state.DeviceId!.Value;
        var flights = new Dictionary<Guid, (FileState State, VaultPath Path, Inflight Flight)>();
        foreach (var (st, path) in files)
        {
            Proceed(ct);
            if (st.FileId is not { } id || st.CheckOut is null || flights.ContainsKey(id)) continue;
            var flight = LockFlight(st, st.CheckOut);
            st.Inflight = flight;
            flights[id] = (st, path, flight);
        }
        if (flights.Count == 0) return;
        await FlushAsync(); // every file's in-flight record is on disk before the call
        var chunks = ArmoryApi.Chunk(flights.Keys);
        activity.Log($"Asking the server to check out {Count(flights.Count, "file", "files")}");
        var answered = 0;
        for (var i = 0; i < chunks.Count; i++)
        {
            var chunk = chunks[i];
            if (online != true) return; // the records stay; each lock is re-sent alone on the next pass
            Proceed(ct);
            wrote = true;
            foreach (var id in chunk) projectsWritten.Add(flights[id].State.ProjectId);
            var operation = OperationIds.Derive(["lock batch", .. chunk.Select(id => flights[id].Flight.Operation.ToString())]);
            Checkpoint("before-lock-batch", ct);
            BatchResult answer;
            try { answer = await deps.Api.LockFilesAsync(chunk, device, operation, ct); }
            catch (ArmoryOfflineException) { online = false; return; }
            catch (ArmoryRpcException error) when (error.IsFunctionMissing)
            {
                BatchesMissing("armory_lock_files");
                await LockOneByOneAsync(chunks.Skip(i).SelectMany(c => c).Select(id => flights[id]).ToList(), ct);
                return;
            }
            catch (ArmoryRpcException error)
            {
                // The call itself was refused (never expected: a bug). Nothing landed; each file's
                // check out is answered and can be asked again.
                deps.Log?.Invoke($"batches: armory_lock_files refused the whole call ({error.SqlState}{(error.Reason is { } reason ? " " + reason : "")}): {error.Message}");
                foreach (var id in chunk) RefuseCheckOut(flights[id].State, flights[id].Path, flights[id].Flight, error.SqlState, error.Message);
                continue;
            }
            Proceed(ct); // an answer that arrives after a crash elsewhere is dropped, its records kept
            Checkpoint("after-lock-batch", ct);
            foreach (var result in answer.Results)
            {
                if (!flights.TryGetValue(result.FileId, out var f) || !ReferenceEquals(f.State.Inflight, f.Flight)) continue;
                var (st, path, _) = f;
                if (!result.Ok)
                {
                    RefuseCheckOut(st, path, f.Flight, result.Code, result.Message);
                    continue;
                }
                st.Inflight = null;
                st.Attempt++; // every answer spends the attempt
                if (result.Done)
                {
                    KnowLock(result.FileId, new RemoteLock(state.Email!, device, deps.Sessions.Current?.DeviceName, deps.Clock.GetUtcNow(), null, null, null, null));
                    SetAttribute(path, st, LockOwnership.ThisDevice);
                    Answer(st, CheckOutOutcome.Done);
                }
                else Answer(st, CheckOutOutcome.Held); // someone else checked it out first
            }
            answered += chunk.Length;
            activity.Log($"Checked out {answered:N0} of {Count(flights.Count, "file", "files")}");
            // A file the answer left out keeps its record: its lock is re-sent alone on the next pass.
            MarkDirty();
        }
    }

    // A site without armory_lock_files: each lock alone, its record already on disk.
    private async Task LockOneByOneAsync(List<(FileState State, VaultPath Path, Inflight Flight)> files, CancellationToken ct)
        => await RunConcurrentlyAsync(files, async (f, token) =>
        {
            try
            {
                if (!ReferenceEquals(f.State.Inflight, f.Flight)) return;
                if (await SendDurableAsync(f.State, f.Flight, token))
                {
                    SetAttribute(f.Path, f.State, LockOwnership.ThisDevice);
                    Answer(f.State, CheckOutOutcome.Done);
                }
                else Answer(f.State, CheckOutOutcome.Held);
            }
            catch (ArmoryOfflineException) { online = false; }
            catch (Exception error) when (error is ArmoryClientException or IOException or UnauthorizedAccessException) { FileProblem(f.State.Path, error); }
            catch (Exception error) when (StopSaving(error)) { throw; }
        }, notStarted: null, ct);

    // One check out a batch refused: nothing was written for it, so its record is cleared, and the
    // answer and a notice say why, in the server's words where there are no plainer ones.
    private void RefuseCheckOut(FileState st, VaultPath path, Inflight flight, string? code, string? message)
    {
        if (ReferenceEquals(st.Inflight, flight)) st.Inflight = null;
        var words = BatchRefusalWords(st, code, message);
        checkOutRefusals[st] = words;
        Problem(NoticeKinds.CantSend, st.Path, words, $"armory_lock_files refused {path}: {code} {message}", $"Armory couldn't check out {path.Name}");
        if (message == ArmoryRpcException.NotMemberMessage && code is "P0001" or "42501")
            NoteNotMember(st.ProjectId, new ArmoryRpcException(400, code, message, null, null));
        Answer(st, CheckOutOutcome.Refused);
    }

    // The releases ready now, in batches, one device at a time (a lock taken under a former device
    // id is let go under it). Returns what still goes one by one (a site without the batch RPC).
    private async Task<List<(FileState State, Inflight Flight)>> ReleaseBatchAsync(List<(FileState State, Inflight Flight)> releases, CancellationToken ct)
    {
        List<(FileState State, Inflight Flight)> rest = [];
        foreach (var group in releases.Where(r => r.Flight.FileId is not null).GroupBy(r => r.Flight.Device ?? state.DeviceId!.Value).ToArray())
        {
            var byFile = new Dictionary<Guid, (FileState State, Inflight Flight)>();
            foreach (var release in group) byFile.TryAdd(release.Flight.FileId!.Value, release);
            var chunks = ArmoryApi.Chunk(byFile.Keys);
            // A check in, an undo, or a lock taken only for a move let go: one plain word for all.
            var verb = byFile.Values.All(r => r.State.Request == CheckoutRequest.Undo) ? "Undid" : byFile.Values.Any(r => r.State.Request == CheckoutRequest.CheckIn) ? "Checked in" : "Let go of";
            var released = 0;
            for (var i = 0; i < chunks.Count; i++)
            {
                var chunk = chunks[i];
                if (online != true || !BatchesAvailable)
                {
                    rest.AddRange(chunks.Skip(i).SelectMany(c => c).Select(id => byFile[id]));
                    break;
                }
                Proceed(ct);
                wrote = true;
                foreach (var id in chunk) projectsWritten.Add(byFile[id].State.ProjectId);
                var operation = OperationIds.Derive(["release batch", .. chunk.Select(id => byFile[id].Flight.Operation.ToString())]);
                Checkpoint("before-release-batch", ct);
                BatchResult answer;
                try { answer = await deps.Api.ReleaseLocksAsync(chunk, group.Key, operation, ct); }
                catch (ArmoryOfflineException)
                {
                    online = false;
                    rest.AddRange(chunks.Skip(i).SelectMany(c => c).Select(id => byFile[id]));
                    break;
                }
                catch (ArmoryRpcException error) when (error.IsFunctionMissing)
                {
                    BatchesMissing("armory_release_locks");
                    rest.AddRange(chunks.Skip(i).SelectMany(c => c).Select(id => byFile[id]));
                    break;
                }
                catch (ArmoryRpcException error)
                {
                    deps.Log?.Invoke($"batches: armory_release_locks refused the whole call ({error.SqlState}{(error.Reason is { } reason ? " " + reason : "")}): {error.Message}");
                    foreach (var id in chunk) RefuseRelease(byFile[id].State, byFile[id].Flight, error.SqlState, error.Message);
                    continue;
                }
                Proceed(ct);
                Checkpoint("after-release-batch", ct);
                foreach (var result in answer.Results)
                {
                    if (!byFile.TryGetValue(result.FileId, out var r) || !ReferenceEquals(r.State.Inflight, r.Flight)) continue;
                    var st = r.State;
                    if (!result.Ok)
                    {
                        RefuseRelease(st, r.Flight, result.Code, result.Message);
                        continue;
                    }
                    // Released, or not held any more (taken back meanwhile): either way it is let go.
                    st.Inflight = null;
                    KnowLock(result.FileId, null);
                    if (st.Request != CheckoutRequest.None) releaseResults[st] = ReleaseOutcome.Released;
                    LetGoDone(st);
                    activity.Done(ActivityTracker.CheckIn, st.Path);
                    activity.Drop(st.Path);
                    MarkDirty();
                }
                released += chunk.Length;
                activity.Log($"{verb} {released:N0} of {Count(byFile.Count, "file", "files")}");
                // A file the answer left out is decided again on the next pass.
                foreach (var id in chunk)
                    if (ReferenceEquals(byFile[id].State.Inflight, byFile[id].Flight)) { byFile[id].State.Inflight = null; activity.Drop(byFile[id].State.Path); MarkDirty(); }
                viewWanted = true;
                PublishSoon();
            }
        }
        rest.AddRange(releases.Where(r => r.Flight.FileId is null));
        return rest;
    }

    // One release a batch refused: as a refused single release, the request stays and the next
    // pass tries again; the notice says why.
    private void RefuseRelease(FileState st, Inflight flight, string? code, string? message)
    {
        if (ReferenceEquals(st.Inflight, flight)) st.Inflight = null;
        Problem(NoticeKinds.CantSend, st.Path, BatchRefusalWords(st, code, message) + " Armory tries again by itself.",
            $"armory_release_locks refused {st.Path}: {code} {message}", $"Armory couldn't finish a change to {NameOf(st.Path)}");
        if (message == ArmoryRpcException.NotMemberMessage && code is "P0001" or "42501")
            NoteNotMember(st.ProjectId, new ArmoryRpcException(400, code, message, null, null));
        activity.Drop(st.Path);
        MarkDirty();
    }

    // A refused file in the window's words: plainer words for what the server is known to say,
    // else the server's own message (ARMORY.md item 6: report per file, using its message).
    private string BatchRefusalWords(FileState st, string? code, string? message) => message switch
    {
        ArmoryRpcException.NotMemberMessage => $"You may no longer be in {state.Projects.GetValueOrDefault(st.ProjectId)?.Name ?? "this project"}. Ask your CAD lead.",
        "device is not registered to caller" => "This computer's sign-in changed. Connect it again in Settings.",
        null or "" => $"The server refused it ({code ?? "no code"}).",
        _ => $"The server said: {message.TrimEnd('.')}.",
    };

    // The refused check outs of a batch, for the action's one sentence: " Plate.SLDPRT and Gear.SLDPRT
    // were refused: the server said ...". Up to three are named.
    private string RefusedWords(List<(FileState State, VaultPath Path)> targets)
    {
        var refused = targets.Where(t => checkOutRefusals.ContainsKey(t.State)).ToList();
        if (refused.Count == 0) return "";
        var named = refused.Take(3).Select(t => $"{t.Path.Name} ({checkOutRefusals[t.State].TrimEnd('.')})").ToList();
        var more = refused.Count > 3 ? $" and {(refused.Count - 3).ToString("N0", CultureInfo.InvariantCulture)} more" : "";
        return $" {Count(refused.Count, "file was", "files were")} refused: {string.Join("; ", named)}{more}.";
    }
}
