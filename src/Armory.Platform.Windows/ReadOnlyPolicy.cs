using System.ComponentModel;
using System.Text.Json;
using Armory.Core;

namespace Armory.Platform.Windows;

// The vault's read-only bits (docs/platform/read-only.md). Every file the server has is
// read-only on disk unless THIS device holds its check out, so SolidWorks opens it read-only
// and cannot save over it. Intents are durable before any bit changes. An intent belongs to
// one file (its NTFS id), not to a path: startup recovery applies it only to that same file,
// so a different file that later appears at the path (a re-added copy, a release-gate draft,
// a new file the server does not have) never inherits it, and the manifest only ever holds
// files that are still there.
public sealed class ReadOnlyPolicy : IDisposable
{
    private sealed record Intent(LockOwnership Ownership, string FileId);
    private readonly WindowsPaths paths;
    private readonly string manifest;
    private readonly FileStream ownership;
    private readonly Dictionary<string, Intent> desired = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<string> loadProblems = [];
    // The intents in memory are not all in the manifest yet (a write failed): the next call
    // writes them before it changes any bit, even when it changes no intent itself.
    private bool unsaved;
    internal Action? AfterIntentPersisted { get; set; }
    // Attributes really written, so tests can tell that applying a rule again changed nothing.
    internal int AttributeWrites { get; private set; }
    public ReadOnlyPolicy(WindowsPaths paths)
    {
        this.paths = paths;
        var folder = paths.PrivateDirectory();
        manifest = Path.Combine(folder, "read-only.json");
        ownership = new(Path.Combine(folder, "read-only.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        if (File.Exists(manifest)) Load(File.ReadAllBytes(manifest));
    }

    // The v2 rule is Armory.Core's CheckoutRules.IsReadOnlyOnDisk, the one function the engine
    // and the end-to-end file system also use: read-only unless this device holds the file's
    // check out. Free, OtherPerson and MyOtherDevice are read-only.
    public static bool IsReadOnly(LockOwnership ownership) => CheckoutRules.IsReadOnlyOnDisk(ownership);

    public void Apply(VaultPath path, LockOwnership state)
    {
        var file = paths.Resolve(path);
        if (!Enum.IsDefined(state)) throw new ArgumentOutOfRangeException(nameof(state));
        Save(Record(path.Value, state, FileIdAt(file)));
        SetAttribute(file, state);
    }

    // Many files with at most ONE durable manifest write, and none when no intent changed (a
    // 5,000-file import would otherwise rewrite and flush the whole manifest 5,000 times, and
    // the engine applies the rule every pass). Bits are then changed one by one, each only
    // when it differs; a file whose bit could not be changed is returned with the reason and
    // never stops the others. A path with no file has nothing to apply and keeps no intent.
    public IReadOnlyList<(VaultPath Path, string Problem)> ApplyMany(IReadOnlyList<(VaultPath Path, LockOwnership Ownership)> items)
    {
        ArgumentNullException.ThrowIfNull(items);
        var resolved = new List<(VaultPath Path, LockOwnership Ownership, string File)>(items.Count);
        foreach (var (path, state) in items)
        {
            if (!Enum.IsDefined(state)) throw new ArgumentOutOfRangeException(nameof(items));
            resolved.Add((path, state, paths.Resolve(path)));
        }
        List<(VaultPath, string)> failed = [];
        // A file that could not be identified gets no intent, so its bit is not touched either.
        HashSet<VaultPath> unidentified = [];
        var changed = false;
        foreach (var (path, state, file) in resolved)
        {
            try { changed |= Record(path.Value, state, FileIdAt(file)); }
            catch (Exception error) when (IsDiskError(error))
            {
                failed.Add((path, error.Message));
                unidentified.Add(path);
            }
        }
        Save(changed);
        foreach (var (path, state, file) in resolved)
        {
            if (unidentified.Contains(path)) continue;
            try { SetAttribute(file, state); }
            catch (Exception error) when (IsDiskError(error)) { failed.Add((path, error.Message)); }
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

    // The agent renamed a file (the same file, so the same id): its intent goes with it.
    public void Move(VaultPath from, VaultPath to)
    {
        if (!desired.Remove(from.Value, out var intent)) return;
        desired[to.Value] = intent;
        Persist();
    }

    // The file left its path (moved to recovery): nothing at that path keeps its intent.
    public void Forget(VaultPath path)
    {
        if (desired.Remove(path.Value)) Persist();
    }

    // The NTFS id of the file at path, or null when no file is there.
    public string? CurrentFileId(VaultPath path) => FileIdAt(paths.Resolve(path));

    // New bytes replaced the file at path (a new file id). An intent made for the file that was
    // there (previous, from CurrentFileId before the replace) follows the new file; one made for
    // another file (it was deleted and something else came) is dropped, so a restart never
    // applies an old file's intent to the new bytes.
    public void Renew(VaultPath path, string? previous)
    {
        if (!desired.TryGetValue(path.Value, out var intent)) return;
        if (previous is null || !string.Equals(intent.FileId, previous, StringComparison.Ordinal))
        {
            desired.Remove(path.Value);
            Save(changed: true);
            return;
        }
        Save(Record(path.Value, intent.Ownership, FileIdAt(paths.Resolve(path))));
    }

    // At startup, before anything can edit or sync. currentLocks are authoritative and apply
    // to whatever file is at their path now. Every recorded intent applies only to the file it
    // was recorded for: an intent whose file is gone, or whose path now holds another file, is
    // dropped without a word (that file never inherits a bit the server's rule did not give
    // it; the engine applies the rule to every file the server has on its first pass). An
    // intent whose path no longer fits this vault, or whose bit cannot be set, is returned as a
    // problem instead of stopping the agent from starting.
    public IReadOnlyList<string> Recover(IReadOnlyDictionary<VaultPath, LockOwnership> currentLocks)
    {
        List<string> problems = [.. loadProblems];
        loadProblems.Clear();
        var changed = false;
        foreach (var (path, state) in currentLocks)
        {
            try { changed |= Record(path.Value, state, FileIdAt(paths.Resolve(path))); }
            catch (Exception error) when (IsDiskError(error)) { problems.Add($"{path.Value}: {error.Message}"); }
        }
        List<(string File, LockOwnership State)> apply = [];
        foreach (var (key, intent) in desired.ToArray())
        {
            string? file = null;
            string? reason = null;
            string? id = null;
            try
            {
                if (VaultPath.TryCreate(key, out var path, out reason, paths.Root, paths.MaximumLength) &&
                    paths.TryResolve(path, out file, out reason)) id = FileIdAt(file!);
            }
            catch (Exception error) when (IsDiskError(error)) { reason = error.Message; }
            if (id is not null && id == intent.FileId) { apply.Add((file!, intent.Ownership)); continue; }
            desired.Remove(key);
            changed = true;
            if (id is null && reason is not null) problems.Add($"{key}: {reason}");
        }
        Save(changed);
        foreach (var (file, state) in apply)
        {
            try { SetAttribute(file, state); }
            catch (Exception error) when (IsDiskError(error)) { problems.Add($"{Path.GetFileName(file)}: {error.Message}"); }
        }
        return problems;
    }

    // Records the intent (or drops it when no file is there), true when the manifest changed.
    private bool Record(string key, LockOwnership state, string? fileId)
    {
        if (fileId is null) return desired.Remove(key);
        var intent = new Intent(state, fileId);
        if (desired.TryGetValue(key, out var old) && old == intent) return false;
        desired[key] = intent;
        return true;
    }

    private static string? FileIdAt(string file)
    {
        try { return NativeMethods.ExistingFileId(file); }
        catch (Win32Exception error) { throw new IOException(error.Message, error); }
    }

    private static bool IsDiskError(Exception error) => error is IOException or UnauthorizedAccessException;

    // Changes the attribute only when it differs, so applying the same rule again writes
    // nothing and raises no change notification (which would wake the engine for nothing).
    private void SetAttribute(string file, LockOwnership state)
    {
        if (!File.Exists(file)) return;
        var attributes = File.GetAttributes(file);
        var wanted = IsReadOnly(state) ? attributes | FileAttributes.ReadOnly : attributes & ~FileAttributes.ReadOnly;
        if (wanted == attributes) return;
        File.SetAttributes(file, wanted);
        AttributeWrites++;
    }

    private void Save(bool changed)
    {
        if (changed || unsaved) Persist();
    }

    // The manifest: {"version": 2, "intents": {"<vault path>": {"ownership": n, "file": "<NTFS id>"}}}.
    private void Persist()
    {
        unsaved = true;
        var temp = manifest + ".pending";
        using (var output = new FileStream(temp, FileMode.Create, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough))
        {
            using (var json = new Utf8JsonWriter(output))
            {
                json.WriteStartObject();
                json.WriteNumber("version", 2);
                json.WriteStartObject("intents");
                foreach (var (key, intent) in desired)
                {
                    json.WriteStartObject(key);
                    json.WriteNumber("ownership", (int)intent.Ownership);
                    json.WriteString("file", intent.FileId);
                    json.WriteEndObject();
                }
                json.WriteEndObject();
                json.WriteEndObject();
            }
            output.Flush(true);
        }
        // A failed rename is a disk error like any other here, so callers that report one and
        // carry on (a move that already happened, a scan's retry) see it as one.
        try { NativeMethods.Move(temp, manifest, replace: true); }
        catch (Win32Exception error) { throw new IOException(error.Message, error); }
        unsaved = false;
        AfterIntentPersisted?.Invoke();
    }

    // Version 2 only. A 0.1.0 manifest ({"<path>": n}) named paths, not files, and meant "Free
    // is writable", so none of it is applied: its paths may hold other files by now, and the
    // engine applies the v2 rule to every file the server has on its first pass. An unreadable
    // manifest is reported and replaced at the next write, never a reason not to start.
    private void Load(byte[] bytes)
    {
        try
        {
            using var document = JsonDocument.Parse(bytes);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object) throw new JsonException("The manifest is not an object.");
            if (!root.TryGetProperty("version", out var version) || version.ValueKind != JsonValueKind.Number || version.GetInt32() != 2 ||
                !root.TryGetProperty("intents", out var intents) || intents.ValueKind != JsonValueKind.Object) return;
            foreach (var item in intents.EnumerateObject())
            {
                var state = (LockOwnership)item.Value.GetProperty("ownership").GetInt32();
                var file = item.Value.GetProperty("file").GetString();
                if (Enum.IsDefined(state) && !string.IsNullOrEmpty(file)) desired[item.Name] = new(state, file);
            }
        }
        catch (Exception error) when (error is JsonException or InvalidOperationException or KeyNotFoundException or FormatException)
        {
            desired.Clear();
            loadProblems.Add("The saved read-only settings could not be read, so the next pass sets them again: " + error.Message);
        }
    }

    public void Dispose() => ownership.Dispose();
}
