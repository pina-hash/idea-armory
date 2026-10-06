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
    // on each file. FirstOpen names a file that is open, when the probe found one.
    public OpenFileStatus InspectAll(IReadOnlyList<string> files, out string? firstOpen)
    {
        firstOpen = null;
        var existing = files.Where(File.Exists).Select(Path.GetFullPath).ToArray();
        List<HoldingProcess> holders = [];
        string? diagnostic = null;
        foreach (var batch in existing.Chunk(500))
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

    private static bool IsBlocked(string file, ref string? diagnostic)
    {
        try { using var probe = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.None); return false; }
        catch (FileNotFoundException) { return false; }
        catch (DirectoryNotFoundException) { return false; }
        catch (IOException error) { diagnostic ??= error.Message; return true; }
        catch (UnauthorizedAccessException error) { diagnostic ??= error.Message; return true; }
    }

    private static (IReadOnlyList<HoldingProcess> Holders, string? Diagnostic) Holders(string[] files)
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
                                try { using var running = Process.GetProcessById(process.Process.Id); name = running.ProcessName; }
                                catch (ArgumentException) { }
                                catch (InvalidOperationException) { }
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
