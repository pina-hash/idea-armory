using System.Text.Json.Nodes;

namespace Armory.Telemetry;

// What an incident is built from, besides the flight recorder.
public sealed class IncidentSources
{
    public required Func<IncidentHeader> Header { get; init; }
    // The engine's compact snapshot (counts by file status, pending requests, check outs held,
    // online state, the last passes, settings). Asked for with a deadline; null if it is late.
    public Func<CancellationToken, Task<JsonNode?>>? Snapshot { get; init; }
    // What can be said at once, without waiting on anything (a crash handler uses it).
    public Func<JsonNode?>? QuickSnapshot { get; init; }
    // The last lines of agent.log.
    public Func<int, IReadOnlyList<string>>? LogTail { get; init; }
    public Scrubber Scrubber { get; init; } = Scrubber.None;
    public Action<string>? Log { get; init; }
    public static readonly TimeSpan SnapshotDeadline = TimeSpan.FromSeconds(3);
    // How long an incident or a note waits for the engine's own snapshot before it takes what can
    // be said at once (QuickSnapshot, the window's last view) instead (0.3.3: 5 of 15 notes and 9 of
    // 23 slowAction incidents had none, the engine thread busy past the deadline).
    public static readonly TimeSpan SnapshotPatience = TimeSpan.FromMilliseconds(500);
}

// Watches the flight recorder's events with the glitch rules and, at most once per kind per
// 10 minutes, saves an incident: built and written on a pool thread, never on the thread that
// recorded the event. Saved is raised with each new file's path (the uploader wakes on it).
public sealed class IncidentReporter : IFlightObserver
{
    private readonly FlightRecorder recorder;
    private readonly IncidentSources sources;
    private readonly TimeProvider clock;
    private readonly GlitchDetector detector = new();
    private readonly IncidentThrottle throttle = new();
    [ThreadStatic] private static bool quiet;

    public IncidentReporter(FlightRecorder recorder, IncidentStore store, IncidentSources sources, TimeProvider? clock = null, LastFlight? lastFlight = null)
    {
        this.recorder = recorder;
        Store = store;
        this.sources = sources;
        this.clock = clock ?? TimeProvider.System;
        LastFlight = lastFlight;
        foreach (var (kind, at) in store.Saved()) throttle.Seed(kind, at);
        recorder.Observer = this;
    }

    public IncidentStore Store { get; }
    public LastFlight? LastFlight { get; }
    public FlightRecorder Recorder => recorder;
    public event Action<string>? Saved;
    // How a write is started off the recording thread (tests run it inline).
    internal Action<Func<Task>> Schedule { get; set; } = work => ThreadPool.UnsafeQueueUserWorkItem(_ => _ = work(), null);

    public void Observe(in FlightEvent e)
    {
        if (e.Kind is FlightKind.PassStart or FlightKind.PassPhase or FlightKind.PassEnd) LastFlight?.Poke();
        if (quiet) return;
        var glitch = detector.Inspect(e);
        if (glitch is null || !throttle.TryAdmit(glitch.Kind, clock.GetUtcNow())) return;
        Schedule(() => WriteAsync(glitch));
    }

    // Builds and saves one incident now, with the engine's snapshot if it comes in time.
    public async Task<string?> WriteAsync(Glitch glitch, IncidentFeedback? feedback = null)
        => Write(glitch, await SnapshotAsync().ConfigureAwait(false), feedback);

    // The engine's snapshot if it answers within SnapshotPatience; what can be said at once
    // otherwise (the window's last view, marked so), never nothing.
    private async Task<JsonNode?> SnapshotAsync()
    {
        JsonNode? snapshot = null;
        if (sources.Snapshot is { } take)
        {
            using var deadline = new CancellationTokenSource(IncidentSources.SnapshotDeadline);
            Task<JsonNode?>? asked = null;
            try
            {
                asked = take(deadline.Token);
                await Task.WhenAny(asked, Task.Delay(IncidentSources.SnapshotPatience)).ConfigureAwait(false);
            }
            catch (Exception error) when (error is not OutOfMemoryException) { snapshot = new JsonObject { ["unavailable"] = error.GetType().Name + ": " + error.Message }; }
            if (asked is { IsCompletedSuccessfully: true } && !deadline.IsCancellationRequested) snapshot = asked.Result;
            else if (asked is { IsFaulted: true } && asked.Exception!.InnerException is { } failed and not OperationCanceledException)
                snapshot = new JsonObject { ["unavailable"] = failed.GetType().Name + ": " + failed.Message };
            else if (snapshot is null)
            {
                snapshot = Quick() ?? new JsonObject();
                if (snapshot is JsonObject quick) quick["engineBusy"] = "the engine did not answer within half a second; this is what the window showed";
            }
        }
        else snapshot = Quick();
        return snapshot;
    }

    // A crash the process will not survive: recorded and saved on this thread, before it ends.
    public string? CrashNow(string where, object? error)
    {
        quiet = true;
        try
        {
            if (error is Exception exception) recorder.Exception(where, exception, fatal: true);
            else recorder.Exception(where, error?.GetType().FullName ?? "unknown", error?.ToString() ?? "(no exception object)", null, fatal: true);
        }
        finally { quiet = false; }
        var trigger = recorder.Snapshot(1);
        var glitch = trigger.Length == 1 ? GlitchRules.Crash(trigger[0]) : null;
        if (glitch is null || !throttle.TryAdmit(glitch.Kind, clock.GetUtcNow())) return null;
        return Write(glitch, Quick(), null);
    }

    // The previous run ended without a word: its crash incident, from the last flight it wrote.
    public string? PreviousRunEnded(string lastLine)
    {
        var flight = LastFlight?.TryRead();
        var glitch = GlitchRules.PreviousRunEnded(lastLine);
        var trigger = new JsonObject
        {
            ["kind"] = "previousRunEnded",
            ["lastLogLine"] = lastLine,
            ["lastFlightWrittenAt"] = flight?["writtenAt"]?.DeepClone(),
        };
        var events = flight?["events"] as JsonArray;
        string? path = null;
        if (throttle.TryAdmit(glitch.Kind, clock.GetUtcNow()))
            path = Write(glitch, new JsonObject { ["previousRun"] = flight is null ? "no last flight was found" : "events are the previous run's last flight" },
                null, trigger, events is null ? new JsonArray() : (JsonArray)events.DeepClone(), flight?["recorded"]?.GetValue<long>() ?? 0);
        LastFlight?.Clear();
        return path;
    }

    // "Report a problem": always saved (no throttle), with the person's words in it.
    public Task<string?> ReportUserAsync(string kind, string body) => WriteAsync(GlitchRules.UserReport(kind, body), new IncidentFeedback(kind, body));

    // "Send feedback": a note on its own, always saved (no throttle), queued here like an incident
    // until the site has it. No incident follows it (IncidentDocument.NoteKind, noteOnly), and it
    // carries no flight events: its context is the snapshot and the log's last lines. tried and
    // area go with the words, scrubbed the same way.
    public async Task<string?> SaveNoteAsync(string kind, string body, string? tried = null, string? area = null)
        => Write(NoteGlitch(kind, body), await SnapshotAsync().ConfigureAwait(false), new IncidentFeedback(kind, body, tried, area), events: [], recorded: 0, noteOnly: true);

    // The same note as SaveNoteAsync would save, rendered and scrubbed (the very document the file
    // would hold), but never written: the window's Send feedback with a picture goes at once and
    // is never kept on disk with its picture.
    public async Task<JsonObject> ComposeNoteAsync(string kind, string body, string? tried = null, string? area = null)
    {
        var snapshot = await SnapshotAsync().ConfigureAwait(false);
        var (incident, _) = Build(NoteGlitch(kind, body), snapshot, new IncidentFeedback(kind, body, tried, area), null, [], 0, noteOnly: true);
        return IncidentDocument.Read(IncidentDocument.Render(incident, sources.Scrubber));
    }

    private static Glitch NoteGlitch(string kind, string body)
        => new(IncidentDocument.NoteKind, GlitchRules.Clip($"A note from the window ({kind}): {body.ReplaceLineEndings(" ").Trim()}"), null);

    private JsonNode? Quick()
    {
        try { return sources.QuickSnapshot?.Invoke(); }
        catch (Exception error) when (error is not OutOfMemoryException) { return new JsonObject { ["unavailable"] = error.GetType().Name }; }
    }

    private string? Write(Glitch glitch, JsonNode? snapshot, IncidentFeedback? feedback, JsonObject? trigger = null, JsonArray? events = null, long recorded = -1,
        bool noteOnly = false)
    {
        try
        {
            var (incident, now) = Build(glitch, snapshot, feedback, trigger, events, recorded, noteOnly);
            var path = Store.Save(now, glitch.Kind, IncidentDocument.Render(incident, sources.Scrubber));
            sources.Log?.Invoke($"incident: saved {Path.GetFileName(path)} ({glitch.Kind})");
            Saved?.Invoke(path);
            return path;
        }
        catch (Exception error) when (error is not OutOfMemoryException)
        {
            try { sources.Log?.Invoke("incident: could not be saved: " + error.GetType().Name + ": " + error.Message); }
            catch (Exception) { }
            return null;
        }
    }

    // The incident document, before scrubbing: with the recorder's events unless given, the
    // trigger, the log's last lines and the header. Returns when it was made.
    private (JsonObject Incident, DateTimeOffset Now) Build(Glitch glitch, JsonNode? snapshot, IncidentFeedback? feedback, JsonObject? trigger, JsonArray? events,
        long recorded, bool noteOnly)
    {
        var now = clock.GetUtcNow();
        if (events is null)
        {
            events = FlightJson.Events(recorder.Snapshot(), recorder.UtcAt);
            recorded = recorder.Recorded;
        }
        if (glitch.Trigger is { } at) trigger = FlightJson.Event(at, recorder.UtcAt(at.Timestamp));
        IReadOnlyList<string> log;
        try { log = sources.LogTail?.Invoke(IncidentDocument.LogLines) ?? []; }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { log = ["(agent.log could not be read: " + error.Message + ")"]; }
        var header = sources.Header();
        var incident = IncidentDocument.Build(glitch, header, now, trigger, events, recorded, recorder.Capacity, snapshot, log, feedback);
        if (noteOnly) incident[IncidentDocument.NoteOnlyField] = true;
        return (incident, now);
    }
}
