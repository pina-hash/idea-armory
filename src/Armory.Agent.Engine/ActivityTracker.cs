using System.Globalization;
using Armory.Agent.Engine.View;

namespace Armory.Agent.Engine;

// What is moving right now (v2-design.md 4.4), per direction: uploading, downloading and moving,
// with files and bytes done and in all, speed and time left, the files in flight and the waiting
// line. The engine thread tells it what a pass will move and when each file starts and ends;
// BlobClient reports bytes from any thread (each transfer is an IProgress<long>). Snapshots are
// immutable ActivityViews. Speed is an exponential average over about 5 seconds of
// TimeProvider.GetTimestamp time (a test clock that freezes UtcNow still moves it), counted from
// the moment the direction's first file started and corrected for the time it has had (an
// average that starts from nothing would read low for its first seconds, and time left high);
// time left appears after 3 seconds and 2 files. After its uploads, a pass lets go of the locks
// of what it checked in or added: "Checking in 412 of 4,900 files", shown as the upload direction.
internal sealed class ActivityTracker(TimeProvider clock)
{
    internal const int ActiveShown = 8;
    // The locks a check in or an add lets go of at the end of a pass (shown as the upload direction).
    internal const string CheckIn = "checkIn";
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

    private sealed class Lane(string direction)
    {
        internal readonly string Direction = direction;
        internal readonly Dictionary<string, long> Expected = new(StringComparer.OrdinalIgnoreCase);
        internal int FilesTotal, FilesDone;
        internal long BytesTotal, BytesDone;
        internal string? Target;
        // When the first file started: speed and time left count from here, never from when the
        // pass planned the files.
        internal long? Begun;
        internal long SampledAt, SampledBytes;
        internal int SampledFiles;
        // Exponential averages that start from zero, and the weight they have gathered
        // (1 - e^(-t/5 s)): the average divided by it is the true weighted average of the rates.
        internal double BytesAverage, FilesAverage, Weight;
        internal double? BytesPerSecond => Weight > 0 ? BytesAverage / Weight : null;
        internal double? FilesPerSecond => Weight > 0 ? FilesAverage / Weight : null;
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
            Begin(lane);
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

    // A planned step with no bytes (a lock let go for a check in) is done.
    internal void Done(string direction, string path)
    {
        lock (gate)
        {
            if (!lanes.TryGetValue(direction, out var lane) || !lane.Expected.Remove(path)) return;
            Begin(lane);
            lane.FilesDone++;
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

    // One move operation starts (a folder renamed here and sent to the team, the team's rename
    // made here, a folder put back, a project renamed, the team's moves of one pass), shown until
    // it ends: "Moving 120 files to Robot 2027 › Gearbox". Each operation has its own count and
    // its one target.
    internal void Moving(int files, string target)
    {
        lock (gate)
        {
            var lane = LaneFor(Directions.Move);
            lane.FilesTotal = Math.Max(0, files);
            lane.FilesDone = 0;
            lane.Target = target;
            Changed();
        }
    }

    // The move operation ended.
    internal void Moved()
    {
        lock (gate)
        {
            if (!lanes.TryGetValue(Directions.Move, out var lane)) return;
            lane.FilesDone = lane.FilesTotal;
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
            DirectionView? upload = null, download = null, move = null, checkIn = null;
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
                    case CheckIn: checkIn = view; break;
                    default: move = view; break;
                }
                var left = lane.FilesTotal - lane.FilesDone;
                if (left > mostLeft) { mostLeft = left; line = view.Line; }
            }
            // The locks let go after the uploads count in the upload direction.
            upload ??= checkIn;
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
        if (lane.Begun is { } begun && clock.GetElapsedTime(begun, now) >= EstimateAfter && lane.FilesDone >= 2 && filesLeft > 0)
        {
            double? byBytes = lane.BytesPerSecond is > 0 && bytesLeft > 0 ? bytesLeft / lane.BytesPerSecond.Value : null;
            double? byFiles = lane.FilesPerSecond is > 0 ? filesLeft / lane.FilesPerSecond.Value : null;
            if (byBytes is not null || byFiles is not null) secondsLeft = (int)Math.Ceiling(Math.Max(byBytes ?? 0, byFiles ?? 0));
        }
        var text = lane.Direction == CheckIn
            ? $"Checking in {Number(lane.FilesDone)} of {Files(lane.FilesTotal)}"
            : $"{(lane.Direction == Directions.Upload ? "Uploading" : "Downloading")} {Number(lane.FilesDone)} of {Files(lane.FilesTotal)}, {Bytes(bytesLeft)} left";
        if (secondsLeft is { } seconds) text += ", " + TimeLeft(seconds);
        return new DirectionView(lane.FilesDone, lane.FilesTotal, bytesDone, lane.BytesTotal, (long)Math.Round(lane.BytesPerSecond ?? 0), secondsLeft, text);
    }

    private static DirectionView MoveView(Lane lane)
        => new(lane.FilesDone, lane.FilesTotal, 0, 0, 0, null, $"Moving {Files(lane.FilesTotal)} to {Where(lane.Target ?? "")}");

    // The direction's first file started: its speed counts from now.
    private void Begin(Lane lane)
    {
        if (lane.Begun is not null) return;
        lane.Begun = lane.SampledAt = clock.GetTimestamp();
        lane.SampledBytes = lane.BytesDone;
        lane.SampledFiles = lane.FilesDone;
    }

    // The rates since the last sample, folded into the averages (time constant about 5 seconds).
    // Both averages start from zero and gather weight 1 - e^(-t/5 s) over the t seconds sampled;
    // divided by that weight, they are the weighted average of the rates seen, so a steady rate
    // reads true from the first seconds.
    private void Sample(Lane lane, long now, long bytesDone)
    {
        if (lane.Begun is null) return;
        var elapsed = clock.GetElapsedTime(lane.SampledAt, now);
        if (elapsed < SampleEvery) return;
        var seconds = elapsed.TotalSeconds;
        var bytesRate = Math.Max(0, bytesDone - lane.SampledBytes) / seconds;
        var filesRate = Math.Max(0, lane.FilesDone - lane.SampledFiles) / seconds;
        var weight = 1 - Math.Exp(-seconds / Smoothing.TotalSeconds);
        lane.BytesAverage += weight * (bytesRate - lane.BytesAverage);
        lane.FilesAverage += weight * (filesRate - lane.FilesAverage);
        lane.Weight += weight * (1 - lane.Weight);
        lane.SampledAt = now;
        lane.SampledBytes = bytesDone;
        lane.SampledFiles = lane.FilesDone;
    }

    private Lane LaneFor(string direction)
    {
        if (!lanes.TryGetValue(direction, out var lane)) lanes[direction] = lane = new Lane(direction);
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
