using System.Text.Json;
using Armory.Core;

namespace Armory.Platform.Windows;

// The vault's read-only bits (docs/platform/read-only.md). Every file the server has is
// read-only on disk unless THIS device holds its check out, so SolidWorks opens it read-only
// and cannot save over it. Intents are durable before any bit changes.
public sealed class ReadOnlyPolicy : IDisposable
{
    private readonly WindowsPaths paths;
    private readonly string manifest;
    private readonly FileStream ownership;
    private readonly Dictionary<string, LockOwnership> desired = new(StringComparer.OrdinalIgnoreCase);
    internal Action? AfterIntentPersisted { get; set; }
    public ReadOnlyPolicy(WindowsPaths paths)
    {
        this.paths = paths;
        var folder = paths.PrivateDirectory();
        manifest = Path.Combine(folder, "read-only.json");
        ownership = new(Path.Combine(folder, "read-only.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        if (File.Exists(manifest))
        {
            var saved = JsonSerializer.Deserialize<Dictionary<string, LockOwnership>>(File.ReadAllText(manifest))
                ?? throw new InvalidDataException("Invalid read-only intent manifest.");
            foreach (var pair in saved) desired[pair.Key] = pair.Value;
        }
    }

    // The v2 rule, written here as Armory.Core's CheckoutRules.IsReadOnlyOnDisk states it (the
    // portable rule function the engine and the end-to-end file system use): read-only unless
    // this device holds the file's check out. Free, OtherPerson and MyOtherDevice are read-only.
    public static bool IsReadOnly(LockOwnership ownership) => ownership != LockOwnership.ThisDevice;

    public void Apply(VaultPath path, LockOwnership state)
    {
        _ = paths.Resolve(path);
        if (!Enum.IsDefined(state)) throw new ArgumentOutOfRangeException(nameof(state));
        desired[path.Value] = state;
        Persist();
        AfterIntentPersisted?.Invoke();
        SetAttribute(path, state);
    }

    // Many files with ONE durable manifest write (a 5,000-file import would otherwise rewrite
    // and flush the whole manifest 5,000 times). Bits are then changed one by one; a file whose
    // bit could not be changed is returned with the reason and never stops the others.
    public IReadOnlyList<(VaultPath Path, string Problem)> ApplyMany(IReadOnlyList<(VaultPath Path, LockOwnership Ownership)> items)
    {
        ArgumentNullException.ThrowIfNull(items);
        foreach (var (path, state) in items)
        {
            _ = paths.Resolve(path);
            if (!Enum.IsDefined(state)) throw new ArgumentOutOfRangeException(nameof(items));
        }
        if (items.Count == 0) return [];
        foreach (var (path, state) in items) desired[path.Value] = state;
        Persist();
        AfterIntentPersisted?.Invoke();
        List<(VaultPath, string)> failed = [];
        foreach (var (path, state) in items)
        {
            try { SetAttribute(path, state); }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException) { failed.Add((path, error.Message)); }
        }
        return failed;
    }

    // The agent moved a folder: its files' intents move with it, in one manifest write.
    public void Rekey(string fromFolder, string toFolder)
    {
        var moved = desired.Where(pair => pair.Key.StartsWith(fromFolder + "/", StringComparison.OrdinalIgnoreCase)).ToArray();
        if (moved.Length == 0) return;
        foreach (var pair in moved) desired.Remove(pair.Key);
        foreach (var pair in moved) desired[toFolder + pair.Key[fromFolder.Length..]] = pair.Value;
        Persist();
    }

    // Startup must supply the latest authoritative locks before enabling editing/sync. An
    // intent whose path no longer fits this vault (the vault folder moved somewhere longer)
    // is dropped, and a bit that cannot be set is skipped; both are returned as problems
    // instead of stopping the agent from starting.
    public IReadOnlyList<string> Recover(IReadOnlyDictionary<VaultPath, LockOwnership> currentLocks)
    {
        foreach (var pair in currentLocks) desired[pair.Key.Value] = pair.Value;
        List<string> dropped = [];
        foreach (var key in desired.Keys.ToArray())
        {
            if (VaultPath.TryCreate(key, out _, out var reason, paths.Root, paths.MaximumLength)) continue;
            desired.Remove(key);
            dropped.Add($"{key}: {reason}");
        }
        Persist();
        foreach (var pair in desired)
        {
            VaultPath.TryCreate(pair.Key, out var path, out _, paths.Root, paths.MaximumLength);
            try { SetAttribute(path, pair.Value); }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException) { dropped.Add($"{pair.Key}: {error.Message}"); }
        }
        return dropped;
    }

    // Changes the attribute only when it differs, so applying the same rule again writes
    // nothing and raises no change notification (which would wake the engine for nothing).
    private void SetAttribute(VaultPath path, LockOwnership state)
    {
        var file = paths.Resolve(path);
        if (!File.Exists(file)) return;
        var attributes = File.GetAttributes(file);
        var wanted = IsReadOnly(state) ? attributes | FileAttributes.ReadOnly : attributes & ~FileAttributes.ReadOnly;
        if (wanted != attributes) File.SetAttributes(file, wanted);
    }

    private void Persist()
    {
        var temp = manifest + ".pending";
        using (var output = new FileStream(temp, FileMode.Create, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough))
        { JsonSerializer.Serialize(output, desired); output.Flush(true); }
        NativeMethods.Move(temp, manifest, replace: true);
    }

    public void Dispose() => ownership.Dispose();
}
