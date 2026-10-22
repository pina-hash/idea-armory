using System.Diagnostics.Eventing.Reader;
using System.Globalization;
using Armory.Telemetry;

namespace Armory.Agent;

// What Windows wrote in its own event logs since the previous run's last line (RunEndCause):
// restarts, shut downs and power losses from System, and IdeaArmory's crashes from Application.
// Read once at start, for a run that ended without a word, within a second or two at most; any
// log that can't be read is left out, never an error.
internal static class WindowsEventHistory
{
    // This Windows account's security id, the one a sign out names (RunEndCause), or null.
    internal static string? CurrentUser()
    {
        if (!OperatingSystem.IsWindows()) return null;
        try
        {
            using var identity = System.Security.Principal.WindowsIdentity.GetCurrent();
            return identity.User?.Value;
        }
        catch (System.Security.SecurityException) { return null; }
    }

    private const int MostEvents = 40;
    private static readonly TimeSpan Budget = TimeSpan.FromSeconds(3);

    internal static IReadOnlyList<WindowsEvent> Since(DateTimeOffset? since, Action<string> log)
    {
        if (!OperatingSystem.IsWindows()) return [];
        var from = (since ?? DateTimeOffset.UtcNow.AddDays(-2)).AddMinutes(-1).UtcDateTime.ToString("yyyy-MM-ddTHH:mm:ss.fffZ", CultureInfo.InvariantCulture);
        List<WindowsEvent> found = [];
        var started = Environment.TickCount64;
        foreach (var (logName, ids) in new[] { ("System", RunEndCause.SystemQueryIds), ("Application", RunEndCause.ApplicationQueryIds) })
        {
            var idTest = string.Join(" or ", ids.Split(',').Select(id => "EventID=" + id));
            var query = new EventLogQuery(logName, PathType.LogName, $"*[System[({idTest}) and TimeCreated[@SystemTime>='{from}']]]") { ReverseDirection = false };
            try
            {
                using var reader = new EventLogReader(query);
                while (found.Count < MostEvents && Environment.TickCount64 - started < Budget.TotalMilliseconds && reader.ReadEvent() is { } record)
                {
                    using (record)
                    {
                        string? text;
                        try { text = record.FormatDescription(); }
                        catch (EventLogException) { text = null; }
                        if (record.TimeCreated is { } at)
                            found.Add(new WindowsEvent(logName, record.ProviderName ?? "", record.Id, new DateTimeOffset(at.ToUniversalTime(), TimeSpan.Zero), text, record.UserId?.Value));
                    }
                }
            }
            catch (Exception error) when (error is EventLogException or UnauthorizedAccessException or InvalidOperationException)
            {
                log("windows event log " + logName + " could not be read: " + error.GetType().Name);
            }
        }
        return found;
    }
}
