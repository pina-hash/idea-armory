using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Principal;

namespace Armory.SolidWorks;

// Discovery: the SolidWorks processes this Armory may link to are this Windows user's own, in
// this sign-in session (another user's SolidWorks on a shared lab PC is never touched). One
// started "as administrator" runs at another integrity level, which COM does not cross: it is
// reported once, plainly, and left alone.
internal static class SolidWorksProcesses
{
    // A test names another process (the fake SolidWorks) with this variable; nothing else reads it.
    internal const string ProcessNameVariable = "ARMORY_SOLIDWORKS_PROCESS";

    internal enum Owner { Mine, Elevated, Other }

    internal sealed record Candidate(int Pid, Owner Owner, string? ExecutablePath);

    internal static string ProcessName()
        => Environment.GetEnvironmentVariable(ProcessNameVariable) is { Length: > 0 } name ? name : SwConstants.ProcessName;

    // Every process of that name in this session, with who owns it. Processes of other sessions
    // are skipped without opening them.
    internal static IReadOnlyList<Candidate> Find(string processName)
    {
        var session = Process.GetCurrentProcess().SessionId;
        List<Candidate> found = [];
        foreach (var process in Process.GetProcessesByName(processName))
        {
            using (process)
            {
                try
                {
                    if (process.SessionId != session) continue;
                    found.Add(Inspect(process.Id));
                }
                catch (Exception error) when (error is InvalidOperationException or System.ComponentModel.Win32Exception) { }
            }
        }
        return found;
    }

    // Whose process this is: this user's at this integrity level, this user's but elevated (or
    // its token can't be read, which is how an elevated process looks from here), or another's
    // (one that can't even be opened is never this user's).
    internal static Candidate Inspect(int pid)
    {
        var process = Native.OpenProcess(Native.PROCESS_QUERY_LIMITED_INFORMATION, false, pid);
        if (process == IntPtr.Zero) return new(pid, Owner.Other, null);
        try
        {
            var path = ImagePath(process);
            if (!Native.OpenProcessToken(process, Native.TOKEN_QUERY, out var token)) return new(pid, Owner.Elevated, path);
            try
            {
                using var current = WindowsIdentity.GetCurrent();
                var me = current.User;
                var owner = TokenUser(token);
                if (owner is null || me is null || !owner.Equals(me)) return new(pid, owner is null ? Owner.Elevated : Owner.Other, path);
                return new(pid, Elevated(token) && !Elevated() ? Owner.Elevated : Owner.Mine, path);
            }
            finally { Native.CloseHandle(token); }
        }
        finally { Native.CloseHandle(process); }
    }

    // SOLIDWORKS.exe's full path (its install folder holds api\redist).
    private static string? ImagePath(IntPtr process)
    {
        var buffer = new char[1024];
        var size = buffer.Length;
        return Native.QueryFullProcessImageNameW(process, 0, buffer, ref size) ? new string(buffer, 0, size) : null;
    }

    private static SecurityIdentifier? TokenUser(IntPtr token)
    {
        Native.GetTokenInformation(token, Native.TokenUser, IntPtr.Zero, 0, out var length);
        if (length <= 0) return null;
        var buffer = Marshal.AllocHGlobal(length);
        try
        {
            if (!Native.GetTokenInformation(token, Native.TokenUser, buffer, length, out _)) return null;
            // TOKEN_USER starts with a SID_AND_ATTRIBUTES whose first field points at the SID.
            return new SecurityIdentifier(Marshal.ReadIntPtr(buffer));
        }
        finally { Marshal.FreeHGlobal(buffer); }
    }

    private static bool Elevated(IntPtr token)
    {
        var buffer = Marshal.AllocHGlobal(4);
        try { return Native.GetTokenInformation(token, Native.TokenElevation, buffer, 4, out _) && Marshal.ReadInt32(buffer) != 0; }
        finally { Marshal.FreeHGlobal(buffer); }
    }

    // Armory itself runs elevated (a CI runner): then an elevated SolidWorks is at its level.
    private static bool Elevated()
    {
        using var identity = WindowsIdentity.GetCurrent();
        return Elevated(identity.Token);
    }

    // Still running?
    internal static bool Alive(int pid)
    {
        try
        {
            using var process = Process.GetProcessById(pid);
            return !process.HasExited;
        }
        catch (Exception error) when (error is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception) { return false; }
    }
}
