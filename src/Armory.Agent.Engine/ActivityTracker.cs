using System.Globalization;
using Armory.Agent.Engine.View;

namespace Armory.Agent.Engine;

// What is moving right now (v2-design.md 4.4), per direction: uploading, downloading and moving,
// with files and bytes done and in all, speed and time left, the files in flight and the waiting
// line. The engine thread tells it what a pass will move and when each file starts and ends;
// BlobClient reports bytes from any thread (each transfer is an IProgress<long>). Snapshots are
// immutable ActivityViews. Speed is an exponential average over about 5 seconds of
// TimeProvider.GetTimestamp time (a test clock that freezes UtcNow still moves it); time left
// appears after 3 seconds and 2 files.
internal sealed class ActivityTracker(TimeProvider clock)
{
    internal const int ActiveShown = 8;
    private static readonly TimeSpan Smoothing = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan EstimateAfter = TimeSpan.FromSeconds(3);
    private static readonly TimeSpan SampleEvery = TimeSpan.FromMilliseconds(200);
    private readonly object gate = new();
    private readonly Dictionary<string, Lane> lanes = new(StringComparer.Ordinal);
    private readonly List<Transfer> active = [];
    private WaitingView? waiting;
    private long version;

    // Moves whenever anything a snapshot shows may have changed.
    internal long Version => Interlocked.Read(ref version);
    internal bool Busy { get { lock (gate) return lanes.Count > 0 || active.Count > 0; } }

    private sealed class Lane(string direction, long started)
    {
        internal readonly string Direction = direction;
        internal readonly long Started = started;
        internal readonly Dictionary<string, long> Expected = new(StringComparer.OrdinalIgnoreCase);
        internal int FilesTotal, FilesDone;
        internal long BytesTotal, BytesDone;
        internal string? Target;
        internal long SampledAt = started, SampledBytes;
        internal int SampledFiles;
        internal double? BytesPerSecond, FilesPerSecond;
    }

    // One file moving now. BlobClient reports the bytes so far (0 again if it starts over).
    internal sealed class Transfer(ActivityTracker tracker, string direction, string path, long bytes) : IProgress<long>
    {
        private long done;
        internal string Direction { get; } = direction;
        internal string Path { get; } = path;
        internal long Bytes { get; } = bytes;
        internal long Done => Interlocked.Read(ref done);
        public void Report(long value)
        {
            Interlocked.Exchange(ref done, Math.Clamp(value, 0, Math.Max(Bytes, 0)));
            Interlocked.Increment(ref tracker.version);
        }
    }

    // A pass's plan: this file will go this way (seeds the totals before anything moves).
    internal void Expect(string direction, string path, long bytes)
    {
        lock (gate)
        {
            var lane = LaneFor(direction);
            if (!lane.Expected.TryAdd(path, bytes)) return;
            lane.FilesTotal++;
            lane.BytesTotal += bytes;
            Changed();
        }
    }

    // A file starts moving. One the plan did not expect (a write finished after a crash) joins the totals.
    internal Transfer Start(string direction, string path, long bytes)
    {
        lock (gate)
        {
            var lane = LaneFor(direction);
            if (lane.Expected.Remove(path, out var expected))
            {
                lane.BytesTotal += bytes - expected;
            }
            else
            {
                lane.FilesTotal++;
                lane.BytesTotal += bytes;
            }
            var transfer = new Transfer(this, direction, path, bytes);
            active.Add(transfer);
            Changed();
            return transfer;
        }
    }

    // The file is where it goes (or was already there): it counts as done.
    internal void Finish(Transfer transfer)
    {
        lock (gate)
        {
            if (!active.Remove(transfer)) return;
            if (lanes.TryGetValue(transfer.Direction, out var lane))
            {
                lane.FilesDone++;
                lane.BytesDone += transfer.Bytes;
            }
            Changed();
        }
    }

    // It did not go this time (a refusal, a problem, the connection): out of the totals.
    internal void Fail(Transfer transfer)
    {
        lock (gate)
        {
            if (!active.Remove(transfer)) return;
            if (lanes.TryGetValue(transfer.Direction, out var lane))
            {
                lane.FilesTotal--;
                lane.BytesTotal -= transfer.Bytes;
            }
            Changed();
        }
    }

    // A planned file that ended without moving (nothing to send after all, refused): out of the totals.
    internal void Drop(string path)
    {
        lock (gate)
        {
            foreach (var lane in lanes.Values)
            {
                if (!lane.Expected.Remove(path, out var bytes)) continue;
                lane.FilesTotal--;
                lane.BytesTotal -= bytes;
                Changed();
            }
        }
    }

    // Files moving to a folder in one step (a folder renamed, put back, a project renamed), or
    // one by one: "Moving 120 files to Robot 2027 › Gearbox".
    internal void Moving(int files, string target)
    {
        if (files <= 0) return;
        lock (gate)
        {
            var lane = LaneFor(Directions.Move);
            lane.FilesTotal += files;
            lane.Target = target;
            Changed();
        }
    }

    internal void Moved(int files)
    {
        lock (gate)
        {
            if (!lanes.TryGetValue(Directions.Move, out var lane)) return;
            lane.FilesDone = Math.Min(lane.FilesTotal, lane.FilesDone + files);
            Changed();
        }
    }

    // Nothing is moving any more (the end of a pass).
    internal void Reset()
    {
        lock (gate)
        {
            if (lanes.Count == 0 && active.Count == 0) return;
            lanes.Clear();
            active.Clear();
            Changed();
        }
    }

    // The waiting line, kept with the activity so every snapshot carries it.
    internal void SetWaiting(WaitingView? value)
    {
        lock (gate)
        {
            if (Equals(waiting, value)) return;
            waiting = value;
            Changed();
        }
    }

    internal ActivityView Snapshot()
    {
        lock (gate)
        {
            var now = clock.GetTimestamp();
            DirectionView? upload = null, download = null, move = null;
            string? line = null;
            var mostLeft = -1;
            foreach (var lane in lanes.Values)
            {
                // A direction shows while files are still on their way.
                if (lane.FilesTotal <= 0 || lane.FilesDone >= lane.FilesTotal) continue;
                var view = lane.Direction == Directions.Move ? MoveView(lane) : TransferView(lane, now);
                switch (lane.Direction)
                {
                    case Directions.Upload: upload = view; break;
                    case Directions.Download: download = view; break;
                    default: move = view; break;
                }
                var left = lane.FilesTotal - lane.FilesDone;
                if (left > mostLeft) { mostLeft = left; line = view.Line; }
            }
            var files = active.Take(ActiveShown).Select(t => new ActiveTransferView(t.Path, NameOf(t.Path), t.Direction, t.Done, t.Bytes)).ToArray();
            return new ActivityView(line, upload, download, move, waiting, files);
        }
    }

    private DirectionView TransferView(Lane lane, long now)
    {
        var inFlight = active.Where(t => t.Direction == lane.Direction).Sum(t => t.Done);
        var bytesDone = Math.Min(lane.BytesTotal, lane.BytesDone + inFlight);
        Sample(lane, now, bytesDone);
        var bytesLeft = Math.Max(0, lane.BytesTotal - bytesDone);
        var filesLeft = Math.Max(0, lane.FilesTotal - lane.FilesDone);
        int? secondsLeft = null;
        if (clock.GetElapsedTime(lane.Started, now) >= EstimateAfter && lane.FilesDone >= 2 && filesLeft > 0)
        {
            double? byBytes = lane.BytesPerSecond is > 0 ? bytesLeft / lane.BytesPerSecond.Value : null;
            double? byFiles = lane.FilesPerSecond is > 0 ? filesLeft / lane.FilesPerSecond.Value : null;
            if (byBytes is not null || byFiles is not null) secondsLeft = (int)Math.Ceiling(Math.Max(byBytes ?? 0, byFiles ?? 0));
        }
        var verb = lane.Direction == Directions.Upload ? "Uploading" : "Downloading";
        var text = $"{verb} {Number(lane.FilesDone)} of {Files(lane.FilesTotal)}, {Bytes(bytesLeft)} left";
        if (secondsLeft is { } seconds) text += ", " + TimeLeft(seconds);
        return new DirectionView(lane.FilesDone, lane.FilesTotal, bytesDone, lane.BytesTotal, (long)Math.Round(lane.BytesPerSecond ?? 0), secondsLeft, text);
    }

    private static DirectionView MoveView(Lane lane)
        => new(lane.FilesDone, lane.FilesTotal, 0, 0, 0, null, $"Moving {Files(lane.FilesTotal)} to {Where(lane.Target ?? "")}");

    // The rates since the last sample, folded into the averages (time constant about 5 seconds).
    private void Sample(Lane lane, long now, long bytesDone)
    {
        var elapsed = clock.GetElapsedTime(lane.SampledAt, now);
        if (elapsed < SampleEvery) return;
        var seconds = elapsed.TotalSeconds;
        var bytesRate = Math.Max(0, bytesDone - lane.SampledBytes) / seconds;
        var filesRate = Math.Max(0, lane.FilesDone - lane.SampledFiles) / seconds;
        var weight = 1 - Math.Exp(-seconds / Smoothing.TotalSeconds);
        lane.BytesPerSecond = lane.BytesPerSecond is { } bytes ? bytes + weight * (bytesRate - bytes) : bytesRate;
        lane.FilesPerSecond = lane.FilesPerSecond is { } files ? files + weight * (filesRate - files) : filesRate;
        lane.SampledAt = now;
        lane.SampledBytes = bytesDone;
        lane.SampledFiles = lane.FilesDone;
    }

    private Lane LaneFor(string direction)
    {
        if (!lanes.TryGetValue(direction, out var lane)) lanes[direction] = lane = new Lane(direction, clock.GetTimestamp());
        return lane;
    }

    private void Changed() => Interlocked.Increment(ref version);

    private static string NameOf(string path) => path[(path.LastIndexOf('/') + 1)..];
    private static string Where(string folder) => folder.Replace("/", " › ", StringComparison.Ordinal);
    private static string Number(long n) => n.ToString("N0", CultureInfo.InvariantCulture);
    private static string Files(int n) => n == 1 ? "1 file" : $"{Number(n)} files";

    // As the window writes sizes (app.js bytes()): 1,024 bytes to a KB, one decimal, ".0" dropped.
    internal static string Bytes(long bytes)
    {
        if (bytes < 1024) return bytes == 1 ? "1 byte" : $"{Number(bytes)} bytes";
        string[] units = ["KB", "MB", "GB", "TB"];
        var value = bytes / 1024.0;
        var unit = 0;
        while (value >= 1024 && unit < units.Length - 1)
        {
            value /= 1024;
            unit++;
        }
        var text = value.ToString("F1", CultureInfo.InvariantCulture);
        if (text.EndsWith(".0", StringComparison.Ordinal)) text = text[..^2];
        return text + " " + units[unit];
    }

    // As the window writes time left (app.js timeLeft()).
    internal static string TimeLeft(int seconds) => seconds switch
    {
        >= 90 => $"about {(int)Math.Round(seconds / 60.0, MidpointRounding.AwayFromZero)} min",
        >= 55 => "about 1 min",
        >= 10 => $"about {(int)Math.Round(seconds / 5.0, MidpointRounding.AwayFromZero) * 5} sec",
        _ => "less than a minute",
    };
}
