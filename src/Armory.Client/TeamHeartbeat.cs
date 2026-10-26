namespace Armory.Client;

// Team status (ARMORY.md v0.3, item 5): while the app runs, armory_heartbeat says this computer
// is here, which version it runs and what it is doing, so the website's Team view reads "Armory
// open". About every 45 seconds, at once when the state changes, and "offline-soon" on a clean
// stop. The server writes nothing for a call within 20 seconds that changes nothing, so there is
// no suppression here.
//
// Since 0.5.1 each beat also carries this computer's key (MachineId, idea-app 0310), so the
// website shows one lab computer that many people sign in to as one computer. A site without
// 0310 answers that form 404 PGRST202: the same beat goes again at once without the key, and
// no beat sends it again until KeylessRetry has passed, so a site that never gets 0310 costs
// one extra call every six hours and never a missed beat.
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
    // A site without the four-argument armory_heartbeat (404 PGRST202 before idea-app 0310) is
    // sent beats without the computer key, and offered the key again after this long.
    public static readonly TimeSpan KeylessRetry = TimeSpan.FromHours(6);
    // The key the server keeps: 1 to 64 letters, digits, dashes or underscores (0310's rule). A
    // key outside it is never sent, since the server would only ignore it.
    public const int MaximumComputerKeyCharacters = 64;
    // armory_heartbeat refuses an app_version longer than this (22023), while feedback and
    // incidents take 64 (ARMORY.md item 5, "The two version limits do not match"). A longer
    // version is cut to it here, so a heartbeat is never refused for it.
    public const int MaximumVersionCharacters = 40;
    private readonly ArmoryApi api;
    private readonly SessionManager sessions;
    private readonly string appVersion;
    private readonly string? computerKey;
    private DateTimeOffset keylessUntil = DateTimeOffset.MinValue;
    // False once the server refused the version anyway (22023, field app_version): from then on
    // every beat sends none (null keeps what the server has), so one refusal never repeats.
    private volatile bool sendVersion = true;
    private readonly TimeProvider clock;
    private readonly Action<string>? log;
    private readonly SemaphoreSlim changed = new(0, int.MaxValue);
    private readonly SemaphoreSlim sending = new(1, 1);
    private string state = Idle;
    private string? lastProblem;
    private DateTimeOffset notLiveUntil = DateTimeOffset.MinValue;

    public TeamHeartbeat(ArmoryApi api, SessionManager sessions, string appVersion, TimeProvider? clock = null, Action<string>? log = null, string? computerKey = null)
    {
        this.api = api;
        this.sessions = sessions;
        this.appVersion = VersionFor(appVersion);
        this.computerKey = KeyFor(computerKey);
        this.clock = clock ?? TimeProvider.System;
        this.log = log;
    }

    public string State => Volatile.Read(ref state);
    // The version each beat sends: the app's, trimmed and cut to MaximumVersionCharacters.
    public string AppVersion => appVersion;

    // The version a heartbeat can carry: trimmed, at most MaximumVersionCharacters.
    public static string VersionFor(string? version)
    {
        var v = (version ?? "").Trim();
        return v.Length <= MaximumVersionCharacters ? v : v[..MaximumVersionCharacters].TrimEnd();
    }
    public int Sent { get; private set; }

    // The key each beat carries, or null for none (no key on this computer, or one the server
    // would not keep).
    public string? ComputerKey => computerKey;

    // A key the server keeps, trimmed, or null.
    public static string? KeyFor(string? key)
    {
        var k = key?.Trim();
        if (string.IsNullOrEmpty(k) || k.Length > MaximumComputerKeyCharacters) return null;
        foreach (var c in k)
            if (!(char.IsAsciiLetterOrDigit(c) || c is '-' or '_')) return null;
        return k;
    }

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
            try { await SendAsync(session.DeviceId, sendVersion ? appVersion : null, beat, timeout.Token); }
            catch (ArmoryRpcException error) when (sendVersion && error.SqlState == ArmoryRpcException.InvalidValueState && error.Detail?.Field == "app_version")
            {
                // Never expected (the version is cut to the limit above). This beat goes again at
                // once without a version, and so does every later one: never the same refusal twice.
                sendVersion = false;
                Problem($"refused the version ({error.Reason}): {error.Message}; beats go on without it");
                await SendAsync(session.DeviceId, null, beat, timeout.Token);
            }
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

    // One beat: with the computer key while the site takes it, else the three-argument form. A
    // 404 PGRST202 for the keyed form means the site does not have idea-app 0310 yet, not that
    // it has no heartbeat: the beat goes again at once without the key. A PGRST202 from the
    // three-argument form is the site with no heartbeat at all, and is BeatAsync's to handle.
    private async Task SendAsync(Guid device, string? version, string beat, CancellationToken ct)
    {
        if (computerKey is { } key && clock.GetUtcNow() >= keylessUntil)
        {
            try
            {
                await api.HeartbeatAsync(device, version, beat, key, ct);
                return;
            }
            catch (ArmoryRpcException error) when (error.IsFunctionMissing)
            {
                keylessUntil = clock.GetUtcNow() + KeylessRetry;
                Problem($"the site does not take a computer key yet; beats go on without it, offered again in {KeylessRetry.TotalHours:0} hours");
            }
        }
        await api.HeartbeatAsync(device, version, beat, ct);
    }

    private void Problem(string what)
    {
        if (what == lastProblem) return;
        lastProblem = what;
        log?.Invoke("team status: " + what);
    }
}
