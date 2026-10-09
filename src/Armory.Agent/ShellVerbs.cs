using System.Runtime.Versioning;
using Armory.Platform.Windows;
using Microsoft.Win32;

namespace Armory.Agent;

// One value under HKCU\Software\Classes: Key relative to Classes, Name "" for (Default), Value
// a string (REG_SZ) or an int (REG_DWORD).
internal sealed record ShellRegistryValue(string Key, string Name, object Value);

internal enum ShellRegistryAction { DeleteKey, DeleteValue, SetValue }

// One change to the registry; SetValue creates its key when missing.
internal sealed record ShellRegistryChange(ShellRegistryAction Action, string Key, string? Name = null, object? Value = null);

// The "IDEA Armory" items on File Explorer's right-click menu (docs/agent/EXPLORER.md): static
// verbs in HKCU\Software\Classes, written by IdeaArmory.exe itself (never by an installer), shown
// only inside the vault by AppliesTo. Each item runs ArmoryShell.exe, which hands its one path to
// the running Armory (ShellInbox). No DLL in Explorer and no administrator.
//
// Apply compares before it writes, and only a real change sends SHCNE_ASSOCCHANGED. Every key
// under the four roots below is Armory's: anything there that the layout does not name is
// removed, so an older layout never lingers.
internal static class ShellVerbs
{
    internal const string FilesRoot = @"AllFilesystemObjects\shell\IDEAArmory";
    internal const string FilesMenu = "IDEAArmory.Menu";
    internal const string BackgroundRoot = @"Directory\Background\shell\IDEAArmory";
    internal const string BackgroundMenu = "IDEAArmory.BackgroundMenu";
    internal static readonly IReadOnlyList<string> Roots = [FilesRoot, FilesMenu, BackgroundRoot, BackgroundMenu];
    internal const string ClassesKey = @"Software\Classes";
    // CommandFlags ECF_SEPARATORBEFORE: a line above the item.
    internal const int SeparatorBefore = 0x20;

    // An AQS string literal: quoted, a quote doubled. Backslashes need no escaping.
    internal static string Quote(string text) => "\"" + text.Replace("\"", "\"\"", StringComparison.Ordinal) + "\"";

    // Items inside folder, never folder itself (the trailing backslash).
    internal static string Inside(string folder) => "System.ItemPathDisplay:~<" + Quote(Trim(folder) + "\\");

    internal static string Exactly(string folder) => "System.ItemPathDisplay:=" + Quote(Trim(folder));

    // AQS wants OR in capitals.
    internal static string Or(IEnumerable<string> conditions) => string.Join(" OR ", conditions);

    // The whole layout. vaultRoot: the vault ("C:\IDEA\Armory"). appFolder: where IdeaArmory.exe
    // and ArmoryShell.exe are. forceCheckInFolders: the vault-relative folders of the projects in
    // which the signed-in account can force a check in (ProjectView.CanTakeBack); Force check in
    // shows on each of those folders and everything in them, and none leaves it out entirely.
    internal static IReadOnlyList<ShellRegistryValue> Layout(string vaultRoot, string appFolder, IEnumerable<string> forceCheckInFolders)
    {
        var vault = Trim(vaultRoot);
        var app = Trim(appFolder);
        var icon = Quoted(app + @"\IdeaArmory.exe") + ",0";
        string Command(string verb, string argument) => Quoted(app + @"\ArmoryShell.exe") + " " + verb + " \"" + argument + "\"";
        var force = forceCheckInFolders
            .Select(f => f.Replace('/', '\\').Trim('\\'))
            .Where(f => f.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Order(StringComparer.OrdinalIgnoreCase)
            .SelectMany(f => new[] { Exactly(vault + "\\" + f), Inside(vault + "\\" + f) })
            .ToArray();

        List<ShellRegistryValue> values =
        [
            new(FilesRoot, "MUIVerb", "IDEA Armory"),
            new(FilesRoot, "Icon", icon),
            new(FilesRoot, "AppliesTo", Inside(vault)),
            new(FilesRoot, "ExtendedSubCommandsKey", FilesMenu),
            new(FilesRoot, "MultiSelectModel", "Player"),
        ];
        void Item(string menu, string key, string label, string verb, string argument, string? select, bool separator = false, string? appliesTo = null)
        {
            var at = menu + @"\shell\" + key;
            values.Add(new(at, "MUIVerb", label));
            if (select is not null) values.Add(new(at, "MultiSelectModel", select));
            if (separator) values.Add(new(at, "CommandFlags", SeparatorBefore));
            if (appliesTo is not null) values.Add(new(at, "AppliesTo", appliesTo));
            values.Add(new(at + @"\command", "", Command(verb, argument)));
        }
        Item(FilesMenu, "01checkout", "Check out", "checkout", "%1", "Player");
        Item(FilesMenu, "02checkoutopen", "Check out and open", "checkoutopen", "%1", "Single");
        Item(FilesMenu, "03checkin", "Check in", "checkin", "%1", "Player");
        Item(FilesMenu, "04undo", "Undo check out", "undo", "%1", "Player");
        Item(FilesMenu, "05show", "Show in Armory", "show", "%1", "Single", separator: true);
        if (force.Length > 0) Item(FilesMenu, "06forcecheckin", "Force check in", "forcecheckin", "%1", "Player", separator: true, appliesTo: Or(force));

        values.Add(new(BackgroundRoot, "MUIVerb", "IDEA Armory"));
        values.Add(new(BackgroundRoot, "Icon", icon));
        values.Add(new(BackgroundRoot, "AppliesTo", Or([Exactly(vault), Inside(vault)])));
        values.Add(new(BackgroundRoot, "ExtendedSubCommandsKey", BackgroundMenu));
        Item(BackgroundMenu, "01checkin", "Check in", "checkin", "%V", null);
        Item(BackgroundMenu, "02show", "Show in Armory", "show", "%V", null);
        return values;
    }

    // What to change so that existing (every key under the four roots, with its values; "" is the
    // default value) becomes exactly layout. Nothing when they already match.
    internal static IReadOnlyList<ShellRegistryChange> Changes(IReadOnlyDictionary<string, IReadOnlyDictionary<string, object>> existing,
        IReadOnlyList<ShellRegistryValue> layout)
    {
        var wanted = new Dictionary<string, Dictionary<string, object>>(StringComparer.OrdinalIgnoreCase);
        foreach (var value in layout)
        {
            if (!wanted.TryGetValue(value.Key, out var names)) wanted[value.Key] = names = new(StringComparer.OrdinalIgnoreCase);
            names[value.Name] = value.Value;
        }
        // Every wanted key and its parents up to its root are kept.
        var kept = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var key in wanted.Keys)
        {
            var root = Roots.First(r => key.Equals(r, StringComparison.OrdinalIgnoreCase) || key.StartsWith(r + "\\", StringComparison.OrdinalIgnoreCase));
            for (var at = key; ; at = at[..at.LastIndexOf('\\')])
            {
                kept.Add(at);
                if (at.Length <= root.Length) break;
            }
        }
        List<ShellRegistryChange> changes = [];
        // Keys nobody wants: only the topmost of each unwanted branch is deleted.
        foreach (var key in existing.Keys.Order(StringComparer.OrdinalIgnoreCase))
        {
            if (kept.Contains(key)) continue;
            var slash = key.LastIndexOf('\\');
            var parentKept = slash > 0 && kept.Contains(key[..slash]);
            var isRoot = Roots.Contains(key, StringComparer.OrdinalIgnoreCase);
            if (parentKept || isRoot) changes.Add(new(ShellRegistryAction.DeleteKey, key));
        }
        foreach (var key in kept.Order(StringComparer.OrdinalIgnoreCase))
        {
            existing.TryGetValue(key, out var has);
            var names = wanted.GetValueOrDefault(key);
            foreach (var name in has?.Keys.Order(StringComparer.OrdinalIgnoreCase) ?? Enumerable.Empty<string>())
                if (names is null || !names.ContainsKey(name)) changes.Add(new(ShellRegistryAction.DeleteValue, key, name));
            foreach (var (name, value) in names?.OrderBy(n => n.Key, StringComparer.OrdinalIgnoreCase) ?? Enumerable.Empty<KeyValuePair<string, object>>())
                if (has is null || !has.TryGetValue(name, out var current) || !Same(current, value)) changes.Add(new(ShellRegistryAction.SetValue, key, name, value));
        }
        return changes;
    }

    // The host's call (AgentHost): at start, when the vault root changes, on sign-in and sign-out,
    // and when the force-check-in folders change. True when something changed (and Explorer was told).
    [SupportedOSPlatform("windows")]
    internal static bool Apply(string vaultRoot, string appFolder, IEnumerable<string> forceCheckInFolders)
    {
        using var classes = Registry.CurrentUser.CreateSubKey(ClassesKey, writable: true);
        var changed = Write(classes, Layout(vaultRoot, appFolder, forceCheckInFolders));
        if (changed) ShellNotify.AssociationsChanged();
        return changed;
    }

    // Removes every key of the menu (the uninstallers remove the same four). True when one existed.
    [SupportedOSPlatform("windows")]
    internal static bool Remove()
    {
        using var classes = Registry.CurrentUser.CreateSubKey(ClassesKey, writable: true);
        var changed = Remove(classes);
        if (changed) ShellNotify.AssociationsChanged();
        return changed;
    }

    // The same against any key standing for HKCU\Software\Classes (tests use a private one).
    [SupportedOSPlatform("windows")]
    internal static bool Write(RegistryKey classes, IReadOnlyList<ShellRegistryValue> layout)
    {
        var changes = Changes(Read(classes), layout);
        foreach (var change in changes)
        {
            switch (change.Action)
            {
                case ShellRegistryAction.DeleteKey:
                    classes.DeleteSubKeyTree(change.Key, throwOnMissingSubKey: false);
                    break;
                case ShellRegistryAction.DeleteValue:
                    using (var key = classes.OpenSubKey(change.Key, writable: true)) key?.DeleteValue(change.Name!, throwOnMissingValue: false);
                    break;
                default:
                    using (var key = classes.CreateSubKey(change.Key, writable: true))
                        key.SetValue(change.Name!, change.Value!, change.Value is int ? RegistryValueKind.DWord : RegistryValueKind.String);
                    break;
            }
        }
        return changes.Count > 0;
    }

    [SupportedOSPlatform("windows")]
    internal static bool Remove(RegistryKey classes)
    {
        var changed = false;
        foreach (var root in Roots)
        {
            using (var key = classes.OpenSubKey(root))
                if (key is null) continue;
            classes.DeleteSubKeyTree(root, throwOnMissingSubKey: false);
            changed = true;
        }
        return changed;
    }

    // Every key under the four roots with its values (strings and DWORDs as themselves, anything
    // else as bytes, which never equals a wanted value).
    [SupportedOSPlatform("windows")]
    internal static IReadOnlyDictionary<string, IReadOnlyDictionary<string, object>> Read(RegistryKey classes)
    {
        var found = new Dictionary<string, IReadOnlyDictionary<string, object>>(StringComparer.OrdinalIgnoreCase);
        void Walk(RegistryKey key, string path)
        {
            var values = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);
            foreach (var name in key.GetValueNames())
            {
                var kind = key.GetValueKind(name);
                var value = key.GetValue(name, null, RegistryValueOptions.DoNotExpandEnvironmentNames);
                values[name] = kind switch
                {
                    RegistryValueKind.String when value is string text => text,
                    RegistryValueKind.DWord when value is int number => number,
                    _ => new byte[] { 0xFF },
                };
            }
            found[path] = values;
            foreach (var child in key.GetSubKeyNames())
                using (var sub = key.OpenSubKey(child))
                    if (sub is not null) Walk(sub, path + "\\" + child);
        }
        foreach (var root in Roots)
            using (var key = classes.OpenSubKey(root))
                if (key is not null) Walk(key, root);
        return found;
    }

    private static bool Same(object current, object wanted) => (current, wanted) switch
    {
        (string a, string b) => string.Equals(a, b, StringComparison.Ordinal),
        (int a, int b) => a == b,
        _ => false,
    };

    private static string Trim(string folder) => folder.Replace('/', '\\').TrimEnd('\\');

    // A command-line path in quotes (a Windows path never holds a quote).
    private static string Quoted(string path) => "\"" + path + "\"";
}
