using System.Text.Json;
using System.Text.Json.Nodes;

namespace Armory.Telemetry;

// The crash nothing can catch (a stack overflow, a native crash, the power) leaves no time to
// write anything. So while passes run, the last few hundred events are written to one small
// file at most once a minute, on a pool thread (never the engine's), and a clean stop deletes
// it. The next start builds that crash's incident from it (IncidentReporter.PreviousRunEnded).
public sealed class LastFlight
{
    public const int Events = 500;
    public static readonly TimeSpan Every = TimeSpan.FromMinutes(1);
    private readonly FlightRecorder recorder;
    private readonly Scrubber scrubber;
    private readonly Action<string>? log;
    private long lastWritten = long.MinValue;
    private int writing;

    public LastFlight(string path, FlightRecorder recorder, Scrubber? scrubber = null, Action<string>? log = null)
    {
        Path = path;
        this.recorder = recorder;
        this.scrubber = scrubber ?? Scrubber.None;
        this.log = log;
    }

    public string Path { get; }
    // How a write is started off the caller's thread (tests run it inline).
    internal Action<Action> Schedule { get; set; } = work => ThreadPool.UnsafeQueueUserWorkItem(_ => work(), null);
    internal int Writes { get; private set; }

    // A pass is under way: write the last flight if the last write is a minute old. Costs a
    // clock read and a comparison when it is not.
    public void Poke()
    {
        var now = recorder.Now();
        var last = Volatile.Read(ref lastWritten);
        if (last != long.MinValue && recorder.MillisecondsSince(last) < (long)Every.TotalMilliseconds) return;
        if (Interlocked.CompareExchange(ref writing, 1, 0) != 0) return;
        Volatile.Write(ref lastWritten, now);
        Schedule(() =>
        {
            try { Write(); }
            finally { Volatile.Write(ref writing, 0); }
        });
    }

    public void Write()
    {
        try
        {
            var events = recorder.Snapshot(Events);
            var document = new JsonObject
            {
                ["writtenAt"] = FlightJson.Time(recorder.UtcAt(recorder.Now())),
                ["recorded"] = recorder.Recorded,
                ["capacity"] = recorder.Capacity,
                ["events"] = FlightJson.Events(events, recorder.UtcAt),
            };
            scrubber.ScrubTree(document);
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(Path)!);
            var temp = Path + ".tmp";
            File.WriteAllBytes(temp, IncidentDocument.Gzip(JsonSerializer.SerializeToUtf8Bytes(document)));
            File.Move(temp, Path, overwrite: true);
            Writes++;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            log?.Invoke("incident: the last flight could not be written: " + error.Message);
        }
    }

    // A clean stop: nothing to build a crash from at the next start.
    public void Clear()
    {
        try { File.Delete(Path); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
    }

    public JsonObject? TryRead()
    {
        try { return File.Exists(Path) ? IncidentDocument.Read(File.ReadAllBytes(Path)) : null; }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException or InvalidDataException) { return null; }
    }
}
