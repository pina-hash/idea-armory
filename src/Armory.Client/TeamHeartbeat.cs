namespace Armory.Client;

// Team status (ARMORY.md v0.3, item 5): while the app runs, armory_heartbeat says this computer
// is here, which version it runs and what it is doing, so the website's Team view reads "Armory
// open". About every 45 seconds, at once when the state changes, and "offline-soon" on a clean
// stop. The server writes nothing for a call within 20 seconds that changes nothing, so there is
// no suppression here.
//
// It runs on its own task with its own deadline: a heartbeat that fails, hangs or is refused is
// logged (once per kind of failure until one goes through) and never blocks or slows a sync pass.
public sealed class TeamHeartbeat
{
    public const string Idle = "idle", Syncing = "syncing", OfflineSoon = "offline-soon";
    public static readonly TimeSpan Every = TimeSpan.FromSeconds(45);
    public static readonly TimeSpan CallTimeout = TimeSpan.FromSeconds(15);
    // A site without armory_heartbeat (404 PGRST202) is asked again after this long.
    public static readonly TimeSpan NotLiveRetry = TimeSpan.FromHours(6);
    private readonly ArmoryApi api;
    private readonly SessionManager sessions;
    private readonly string appVersion;
    private readonly TimeProvider clock;
    private readonly Action<string>? log;
    private readonly SemaphoreSlim changed = new(0, int.MaxValue);
    private readonly SemaphoreSlim sending = new(1, 1);
    private string state = Idle;
    private string? lastProblem;
    private DateTimeOffset notLiveUntil = DateTimeOffset.MinValue;

    public TeamHeartbeat(ArmoryApi api, SessionManager sessions, string appVersion, TimeProvider? clock = null, Action<string>? log = null)
    {
        this.api = api;
        this.sessions = sessions;
        this.appVersion = appVersion;
        this.clock = clock ?? TimeProvider.System;
        this.log = log;
    }

    public string State => Volatile.Read(ref state);
    public int Sent { get; private set; }

    // "syncing" while files move, "idle" otherwise.
    public static string StateFor(bool moving) => moving ? Syncing : Idle;

    // A new state goes at once (the next beat is sent now); the same state again changes nothing.
    public void SetState(string next)
    {
        if (Interlocked.Exchange(ref state, next) != next) changed.Release();
    }

    // Beats until canceled: one at the start, then every Every, and at once on a state change.
    public async Task RunAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            await BeatAsync(State, ct);
            try
            {
                await changed.WaitAsync(Every, ct);
                while (changed.CurrentCount > 0) await changed.WaitAsync(0, ct);
            }
            catch (OperationCanceledException) { return; }
        }
    }

    // A clean stop: "offline-soon", within a short deadline so a stop is never held up.
    public async Task<bool> SayGoodbyeAsync(TimeSpan deadline)
    {
        Volatile.Write(ref state, OfflineSoon);
        using var timeout = new CancellationTokenSource(deadline);
        return await BeatAsync(OfflineSoon, timeout.Token);
    }

    // One heartbeat. True when the server took it. Never throws.
    public async Task<bool> BeatAsync(string beat, CancellationToken ct)
    {
        if (clock.GetUtcNow() < notLiveUntil || sessions.Current is not { } session) return false;
        try { await sending.WaitAsync(ct); }
        catch (OperationCanceledException) { return false; }
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(CallTimeout);
            await api.HeartbeatAsync(session.DeviceId, appVersion, beat, timeout.Token);
            Sent++;
            lastProblem = null;
            return true;
        }
        catch (ArmoryRpcException error) when (error.IsFunctionMissing)
        {
            notLiveUntil = clock.GetUtcNow() + NotLiveRetry;
            Problem($"the site has no armory_heartbeat yet; asked again in {NotLiveRetry.TotalHours:0} hours");
        }
        catch (ArmoryRpcException error)
        {
            // P0001 "device is not registered to caller" (this computer was connected again
            // elsewhere), or 22023 for a version or state the server won't take: a bug to log.
            Problem($"refused ({error.SqlState}{(error.Reason is { } reason ? " " + reason : "")}): {error.Message}");
        }
        catch (OperationCanceledException) { Problem("no answer in time"); }
        catch (Exception error) when (error is ArmoryClientException or InvalidDataException or HttpRequestException)
        {
            Problem(error is ArmoryOfflineException ? "offline" : error.GetType().Name + ": " + error.Message);
        }
        finally { sending.Release(); }
        return false;
    }

    private void Problem(string what)
    {
        if (what == lastProblem) return;
        lastProblem = what;
        log?.Invoke("team status: " + what);
    }
}
