using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

namespace Armory.Platform.Windows;

public sealed record HoldingProcess(int Id, string Name);
public sealed record OpenFileStatus(bool IsOpen, IReadOnlyList<HoldingProcess> Processes, string? Diagnostic);

public sealed class OpenFileDetector
{
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
    // never holds the engine for minutes. The set holds the files as given.
    public IReadOnlySet<string> OpenAmong(IReadOnlyList<string> files, TimeSpan budget, out string? diagnostic)
    {
        ArgumentNullException.ThrowIfNull(files);
        diagnostic = null;
        var open = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        List<string> rest = [];
        foreach (var file in files)
        {
            if (!File.Exists(file)) continue;
            if (IsBlocked(file, ref diagnostic)) open.Add(file);
            else rest.Add(file);
        }
        if (rest.Count == 0) return open;
        // Its own set: a query still running past the budget never touches the answer returned.
        var held = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var attribute = Task.Run(() =>
        {
            foreach (var batch in rest.Chunk(500)) Attribute(batch, held);
        });
        if (!attribute.Wait(budget))
        {
            diagnostic ??= $"Restart Manager did not answer within {budget.TotalSeconds:0} s; exclusive-open probe used.";
            return open;
        }
        open.UnionWith(held);
        return open;
    }

    private static void Attribute(string[] batch, HashSet<string> held)
    {
        if (Holders(batch.Select(Path.GetFullPath).ToArray(), names: false).Holders.Count == 0) return;
        if (batch.Length == 1) { held.Add(batch[0]); return; }
        var half = batch.Length / 2;
        Attribute(batch[..half], held);
        Attribute(batch[half..], held);
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
