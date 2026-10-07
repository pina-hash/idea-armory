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

    public void Info(string message) => Append(file, Line(message));

    public void Error(string message, Exception? error = null)
        => Append(file, Line(error is null ? message : message + ": " + error.GetType().Name + ": " + error.Message));

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

    // How the last run ended, read before this run writes its first line: null when it said
    // "stopped" (or there is no earlier run), else the last line it wrote about a pass (or its
    // last line), so the log says where a run that died without a word stopped.
    public string? PreviousRunEndedUnexpectedly()
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
        return UncleanEnd(lines);
    }

    internal static string? UncleanEnd(IReadOnlyList<string> lines)
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
        for (var i = start + 1; i < lines.Count; i++)
        {
            var message = Message(lines[i]);
            if (message == "stopped") return null;
            last = lines[i];
            if (message.StartsWith("pass: ", StringComparison.Ordinal)) lastPass = lines[i];
        }
        return lastPass ?? last ?? lines[start];
    }

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
