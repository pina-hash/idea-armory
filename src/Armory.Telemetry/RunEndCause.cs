using System.Globalization;

namespace Armory.Telemetry;

// One record Windows kept in its own event logs (System or Application), as read at the next
// start. Text is the event's message, or null when it could not be read.
public sealed record WindowsEvent(string Log, string Provider, int Id, DateTimeOffset At, string? Text);

// Why the previous run ended without saying "stopped" (0.4.0). Until then every such end was a
// crash incident: on IDEA-06 alone 0.3.3 reported 26 in a week, most of them overnight, when the
// computer restarted for Windows updates or lost power, and one right after "the computer is going
// to sleep". Windows writes down a restart, a shut down, a power loss and an app crash in its own
// event logs; the next start reads them (WindowsEventHistory in the agent) and only a real crash,
// or an end nobody recorded, stays a crash incident. The words go in the log and the incident.
public static class RunEndCause
{
    public sealed record Verdict(bool Crash, string Words);

    // System: Kernel-Power 41 (rebooted without cleanly shutting down), EventLog 6008 (the previous
    // shut down was unexpected). User32 1074 (a program or person restarted or shut down Windows),
    // Kernel-General 13 (the operating system is shutting down), EventLog 6006 (the event log
    // stopped, part of every shut down). Application: Application Error 1000, .NET Runtime 1026,
    // Windows Error Reporting 1001, each only when it names IdeaArmory.
    public const string SystemQueryIds = "41,6008,1074,13,6006";
    public const string ApplicationQueryIds = "1000,1026,1001";
    public const string ProcessName = "IdeaArmory";

    public static Verdict Explain(DateTimeOffset? lastLineAt, bool asleep, IReadOnlyList<WindowsEvent> events)
    {
        // Only what Windows wrote after the run's last line (a minute's grace for the clock).
        var since = lastLineAt?.AddMinutes(-1) ?? DateTimeOffset.MinValue;
        var after = events.Where(e => e.At >= since).OrderBy(e => e.At).ToList();
        var crash = after.FirstOrDefault(e => e.Log == "Application" && e.Id is 1000 or 1026 or 1001 &&
            e.Text?.Contains(ProcessName, StringComparison.OrdinalIgnoreCase) == true);
        if (crash is not null)
            return new(true, $"Windows recorded Armory crashing at {Time(crash.At)} ({crash.Provider} {crash.Id}): {FirstLine(crash.Text)}");
        var power = after.FirstOrDefault(e => e.Log == "System" && (e.Id == 41 && e.Provider.Contains("Kernel-Power", StringComparison.OrdinalIgnoreCase) || e.Id == 6008));
        if (power is not null)
            return new(false, $"The computer stopped without shutting down (power lost or held off) and started again at {Time(power.At)}.");
        var restart = after.FirstOrDefault(e => e.Log == "System" && e.Id == 1074);
        if (restart is not null)
            return new(false, $"Windows restarted or shut down at {Time(restart.At)}: {FirstLine(restart.Text)}");
        var shutdown = after.FirstOrDefault(e => e.Log == "System" && (e.Id == 13 && e.Provider.Contains("Kernel-General", StringComparison.OrdinalIgnoreCase) || e.Id == 6006));
        if (shutdown is not null)
            return new(false, $"Windows shut down at {Time(shutdown.At)} before Armory could stop.");
        if (asleep)
            return new(false, "The computer went to sleep and was turned off or restarted before it woke up.");
        return new(true, "Windows has no record of why it ended.");
    }

    private static string Time(DateTimeOffset at) => at.ToUniversalTime().ToString("yyyy-MM-dd HH:mm 'UTC'", CultureInfo.InvariantCulture);

    private static string FirstLine(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return "no details";
        var line = text.ReplaceLineEndings("\n").Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).FirstOrDefault() ?? "no details";
        return line.Length <= 240 ? line : line[..237] + "...";
    }
}
