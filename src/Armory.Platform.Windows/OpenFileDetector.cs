using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

namespace Armory.Platform.Windows;

public sealed record HoldingProcess(int Id, string Name);
public sealed record OpenFileStatus(bool IsOpen, IReadOnlyList<HoldingProcess> Processes, string? Diagnostic);
// One question about many files: the files open (as given), whether Restart Manager gave up on
// part of it (the probe alone answered those), why, and the programs it found holding files.
public sealed record OpenAmongResult(IReadOnlySet<string> Open, bool TimedOut, string? Diagnostic, IReadOnlyList<string> Holders);

public sealed class OpenFileDetector(TimeProvider? clock = null)
{
    // How long a file Restart Manager cleared stays cleared while its NTFS id and last-write time
    // are the same (a pass asks about the same files in its plan, its check ins and its read-only
    // rule within a few seconds).
    public static readonly TimeSpan RememberFor = TimeSpan.FromSeconds(3);
    // A Restart Manager query still running this long after it started is canceled.
    internal static readonly TimeSpan AttributionLimit = TimeSpan.FromSeconds(60);
    private readonly TimeProvider clock = clock ?? TimeProvider.System;
    private readonly object gate = new();
    private readonly Dictionary<string, (FileStamp Stamp, long At)> cleared = new(StringComparer.OrdinalIgnoreCase);
    private Task? attributing;
    private int running;
    // Restart Manager questions started, and the most that ever ran at once (tests: one).
    internal int Attributions { get; private set; }
    internal int MostAtOnce { get; private set; }
    private sealed record FileStamp(string Id, DateTime LastWriteUtc);

    public OpenFileStatus Inspect(string file)
    {
        if (!File.Exists(file)) return new(false, [], null);
        var (holders, diagnostic) = Holders([Path.GetFullPath(file)]);
        var blocked = IsBlocked(file, ref diagnostic);
        return new(blocked || holders.Count > 0, holders, diagnostic);
    }

    // A whole folder before it is moved: one Restart Manager session per batch of files (one
    // session per file would take seconds for a large assembly), then the exclusive-open probe
    // on each file. firstOpen is the open file (as given) when the probe found one.
    public OpenFileStatus InspectAll(IReadOnlyList<string> files, out string? firstOpen)
    {
        firstOpen = null;
        var existing = files.Where(File.Exists).ToArray();
        List<HoldingProcess> holders = [];
        string? diagnostic = null;
        foreach (var batch in existing.Select(Path.GetFullPath).Chunk(500))
        {
            var (found, problem) = Holders(batch);
            holders.AddRange(found);
            diagnostic ??= problem;
        }
        foreach (var file in existing)
        {
            if (!IsBlocked(file, ref diagnostic)) continue;
            firstOpen = file;
            break;
        }
        var processes = holders.DistinctBy(p => p.Id).OrderBy(p => p.Id).ToArray();
        return new(firstOpen is not null || processes.Length > 0, processes, diagnostic);
    }

    // Which of many files are open, each answered as Inspect answers it: the exclusive-open probe
    // on each file (cheap), then Restart Manager for the rest, one session per batch of 500, a
    // batch with a holder split in halves until each held file is found (a few sessions per held
    // file). One session per file cost about 28 ms, so a pass over 1,500 files spent 40 seconds
    // here (the field reports of 0.3.1). Restart Manager has budget to answer; past it, the files
    // it has not cleared are answered by the probe alone and diagnostic says so, so a hung query
    // never holds the engine for minutes. The set holds the files as given. Nothing remembered
    // is used: every file is asked now.
    public IReadOnlySet<string> OpenAmong(IReadOnlyList<string> files, TimeSpan budget, out string? diagnostic)
    {
        ArgumentNullException.ThrowIfNull(files);
        var answer = OpenAmongAsync(files, budget, fresh: true, CancellationToken.None).GetAwaiter().GetResult();
        diagnostic = answer.Diagnostic;
        return answer.Open;
    }

    // The same question off the caller's thread (0.3.3, feedback N6: the engine thread waited out
    // the 10 second budget every pass on 0.3.2). The probe runs on a worker thread and stops
    // between files when the token is canceled. Restart Manager runs one question at a time:
    // while an earlier one still runs (it went past its budget), the probe alone answers and
    // TimedOut says so, so queries never pile up and slow each other down. A query goes on past
    // its budget until it ends (at most AttributionLimit, or a canceled token, checked between
    // sessions), and what it found is remembered for the next question: a file it cleared, whose
    // NTFS id and last-write time are still the same, is not asked again for RememberFor, unless
    // fresh. Holders names the programs Restart Manager found holding files.
    public Task<OpenAmongResult> OpenAmongAsync(IReadOnlyList<string> files, TimeSpan budget, bool fresh, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(files);
        return Task.Run(() => AskAsync([.. files], budget, fresh, cancellationToken), cancellationToken);
    }

    private async Task<OpenAmongResult> AskAsync(string[] files, TimeSpan budget, bool fresh, CancellationToken ct)
    {
        string? diagnostic = null;
        var open = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        List<(string File, FileStamp Stamp)> rest = [];
        foreach (var file in files)
        {
            ct.ThrowIfCancellationRequested();
            var stamp = Stamp(file);
            if (stamp is null) continue; // not there: not open
            if (IsBlocked(file, ref diagnostic)) { open.Add(file); continue; }
            if (!fresh && Cleared(file, stamp)) continue;
            rest.Add((file, stamp));
        }
        if (rest.Count == 0) return new(open, false, diagnostic, []);
        Task<(HashSet<string> Held, List<string> Holders, bool Complete)>? attribution = null;
        lock (gate)
        {
            if (attributing is null || attributing.IsCompleted) attributing = attribution = Attribute(rest, ct);
        }
        if (attribution is null) return new(open, true, diagnostic ?? "Restart Manager is still answering an earlier question; exclusive-open probe used.", []);
        if (await Task.WhenAny(attribution, Task.Delay(budget, ct)).ConfigureAwait(false) != attribution)
        {
            ct.ThrowIfCancellationRequested();
            return new(open, true, diagnostic ?? $"Restart Manager did not answer within {budget.TotalSeconds:0} s; exclusive-open probe used.", []);
        }
        var (held, holders, complete) = await attribution.ConfigureAwait(false);
        ct.ThrowIfCancellationRequested();
        if (!complete) return new(open, true, diagnostic ?? "Restart Manager did not finish; exclusive-open probe used.", []);
        open.UnionWith(held);
        return new(open, false, diagnostic, holders);
    }

    // One Restart Manager question on a worker thread, stopped between sessions by the token or
    // after AttributionLimit (then not complete, and nothing of it remembered), and what it found
    // remembered as of the moment it was asked.
    private Task<(HashSet<string> Held, List<string> Holders, bool Complete)> Attribute(List<(string File, FileStamp Stamp)> files, CancellationToken ct) => Task.Run(() =>
    {
        using var limit = CancellationTokenSource.CreateLinkedTokenSource(ct);
        limit.CancelAfter(AttributionLimit);
        var now = Interlocked.Increment(ref running);
        lock (gate) { Attributions++; MostAtOnce = Math.Max(MostAtOnce, now); }
        try
        {
            var held = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var holders = new List<string>();
            try { foreach (var batch in files.Select(f => f.File).Chunk(500)) Attribute(batch, held, holders, limit.Token); }
            catch (OperationCanceledException) { return (held, holders, false); }
            var at = clock.GetTimestamp();
            lock (gate)
                foreach (var (file, stamp) in files)
                {
                    if (held.Contains(file)) cleared.Remove(file);
                    else cleared[file] = (stamp, at);
                }
            return (held, holders, true);
        }
        finally { Interlocked.Decrement(ref running); }
    });

    // A file Restart Manager cleared a moment ago and that is still the same file, unchanged.
    private bool Cleared(string file, FileStamp stamp)
    {
        lock (gate)
            return cleared.TryGetValue(file, out var known) && known.Stamp == stamp && clock.GetElapsedTime(known.At) < RememberFor;
    }

    // The file's NTFS id and last-write time, or null when no file is there. One that can't be
    // read is asked about, never remembered.
    private static FileStamp? Stamp(string file)
    {
        try { return NativeMethods.FileStamp(file) is { } stamp ? new(stamp.Id, stamp.LastWriteUtc) : null; }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception)
        { return new(Guid.NewGuid().ToString("N"), DateTime.MinValue); }
    }

    private static void Attribute(string[] batch, HashSet<string> held, List<string> holders, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var found = Holders(batch.Select(Path.GetFullPath).ToArray(), names: false).Holders;
        if (found.Count == 0) return;
        if (batch.Length == 1)
        {
            held.Add(batch[0]);
            foreach (var holder in found) if (!holders.Contains(holder.Name, StringComparer.OrdinalIgnoreCase)) holders.Add(holder.Name);
            return;
        }
        var half = batch.Length / 2;
        Attribute(batch[..half], held, holders, ct);
        Attribute(batch[half..], held, holders, ct);
    }

    private static bool IsBlocked(string file, ref string? diagnostic)
    {
        try { using var probe = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.None); return false; }
        catch (FileNotFoundException) { return false; }
        catch (DirectoryNotFoundException) { return false; }
        catch (IOException error) { diagnostic ??= error.Message; return true; }
        catch (UnauthorizedAccessException error) { diagnostic ??= error.Message; return true; }
    }

    private static (IReadOnlyList<HoldingProcess> Holders, string? Diagnostic) Holders(string[] files, bool names = true)
    {
        List<HoldingProcess> holders = [];
        string? diagnostic = null;
        var key = new StringBuilder(33);
        var start = RmStartSession(out var session, 0, key);
        if (start == 0)
        {
            try
            {
                var registered = RmRegisterResources(session, (uint)files.Length, files, 0, null, 0, null);
                if (registered == 0)
                {
                    uint needed = 0, count = 0, reasons = 0;
                    var result = RmGetList(session, out needed, ref count, null, ref reasons);
                    for (var attempt = 0; result == 234 && attempt < 4; attempt++)
                    {
                        var processes = new RmProcessInfo[needed];
                        count = needed;
                        result = RmGetList(session, out needed, ref count, processes, ref reasons);
                        if (result == 0)
                            foreach (var process in processes.Take((int)count))
                            {
                                var name = process.AppName;
                                if (names)
                                {
                                    try { using var running = Process.GetProcessById(process.Process.Id); name = running.ProcessName; }
                                    catch (ArgumentException) { }
                                    catch (InvalidOperationException) { }
                                }
                                holders.Add(new(process.Process.Id, name));
                            }
                    }
                    if (result != 0) diagnostic = $"Restart Manager returned {result}; exclusive-open probe used.";
                }
                else diagnostic = $"Restart Manager registration returned {registered}; exclusive-open probe used.";
            }
            finally { _ = RmEndSession(session); }
        }
        else diagnostic = $"Restart Manager session returned {start}; exclusive-open probe used.";
        return (holders.OrderBy(p => p.Id).ToArray(), diagnostic);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct RmUniqueProcess
    {
        internal int Id;
        internal System.Runtime.InteropServices.ComTypes.FILETIME StartTime;
    }
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct RmProcessInfo
    {
        internal RmUniqueProcess Process;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)] internal string AppName;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)] internal string ServiceName;
        internal uint ApplicationType;
        internal uint Status;
        internal uint SessionId;
        [MarshalAs(UnmanagedType.Bool)] internal bool Restartable;
    }
    [DllImport("rstrtmgr.dll", CharSet = CharSet.Unicode)]
    private static extern int RmStartSession(out uint session, int flags, StringBuilder key);
    [DllImport("rstrtmgr.dll", CharSet = CharSet.Unicode)]
    private static extern int RmRegisterResources(uint session, uint files, string[] fileNames, uint applications, RmUniqueProcess[]? processes, uint services, string[]? serviceNames);
    [DllImport("rstrtmgr.dll")]
    private static extern int RmGetList(uint session, out uint needed, ref uint count, [In, Out] RmProcessInfo[]? processes, ref uint reasons);
    [DllImport("rstrtmgr.dll")]
    private static extern int RmEndSession(uint session);
}
