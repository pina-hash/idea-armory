using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Microsoft.Win32;

namespace Armory.Agent;

// Who Armory is to Windows (docs/agent/EXPLORER.md, "Windows notifications"): one
// AppUserModelID, IdeaBosco.Armory, on the process, the Start menu shortcut and the registration
// that names the app and its icon in a notification and in Settings > Notifications; and the
// idea-armory: link scheme that a notification's buttons open (ProtocolLink). Both installers
// write the same keys and remove them; Armory repairs them at start, comparing before it
// writes, but only the installed copy (%LOCALAPPDATA%\Programs\IDEA Armory\IdeaArmory.exe), so a
// test instance or a developer's build never takes the registration over.
internal static class ShellIdentity
{
    internal const string AppId = "IdeaBosco.Armory";
    internal const string DisplayName = "IDEA Armory";
    // Relative to HKCU\Software\Classes.
    internal const string AppIdKey = @"AppUserModelId\" + AppId;
    internal const string SchemeKey = ProtocolLink.Scheme;
    internal const string ShortcutName = "IDEA Armory.lnk";

    // %LOCALAPPDATA%\Programs\IDEA Armory: where both installers put the app.
    internal static string InstallFolder(string localAppData) => Path.Combine(localAppData, "Programs", "IDEA Armory");

    internal static string InstallFolder() =>
        InstallFolder(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData, Environment.SpecialFolderOption.DoNotVerify));

    // The installed copy: IdeaArmory.exe in the install folder, and not a test instance.
    internal static bool IsInstalledCopy(AgentPaths paths, string? processPath, string installFolder)
    {
        if (paths.IsOverridden || string.IsNullOrEmpty(processPath)) return false;
        // Windows paths, read the same on every host.
        var cut = processPath.LastIndexOfAny(['\\', '/']);
        if (cut <= 0 || !processPath[(cut + 1)..].Equals("IdeaArmory.exe", StringComparison.OrdinalIgnoreCase)) return false;
        return string.Equals(processPath[..cut].TrimEnd('\\', '/'), installFolder.TrimEnd('\\', '/'), StringComparison.OrdinalIgnoreCase);
    }

    internal static bool IsInstalledCopy(AgentPaths paths) => IsInstalledCopy(paths, Environment.ProcessPath, InstallFolder());

    // Every value, relative to HKCU\Software\Classes. appFolder: where IdeaArmory.exe is.
    internal static IReadOnlyList<ShellRegistryValue> Layout(string appFolder)
    {
        var app = appFolder.Replace('/', '\\').TrimEnd('\\');
        var exe = app + @"\IdeaArmory.exe";
        return
        [
            new(AppIdKey, "DisplayName", DisplayName),
            new(AppIdKey, "IconUri", app + @"\Assets\armory.ico"),
            new(SchemeKey, "", "URL:IDEA Armory"),
            new(SchemeKey, "URL Protocol", ""),
            new(SchemeKey + @"\DefaultIcon", "", "\"" + exe + "\",0"),
            new(SchemeKey + @"\shell\open\command", "", "\"" + exe + "\" \"%1\""),
        ];
    }

    // The values of layout that differ from existing (key and value name to its value; a
    // missing value is absent), in layout order. Nothing when they already match.
    internal static IReadOnlyList<ShellRegistryValue> Changes(IReadOnlyDictionary<(string Key, string Name), object> existing, IReadOnlyList<ShellRegistryValue> layout)
        => layout.Where(v => !existing.TryGetValue((v.Key, v.Name), out var has) || !Equals(has, v.Value)).ToArray();

    // Compare, then write only what differs. True when it wrote anything.
    [SupportedOSPlatform("windows")]
    internal static bool Apply(RegistryKey classes, IReadOnlyList<ShellRegistryValue> layout)
    {
        var existing = new Dictionary<(string Key, string Name), object>();
        foreach (var value in layout)
        {
            using var key = classes.OpenSubKey(value.Key);
            if (key?.GetValue(value.Name, null, RegistryValueOptions.DoNotExpandEnvironmentNames) is string text && key.GetValueKind(value.Name) == RegistryValueKind.String)
                existing[(value.Key, value.Name)] = text;
        }
        var changes = Changes(existing, layout);
        foreach (var value in changes)
        {
            using var key = classes.CreateSubKey(value.Key, writable: true);
            key.SetValue(value.Name, value.Value, RegistryValueKind.String);
        }
        return changes.Count > 0;
    }

    // At start, the installed copy only: the registration and the link scheme, then the Start
    // menu shortcut's AppUserModelID (the flash-drive install makes the shortcut without it).
    [SupportedOSPlatform("windows")]
    internal static void Repair(string appFolder, Action<string> log)
    {
        try
        {
            using var classes = Registry.CurrentUser.CreateSubKey(ShellVerbs.ClassesKey, writable: true);
            if (Apply(classes, Layout(appFolder))) log("wrote Armory's notification and link registration");
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        { log("could not write Armory's notification and link registration: " + error.Message); }
        var shortcut = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Programs, Environment.SpecialFolderOption.DoNotVerify), ShortcutName);
        try
        {
            if (ShortcutAppId.Stamp(shortcut, Path.Combine(appFolder, "IdeaArmory.exe"), AppId)) log("gave the Start menu shortcut Armory's notification identity");
        }
        catch (Exception error) when (error is COMException or IOException or UnauthorizedAccessException or InvalidCastException or ArgumentException)
        { log("could not give the Start menu shortcut Armory's notification identity: " + error.Message); }
    }

    // Before any window: the taskbar, the tray and the notifications are one app with one switch.
    [SupportedOSPlatform("windows")]
    internal static void SetProcessAppId()
    {
        try { SetCurrentProcessExplicitAppUserModelID(AppId); }
        catch (Exception error) when (error is COMException or EntryPointNotFoundException or DllNotFoundException) { }
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, PreserveSig = false)]
    private static extern void SetCurrentProcessExplicitAppUserModelID(string appId);
}

// System.AppUserModel.ID on a .lnk, the way Windows documents it: the shortcut loaded through the
// shell's own link object (IShellLinkW), its target read there, and the value written through
// that object's property store and saved. Only a shortcut that exists and points to this
// IdeaArmory.exe is touched, and only when its value differs.
[SupportedOSPlatform("windows")]
internal static class ShortcutAppId
{
    private static readonly PropertyKey AppIdKey = new(new Guid("9F4C2855-9F79-4B39-A8D0-E1D42DE1D5F3"), 5);
    private const int ReadWriteMode = 2; // STGM_READWRITE
    private const ushort StringType = 31; // VT_LPWSTR
    private const ushort BstrType = 8; // VT_BSTR: how Inno Setup writes the setup's shortcut

    // True when it wrote the value.
    internal static bool Stamp(string shortcut, string exe, string appId)
    {
        if (!File.Exists(shortcut)) return false;
        var link = (IShellLinkW)new ShellLinkObject();
        try
        {
            var file = (System.Runtime.InteropServices.ComTypes.IPersistFile)link;
            file.Load(shortcut, ReadWriteMode);
            var buffer = new System.Text.StringBuilder(32768);
            link.GetPath(buffer, buffer.Capacity, IntPtr.Zero, 0);
            var target = buffer.ToString();
            if (target.Length == 0 || !string.Equals(LinkForwarder.LongName(Path.GetFullPath(target)), LinkForwarder.LongName(Path.GetFullPath(exe)), StringComparison.OrdinalIgnoreCase)) return false;
            var store = (IPropertyStore)link;
            if (string.Equals(Read(store, AppIdKey), appId, StringComparison.Ordinal)) return false;
            var value = new PropVariant { Type = StringType, Pointer = Marshal.StringToCoTaskMemUni(appId) };
            try
            {
                var key = AppIdKey;
                Marshal.ThrowExceptionForHR(store.SetValue(ref key, ref value));
                Marshal.ThrowExceptionForHR(store.Commit());
            }
            finally { PropVariantClear(ref value); }
            file.Save(shortcut, true);
            return true;
        }
        finally { Marshal.FinalReleaseComObject(link); }
    }

    // The shortcut's AppUserModelID, or null.
    internal static string? ReadAppId(string shortcut)
    {
        var link = (IShellLinkW)new ShellLinkObject();
        try
        {
            ((System.Runtime.InteropServices.ComTypes.IPersistFile)link).Load(shortcut, 0);
            return Read((IPropertyStore)link, AppIdKey);
        }
        finally { Marshal.FinalReleaseComObject(link); }
    }

    // CLSID_ShellLink.
    [ComImport]
    [Guid("00021401-0000-0000-C000-000000000046")]
    private class ShellLinkObject { }

    // IShellLinkW: only its first member is used, so only it is declared.
    [ComImport]
    [Guid("000214F9-0000-0000-C000-000000000046")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IShellLinkW
    {
        void GetPath([Out, MarshalAs(UnmanagedType.LPWStr)] System.Text.StringBuilder file, int size, IntPtr findData, uint flags);
    }

    private static string? Read(IPropertyStore store, PropertyKey key)
    {
        if (store.GetValue(ref key, out var value) != 0) return null;
        try { return value.Type is StringType or BstrType && value.Pointer != IntPtr.Zero ? Marshal.PtrToStringUni(value.Pointer) : null; }
        finally { PropVariantClear(ref value); }
    }

    [StructLayout(LayoutKind.Sequential)]
    private readonly record struct PropertyKey(Guid FormatId, int PropertyId);

    // PROPVARIANT for one string: its type, then the pointer at offset 8.
    [StructLayout(LayoutKind.Explicit, Size = 24)]
    private struct PropVariant
    {
        [FieldOffset(0)] public ushort Type;
        [FieldOffset(8)] public IntPtr Pointer;
    }

    [ComImport]
    [Guid("886D8EEB-8CF2-4446-8D02-CDBA1DBDCF99")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IPropertyStore
    {
        [PreserveSig] int GetCount(out uint count);
        [PreserveSig] int GetAt(uint index, out PropertyKey key);
        [PreserveSig] int GetValue(ref PropertyKey key, out PropVariant value);
        [PreserveSig] int SetValue(ref PropertyKey key, ref PropVariant value);
        [PreserveSig] int Commit();
    }

    [DllImport("ole32.dll")]
    private static extern int PropVariantClear(ref PropVariant value);
}
