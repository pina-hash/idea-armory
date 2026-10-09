using System.ComponentModel;
using System.Globalization;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security.Principal;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace Armory.Agent;

// The right-click items (docs/agent/EXPLORER.md), in the words ArmoryShell.exe sends.
internal enum ShellVerb { CheckOut, CheckOutAndOpen, CheckIn, Undo, Show, ForceCheckIn }

internal static class ShellVerbNames
{
    private static readonly IReadOnlyDictionary<string, ShellVerb> ByName = new Dictionary<string, ShellVerb>(StringComparer.Ordinal)
    {
        ["checkout"] = ShellVerb.CheckOut,
        ["checkoutopen"] = ShellVerb.CheckOutAndOpen,
        ["checkin"] = ShellVerb.CheckIn,
        ["undo"] = ShellVerb.Undo,
        ["show"] = ShellVerb.Show,
        ["forcecheckin"] = ShellVerb.ForceCheckIn,
    };

    internal static bool TryParse(string name, out ShellVerb verb) => ByName.TryGetValue(name, out verb);

    internal static string Name(ShellVerb verb) => ByName.First(p => p.Value == verb).Key;

    // These act on one item at once (their menu items allow a single selection).
    internal static bool IsSingle(ShellVerb verb) => verb is ShellVerb.CheckOutAndOpen or ShellVerb.Show;
}

// One click on one item: the verb, the full path Explorer gave, the forwarder's
// GetTickCount64() when it sent it, and when Armory received it.
internal sealed record ShellRequest(ShellVerb Verb, string Path, ulong SentTick, DateTimeOffset ArrivedAt);

// What the host turns into one engine action: one verb, 1 to 100 distinct paths in arrival order.
internal sealed record ShellBatch(ShellVerb Verb, IReadOnlyList<string> Paths, DateTimeOffset FirstArrived, DateTimeOffset Closed);

// The line ArmoryShell.exe writes: "1<TAB>verb<TAB>path<TAB>tick<LF>" in UTF-8, at most 100 KB.
internal static class ShellLine
{
    internal const int MaxBytes = 100 * 1024;
    internal const int MaxPathLength = 32767;
    internal const byte Ack = 0x06;
    internal const byte Refused = 0x15;

    internal static bool TryParse(ReadOnlySpan<byte> line, DateTimeOffset arrivedAt, out ShellRequest? request)
    {
        request = null;
        if (line.Length == 0 || line.Length > MaxBytes || line[^1] != (byte)'\n') return false;
        string text;
        try { text = new UTF8Encoding(false, throwOnInvalidBytes: true).GetString(line[..^1]); }
        catch (DecoderFallbackException) { return false; }
        var fields = text.Split('\t');
        if (fields.Length != 4 || fields[0] != "1") return false;
        if (!ShellVerbNames.TryParse(fields[1], out var verb)) return false;
        var path = fields[2];
        // The forwarder's own rule: no control character (no tab or line break) and no quote.
        if (path.Length is 0 or > MaxPathLength || path.Any(c => c < ' ' || c == '"')) return false;
        if (fields[3].Length is 0 or > 20 || !fields[3].All(char.IsAsciiDigit)) return false;
        if (!ulong.TryParse(fields[3], NumberStyles.None, CultureInfo.InvariantCulture, out var tick)) return false;
        request = new ShellRequest(verb, path, tick, arrivedAt);
        return true;
    }

    internal static byte[] Format(ShellVerb verb, string path, ulong tick) =>
        Encoding.UTF8.GetBytes("1\t" + ShellVerbNames.Name(verb) + "\t" + path + "\t" + tick.ToString(CultureInfo.InvariantCulture) + "\n");
}

// Gathers the forwarders of one right-click into one batch per verb (Explorer starts one
// ArmoryShell.exe per selected item). A batch closes Quiet after its last arrival, Cap after its
// first, or at MaxPaths paths (Explorer's limit for these items); verbs never mix; Check out and
// open and Show in Armory go at once; a path twice in one batch counts once. Pure: time comes
// in with each call, and every closed batch goes to ready, outside the lock, at once.
internal sealed class ShellBatcher(Action<ShellBatch> ready)
{
    internal static readonly TimeSpan Quiet = TimeSpan.FromMilliseconds(300);
    internal static readonly TimeSpan Cap = TimeSpan.FromSeconds(5);
    internal const int MaxPaths = 100;

    private sealed class Open(DateTimeOffset first)
    {
        internal DateTimeOffset First { get; } = first;
        internal DateTimeOffset Last { get; set; } = first;
        internal List<string> Paths { get; } = [];
        internal HashSet<string> Seen { get; } = new(StringComparer.OrdinalIgnoreCase);
        internal DateTimeOffset Due => Min(Last + Quiet, First + Cap);
    }

    private readonly Lock gate = new();
    private readonly Dictionary<ShellVerb, Open> open = [];

    // When the next batch closes by itself, or null with nothing open.
    internal DateTimeOffset? NextDue
    {
        get
        {
            lock (gate) return open.Count == 0 ? null : open.Values.Min(b => b.Due);
        }
    }

    internal void Add(ShellRequest request)
    {
        List<ShellBatch> closed = [];
        var at = request.ArrivedAt;
        lock (gate)
        {
            CloseDue(at, closed);
            if (ShellVerbNames.IsSingle(request.Verb)) closed.Add(new(request.Verb, [request.Path], at, at));
            else
            {
                if (!open.TryGetValue(request.Verb, out var batch)) open[request.Verb] = batch = new(at);
                batch.Last = at;
                if (batch.Seen.Add(request.Path)) batch.Paths.Add(request.Path);
                if (batch.Paths.Count >= MaxPaths)
                {
                    open.Remove(request.Verb);
                    closed.Add(new(request.Verb, batch.Paths, batch.First, at));
                }
            }
        }
        foreach (var batch in closed) ready(batch);
    }

    // Closes every batch that is due at now.
    internal void Tick(DateTimeOffset now)
    {
        List<ShellBatch> closed = [];
        lock (gate) CloseDue(now, closed);
        foreach (var batch in closed) ready(batch);
    }

    // Closes everything still open (Armory is stopping).
    internal void Flush(DateTimeOffset now)
    {
        List<ShellBatch> closed = [];
        lock (gate)
        {
            foreach (var (verb, batch) in open.OrderBy(b => b.Value.First)) closed.Add(new(verb, batch.Paths, batch.First, now));
            open.Clear();
        }
        foreach (var batch in closed) ready(batch);
    }

    private void CloseDue(DateTimeOffset now, List<ShellBatch> closed)
    {
        foreach (var (verb, batch) in open.Where(b => b.Value.Due <= now).OrderBy(b => b.Value.Due).ToArray())
        {
            open.Remove(verb);
            closed.Add(new(verb, batch.Paths, batch.First, now));
        }
    }

    private static DateTimeOffset Min(DateTimeOffset a, DateTimeOffset b) => a < b ? a : b;
}

// The pipe ArmoryShell.exe writes to: \\.\pipe\IDEA-Armory-Agent-<user SID><instance suffix>-shell,
// for this Windows user only, never from another computer. Each connection carries one line;
// Armory answers 0x06 once the line is taken (0x15 when it is refused) and hands it to a
// ShellBatcher; batch gets each closed batch on a thread-pool thread and must not block (it
// hands the batch to the engine and returns). The inbox never calls the engine itself.
internal sealed class ShellInbox : IDisposable
{
    internal const string PipeVariable = "ARMORY_SHELL_PIPE";
    internal const int Listeners = 4;
    private static readonly TimeSpan ReadLimit = TimeSpan.FromSeconds(5);

    private readonly string pipeName;
    private readonly Action<string>? log;
    private readonly TimeProvider time;
    private readonly ShellBatcher batcher;
    private readonly Lock timerGate = new();
    private readonly CancellationTokenSource stopping = new();
    private readonly List<Task> loops = [];
    private ITimer? timer;
    private int started;

    internal ShellInbox(AgentPaths paths, Action<ShellBatch> batch, Action<string>? log = null, TimeProvider? timeProvider = null)
        : this(PipeName(paths), batch, log, timeProvider) { }

    internal ShellInbox(string pipeName, Action<ShellBatch> batch, Action<string>? log = null, TimeProvider? timeProvider = null)
    {
        this.pipeName = pipeName;
        this.log = log;
        time = timeProvider ?? TimeProvider.System;
        // A failing host never stops the inbox, nor the timer thread.
        batcher = new ShellBatcher(closed =>
        {
            try { batch(closed); }
            catch (Exception error) { log?.Invoke("shell batch failed: " + error); }
        });
    }

    internal string Name => pipeName;

    // The agent's single-instance name plus "-shell". ARMORY_SHELL_PIPE replaces it for a test
    // instance only (ARMORY_DATA_DIR), exactly as ArmoryShell.exe reads it.
    internal static string PipeName(AgentPaths paths, string userSid)
    {
        var chosen = Environment.GetEnvironmentVariable(PipeVariable);
        if (paths.IsOverridden && !string.IsNullOrWhiteSpace(chosen)) return chosen.Trim();
        return "IDEA-Armory-Agent-" + userSid + paths.InstanceSuffix + "-shell";
    }

    [SupportedOSPlatform("windows")]
    internal static string PipeName(AgentPaths paths) =>
        PipeName(paths, WindowsIdentity.GetCurrent().User?.Value ?? throw new InvalidOperationException("This Windows account has no SID."));

    // Starts the listeners; call once, right after SingleInstance.Acquire and before the window,
    // so a forwarder never waits for WebView2. Throws IOException when another process already
    // serves this pipe.
    [SupportedOSPlatform("windows")]
    internal void Start()
    {
        if (Interlocked.Exchange(ref started, 1) != 0) throw new InvalidOperationException("The shell inbox is already started.");
        timer = time.CreateTimer(_ => OnTimer(), null, Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
        // The first instance claims the name, so nothing else can serve it to our forwarders.
        var first = CreateServer(first: true);
        for (var i = 0; i < Listeners; i++)
        {
            var server = i == 0 ? first : CreateServer(first: false);
            loops.Add(Task.Run(() => Listen(server)));
        }
    }

    internal void Dispose(bool flush)
    {
        if (stopping.IsCancellationRequested) return;
        stopping.Cancel();
        try { Task.WaitAll(loops.ToArray(), TimeSpan.FromSeconds(5)); }
        catch (AggregateException) { }
        lock (timerGate) timer?.Dispose();
        if (flush) batcher.Flush(time.GetUtcNow());
    }

    // Stops listening; batches still open are dropped (Armory is quitting).
    public void Dispose() => Dispose(flush: false);

    [SupportedOSPlatform("windows")]
    private async Task Listen(NamedPipeServerStream server)
    {
        var token = stopping.Token;
        while (!token.IsCancellationRequested)
        {
            try
            {
                await server.WaitForConnectionAsync(token).ConfigureAwait(false);
                var connected = server;
                server = CreateServer(first: false);
                _ = Serve(connected, token);
            }
            catch (OperationCanceledException) { break; }
            catch (Exception error) when (error is IOException or Win32Exception or ObjectDisposedException)
            {
                log?.Invoke("shell pipe: " + error.Message);
                await server.DisposeAsync().ConfigureAwait(false);
                try
                {
                    await Task.Delay(TimeSpan.FromSeconds(1), token).ConfigureAwait(false);
                    server = CreateServer(first: false);
                }
                catch (OperationCanceledException) { return; }
                catch (Exception again) when (again is IOException or Win32Exception)
                {
                    log?.Invoke("shell pipe: " + again.Message);
                    return;
                }
            }
        }
        await server.DisposeAsync().ConfigureAwait(false);
    }

    // One connection, one line.
    private async Task Serve(NamedPipeServerStream pipe, CancellationToken stop)
    {
        await using (pipe.ConfigureAwait(false))
        {
            try
            {
                using var limit = CancellationTokenSource.CreateLinkedTokenSource(stop);
                limit.CancelAfter(ReadLimit);
                var buffer = new byte[ShellLine.MaxBytes + 1];
                var length = 0;
                while (length < buffer.Length)
                {
                    var read = await pipe.ReadAsync(buffer.AsMemory(length), limit.Token).ConfigureAwait(false);
                    if (read == 0) break;
                    length += read;
                    if (buffer[length - 1] == (byte)'\n') break;
                }
                var taken = ShellLine.TryParse(buffer.AsSpan(0, length), time.GetUtcNow(), out var request);
                await pipe.WriteAsync(new[] { taken ? ShellLine.Ack : ShellLine.Refused }, limit.Token).ConfigureAwait(false);
                await pipe.FlushAsync(limit.Token).ConfigureAwait(false);
                if (taken)
                {
                    batcher.Add(request!);
                    Rearm();
                }
                else log?.Invoke("shell pipe: refused a line of " + length + " bytes");
                // The forwarder closes its end once it has read the answer.
                while (await pipe.ReadAsync(buffer.AsMemory(0, 1), limit.Token).ConfigureAwait(false) > 0) { }
            }
            catch (Exception error) when (error is OperationCanceledException or IOException or ObjectDisposedException) { }
        }
    }

    private void OnTimer()
    {
        try { batcher.Tick(time.GetUtcNow()); }
        finally { Rearm(); }
    }

    // The timer is set from the batcher's state read under the same lock, so the last setting
    // always reflects every arrival before it.
    private void Rearm()
    {
        lock (timerGate)
        {
            if (stopping.IsCancellationRequested || timer is null) return;
            var due = batcher.NextDue;
            var wait = due is null ? Timeout.InfiniteTimeSpan : due.Value - time.GetUtcNow();
            if (due is not null && wait < TimeSpan.Zero) wait = TimeSpan.Zero;
            timer.Change(wait, Timeout.InfiniteTimeSpan);
        }
    }

    // CreateNamedPipe with PIPE_REJECT_REMOTE_CLIENTS (NamedPipeServerStream does not set it),
    // overlapped, and a DACL that admits only this Windows user.
    [SupportedOSPlatform("windows")]
    private NamedPipeServerStream CreateServer(bool first)
    {
        var user = WindowsIdentity.GetCurrent().User?.Value ?? throw new InvalidOperationException("This Windows account has no SID.");
        if (!ConvertStringSecurityDescriptorToSecurityDescriptorW("O:" + user + "D:P(A;;GA;;;" + user + ")", 1, out var descriptor, IntPtr.Zero))
            throw new Win32Exception(Marshal.GetLastWin32Error());
        try
        {
            var attributes = new SecurityAttributes { Length = Marshal.SizeOf<SecurityAttributes>(), Descriptor = descriptor, InheritHandle = 0 };
            var mode = AccessDuplex | FlagOverlapped | (first ? FlagFirstPipeInstance : 0);
            var handle = CreateNamedPipeW(@"\\.\pipe\" + pipeName, mode, TypeByte | ReadModeByte | Wait | RejectRemoteClients,
                UnlimitedInstances, 4096, 4096, 0, ref attributes);
            if (handle.IsInvalid)
            {
                var error = Marshal.GetLastWin32Error();
                handle.Dispose();
                throw new IOException("The shell pipe " + pipeName + " cannot be created (" + new Win32Exception(error).Message + ").", error);
            }
            return new NamedPipeServerStream(PipeDirection.InOut, isAsync: true, isConnected: false, handle);
        }
        finally
        {
            LocalFree(descriptor);
        }
    }

    private const uint AccessDuplex = 0x00000003;
    private const uint FlagFirstPipeInstance = 0x00080000;
    private const uint FlagOverlapped = 0x40000000;
    private const uint TypeByte = 0x00000000;
    private const uint ReadModeByte = 0x00000000;
    private const uint Wait = 0x00000000;
    private const uint RejectRemoteClients = 0x00000008;
    private const uint UnlimitedInstances = 255;

    [StructLayout(LayoutKind.Sequential)]
    private struct SecurityAttributes
    {
        public int Length;
        public IntPtr Descriptor;
        public int InheritHandle;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafePipeHandle CreateNamedPipeW(string name, uint openMode, uint pipeMode, uint maxInstances, uint outBufferSize,
        uint inBufferSize, uint defaultTimeout, ref SecurityAttributes attributes);

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ConvertStringSecurityDescriptorToSecurityDescriptorW(string text, uint revision, out IntPtr descriptor, IntPtr size);

    [DllImport("kernel32.dll")]
    private static extern IntPtr LocalFree(IntPtr memory);
}
