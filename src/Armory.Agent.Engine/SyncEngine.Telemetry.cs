using System.Text.Json.Nodes;
using Armory.Agent.Engine.View;
using Armory.Core;
using Armory.Telemetry;

namespace Armory.Agent.Engine;

// What the engine tells the flight recorder and the incident files (docs/agent/TELEMETRY.md).
// Everything here runs on the engine thread; recording is a few dozen nanoseconds and the
// snapshot is built only when an incident is saved.
public sealed partial class SyncEngine
{
    private const int MaximumNoticesRecorded = 200;
    private const int PassesKept = 3;
    private readonly FlightRecorder? flight;
    private long phaseStarted;
    private int noticesRecorded;
    // Files that failed and have not gone through since (FileRecovered ends a row of failures).
    private readonly HashSet<string> failedFiles = new(StringComparer.OrdinalIgnoreCase);
    private readonly Queue<JsonObject> recentPasses = new();

    // One phase of a pass ended: how long it took since the last one.
    private void Phase(string name)
    {
        if (flight is null) return;
        flight.PassPhase(name, flight.MillisecondsSince(phaseStarted));
        phaseStarted = flight.Now();
    }

    private void RecordPass(string kind, bool failed)
    {
        var ms = (long)deps.Clock.GetElapsedTime(passStarted).TotalMilliseconds;
        flight?.PassEnd(kind, failed, ms, downloaded, uploaded, sideVersions, refused);
        if (flight is null) return;
        recentPasses.Enqueue(new JsonObject
        {
            ["endedAt"] = FlightJson.Time(deps.Clock.GetUtcNow()),
            ["pass"] = kind,
            ["ok"] = !failed,
            ["ms"] = ms,
            ["moving"] = passMoving,
            ["downloaded"] = downloaded,
            ["uploaded"] = uploaded,
            ["keptCopies"] = sideVersions,
            ["refused"] = refused,
            ["problems"] = problems.Count,
            ["cutShort"] = cutShort,
        });
        while (recentPasses.Count > PassesKept) recentPasses.Dequeue();
    }

    // The engine's compact picture for an incident: counts by file status, the check out
    // requests waiting, the check outs this computer holds, whether it is online, the last
    // three passes and the settings (which hold no secret). File names and paths, never file
    // contents. Built on the engine thread; a caller that cannot wait gives up at its deadline.
    public Task<JsonObject> DescribeAsync(CancellationToken cancellationToken = default) => engineThread.InvokeAsync(() =>
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(DescribeNow());
    });

    private JsonObject DescribeNow()
    {
        var current = View;
        var described = DescribeView(current);
        var requests = new JsonObject();
        int checkOuts = 0, checkIns = 0, undos = 0, autoCheckIns = 0, transient = 0, moves = 0, inflight = 0, journaled = 0;
        foreach (var st in state.Files.Values)
        {
            if (st.CheckOut is not null) checkOuts++;
            if (st.Request == CheckoutRequest.CheckIn) checkIns++;
            if (st.Request == CheckoutRequest.Undo) undos++;
            if (st.AutoCheckIn) autoCheckIns++;
            if (st.TransientLock) transient++;
            if (st.LocalMoveTo is not null) moves++;
            if (st.Inflight is not null) inflight++;
            journaled += st.Entries.Count;
        }
        requests["checkOut"] = checkOuts;
        requests["checkIn"] = checkIns;
        requests["undo"] = undos;
        requests["checkInWhenClosed"] = autoCheckIns;
        requests["moveOrRemoval"] = transient;
        requests["localMoves"] = moves;
        requests["writesInFlight"] = inflight;
        requests["savesWaiting"] = journaled;
        described["engine"] = new JsonObject
        {
            ["online"] = online,
            ["lastOnline"] = lastOnline is { } at ? FlightJson.Time(at) : null,
            ["paused"] = paused,
            ["syncing"] = syncing,
            ["inPass"] = inPass,
            ["cutShort"] = cutShort,
            ["actionsWaiting"] = actionsWaiting,
            ["lastLoopError"] = lastLoopError,
            ["records"] = state.Files.Count,
            ["projects"] = new JsonArray(state.Projects.Values.Select(p => (JsonNode)new JsonObject
            {
                ["id"] = p.Id.ToString(),
                ["name"] = p.Name,
                ["folder"] = p.Folder,
                ["role"] = p.Role,
                ["archived"] = p.Archived,
                ["usable"] = p.Usable,
                ["putBackFrom"] = p.PutBackFrom,
            }).ToArray()),
        };
        described["pendingRequests"] = requests;
        described["lastPasses"] = new JsonArray(recentPasses.Select(p => (JsonNode)p.DeepClone()).ToArray());
        return described;
    }

    // What the window's view says, which any thread may read (the published view is immutable):
    // the connection, the status line, files by status and the check outs held here.
    public static JsonObject DescribeView(AgentView view)
    {
        var byStatus = new SortedDictionary<string, int>(StringComparer.Ordinal);
        var files = 0;
        foreach (var project in view.Projects)
            foreach (var folder in project.Folders)
                foreach (var file in folder.Files)
                {
                    files++;
                    byStatus[file.Status] = byStatus.GetValueOrDefault(file.Status) + 1;
                }
        var statuses = new JsonObject();
        foreach (var (status, count) in byStatus) statuses[status] = count;
        return new JsonObject
        {
            ["connection"] = view.Connection,
            ["connectPhase"] = view.Connect.Phase,
            ["sync"] = new JsonObject
            {
                ["state"] = view.Sync.State,
                ["line"] = view.Sync.Line,
                ["detail"] = view.Sync.Detail,
                ["pendingCount"] = view.Sync.PendingCount,
            },
            ["activity"] = view.Activity.Line,
            ["files"] = files,
            ["filesByStatus"] = statuses,
            ["checkedOutHere"] = view.MyFiles.Count,
            ["checkedOutHerePaths"] = new JsonArray(view.MyFiles.Take(50).Select(f => (JsonNode)JsonValue.Create(f.Path)).ToArray()),
            ["notices"] = new JsonArray(view.Notices.Select(n => (JsonNode)new JsonObject { ["kind"] = n.Kind, ["title"] = n.Title, ["count"] = n.Count }).ToArray()),
            ["prompt"] = view.Prompt?.Path,
            ["settings"] = new JsonObject
            {
                ["vaultRoot"] = view.Settings.VaultRoot,
                ["startAtSignIn"] = view.Settings.StartAtSignIn,
                ["theme"] = view.Settings.Theme,
            },
        };
    }
}
