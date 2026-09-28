using System.Text.Json;
using Armory.Core;

namespace Armory.Platform.Windows;

public sealed class ReadOnlyPolicy : IDisposable
{
    private readonly WindowsPaths paths;
    private readonly string manifest;
    private readonly FileStream ownership;
    private readonly Dictionary<string, LockOwnership> desired;
    internal Action? AfterIntentPersisted { get; set; }
    public ReadOnlyPolicy(WindowsPaths paths)
    {
        this.paths = paths;
        var folder = paths.PrivateDirectory();
        manifest = Path.Combine(folder, "read-only.json");
        ownership = new(Path.Combine(folder, "read-only.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        desired = File.Exists(manifest) ? JsonSerializer.Deserialize<Dictionary<string, LockOwnership>>(File.ReadAllText(manifest))
            ?? throw new InvalidDataException("Invalid read-only intent manifest.") : new(StringComparer.OrdinalIgnoreCase);
    }
    public void Apply(VaultPath path, LockOwnership state)
    {
        _ = paths.Resolve(path);
        if (!Enum.IsDefined(state)) throw new ArgumentOutOfRangeException(nameof(state));
        desired[path.Value] = state;
        Persist();
        AfterIntentPersisted?.Invoke();
        SetAttribute(path, state);
    }
    // Startup must supply the latest authoritative locks before enabling editing/sync.
    public void Recover(IReadOnlyDictionary<VaultPath, LockOwnership> currentLocks)
    {
        foreach (var pair in currentLocks) desired[pair.Key.Value] = pair.Value;
        Persist();
        foreach (var pair in desired)
        {
            if (!VaultPath.TryCreate(pair.Key, out var path, out var reason, paths.Root, paths.MaximumLength)) throw new InvalidDataException(reason);
            SetAttribute(path, pair.Value);
        }
    }
    private void SetAttribute(VaultPath path, LockOwnership state)
    {
        var file = paths.Resolve(path);
        if (!File.Exists(file)) return;
        var attributes = File.GetAttributes(file);
        attributes = state == LockOwnership.OtherPerson ? attributes | FileAttributes.ReadOnly : attributes & ~FileAttributes.ReadOnly;
        File.SetAttributes(file, attributes);
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
