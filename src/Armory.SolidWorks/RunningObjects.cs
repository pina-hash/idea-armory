using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;

namespace Armory.SolidWorks;

// Finding one SolidWorks session's application object: its entry in the Running Object Table
// ("SolidWorks_PID_<pid>", shown as "!SolidWorks_PID_<pid>" for an item moniker), else the
// registered active object if its GetProcessID is that process (measured on a real 2026 SP4.1).
internal static class RunningObjects
{
    // True when the table names this process's SolidWorks.
    internal static bool Names(string displayName, int pid)
        => displayName.TrimStart('!').Equals(SwConstants.RotName(pid), StringComparison.OrdinalIgnoreCase);

    internal static object? Find(int pid, Action<string>? log = null)
        => FromTable(pid, log) ?? FromActiveObject(pid);

    private static object? FromTable(int pid, Action<string>? log)
    {
        if (Native.GetRunningObjectTable(0, out var table) < 0 || table is null) return null;
        IEnumMoniker? monikers = null;
        IBindCtx? context = null;
        try
        {
            if (Native.CreateBindCtx(0, out context) < 0 || context is null) return null;
            table.EnumRunning(out monikers);
            if (monikers is null) return null;
            var one = new IMoniker[1];
            while (monikers.Next(1, one, IntPtr.Zero) == 0)
            {
                var moniker = one[0];
                try
                {
                    string? name = null;
                    try { moniker.GetDisplayName(context, null, out name); }
                    catch (COMException) { continue; }
                    if (name is null || !Names(name, pid)) continue;
                    if (table.GetObject(moniker, out var found) < 0) return null;
                    log?.Invoke($"solidworks: pid {pid} found in the running object table as {name}");
                    return found;
                }
                finally { Marshal.ReleaseComObject(moniker); }
            }
            return null;
        }
        catch (COMException) { return null; }
        finally
        {
            if (monikers is not null) Marshal.ReleaseComObject(monikers);
            if (context is not null) Marshal.ReleaseComObject(context);
            Marshal.ReleaseComObject(table);
        }
    }

    private static object? FromActiveObject(int pid)
    {
        if (Native.CLSIDFromProgID(SwConstants.ProgId, out var clsid) < 0) return null;
        if (Native.GetActiveObject(ref clsid, IntPtr.Zero, out var active) < 0 || active is null) return null;
        try
        {
            if (Com.Call(active, "GetProcessID") is int id && id == pid) return active;
        }
        catch (Exception error) when (Com.IsComFailure(error)) { }
        Com.Release(active);
        return null;
    }
}
