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

    public void Crash(string source, object? error)
    {
        var text = new StringBuilder()
            .Append(Line("crash in " + source))
            .Append(Redactor.Scrub(error?.ToString() ?? "(no exception object)"))
            .Append(Environment.NewLine).Append(Environment.NewLine).ToString();
        Append(crashFile, text);
        Append(file, Line("crash in " + source + " (details in crash.log): " + (error as Exception)?.GetType().Name));
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
