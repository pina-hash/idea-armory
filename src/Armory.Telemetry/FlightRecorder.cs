namespace Armory.Telemetry;

// What one flight event is about (docs/agent/TELEMETRY.md, "What is collected").
public enum FlightKind : byte
{
    None = 0,
    PassStart,
    PassPhase,
    PassEnd,
    PassYield,
    Rpc,
    Transfer,
    WindowAction,
    Notice,
    FileFailed,
    FileRecovered,
    Exception,
    ReadOnlyBroken,
    RepairedCheckout,
    Note,
    Refusal,
    // 0.3.3: one open-files question (feedback N6), and the computer going to sleep or waking.
    OpenFiles,
    Power,
}

// One event, stored in place in the recorder's ring (no allocation per event). Strings are
// references to strings the caller already has (an RPC name, a path); only an exception's
// text is made when it is recorded. Which fields mean what depends on Kind (FlightJson).
public struct FlightEvent
{
    public long Sequence;
    public long Timestamp;
    public FlightKind Kind;
    public bool Ok;
    public bool Fatal;
    public int Status;
    public long Ms;
    public long Bytes;
    public int Count;
    public int Count2;
    public int Count3;
    public int Count4;
    public string? Name;
    public string? Target;
    public string? Detail;
    public string? Stack;
}

// Sees the events that can start an incident (never the frequent server calls and transfers),
// on the thread that recorded them, after the recorder's lock is released. Must be quick.
public interface IFlightObserver
{
    void Observe(in FlightEvent e);
}

// The flight recorder: a fixed ring of the last Capacity events, allocated once. Recording
// takes one uncontended lock, writes the slot in place and never touches the disk or the
// network; nothing is kept beyond the ring (FlightRecorderTests measures it). The incident
// files read it with Snapshot.
public sealed class FlightRecorder
{
    public const int DefaultCapacity = 4000;
    private readonly FlightEvent[] ring;
    private readonly object gate = new();
    private readonly TimeProvider clock;
    private readonly long anchorTimestamp;
    private readonly DateTimeOffset anchorUtc;
    private long written;

    public FlightRecorder(int capacity = DefaultCapacity, TimeProvider? clock = null)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(capacity, 16);
        ring = new FlightEvent[capacity];
        this.clock = clock ?? TimeProvider.System;
        anchorTimestamp = this.clock.GetTimestamp();
        anchorUtc = this.clock.GetUtcNow();
    }

    public int Capacity => ring.Length;
    // Every event recorded since the start (the ring keeps the last Capacity of them).
    public long Recorded { get { lock (gate) return written; } }
    public IFlightObserver? Observer { get; set; }

    // When an event happened, on the wall clock.
    public DateTimeOffset UtcAt(long timestamp) => anchorUtc + clock.GetElapsedTime(anchorTimestamp, timestamp);
    public long Now() => clock.GetTimestamp();
    public long MillisecondsSince(long timestamp) => (long)clock.GetElapsedTime(timestamp).TotalMilliseconds;

    // ---- Recording ------------------------------------------------------------------------

    public void PassStart(string kind) => Write(FlightKind.PassStart, kind, null, null, true);

    public void PassPhase(string phase, long ms) => Write(FlightKind.PassPhase, phase, null, null, true, ms: ms);

    public void PassEnd(string kind, bool failed, long ms, int downloaded, int uploaded, int keptCopies, int refused)
        => Write(FlightKind.PassEnd, kind, null, null, !failed, ms: ms, count: downloaded, count2: uploaded, count3: keptCopies, count4: refused);

    // A loop pass gave way (its time slice was up, a window action waited, or Armory was paused)
    // with units left, and carried the units still in flight on to the passes after it (0.3.3).
    public void PassYield(string reason, int unitsLeft, long ms, int carried = 0)
        => Write(FlightKind.PassYield, reason, null, null, true, ms: ms, count: unitsLeft, count2: carried);

    // One server call: its name, how long, and its answer (an HTTP status, or 0 with an error
    // code such as "offline"). Never a token or a body.
    public void Rpc(string name, long ms, int status, string? error) => Write(FlightKind.Rpc, name, null, error, error is null, status, ms);

    // One storage transfer ("upload" or "download"): its size, how long, and how it ended.
    public void Transfer(string direction, long bytes, long ms, bool ok, int status, string? error)
        => Write(FlightKind.Transfer, direction, null, error, ok, status, ms, bytes);

    // One window action: its type, how many files or folders it named, and how long the
    // window waited for its answer.
    public void WindowAction(string type, int targets, long ms, bool ok) => Write(FlightKind.WindowAction, type, null, null, ok, ms: ms, count: targets);

    // A notice or problem the window was told about (kind, path, and the raw text).
    public void Notice(string kind, string? path, string? raw) => Write(FlightKind.Notice, kind, path, raw, true);

    // One file's plan or action failed this pass.
    public void FileFailed(string path, Exception error)
        => Write(FlightKind.FileFailed, error.GetType().Name, path, error.Message, false, stack: error.StackTrace);

    // A file that failed before went through.
    public void FileRecovered(string path) => Write(FlightKind.FileRecovered, null, path, null, true);

    // An exception caught somewhere (where). Fatal: nothing handled it (the process or the
    // engine's loop stopped because of it).
    public void Exception(string where, Exception error, bool fatal = false)
        => Write(FlightKind.Exception, error.GetType().FullName, where, error.Message, false, stack: error.ToString(), fatal: fatal);

    // An unhandled exception object that is not an Exception (rare, from native code).
    public void Exception(string where, string type, string message, string? stack, bool fatal)
        => Write(FlightKind.Exception, type, where, message, false, stack: stack, fatal: fatal);

    // A file the read-only rule had made read-only was found writable (and is made read-only again).
    public void ReadOnlyBroken(string path) => Write(FlightKind.ReadOnlyBroken, null, path, null, false);

    // A check out this computer holds on the server had no record here and was repaired.
    public void RepairedCheckout(string path) => Write(FlightKind.RepairedCheckout, null, path, null, false);

    public void Note(string name, string? detail) => Write(FlightKind.Note, name, null, detail, true);

    // A file's refusal started or changed (kind: nameTaken, tooLarge, gate or refused; namesake:
    // the path of the file that holds a taken name), or ended (kind null). Recorded only when it
    // changes, never again on every pass while it stands.
    public void Refusal(string path, string? kind, string? namesake) => Write(FlightKind.Refusal, kind, path, namesake, kind is null);

    // One open-files question the engine asked the platform (0.3.3): how long it took, how many
    // files it named, and whether the platform gave up on part of it at its budget.
    public void OpenFiles(long ms, int files, bool timedOut) => Write(FlightKind.OpenFiles, null, null, null, !timedOut, ms: ms, count: files);

    // The computer went to sleep ("suspend") or woke ("resume"): a pass that spans it is not slow.
    public void Power(string mode) => Write(FlightKind.Power, mode, null, null, true);

    private void Write(FlightKind kind, string? name, string? target, string? detail, bool ok, int status = 0, long ms = 0, long bytes = 0,
        int count = 0, int count2 = 0, int count3 = 0, int count4 = 0, string? stack = null, bool fatal = false)
    {
        var timestamp = clock.GetTimestamp();
        var observer = Observer;
        var watched = observer is not null && kind is not (FlightKind.Rpc or FlightKind.Transfer or FlightKind.Notice or FlightKind.Refusal);
        FlightEvent copy = default;
        lock (gate)
        {
            ref var e = ref ring[(int)(written % ring.Length)];
            e.Sequence = ++written;
            e.Timestamp = timestamp;
            e.Kind = kind;
            e.Ok = ok;
            e.Fatal = fatal;
            e.Status = status;
            e.Ms = ms;
            e.Bytes = bytes;
            e.Count = count;
            e.Count2 = count2;
            e.Count3 = count3;
            e.Count4 = count4;
            e.Name = name;
            e.Target = target;
            e.Detail = detail;
            e.Stack = stack;
            if (watched) copy = e;
        }
        if (watched) observer!.Observe(in copy);
    }

    // ---- Reading --------------------------------------------------------------------------

    // The last max events (all of them by default), oldest first. Copies the ring under the
    // lock; never called by the recording path.
    public FlightEvent[] Snapshot(int max = int.MaxValue)
    {
        lock (gate)
        {
            var held = (int)Math.Min(written, ring.Length);
            var count = Math.Min(held, Math.Max(0, max));
            var events = new FlightEvent[count];
            var first = written - count;
            for (var i = 0; i < count; i++) events[i] = ring[(int)((first + i) % ring.Length)];
            return events;
        }
    }
}
