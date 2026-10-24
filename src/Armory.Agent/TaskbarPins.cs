using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace Armory.Agent;

// Taskbar pins that survive an update (0.4.3, Mr. Pina's "the pin turned into a blank page
// instead of the Armory icon, and I had to repin the Armory"; docs/agent/INSTALL.md, "Taskbar
// pins"). Two things broke a pin. Velopack stamped its own identity, velopack.IDEAArmory, on the
// Start menu shortcut and, at every update, on each pin of Armory it found under User Pinned,
// while the running window is IdeaBosco.Armory, so the pin and the window stopped being one app.
// And moving onto the install that updates itself (0.4.0 and 0.4.1) deleted the older copy a
// pin could point at. tools/package-velopack.ps1 now gives Velopack Armory's own identity; this
// class puts right a pin made before that: every pin of Armory points at this IdeaArmory.exe
// with IdeaBosco.Armory, and the person is told once, in one sentence, how to pin it again,
// because File Explorer can keep showing a blank picture it saved for the old pin.
internal static class TaskbarPins
{
    // Velopack's identity for this app before 0.4.3 ("velopack." and the packId).
    internal const string VelopackAppId = "velopack." + Updates.PackId;

    internal const string RepinWords =
        "If Armory's taskbar button shows a blank picture, right-click it, choose Unpin from taskbar, then find IDEA Armory in Start, right-click it and choose Pin to taskbar.";

    // %APPDATA%\Microsoft\Internet Explorer\Quick Launch\User Pinned\TaskBar.
    internal static string Folder(string roamingAppData) =>
        Path.Combine(roamingAppData, "Microsoft", "Internet Explorer", "Quick Launch", "User Pinned", "TaskBar");

    internal static string Folder() =>
        Folder(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData, Environment.SpecialFolderOption.DoNotVerify));

    // One .lnk in the pins folder, as read.
    internal sealed record Pin(string File, string? Target, string? AppId);

    // One pin to rewrite. Broken: it pointed at nothing.
    internal sealed record Fix(string File, bool Broken);

    // The pins that are Armory's and are not already this exe under Armory's identity. A pin is
    // Armory's by its identity (either one), or by its target: an IdeaArmory.exe in one of the
    // two install folders, or one that no longer exists. A pin of anything else is never touched.
    internal static IReadOnlyList<Fix> Plan(IEnumerable<Pin> pins, string exe, Func<string, bool> fileExists)
    {
        var fixes = new List<Fix>();
        foreach (var pin in pins)
        {
            if (!IsArmorys(pin, fileExists)) continue;
            var pointsHere = pin.Target is not null && SamePath(pin.Target, exe);
            if (pointsHere && string.Equals(pin.AppId, ShellIdentity.AppId, StringComparison.Ordinal)) continue;
            fixes.Add(new Fix(pin.File, pin.Target is null || !fileExists(pin.Target)));
        }
        return fixes;
    }

    internal static bool IsArmorys(Pin pin, Func<string, bool> fileExists)
    {
        if (string.Equals(pin.AppId, ShellIdentity.AppId, StringComparison.Ordinal) || string.Equals(pin.AppId, VelopackAppId, StringComparison.OrdinalIgnoreCase)) return true;
        if (pin.Target is not { Length: > 0 } target) return false;
        var path = target.Replace('/', '\\');
        var cut = path.LastIndexOf('\\');
        if (cut <= 0 || !path[(cut + 1)..].Equals("IdeaArmory.exe", StringComparison.OrdinalIgnoreCase)) return false;
        var folder = path[..cut].TrimEnd('\\');
        return !fileExists(target)
            || folder.EndsWith(@"\" + Updates.PackId + @"\current", StringComparison.OrdinalIgnoreCase)
            || folder.EndsWith(@"\Programs\IDEA Armory", StringComparison.OrdinalIgnoreCase);
    }

    private static bool SamePath(string a, string b) =>
        string.Equals(a.Replace('/', '\\').TrimEnd('\\'), b.Replace('/', '\\').TrimEnd('\\'), StringComparison.OrdinalIgnoreCase);

    // Read every pin, rewrite the ones Plan names, and answer what it rewrote. Never throws.
    [SupportedOSPlatform("windows")]
    internal static IReadOnlyList<Fix> Repair(string folder, string exe, Action<string> log)
    {
        try
        {
            if (!Directory.Exists(folder)) return [];
            var pins = new List<Pin>();
            foreach (var file in Directory.EnumerateFiles(folder, "*.lnk"))
            {
                try { pins.Add(new Pin(file, PinLink.Target(file), ShortcutAppId.ReadAppId(file))); }
                catch (Exception error) when (error is COMException or IOException or UnauthorizedAccessException or InvalidCastException or ArgumentException)
                { log("could not read the taskbar pin " + Path.GetFileName(file) + ": " + error.Message); }
            }
            var done = new List<Fix>();
            foreach (var fix in Plan(pins, exe, File.Exists))
            {
                try
                {
                    PinLink.Point(fix.File, exe, ShellIdentity.AppId);
                    done.Add(fix);
                    log("pointed the taskbar pin " + Path.GetFileName(fix.File) + " at " + exe + (fix.Broken ? " (it pointed at a copy that is gone)" : ""));
                }
                catch (Exception error) when (error is COMException or IOException or UnauthorizedAccessException or InvalidCastException or ArgumentException)
                { log("could not point the taskbar pin " + Path.GetFileName(fix.File) + " at Armory: " + error.Message); }
            }
            return done;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            log("could not look at the taskbar pins: " + error.Message);
            return [];
        }
    }
}

// A .lnk rewritten the way Windows documents it: the shell's own link object (IShellLinkW), its
// target, working folder and icon set there, System.AppUserModel.ID written through its
// property store (ShortcutAppId), saved, and File Explorer told the file changed.
[SupportedOSPlatform("windows")]
internal static class PinLink
{
    private const int ReadWriteMode = 2; // STGM_READWRITE

    internal static string? Target(string shortcut) => ShortcutAppId.Target(shortcut);

    internal static void Point(string shortcut, string exe, string appId)
    {
        var link = (IShellLinkW)new ShellLinkObject();
        try
        {
            var file = (System.Runtime.InteropServices.ComTypes.IPersistFile)link;
            file.Load(shortcut, ReadWriteMode);
            link.SetPath(exe);
            link.SetWorkingDirectory(Path.GetDirectoryName(exe) ?? "");
            link.SetIconLocation(exe, 0);
            file.Save(shortcut, true);
        }
        finally { Marshal.FinalReleaseComObject(link); }
        // The identity through the same property store the Start menu shortcut's is written with.
        ShortcutAppId.Stamp(shortcut, exe, appId);
        SHChangeNotify(0x00002000 /* SHCNE_UPDATEITEM */, 0x0005 /* SHCNF_PATHW */, shortcut, IntPtr.Zero);
    }

    [ComImport]
    [Guid("00021401-0000-0000-C000-000000000046")]
    private class ShellLinkObject { }

    // IShellLinkW, every member in its order (the ones written are the ones used).
    [ComImport]
    [Guid("000214F9-0000-0000-C000-000000000046")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IShellLinkW
    {
        void GetPath([Out, MarshalAs(UnmanagedType.LPWStr)] System.Text.StringBuilder file, int size, IntPtr findData, uint flags);
        void GetIDList(out IntPtr list);
        void SetIDList(IntPtr list);
        void GetDescription([Out, MarshalAs(UnmanagedType.LPWStr)] System.Text.StringBuilder name, int size);
        void SetDescription([MarshalAs(UnmanagedType.LPWStr)] string name);
        void GetWorkingDirectory([Out, MarshalAs(UnmanagedType.LPWStr)] System.Text.StringBuilder dir, int size);
        void SetWorkingDirectory([MarshalAs(UnmanagedType.LPWStr)] string dir);
        void GetArguments([Out, MarshalAs(UnmanagedType.LPWStr)] System.Text.StringBuilder args, int size);
        void SetArguments([MarshalAs(UnmanagedType.LPWStr)] string args);
        void GetHotkey(out short hotkey);
        void SetHotkey(short hotkey);
        void GetShowCmd(out int showCmd);
        void SetShowCmd(int showCmd);
        void GetIconLocation([Out, MarshalAs(UnmanagedType.LPWStr)] System.Text.StringBuilder path, int size, out int icon);
        void SetIconLocation([MarshalAs(UnmanagedType.LPWStr)] string path, int icon);
        void SetRelativePath([MarshalAs(UnmanagedType.LPWStr)] string path, uint reserved);
        void Resolve(IntPtr window, uint flags);
        void SetPath([MarshalAs(UnmanagedType.LPWStr)] string file);
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern void SHChangeNotify(int eventId, uint flags, string item1, IntPtr item2);
}
