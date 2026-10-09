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
            // Made writable together once the chunk's answers are in (one manifest write).
            List<(FileState State, VaultPath Path)> taken = [];
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
                    taken.Add((st, path));
                    Answer(st, CheckOutOutcome.Done);
                }
                else Answer(st, CheckOutOutcome.Held); // someone else checked it out first
            }
            if (taken.Count > 0) SetAttributes(taken, LockOwnership.ThisDevice);
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
                    Released(st);
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

    // ---- Force check in of many files (v0.3.1, idea-app 0234: armory_break_locks) ----------------
    //
    // At most 500 files a call, in id order (ArmoryApi.Chunk), each call under an operation id
    // derived from the check outs it ends (each file's own Force check in id) and this computer,
    // so asking again for the same check outs answers from the server's receipt. Each call's
    // record (PendingForceCheckIn) is saved before it is sent and dropped once its answer is
    // applied; a stop in between is finished by the next online pass (ResumeForceCheckInsAsync).
    // PostgrestClient resends a whole call the server rolled back with 40P01 or 40001 (up to 3
    // times, the same body), as for the other batches; a file the batch answers with 40P01 or
    // 40001 (its savepoint rolled back) goes again in a later call, up to as many rounds. A site
    // without armory_break_locks (404 PGRST202) gets one armory_break_lock per file and is asked
    // for the batch again in an hour. Crash points: before-break-batch, after-break-batch.

    // One check out a Force check in ends: the file, its project, and the check out as last read.
    private readonly record struct TakeBackTarget(Guid Id, ProjectState Project, RemoteLock Held);

    private DateTimeOffset breakBatchMissingUntil = DateTimeOffset.MinValue;
    private bool BreakBatchAvailable => deps.Clock.GetUtcNow() >= breakBatchMissingUntil;

    private void BreakBatchMissing()
    {
        breakBatchMissingUntil = deps.Clock.GetUtcNow() + BatchesMissingRetry;
        deps.Log?.Invoke($"batches: the site has no {ArmoryApi.BreakLocksRpc}; a force check in goes file by file, and it is asked again in {BatchesMissingRetry.TotalMinutes:0} minutes");
    }

    // What a Force check in of many files came to, for its one sentence.
    private sealed class TakeBackTally(int total)
    {
        public int Total { get; } = total;
        public List<Guid> Broken { get; } = [];
        // Files read again in the one pass after: the server would not take them back as asked.
        public List<Guid> Reread { get; } = [];
        // Not checked out any more (broken false), a role refusal, no longer a member, still busy
        // after every round (40P01 or 40001), refused for another reason (with the first reason).
        public int Gone, RefusedRole, NotMember, Busy, Refused;
        public string? RefusedWhy;
        public bool Offline;

        public string Sentence(int notOut, int notAllowed, int mine)
        {
            var explained = Broken.Count + Gone + RefusedRole + NotMember + Busy + Refused == Total;
            var message = !explained ? $"Force checked in {Broken.Count:N0} of {Count(Total, "file", "files")}."
                : Broken.Count > 0 ? $"Force checked in {Count(Broken.Count, "file", "files")}." : "No files were force checked in.";
            if (Broken.Count > 0) message += " Anything that wasn't checked in is kept as its holder's own copy.";
            if (Gone + notOut > 0) message += $" {Count(Gone + notOut, "file wasn't", "files weren't")} checked out any more.";
            if (RefusedRole + notAllowed > 0) message += $" {Count(RefusedRole + notAllowed, "file is", "files are")} in a project where only a mentor or CAD lead can force a check in.";
            if (mine > 0) message += $" {Count(mine, "file is", "files are")} checked out by you: check {(mine == 1 ? "it" : "them")} in instead.";
            if (NotMember > 0) message += $" {Count(NotMember, "file is", "files are")} in a project you may no longer be in.";
            if (Busy > 0) message += $" {Count(Busy, "file was", "files were")} busy on the server: try {(Busy == 1 ? "it" : "them")} again in a moment.";
            if (Refused > 0) message += $" {Count(Refused, "file was", "files were")} refused: {(RefusedWhy ?? "The server refused it.").TrimEnd('.')}.";
            if (Offline) message += " You went offline: try again once this computer is back online to finish the rest.";
            return message;
        }
    }

    // The targets in batches. Returns the files still to go one by one (a site without the batch).
    private async Task<List<TakeBackTarget>> BreakLocksInBatchesAsync(List<TakeBackTarget> targets, TakeBackTally tally, CancellationToken ct)
    {
        var device = state.DeviceId!.Value;
        var byId = targets.ToDictionary(t => t.Id);
        List<Guid> pending = [.. byId.Keys];
        // Each busy file's last call (the operation that answered it 40P01 or 40001): a later
        // round's id is chained from it, so it is new for every ask. Built from the round number
        // alone, a second ask's rounds would rebuild the first ask's ids and the server would
        // answer its old busy receipt instead of trying.
        Dictionary<Guid, Guid> busyFrom = [];
        for (var round = 0; pending.Count > 0; round++)
        {
            List<Guid> again = [];
            var chunks = ArmoryApi.Chunk(pending);
            for (var i = 0; i < chunks.Count; i++)
            {
                var chunk = chunks[i];
                // The first round's id is the same whenever the same check outs are asked again.
                string[] parts = round == 0
                    ? ["take back batch", .. chunk.Select(id => TakeBackOperation(id, byId[id].Held).ToString())]
                    : ["take back batch, again", .. chunk.Select(id => busyFrom[id].ToString() + ":" + TakeBackOperation(id, byId[id].Held).ToString())];
                var record = new PendingForceCheckIn(OperationIds.Derive(parts), device,
                    [.. chunk.Select(id => new ForcedCheckOut(id, byId[id].Held.HolderDeviceId, byId[id].Held.AcquiredAt))]);
                state.ForceCheckIns.RemoveAll(r => r.Operation == record.Operation);
                state.ForceCheckIns.Add(record);
                MarkDirty();
                await FlushAsync(); // the record is on disk before the call
                CrashAt("before-break-batch");
                foreach (var id in chunk) projectsWritten.Add(byId[id].Project.Id);
                BatchResult answer;
                // A click sends it without the pass gate (0.3.3): a pass meanwhile never sends it too.
                breaksSending.Add(record.Operation);
                try { answer = await deps.Api.BreakLocksAsync(chunk, device, record.Operation, ct); }
                catch (ArmoryOfflineException)
                {
                    // The record stays: the next online pass finishes this call if nothing changed.
                    breaksSending.Remove(record.Operation);
                    online = false;
                    tally.Offline = true;
                    return [];
                }
                catch (ArmoryRpcException error) when (error.IsFunctionMissing)
                {
                    ForgetForceCheckIn(record);
                    BreakBatchMissing();
                    return [.. chunks.Skip(i).SelectMany(c => c).Concat(again).Select(id => byId[id])];
                }
                catch (ArmoryRpcException error)
                {
                    // The whole call was refused (this computer's device is not the caller's, or
                    // still a deadlock after PostgrestClient's resends): nothing landed.
                    ForgetForceCheckIn(record);
                    deps.Log?.Invoke($"batches: {ArmoryApi.BreakLocksRpc} refused the whole call ({error.SqlState}{(error.Reason is { } reason ? " " + reason : "")}): {error.Message}");
                    if (error.IsTransient) tally.Busy += chunk.Length;
                    else
                    {
                        tally.Refused += chunk.Length;
                        tally.RefusedWhy ??= ForceCheckInRefusalWords(error.SqlState, error.Message);
                    }
                    continue;
                }
                CrashAt("after-break-batch");
                var answered = new HashSet<Guid>();
                foreach (var result in answer.Results)
                {
                    if (!byId.TryGetValue(result.FileId, out var t) || !answered.Add(result.FileId)) continue;
                    if (result.Ok)
                    {
                        // broken false: nobody had it checked out any more.
                        if (result.Done) tally.Broken.Add(t.Id); else tally.Gone++;
                        continue;
                    }
                    if (result.Code is "40P01" or "40001")
                    {
                        // Its savepoint rolled back: nothing was written for it.
                        if (round < PostgrestClient.MaximumResends)
                        {
                            again.Add(t.Id);
                            busyFrom[t.Id] = record.Operation;
                        }
                        else tally.Busy++;
                    }
                    else if (result.Message == ArmoryRpcException.NotMemberMessage && result.Code is "P0001" or "42501")
                    {
                        NoteNotMember(t.Project.Id, new ArmoryRpcException(400, result.Code, result.Message, null, null));
                        tally.NotMember++;
                    }
                    else if (IsTakeBackRoleRefusal(result.Code, result.Message)) tally.RefusedRole++;
                    else
                    {
                        tally.Refused++;
                        tally.RefusedWhy ??= ForceCheckInRefusalWords(result.Code, result.Message);
                        tally.Reread.Add(t.Id);
                        deps.Log?.Invoke($"take back: {t.Id}: {result.Code} {result.Message}");
                    }
                }
                // A file the answer left out (never expected) is read again and counted as refused.
                foreach (var id in chunk.Where(id => !answered.Contains(id)))
                {
                    tally.Refused++;
                    tally.Reread.Add(id);
                }
                ForgetForceCheckIn(record);
                ForcedSoFar(tally);
            }
            pending = again;
        }
        return [];
    }

    private void ForgetForceCheckIn(PendingForceCheckIn record)
    {
        breaksSending.Remove(record.Operation);
        state.ForceCheckIns.Remove(record);
        MarkDirty();
    }

    // The Force check in calls a click is sending right now, which no pass sends again.
    private readonly HashSet<Guid> breaksSending = [];

    // A file a Force check in could not end, in the window's words.
    private static string ForceCheckInRefusalWords(string? code, string? message) => message switch
    {
        "device is not registered to caller" => "this computer's sign-in changed. Connect it again in Settings",
        null or "" => $"the server refused it ({code ?? "no code"})",
        _ => $"the server said {message.TrimEnd('.')}",
    };

    // A Force check in a stop left in flight (its record saved before its call): sent again with
    // the same operation id, but only while every file in it still has exactly the check out it
    // named. Then the call never landed (it does now, as asked), or landed with only refusals
    // (the receipt answers them again and writes nothing). Otherwise it landed, or someone acted
    // since: dropped, and anything still checked out is the mentor's to ask about again. So a
    // check out nobody asked about is never ended. Returns true when anything was sent.
    private async Task<bool> ResumeForceCheckInsAsync(CancellationToken ct)
    {
        var sent = false;
        foreach (var record in state.ForceCheckIns.ToArray())
        {
            if (breaksSending.Contains(record.Operation)) continue;
            var unchanged = record.Files.Length > 0 && record.Files.All(f => remoteById.TryGetValue(f.FileId, out var r) && !r.File.Deleted &&
                r.File.Lock is { IsLive: true } held && held.HolderDeviceId == f.HolderDevice && held.AcquiredAt.UtcTicks == f.AcquiredAt.UtcTicks);
            if (!unchanged || !BreakBatchAvailable)
            {
                ForgetForceCheckIn(record);
                deps.Log?.Invoke($"take back: a force check in of {Count(record.Files.Length, "file", "files")} a stop interrupted is not sent again: its check outs changed since");
                continue;
            }
            CrashAt("before-break-batch");
            BatchResult answer;
            try { answer = await deps.Api.BreakLocksAsync([.. record.Files.Select(f => f.FileId)], record.Device, record.Operation, ct); }
            catch (ArmoryOfflineException) { online = false; return sent; }
            catch (ArmoryRpcException error)
            {
                if (error.IsFunctionMissing) BreakBatchMissing();
                ForgetForceCheckIn(record);
                deps.Log?.Invoke($"take back: a force check in a stop interrupted was refused ({error.SqlState}): {error.Message}");
                continue;
            }
            CrashAt("after-break-batch");
            sent = true;
            foreach (var result in answer.Results.Where(r => r.Ok && r.Done)) KnowLock(result.FileId, null);
            foreach (var project in record.Files.Select(f => remoteById[f.FileId].Project.Id).Distinct()) staleProjects.Add(project);
            ForgetForceCheckIn(record);
            deps.Log?.Invoke($"take back: finished a force check in a stop interrupted: {answer.Results.Count(r => r.Ok && r.Done)} of {Count(record.Files.Length, "file", "files")} force checked in");
        }
        return sent;
    }
}
