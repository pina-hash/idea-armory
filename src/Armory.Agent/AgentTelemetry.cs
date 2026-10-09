using System.Runtime.InteropServices;
using System.Text.Json.Nodes;
using Armory.Client;
using Armory.Telemetry;

namespace Armory.Agent;

// The agent's half of telemetry and "Report a problem" (docs/agent/TELEMETRY.md): one flight
// recorder for the whole process, the glitch rules watching it, incident files in
// %LOCALAPPDATA%\IDEA Armory\incidents, the last flight for the crash nothing can catch, and
// the background uploader. Made first thing at start, so a crash in anything after it is kept.
// Every string an incident keeps passes the agent's Redactor and loses this computer's own
// tokens and keys by exact match.
internal sealed class AgentTelemetry : IAsyncDisposable
{
    public const int MaximumReportCharacters = 8000;
    // Report a problem: bug, idea or other. Send feedback takes praise too (FeedbackSender.Kinds).
    public static readonly IReadOnlyList<string> ReportKinds = ["bug", "idea", "other"];
    private readonly AgentPaths paths;
    private readonly AgentLog log;
    private readonly CancellationTokenSource stopping = new();
    private Func<ArmorySession?> session = () => null;
    private Func<CancellationToken, Task<JsonNode?>>? snapshot;
    private Func<JsonNode?>? quickSnapshot;
    private IncidentUploader? uploader;
    private FeedbackSender? feedback;
    private Task? uploading;

    internal AgentTelemetry(AgentPaths paths, AgentLog log, TimeProvider? clock = null)
    {
        this.paths = paths;
        this.log = log;
        Recorder = new FlightRecorder(clock: clock);
        // Other people's addresses are masked too: a site admin reads these reports (v0.3).
        Scrubber = new Scrubber(Redactor.Scrub, Secrets, () => session()?.Email);
        LastFlight = new LastFlight(paths.LastFlightFile, Recorder, Scrubber, log.Info);
        Reporter = new IncidentReporter(Recorder, new IncidentStore(paths.IncidentsFolder), new IncidentSources
        {
            Header = Header,
            Snapshot = ct => snapshot is { } take ? take(ct) : Task.FromResult(quickSnapshot?.Invoke()),
            QuickSnapshot = () => quickSnapshot?.Invoke(),
            LogTail = log.Tail,
            Scrubber = Scrubber,
            Log = log.Info,
        }, clock, LastFlight);
        Reporter.Saved += _ => uploader?.Wake();
        // One wait per RPC for the uploader and the window's Send feedback (a PT429 makes both wait).
        Limiter = new SubmitLimiter(Reporter.Store, clock);
    }

    internal SubmitLimiter Limiter { get; }

    internal FlightRecorder Recorder { get; }
    internal Scrubber Scrubber { get; }
    internal LastFlight LastFlight { get; }
    internal IncidentReporter Reporter { get; }
    internal string IncidentsFolder => paths.IncidentsFolder;

    private IEnumerable<string?> Secrets()
    {
        var current = session();
        if (current is null) yield break;
        yield return current.AccessToken;
        yield return current.RefreshToken;
        yield return current.AnonKey;
    }

    private IncidentHeader Header()
    {
        var current = session();
        string os;
        try { os = RuntimeInformation.OSDescription + " (" + RuntimeInformation.OSArchitecture + ")"; }
        catch (Exception error) when (error is not OutOfMemoryException) { os = Environment.OSVersion.VersionString; }
        return new IncidentHeader(AgentPaths.Version, os, current?.DeviceName ?? Environment.MachineName, current?.Email);
    }

    // The host is up: who is signed in, the engine's snapshot, and the uploader on its network.
    // feedback: the window's FeedbackSender (made over Limiter); a note's words go through it.
    internal void Attach(Func<ArmorySession?> currentSession, Func<CancellationToken, Task<JsonNode?>> describe, Func<JsonNode?> quick,
        ArmoryApi api, Func<bool> transferring, FeedbackSender? feedback = null)
    {
        session = currentSession;
        snapshot = describe;
        quickSnapshot = quick;
        this.feedback = feedback;
        uploader = new IncidentUploader(api, Reporter.Store, transferring, log: log.Info, feedback: feedback, limiter: Limiter);
        uploading = Task.Run(() => uploader.RunAsync(stopping.Token));
    }

    // At start: the run before ended without "stopped". Its crash incident comes from the last
    // flight it wrote (a stack overflow leaves nothing else).
    internal string? PreviousRunEnded(string lastLine) => Reporter.PreviousRunEnded(lastLine);

    // An exception nothing caught; the process ends after this returns.
    internal void CrashNow(string where, object? error)
    {
        try { Reporter.CrashNow(where, error); }
        catch (Exception) { }
    }

    // "Report a problem": saved here with a fresh userReport incident, then the words go to the
    // site at once when it can take them. The answer is one plain sentence for the window.
    internal async Task<(bool Ok, string Message)> ReportProblemAsync(string? kind, string? body)
    {
        var words = (body ?? "").Trim();
        if (words.Length == 0) return (false, "Write a few words about what happened.");
        if (words.Length > MaximumReportCharacters) words = words[..MaximumReportCharacters];
        var normalized = ReportKinds.Contains(kind ?? "") ? kind! : "other";
        var path = await Reporter.ReportUserAsync(normalized, words).ConfigureAwait(false);
        if (path is null) return (false, "Armory couldn't save your report. Try again in a moment.");
        var outcome = UploadOutcome.Offline;
        if (uploader is { } sending)
        {
            try { outcome = await sending.SendFeedbackNowAsync(path, stopping.Token).ConfigureAwait(false); }
            catch (Exception error) when (error is not OutOfMemoryException)
            {
                log.Error("report a problem: the report was saved and waits to be sent", error);
                outcome = UploadOutcome.Failed;
            }
            sending.Wake();
        }
        log.Info("report a problem: " + normalized + ", " + outcome);
        return outcome switch
        {
            UploadOutcome.Sent => (true, "Sent. Thank you for telling us."),
            UploadOutcome.NotLive or UploadOutcome.Waiting => (true, "Saved. It will be sent when the website is ready."),
            UploadOutcome.Offline => (true, "Saved. It will be sent when this computer is back online."),
            UploadOutcome.RateLimited => (true, "Saved. You've sent a lot today, so it will be sent in a little while."),
            _ => (true, "Saved. It will be sent a little later."),
        };
    }

    // "Send feedback" without a picture (0.3.3, the same as the website's): the person's words as a
    // note on its own (armory_submit_app_feedback), with what they tried and the area of the app it
    // is about, Armory's version and what it was doing as its context, never file contents. kind:
    // bug, idea, praise or other. Saved here first, so a note that can't go now is sent later; the
    // answer is one plain sentence. (A note with a picture never comes here: FeedbackDesk.)
    internal async Task<(bool Ok, string Message)> SendFeedbackAsync(string? kind, string? body, string? tried = null, string? area = null)
    {
        var words = (body ?? "").Trim();
        if (words.Length == 0) return (false, "Write a few words first.");
        if (words.Length > MaximumReportCharacters) words = words[..MaximumReportCharacters];
        var normalized = FeedbackSender.KindOf(kind);
        var path = await Reporter.SaveNoteAsync(normalized, words, FeedbackSender.Optional(tried, FeedbackSender.MaximumTriedCharacters),
            FeedbackSender.Optional(area, FeedbackSender.MaximumAreaCharacters)).ConfigureAwait(false);
        if (path is null) return (false, "Armory couldn't save your feedback. Try again in a moment.");
        var outcome = UploadOutcome.Offline;
        if (uploader is { } sending)
        {
            try { outcome = await sending.SendFeedbackNowAsync(path, stopping.Token).ConfigureAwait(false); }
            catch (Exception error) when (error is not OutOfMemoryException)
            {
                log.Error("send feedback: the note was saved and waits to be sent", error);
                outcome = UploadOutcome.Failed;
            }
            sending.Wake();
        }
        log.Info("send feedback: " + normalized + ", " + outcome);
        return outcome switch
        {
            // A site before 0235 took it with the five arguments: praise went as other.
            UploadOutcome.Sent when normalized == "praise" && feedback?.NewFieldsMissing == true
                => (true, "Sent. Thank you for the feedback. The website doesn't take praise yet, so it went as other feedback."),
            UploadOutcome.Sent => (true, "Sent. Thank you for the feedback."),
            UploadOutcome.NotLive or UploadOutcome.Waiting => (true, "Saved. It will be sent when the website is ready."),
            UploadOutcome.Offline => (true, "Saved. It will be sent when this computer is back online."),
            UploadOutcome.RateLimited => (true, "Saved. You've sent a lot today, so it will be sent in a little while."),
            UploadOutcome.Held => (false, "Armory couldn't send that feedback. It is kept in the incidents folder."),
            _ => (true, "Saved. It will be sent a little later."),
        };
    }

    // A clean stop: the last flight goes (nothing to build a crash from at the next start).
    public async ValueTask DisposeAsync()
    {
        await stopping.CancelAsync();
        if (uploading is not null)
        {
            try { await uploading.ConfigureAwait(false); }
            catch (OperationCanceledException) { }
        }
        LastFlight.Clear();
        stopping.Dispose();
    }
}
