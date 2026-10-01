using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Armory.Core;

namespace Armory.Agent.Engine;

// Operation ids are derived, never invented at call time: SHA-256 over a Core journal
// entry id (or another durable value) and the step, formatted as a version-5-style UUID.
// The same intent therefore carries the same id after any crash, and the server answers
// a replay from its receipt.
public static class OperationIds
{
    public static Guid Derive(params string[] parts)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes("armory-operation\n" + string.Join('\n', parts)));
        bytes[6] = (byte)((bytes[6] & 0x0F) | 0x50);
        bytes[8] = (byte)((bytes[8] & 0x3F) | 0x80);
        return new Guid(bytes.AsSpan(0, 16), bigEndian: true);
    }
}

// The engine's durable document. Saved atomically after every step that changes what the
// engine knows, and always before a server write (the in-flight record).
internal sealed class EngineState
{
    public int Schema { get; set; } = 1;
    public string? Email { get; set; }
    public Guid? DeviceId { get; set; }
    public long Sequence { get; set; }
    public Dictionary<Guid, ProjectState> Projects { get; set; } = [];
    public Dictionary<string, FileState> Files { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public HashSet<string> Completed { get; set; } = new(StringComparer.Ordinal);
    public List<PendingMove> Moves { get; set; } = [];
    // Device ids this vault was synced under before a reconnect. Their locks are still this
    // computer's, and writes under those locks use the holding id (the server accepts any
    // device the same person registered).
    public List<Guid> FormerDevices { get; set; } = [];
    // One-off events a student should still see on the next screen, such as a rename that
    // was put back. Shown for a while, then dropped.
    public List<RememberedNotice> Remembered { get; set; } = [];

    internal bool IsMine(Guid device) => device == DeviceId || FormerDevices.Contains(device);

    internal string NextId(string kind) => $"{DeviceId}:{kind}:{++Sequence}";

    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web) { DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull };
    internal byte[] Serialize() => JsonSerializer.SerializeToUtf8Bytes(this, Options);
    internal static EngineState Load(IEngineStateStore store)
    {
        var bytes = store.Load();
        if (bytes is null || bytes.Length == 0) return new();
        var state = JsonSerializer.Deserialize<EngineState>(bytes, Options) ?? new();
        // JSON does not preserve the comparers.
        state.Files = new(state.Files, StringComparer.OrdinalIgnoreCase);
        state.Completed = new(state.Completed, StringComparer.Ordinal);
        return state;
    }
}

internal sealed class ProjectState
{
    public Guid Id { get; set; }
    public string Name { get; set; } = "";
    public long Cursor { get; set; }
    public int PinnedRelease { get; set; } = 2025;
    public bool Enforce { get; set; }
    public string Role { get; set; } = "student";
    public bool Usable { get; set; } = true;
}

internal sealed class FileState
{
    public string Path { get; set; } = "";
    public Guid ProjectId { get; set; }
    public Guid? FileId { get; set; }
    // BASE: the remote revision the local file last matched. A tombstone base has a null hash.
    public string? BaseId { get; set; }
    public string? BaseHash { get; set; }
    public string? Preserved { get; set; }
    public string? LastCaptured { get; set; }
    public List<string> Entries { get; set; } = [];
    public string? CreateEntry { get; set; }
    public string? DeleteEntry { get; set; }
    public string? MarkerEntry { get; set; }
    public int Attempt { get; set; }
    public bool BreakNotice { get; set; }
    public string? Refusal { get; set; }
    public string? RefusalKind { get; set; }
    public bool ReleaseNotChecked { get; set; }
    public bool NewerWaiting { get; set; }
    public string? NewerAuthor { get; set; }
    public LockOwnership? AppliedOwnership { get; set; }
    public List<SideRecord> Sides { get; set; } = [];
    public Inflight? Inflight { get; set; }
    // Consecutive scans that did not find a file this computer had: a deletion is planned
    // only after two, so one bad scan never deletes for the team.
    public int AbsentScans { get; set; }
    // An Explorer rename or move of this file to another path, being sent as a server move.
    public string? LocalMoveTo { get; set; }
    // Journaled saves the release gate refused: private drafts kept on this computer. They
    // never hold the lock and are offered again whenever the gate would allow them.
    public List<string> Drafts { get; set; } = [];

    [JsonIgnore] public Revision? Base => BaseId is null ? null : new(BaseId, BaseHash, "");
    public void SetBase(Revision? revision) { BaseId = revision?.Id; BaseHash = revision?.Hash; }
}

internal sealed record RememberedNotice(string Kind, Guid? FileId, string Path, string Title, string Detail, DateTimeOffset At);
internal sealed record SideRecord(Guid VersionId, string Hash, string Reason, DateTimeOffset At);

// One server write that was about to be sent. Re-sent with the same operation id on the
// next pass if the engine stopped before recording its answer.
internal sealed record Inflight(string Kind, Guid Operation, string? EntryId = null, Guid? ProjectId = null, Guid? FileId = null,
    string? Folder = null, string? Name = null, string? ParentId = null, string? Hash = null, long Bytes = 0, string? SnapshotId = null,
    int? SavedRelease = null, string? Reason = null, bool ReleaseNotChecked = false, Guid? Device = null);

// A rename sent through armory_move_file: requested with MoveAsync, or an Explorer rename the
// engine detected (Local), whose bytes already sit at To.
internal sealed record PendingMove(Guid Operation, Guid FileId, string From, string To, bool Local = false);
