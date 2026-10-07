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
    // 1 is 0.1.0. 2 is v2 check out (decision D15): see Migrate.
    internal const int CurrentSchema = 2;
    public int Schema { get; set; } = CurrentSchema;
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
    // Notice cards the student dismissed: the card's key and the items it showed then. Those
    // items stay hidden; a new item brings the card back with only the new ones. Pruned only at
    // the end of a whole online pass, when every notice of that pass is known.
    public Dictionary<string, HashSet<string>> Dismissed { get; set; } = new(StringComparer.Ordinal);
    // When each file was revived (contract C4, change file_revived), from the change feed. The
    // server drops a revived file's removal from its history, so File detail marks the first
    // version after a revival from these.
    public Dictionary<Guid, List<DateTimeOffset>> Revivals { get; set; } = [];

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
        state.Dismissed = new(state.Dismissed, StringComparer.Ordinal);
        state.Revivals ??= [];
        state.Migrate();
        return state;
    }

    // 0.1.0 (schema 1) to v2 (decision D15). 0.1.0 made a file read-only only while someone
    // else held its lock, and recorded the ownership it applied. That ownership is kept as this
    // computer's last knowledge of who holds each file: the v2 rule reads it (Free and someone
    // else are read-only now, ThisDevice stays writable), and the first pass, online or offline,
    // applies the v2 rule to every file whose bit on disk differs from it (0.1.0 left Free
    // files writable). A lock this computer holds is kept: it is now a check out ("Checked out
    // by you"), never released by itself. A project's local folder is its name. Roles are kept
    // in the server's words.
    internal void Migrate()
    {
        if (Schema >= CurrentSchema) return;
        foreach (var file in Files.Values)
        {
            if (file.MarkerEntry is { } marker) Completed.Add(marker);
            file.MarkerEntry = null;
        }
        foreach (var project in Projects.Values)
        {
            if (string.IsNullOrEmpty(project.Folder)) project.Folder = project.Name;
            project.Role = ProjectState.RoleName(project.Role);
        }
        Schema = CurrentSchema;
    }
}

internal sealed class ProjectState
{
    public Guid Id { get; set; }
    public string Name { get; set; } = "";
    // The project's folder on this computer, the first segment of every local path in it.
    // Stage E2 moves it in place when the project is renamed on the site; until then it
    // follows the name.
    public string Folder { get; set; } = "";
    public long Cursor { get; set; }
    public int PinnedRelease { get; set; } = 2025;
    public bool Enforce { get; set; }
    public string Role { get; set; } = "student";
    public bool Usable { get; set; } = true;
    public bool Archived { get; set; }

    // A mentor or CAD lead may take a file back (armory_break_lock).
    [JsonIgnore] public bool CanTakeBack => Role is "mentor" or "cad_lead";

    // The server's words for a role; 0.1.0 stored the client's enum names.
    internal static string RoleName(string? role) => role switch
    {
        "Student" or "student" => "student",
        "CadLead" or "cad_lead" => "cad_lead",
        "Mentor" or "mentor" => "mentor",
        "Instructor" or "instructor" => "instructor",
        _ => "student",
    };
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
    // 0.1.0 only: the journal intent of a lock its ~$ marker took. v2 markers never lock;
    // Migrate completes it.
    public string? MarkerEntry { get; set; }
    public int Attempt { get; set; }
    public bool BreakNotice { get; set; }
    public string? Refusal { get; set; }
    public string? RefusalKind { get; set; }
    public bool ReleaseNotChecked { get; set; }
    public bool NewerWaiting { get; set; }
    public string? NewerAuthor { get; set; }
    // The ownership the read-only rule was last applied from: this computer's last knowledge
    // of who holds the file, used again while offline.
    public LockOwnership? AppliedOwnership { get; set; }
    // The file's live check out as this computer last knew it (from the server, or from its own
    // check out and let go), so the window still says who has it while offline.
    public KnownLock? Holder { get; set; }
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
    // v2 check out (docs/agent/ENGINE.md). Each request is durable here before any server call,
    // so a crash finishes it on the next pass.
    // CheckOut: the student asked to check this file out; the lock's operation id derives from it.
    public string? CheckOut { get; set; }
    // Check in or Undo check out, asked for a file this computer has checked out.
    public CheckoutRequest Request { get; set; }
    // A file added while open stays checked out to its creator and is checked in when it
    // closes (decision D2). A closed add is checked in by the same pass.
    public bool AutoCheckIn { get; set; }
    // The lock was taken only for a move or a removal, and is let go once that is done.
    public bool TransientLock { get; set; }

    [JsonIgnore] public Revision? Base => BaseId is null ? null : new(BaseId, BaseHash, "");
    public void SetBase(Revision? revision) { BaseId = revision?.Id; BaseHash = revision?.Hash; }
}

internal sealed record KnownLock(string Email, Guid Device, string? DeviceName, DateTimeOffset Since);
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
