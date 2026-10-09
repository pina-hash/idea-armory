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

// System.AppUserModel.ID on a .lnk, through the shell's property store. Only a shortcut that
// exists and points to this IdeaArmory.exe is touched, and only when its value differs.
[SupportedOSPlatform("windows")]
internal static class ShortcutAppId
{
    private static readonly PropertyKey AppIdKey = new(new Guid("9F4C2855-9F79-4B39-A8D0-E1D42DE1D5F3"), 5);
    private static readonly PropertyKey TargetKey = new(new Guid("B9B4B3FC-2B51-4A42-B5D8-324146AFCF25"), 2);
    private const int ReadWrite = 2;
    private const ushort StringType = 31; // VT_LPWSTR

    // True when it wrote the value.
    internal static bool Stamp(string shortcut, string exe, string appId)
    {
        if (!File.Exists(shortcut)) return false;
        var iid = typeof(IPropertyStore).GUID;
        SHGetPropertyStoreFromParsingName(shortcut, IntPtr.Zero, ReadWrite, ref iid, out var store);
        try
        {
            var target = Read(store, TargetKey);
            if (target is null || !string.Equals(LinkForwarder.LongName(Path.GetFullPath(target)), LinkForwarder.LongName(Path.GetFullPath(exe)), StringComparison.OrdinalIgnoreCase)) return false;
            if (string.Equals(Read(store, AppIdKey), appId, StringComparison.Ordinal)) return false;
            var value = new PropVariant { Type = StringType, Pointer = Marshal.StringToCoTaskMemUni(appId) };
            try
            {
                var key = AppIdKey;
                Marshal.ThrowExceptionForHR(store.SetValue(ref key, ref value));
                Marshal.ThrowExceptionForHR(store.Commit());
            }
            finally { PropVariantClear(ref value); }
            return true;
        }
        finally { Marshal.ReleaseComObject(store); }
    }

    // A string property, or null.
    internal static string? Read(string shortcut, Guid formatId, int propertyId)
    {
        var iid = typeof(IPropertyStore).GUID;
        SHGetPropertyStoreFromParsingName(shortcut, IntPtr.Zero, 0, ref iid, out var store);
        try { return Read(store, new PropertyKey(formatId, propertyId)); }
        finally { Marshal.ReleaseComObject(store); }
    }

    internal static string? ReadAppId(string shortcut) => Read(shortcut, AppIdKey.FormatId, AppIdKey.PropertyId);

    private static string? Read(IPropertyStore store, PropertyKey key)
    {
        if (store.GetValue(ref key, out var value) != 0) return null;
        try { return value.Type == StringType && value.Pointer != IntPtr.Zero ? Marshal.PtrToStringUni(value.Pointer) : null; }
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

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, PreserveSig = false)]
    private static extern void SHGetPropertyStoreFromParsingName(string path, IntPtr bindContext, int flags, ref Guid iid,
        [MarshalAs(UnmanagedType.Interface)] out IPropertyStore store);

    [DllImport("ole32.dll")]
    private static extern int PropVariantClear(ref PropVariant value);
}
