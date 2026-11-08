using System.Globalization;

namespace Armory.Telemetry;

// The incident kinds, exactly as armory_submit_app_incident takes them
// (docs/agent/website-requests-v0.3.md, section 4b).
public static class GlitchKinds
{
    public const string Crash = "crash", SlowAction = "slowAction", SlowPass = "slowPass", RepeatedFailure = "repeatedFailure",
        RepairedCheckout = "repairedCheckout", ReadOnlyBroken = "readOnlyBroken", UserReport = "userReport",
        // 0.5.8: the cleanup of other Armory installs did not finish, and Armory opened anyway; and a
        // launch that found Armory already running but could not reach it, and said so. The site takes
        // any kind that is one word (idea-app 0233, armory_submit_app_incident).
        RetireFailed = "retireFailed", StartupBlocked = "startupBlocked", ServerOutage = "serverOutage";
    public static readonly IReadOnlyList<string> All = [Crash, SlowAction, SlowPass, RepeatedFailure, RepairedCheckout, ReadOnlyBroken, UserReport,
        RetireFailed, StartupBlocked, ServerOutage];
}

// Something went wrong enough to keep: its kind, one plain sentence, and the event that showed it.
public sealed record Glitch(string Kind, string Summary, FlightEvent? Trigger);

// The glitch rules (docs/agent/TELEMETRY.md, "Triggers"). Each one is a pure function of the
// event in front of it (and, for repeated failures, of the failures before it).
public static class GlitchRules
{
    public const long SlowActionMs = 10_000;
    public const long SlowPassMs = 60_000;
    public const int RepeatedFailures = 3;
    public const int MaximumSummary = 500;

    // An exception nothing handled: the process, the window's thread or the engine's loop.
    public static Glitch? Crash(in FlightEvent e)
        => e.Kind == FlightKind.Exception && e.Fatal
            ? new(GlitchKinds.Crash, Clip($"Armory crashed in {e.Target ?? "an unknown place"}: {e.Name}: {e.Detail}"), e)
            : null;

    // A window action whose answer took more than 10 seconds.
    public static Glitch? SlowAction(in FlightEvent e)
        => e.Kind == FlightKind.WindowAction && e.Ms > SlowActionMs
            ? new(GlitchKinds.SlowAction, Clip($"The window waited {Seconds(e.Ms)} for {e.Name} ({Plural(e.Count, "target", "targets")}) to answer{(e.Ok ? "" : ", and it was refused")}."), e)
            : null;

    // A pass that took more than 60 seconds from start to end, unless the computer slept during
    // it (0.3.3: a 52 minute "pass" on IDEA-06 was the computer asleep, the clock counting on).
    // A pass that moved files is slow only past MovingPassMs: a long download's slices run about a
    // minute each by design (0.3.3 reported "61.8 s: 608 downloaded" as slow). Since 0.4.2 it is
    // judged by its work too: each file it moved gives it PerFileMs more, so a pass that moved
    // 3,000 files in five minutes (ten a second) is not slow, and one that moved 2 in five is.
    public const long MovingPassMs = 180_000;
    public const long PerFileMs = 250;
    public static long SlowPassLimitMs(int moved) => moved > 0 ? Math.Max(MovingPassMs, SlowPassMs + PerFileMs * moved) : SlowPassMs;
    public static Glitch? SlowPass(in FlightEvent e, bool slept = false)
        => e.Kind == FlightKind.PassEnd && e.Ms > SlowPassLimitMs(e.Count + e.Count2 + e.Count3) && !slept
            ? new(GlitchKinds.SlowPass, Clip($"A {e.Name} pass took {Seconds(e.Ms)}{(e.Ok ? "" : " and failed")}: {e.Count:N0} downloaded, {e.Count2:N0} uploaded, " +
                $"{e.Count3:N0} kept copies, {e.Count4:N0} refused."), e)
            : null;

    public static Glitch? RepairedCheckout(in FlightEvent e)
        => e.Kind == FlightKind.RepairedCheckout
            ? new(GlitchKinds.RepairedCheckout, Clip($"The check out of {e.Target} had no record on this computer, and Armory repaired it."), e)
            : null;

    public static Glitch? ReadOnlyBroken(in FlightEvent e)
        => e.Kind == FlightKind.ReadOnlyBroken
            ? new(GlitchKinds.ReadOnlyBroken, Clip($"{e.Target} was writable though it should have been read-only, and Armory made it read-only again."), e)
            : null;

    public static Glitch RepeatedFailure(in FlightEvent e, int times)
        => new(GlitchKinds.RepeatedFailure, Clip($"{e.Target} failed {times} times in a row, the last with {e.Name}: {e.Detail}"), e);

    // The run before this one ended without saying "stopped" (a stack overflow, a native crash,
    // the power): found at the next start from the log and the last flight.
    public static Glitch PreviousRunEnded(string lastLine, string? why = null)
        => new(GlitchKinds.Crash, Clip("The previous run ended unexpectedly. " + (why is null ? "" : why + " ") + "Its last log line was: " + lastLine), null);

    // 0.5.8: the cleanup of other Armory installs (InstallRetirement) failed or gave way; Armory
    // opened anyway. detail is the reason as the audit says it.
    public static Glitch RetireFailed(string detail)
        => new(GlitchKinds.RetireFailed, Clip("Armory opened, but the cleanup of other Armory installs did not finish: " + detail), null);

    // 0.5.8: a launch found Armory already running for this Windows account and could not reach it,
    // and told the person so instead of leaving without a word.
    public static Glitch StartupBlocked(string detail)
        => new(GlitchKinds.StartupBlocked, Clip("A launch found Armory already running but could not reach it: " + detail), null);

    public static Glitch UserReport(string kind, string body)
        => new(GlitchKinds.UserReport, Clip($"Reported from the window ({kind}): {body.ReplaceLineEndings(" ").Trim()}"), null);

    public static string Clip(string text)
    {
        text = text.ReplaceLineEndings(" ");
        return text.Length <= MaximumSummary ? text : text[..(MaximumSummary - 3)] + "...";
    }

    private static string Seconds(long ms) => (ms / 1000.0).ToString("0.0", CultureInfo.InvariantCulture) + " s";
    private static string Plural(int n, string one, string many) => n.ToString("N0", CultureInfo.InvariantCulture) + " " + (n == 1 ? one : many);
}

// The same file failing again and again: three failures with no success of that file between
// them is one glitch, and the count starts again after it.
public sealed class RepeatedFailureRule
{
    private readonly Dictionary<string, int> failures = new(StringComparer.OrdinalIgnoreCase);

    public int Tracked => failures.Count;

    // Returns how many times in a row the file has failed when that reaches the limit, else 0.
    public int Failed(string path)
    {
        var times = failures.GetValueOrDefault(path) + 1;
        if (times >= GlitchRules.RepeatedFailures)
        {
            failures.Remove(path);
            return times;
        }
        failures[path] = times;
        // A runaway list of paths never grows without bound: the oldest counts are forgotten.
        if (failures.Count > 10_000) failures.Clear();
        return 0;
    }

    public void Succeeded(string path) => failures.Remove(path);
}

// Runs every rule on the events the recorder hands its observer. Thread safe: events come from
// the engine's thread, the window's thread and the network's.
public sealed class GlitchDetector(bool initiallyOffline = false)
{
    private readonly object gate = new();
    private readonly RepeatedFailureRule repeated = new();
    // The computer slept or woke since the pass that is running started (a power event).
    private bool sleptDuringPass;
    private bool serverOutage = initiallyOffline;

    public Glitch? Inspect(in FlightEvent e)
    {
        switch (e.Kind)
        {
            case FlightKind.ServerAvailability:
                lock (gate)
                {
                    if (e.Ok) { serverOutage = false; return null; }
                    if (serverOutage) return null;
                    serverOutage = true;
                    return new(GlitchKinds.ServerOutage, "Armory could not reach the server. Work stays safe here while it retries with longer waits.", e);
                }
            case FlightKind.Exception: return GlitchRules.Crash(e);
            case FlightKind.WindowAction:
                lock (gate) return serverOutage ? null : GlitchRules.SlowAction(e);
            case FlightKind.PassStart:
                lock (gate) sleptDuringPass = false;
                return null;
            case FlightKind.Power:
                lock (gate) sleptDuringPass = true;
                return null;
            case FlightKind.PassEnd:
                bool slept;
                lock (gate) { if (serverOutage) return null; slept = sleptDuringPass; }
                return GlitchRules.SlowPass(e, slept);
            case FlightKind.RepairedCheckout: return GlitchRules.RepairedCheckout(e);
            case FlightKind.ReadOnlyBroken: return GlitchRules.ReadOnlyBroken(e);
            case FlightKind.FileFailed when e.Target is { } failed:
                int times;
                lock (gate) times = repeated.Failed(failed);
                return times > 0 ? GlitchRules.RepeatedFailure(e, times) : null;
            case FlightKind.FileRecovered when e.Target is { } recovered:
                lock (gate) repeated.Succeeded(recovered);
                return null;
            default: return null;
        }
    }
}

// At most one incident per kind per 10 minutes on this computer. A user's own report is never
// held back.
public sealed class IncidentThrottle
{
    public static readonly TimeSpan Window = TimeSpan.FromMinutes(10);
    private readonly Dictionary<string, DateTimeOffset> last = new(StringComparer.Ordinal);
    private readonly object gate = new();

    // An incident of this kind saved at this time (by an earlier run, read from the files).
    public void Seed(string kind, DateTimeOffset at)
    {
        lock (gate) if (!last.TryGetValue(kind, out var known) || at > known) last[kind] = at;
    }

    public bool TryAdmit(string kind, DateTimeOffset now)
    {
        if (kind == GlitchKinds.UserReport) return true;
        lock (gate)
        {
            if (last.TryGetValue(kind, out var at) && now - at < Window && now >= at) return false;
            last[kind] = now;
            return true;
        }
    }
}
