using System.Globalization;
using System.Text;

namespace Armory.Agent;

// The agent's only log writer. Every line goes through Redactor.Scrub first, so no token,
// code or verifier reaches the disk even inside an exception message or a URL. Crashes are
// written in full (still scrubbed) to crash.log, with one pointer line in agent.log.
public sealed class AgentLog
{
    private const long RollAtBytes = 4 * 1024 * 1024;
    private readonly object gate = new();
    private readonly string file;
    private readonly string crashFile;

    public AgentLog(string file, string crashFile)
    {
        this.file = file;
        this.crashFile = crashFile;
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(file)!);
            Roll(file);
            Roll(crashFile);
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    public void Info(string message) => Write(message);

    public void Error(string message, Exception? error = null)
        => Write(error is null ? message : message + ": " + error.GetType().Name + ": " + error.Message);

    private void Write(string message)
    {
        var line = Line(message);
        Append(file, line);
        if (IsKept(message)) lock (gate) if (kept.Count < KeptLines) kept.Add(line.TrimEnd());
    }

    // 0.5.4: the first lines of this start about thumbnails (MainWindow, ShellThumbnails) are kept
    // in memory and go with every incident and note (TailWithKept), even once agent.log's last 300
    // lines have moved past them: 0.5.3 wrote them a minute after the window opened, and none had
    // reached the site, since an incident or a note carries only the log's last lines.
    internal const int KeptLines = 16;
    private readonly List<string> kept = [];

    internal static bool IsKept(string message)
        => message.StartsWith("thumbnail", StringComparison.Ordinal) ||
           message.StartsWith("could not serve a thumbnail", StringComparison.Ordinal) ||
           message.StartsWith("could not finish a thumbnail", StringComparison.Ordinal);

    // The log's last lines (Tail), after the kept lines they no longer hold; count lines at most.
    public IReadOnlyList<string> TailWithKept(int count)
    {
        string[] held;
        lock (gate) held = [.. kept];
        return WithKept(Tail(count), held, count);
    }

    internal const string KeptHeading = "(kept from earlier in this run, for reports:)";

    internal static IReadOnlyList<string> WithKept(IReadOnlyList<string> tail, IReadOnlyList<string> held, int count)
    {
        var inTail = new HashSet<string>(tail, StringComparer.Ordinal);
        var missing = held.Where(line => !inTail.Contains(line)).ToList();
        if (missing.Count == 0 || count <= missing.Count + 1) return tail;
        var room = count - missing.Count - 1;
        List<string> lines = [KeptHeading, .. missing];
        lines.AddRange(tail.Skip(Math.Max(0, tail.Count - room)));
        return lines;
    }

    // Never throws: a crash handler that fails loses the one line that says why the agent
    // stopped. Whatever part can't be written (the scrubbed details, the type name) is left out.
    public void Crash(string source, object? error)
    {
        string details;
        try { details = Redactor.Scrub(error?.ToString() ?? "(no exception object)"); }
        catch (Exception scrub) { details = "(the details could not be written: " + scrub.GetType().Name + ")"; }
        string kind;
        try { kind = (error as Exception)?.GetType().Name ?? error?.GetType().Name ?? "unknown"; }
        catch (Exception) { kind = "unknown"; }
        try
        {
            Append(crashFile, SafeLine("crash in " + source) + details + Environment.NewLine + Environment.NewLine);
            Append(file, SafeLine("crash in " + source + " (details in crash.log): " + kind));
        }
        catch (Exception) { }
    }

    // The line the agent writes when Windows ends the session (TrayApp, 0.3.3): a run that wrote
    // it, or "quitting", was stopped on purpose, never a crash.
    internal const string SessionEndingLine = "windows is ending the session";

    // How the last run ended, read before this run writes its first line: null when it said
    // "stopped" (or there is no earlier run), or when it was quitting (PreviousRun says so), else
    // the last line it wrote about a pass (or its last line), so the log says where a run that
    // died without a word stopped.
    public string? PreviousRunEndedUnexpectedly() => PreviousRun() is { Quitting: false } end ? end.LastLine : null;

    // The same, with whether the run had begun to quit (it logged "quitting", or that Windows was
    // ending the session) when it ended without "stopped": Windows ends a process right after the
    // session ends, whatever it is doing (0.3.3: four such ends were reported as crashes).
    public RunEnd? PreviousRun()
    {
        string[] lines;
        try
        {
            // Rolled over by this start (agent.log grew past 4 MB): the last run's lines are in .1.
            var last = File.Exists(file) ? file : file + ".1";
            if (!File.Exists(last)) return null;
            using var input = new FileStream(last, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            var tail = (int)Math.Min(input.Length, 256 * 1024);
            input.Seek(-tail, SeekOrigin.End);
            var buffer = new byte[tail];
            input.ReadExactly(buffer);
            lines = Encoding.UTF8.GetString(buffer).Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { return null; }
        return EndOf(lines);
    }

    // The last lines of agent.log (an incident keeps 300). Already scrubbed when written.
    public IReadOnlyList<string> Tail(int count)
    {
        var lines = ReadTail(file);
        return lines.Count <= count ? lines : lines.Skip(lines.Count - count).ToList();
    }

    private static List<string> ReadTail(string target)
    {
        try
        {
            if (!File.Exists(target)) return [];
            using var input = new FileStream(target, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            var tail = (int)Math.Min(input.Length, 256 * 1024);
            input.Seek(-tail, SeekOrigin.End);
            var buffer = new byte[tail];
            input.ReadExactly(buffer);
            var lines = Encoding.UTF8.GetString(buffer).Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();
            // The first line of a cut read is only part of a line.
            if (tail < input.Length && lines.Count > 0) lines.RemoveAt(0);
            return lines;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { return []; }
    }

    // A crash's last line (null for a run that stopped, or was quitting).
    internal static string? UncleanEnd(IReadOnlyList<string> lines) => EndOf(lines) is { Quitting: false } end ? end.LastLine : null;

    internal static RunEnd? EndOf(IReadOnlyList<string> lines)
    {
        static string Message(string line) => line.IndexOf(' ') is var space and > 0 ? line[(space + 1)..] : line;
        var start = -1;
        for (var i = lines.Count - 1; i >= 0; i--)
        {
            if (!Message(lines[i]).StartsWith("started ", StringComparison.Ordinal)) continue;
            start = i;
            break;
        }
        if (start < 0) return null;
        string? lastPass = null, last = null;
        var quitting = false;
        var asleep = false;
        DateTimeOffset? updatedAt = null;
        for (var i = start + 1; i < lines.Count; i++)
        {
            var message = Message(lines[i]);
            if (message == "stopped") return null;
            // Written by the new version's install step after the run ended, as is everything after
            // it until the next start: never the run's own lines.
            if (message.StartsWith(UpdatedLine, StringComparison.Ordinal)) updatedAt ??= TimeOf(lines[i]) ?? DateTimeOffset.MinValue;
            if (updatedAt is not null) continue;
            if (message == "quitting" || message.StartsWith(SessionEndingLine, StringComparison.Ordinal)) quitting = true;
            // Asleep: the run's last word about the power was that the computer went to sleep.
            if (message == SleepLine) asleep = true;
            else if (message == WakeLine) asleep = false;
            last = lines[i];
            if (message.StartsWith("pass: ", StringComparison.Ordinal)) lastPass = lines[i];
        }
        var veryLast = last ?? lines[start];
        return new(lastPass ?? veryLast, quitting, veryLast, asleep, TimeOf(veryLast), updatedAt);
    }

    // 0.5.4: the line Velopack's install step writes in the new version once an update is in place
    // (Updates.AfterUpdate). An update applied when Armory opens stops the older copy still
    // running in the tray first (incident 0269c86e: 0.5.1 had downloaded 0.5.3, a student opened
    // Armory, and 0.5.3 reported 0.5.1's end as a crash). After the run's last line, it says why
    // the run ended without "stopped" (RunEndCause.Explain's updatedAt).
    internal const string UpdatedLine = "updates: put in place as Armory ";

    internal const string SleepLine = "the computer is going to sleep", WakeLine = "the computer woke up";

    // When a line was written (its first word), or null.
    internal static DateTimeOffset? TimeOf(string line)
        => line.IndexOf(' ') is var space and > 0 &&
           DateTimeOffset.TryParse(line[..space], CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var at) ? at : null;

    private static string SafeLine(string message)
    {
        try { return Line(message); }
        catch (Exception) { return DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture) + " " + message.Replace('\r', ' ').Replace('\n', ' ') + Environment.NewLine; }
    }

    private static string Line(string message)
        => DateTimeOffset.UtcNow.ToString("yyyy-MM-ddTHH:mm:ss.fffZ", CultureInfo.InvariantCulture) + " " +
           Redactor.Scrub(message).Replace('\r', ' ').Replace('\n', ' ') + Environment.NewLine;

    // Logging never throws into the caller: a full disk must not take the agent down.
    private void Append(string target, string text)
    {
        lock (gate)
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                using var output = new FileStream(target, FileMode.Append, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete);
                output.Write(Encoding.UTF8.GetBytes(text));
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }

    private static void Roll(string target)
    {
        var info = new FileInfo(target);
        if (info.Exists && info.Length > RollAtBytes) File.Move(target, target + ".1", overwrite: true);
    }
}

// How a run that never wrote "stopped" ended: the last line it wrote about a pass (or its last
// line), and whether it had begun to quit.
// VeryLastLine is the last line it wrote whatever it was about, Asleep whether the computer had
// gone to sleep and not woken, LastAt when the last line was written.
// UpdatedAt is when an update was put in place after the run's last line (AgentLog.UpdatedLine).
public sealed record RunEnd(string LastLine, bool Quitting, string? VeryLastLine = null, bool Asleep = false, DateTimeOffset? LastAt = null,
    DateTimeOffset? UpdatedAt = null);
