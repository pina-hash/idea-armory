using System.Text.Json;
using System.Text.Json.Nodes;

namespace Armory.Client;

public enum MemberRole { Student, CadLead, Mentor, Instructor }
public enum ProjectReleaseGate { Warn, Enforce }

// Season is null for a project made without one (contract v2, C1). Archived is false when the
// server does not send the flag (a server older than 0232). CanTakeBack is the server's
// can_take_back (v0.3, 0233: mentor, CAD lead or site admin), or null from a server older than
// 0233, which never sends it.
public sealed record RemoteProject(Guid Id, string Name, int? Season, MemberRole Role, int PinnedRelease, ProjectReleaseGate ReleaseGate, bool Archived,
    bool? CanTakeBack = null);
public sealed record RemoteVersion(Guid Id, string Hash, long Bytes, string Author, DateTimeOffset CreatedAt, int? SavedRelease, bool? ReleaseChecked);
public sealed record RemoteLock(string HolderEmail, Guid HolderDeviceId, string? HolderDeviceName, DateTimeOffset AcquiredAt,
    DateTimeOffset? BrokenAt, string? BrokenBy, string? BrokenHolderEmail, Guid? BrokenHolderDeviceId)
{
    public bool IsLive => BrokenAt is null;
}
public sealed record RemoteFile(Guid Id, string Folder, string Name, bool Deleted, DateTimeOffset CreatedAt, RemoteVersion? Current, RemoteLock? Lock);
public sealed record RemoteChange(long Cursor, Guid ProjectId, string Kind, Guid EntityId, JsonObject Payload, DateTimeOffset CreatedAt);
public sealed record RemoteHistoryEntry(Guid Id, string Kind, string Author, DateTimeOffset CreatedAt, long Bytes, string? Hash, Guid? Parent,
    string? Reason, int? SavedRelease, bool? ReleaseChecked);
public sealed record CommitResult(Guid VersionId, bool Advanced);
// One live check out (contract v2, C7). HolderName is the holder's profile name, or null when
// they have none; DeviceName is null only when the device row is gone.
public sealed record RemoteCheckout(Guid FileId, string Folder, string Name, string HolderEmail, string? HolderName, string? DeviceName, DateTimeOffset Since);
// One file of a batch (v0.3, armory_lock_files and armory_release_locks). Ok with Done (acquired
// or released), or not Ok with the SQLSTATE and message the per-file call refused with.
public sealed record BatchFileResult(Guid FileId, bool Ok, bool Done, string? Code, string? Message);
// {total, succeeded, refused, results}. Some files can land while others are refused: a batch is
// never all or nothing, so callers report each file.
public sealed record BatchResult(int Total, int Succeeded, int Refused, IReadOnlyList<BatchFileResult> Results);
// One of the caller's own feedback notes (v0.3.2, idea-app 0235, armory_my_app_feedback), newest
// first. Status is new, seen, resolved or closed: a note the site marked spam reads closed, so the
// app never calls a person's note spam. There are no replies from the team: the site has none.
public sealed record AppFeedbackNote(Guid Id, DateTimeOffset CreatedAt, string Kind, string Body, string? Tried, string? Area, bool HasScreenshot,
    string AppVersion, string? DeviceName, string Status, DateTimeOffset? ReviewedAt)
{
    public const string New = "new", Seen = "seen", Resolved = "resolved", Closed = "closed";

    // Where a note is, in the words "Your feedback" shows. There are no replies: the status is all
    // the site keeps for the person.
    public static string StatusWords(string status) => status switch
    {
        New => "Not read yet",
        Seen => "Read by the IDEA team",
        Resolved => "Done",
        _ => "Closed",
    };
}
// The payload of a folder_purged change (v0.3, armory_purge_folder): those files and their
// history are gone from the server.
public sealed record FolderPurge(string Folder, int Files, IReadOnlyList<Guid> FileIds, string? By)
{
    public const string Kind = "folder_purged";

    public static FolderPurge? From(RemoteChange change)
    {
        if (change.Kind != Kind) return null;
        var ids = new List<Guid>();
        if (change.Payload["file_ids"] is JsonArray list)
            foreach (var item in list)
                if (item is JsonValue v && v.TryGetValue<string>(out var text) && Guid.TryParse(text, out var id)) ids.Add(id);
        var folder = change.Payload["folder"] is JsonValue f && f.TryGetValue<string>(out var name) ? name : "";
        var files = change.Payload["files"] is JsonValue n && n.TryGetValue<int>(out var count) ? count : ids.Count;
        var by = change.Payload["by"] is JsonValue b && b.TryGetValue<string>(out var who) ? who : null;
        return new(folder, files, ids, by);
    }
}

// Typed calls for every RPC in docs/agent/CONTRACT.md, server/sql/001-005. Every write
// takes the caller's operation id; replaying the same id returns the stored receipt.
public sealed class ArmoryApi(PostgrestClient rest)
{
    public async Task<Guid> RegisterDeviceAsync(string name, Guid operation, CancellationToken ct = default)
        => GuidOf(await rest.CallAsync("armory_register_device", Args(("p_name", name), ("p_operation", operation)), ct));
    public async Task<bool> IsMemberAsync(Guid project, CancellationToken ct = default)
        => BoolOf(await rest.CallAsync("armory_is_member", Args(("p_project", project)), ct));
    public async Task<bool> AcquireLockAsync(Guid file, Guid device, Guid operation, CancellationToken ct = default)
        => BoolOf(await rest.CallAsync("armory_acquire_lock", Args(("p_file", file), ("p_device", device), ("p_operation", operation)), ct));
    public async Task<bool> ReleaseLockAsync(Guid file, Guid device, Guid operation, CancellationToken ct = default)
        => BoolOf(await rest.CallAsync("armory_release_lock", Args(("p_file", file), ("p_device", device), ("p_operation", operation)), ct));
    public async Task<bool> BreakLockAsync(Guid file, Guid device, Guid operation, CancellationToken ct = default)
        => BoolOf(await rest.CallAsync("armory_break_lock", Args(("p_file", file), ("p_device", device), ("p_operation", operation)), ct));
    public async Task<Guid> SaveSideVersionAsync(Guid file, Guid? parent, string key, string hash, long bytes, string reason, Guid device, Guid operation, CancellationToken ct = default)
        => GuidOf(await rest.CallAsync("armory_save_side_version", Args(("p_file", file), ("p_parent", parent), ("p_key", key), ("p_hash", hash), ("p_bytes", bytes), ("p_reason", reason), ("p_device", device), ("p_operation", operation)), ct));
    public async Task<CommitResult> CommitVersionAsync(Guid file, Guid? parent, string key, string hash, long bytes, Guid device, Guid operation, CancellationToken ct = default)
        => CommitOf(await rest.CallAsync("armory_commit_version", Args(("p_file", file), ("p_parent", parent), ("p_key", key), ("p_hash", hash), ("p_bytes", bytes), ("p_device", device), ("p_operation", operation)), ct));
    // savedRelease null means the release could not be read ("release not checked").
    public async Task<CommitResult> CommitVersionWithReleaseAsync(Guid file, Guid? parent, string key, string hash, long bytes, Guid device, Guid operation, int? savedRelease, CancellationToken ct = default)
        => CommitOf(await rest.CallAsync("armory_commit_version_with_release", Args(("p_file", file), ("p_parent", parent), ("p_key", key), ("p_hash", hash), ("p_bytes", bytes), ("p_device", device), ("p_operation", operation), ("p_saved_release", (short?)savedRelease)), ct));
    public async Task<Guid> SaveSideVersionWithReleaseAsync(Guid file, Guid? parent, string key, string hash, long bytes, string reason, Guid device, Guid operation, int? savedRelease, CancellationToken ct = default)
        => GuidOf(await rest.CallAsync("armory_save_side_version_with_release", Args(("p_file", file), ("p_parent", parent), ("p_key", key), ("p_hash", hash), ("p_bytes", bytes), ("p_reason", reason), ("p_device", device), ("p_operation", operation), ("p_saved_release", (short?)savedRelease)), ct));
    public async Task<bool> TombstoneAsync(Guid file, Guid? parent, Guid device, Guid operation, CancellationToken ct = default)
        => BoolOf(await rest.CallAsync("armory_tombstone", Args(("p_file", file), ("p_parent", parent), ("p_device", device), ("p_operation", operation)), ct));
    public async Task<(string? PartNumber, bool SubsystemFull)> AllocatePartNumberAsync(Guid project, int subsystem, int? season, Guid operation, CancellationToken ct = default)
    {
        var row = FirstRow(await rest.CallAsync("armory_allocate_part_number", Args(("p_project", project), ("p_subsystem", subsystem), ("p_season", season), ("p_operation", operation)), ct));
        return (row["part_number"]?.GetValue<string>(), row["subsystem_full"]?.GetValue<bool>() ?? false);
    }
    public async Task<IReadOnlyList<RemoteChange>> ListChangesAsync(Guid project, long after, CancellationToken ct = default)
    {
        var node = await rest.CallAsync("armory_list_changes", Args(("p_project", project), ("p_after", after)), ct);
        return Array(node).Select(c => new RemoteChange(c["cursor"]!.GetValue<long>(), Guid.Parse(c["project_id"]!.GetValue<string>()),
            c["kind"]!.GetValue<string>(), Guid.Parse(c["entity_id"]!.GetValue<string>()), c["payload"] as JsonObject is { } p ? (JsonObject)p.DeepClone() : new JsonObject(),
            Time(c["created_at"])!.Value)).ToArray();
    }
    // season null makes a project without one (contract v2, C1); p_season is always sent.
    public async Task<Guid> CreateProjectAsync(string name, int? season, Guid operation, CancellationToken ct = default)
        => GuidOf(await rest.CallAsync("armory_create_project", Args(("p_name", name), ("p_season", (short?)season), ("p_operation", operation)), ct));
    // Contract v2. Mentor only; false when the project already has that exact name.
    public async Task<bool> RenameProjectAsync(Guid project, string name, Guid operation, CancellationToken ct = default)
        => BoolOf(await rest.CallAsync("armory_rename_project", Args(("p_project", project), ("p_name", name), ("p_operation", operation)), ct));
    // Contract v2. Mentor only; false when the project was already in that state.
    public async Task<bool> SetProjectArchivedAsync(Guid project, bool archived, Guid operation, CancellationToken ct = default)
        => BoolOf(await rest.CallAsync("armory_set_project_archived", Args(("p_project", project), ("p_archived", archived), ("p_operation", operation)), ct));
    // Contract v2. Returns the number of live files moved. A refusal is ArmoryRpcException with
    // IsInUse; FolderRefusal.TryParse reads its Details.
    public async Task<int> RenameFolderAsync(Guid project, string from, string to, Guid device, Guid operation, CancellationToken ct = default)
        => IntOf(await rest.CallAsync("armory_rename_folder", Args(("p_project", project), ("p_from", from), ("p_to", to), ("p_device", device), ("p_operation", operation)), ct));
    // Contract v2. Returns the number of live files removed; refusals as RenameFolderAsync.
    public async Task<int> DeleteFolderAsync(Guid project, string folder, Guid device, Guid operation, CancellationToken ct = default)
        => IntOf(await rest.CallAsync("armory_delete_folder", Args(("p_project", project), ("p_folder", folder), ("p_device", device), ("p_operation", operation)), ct));
    public async Task<bool> AddMemberAsync(Guid project, string email, MemberRole role, Guid operation, CancellationToken ct = default)
        => BoolOf(await rest.CallAsync("armory_add_member", Args(("p_project", project), ("p_email", email), ("p_role", RoleName(role)), ("p_operation", operation)), ct));
    public async Task<bool> RemoveMemberAsync(Guid project, string email, Guid operation, CancellationToken ct = default)
        => BoolOf(await rest.CallAsync("armory_remove_member", Args(("p_project", project), ("p_email", email), ("p_operation", operation)), ct));
    public async Task<Guid> CreateFileAsync(Guid project, string folder, string name, Guid device, Guid operation, CancellationToken ct = default)
        => GuidOf(await rest.CallAsync("armory_create_file", Args(("p_project", project), ("p_folder", folder), ("p_name", name), ("p_device", device), ("p_operation", operation)), ct));
    public async Task<bool> MoveFileAsync(Guid file, string folder, string name, Guid device, Guid operation, CancellationToken ct = default)
        => BoolOf(await rest.CallAsync("armory_move_file", Args(("p_file", file), ("p_folder", folder), ("p_name", name), ("p_device", device), ("p_operation", operation)), ct));
    public async Task<bool> SetReleaseGateAsync(Guid project, ProjectReleaseGate mode, Guid operation, CancellationToken ct = default)
        => BoolOf(await rest.CallAsync("armory_set_release_gate", Args(("p_project", project), ("p_mode", mode == ProjectReleaseGate.Enforce ? "enforce" : "warn"), ("p_operation", operation)), ct));
    public async Task<bool> RaisePinnedReleaseAsync(Guid project, int release, Guid operation, CancellationToken ct = default)
        => BoolOf(await rest.CallAsync("armory_raise_pinned_release", Args(("p_project", project), ("p_release", (short)release), ("p_operation", operation)), ct));

    // Website v0.3 (docs/agent/website-requests-v0.3.md, sections 4 and 4b). Until the site's
    // migration is live both answer 404 PGRST202 (ArmoryRpcException.IsFunctionMissing).
    public const string SubmitFeedbackRpc = "armory_submit_app_feedback", SubmitIncidentRpc = "armory_submit_app_incident";
    // kind: bug, idea or other. context: what the window was doing, never file contents.
    public async Task<Guid> SubmitAppFeedbackAsync(string kind, string body, string appVersion, string? deviceName, JsonObject context, CancellationToken ct = default)
        => GuidOf(await rest.CallAsync(SubmitFeedbackRpc, Args(("p_kind", kind), ("p_body", body), ("p_app_version", appVersion), ("p_device_name", deviceName),
            ("p_context", context)), ct));
    // v0.3.2 (idea-app 0235): the eight-argument form, with what was tried, the area of the app and
    // a screenshot's key in armory-feedback-shots (uploaded first, FeedbackScreenshots). It has no
    // defaults, so all eight named arguments always go, a blank one as null (no call can match both
    // forms). kind: bug, idea, praise or other. A site before 0235 answers 404 PGRST202
    // (FeedbackSender falls back to the five arguments). Refusals are 22023 with DETAIL {reason,
    // field}: too_long for tried (1000) and area (120), bad_path, not_found and in_use for the
    // screenshot; PT429 is shared with the five-argument form (20 an hour).
    public async Task<Guid> SubmitAppFeedbackAsync(string kind, string body, string appVersion, string? deviceName, JsonObject context,
        string? tried, string? area, string? screenshot, CancellationToken ct = default)
        => GuidOf(await rest.CallAsync(SubmitFeedbackRpc, Args(("p_kind", kind), ("p_body", body), ("p_app_version", appVersion), ("p_device_name", deviceName),
            ("p_context", context), ("p_tried", Blank(tried)), ("p_area", Blank(area)), ("p_screenshot", Blank(screenshot))), ct));

    // v0.3.2 (idea-app 0235): the caller's own notes, newest first, at most limit (the site clamps
    // it to 1 to 200). Null when the site does not have it yet (404 PGRST202): the window hides
    // "Your feedback". No session: ArmoryRpcException 42501.
    public const string MyFeedbackRpc = "armory_my_app_feedback";
    public async Task<IReadOnlyList<AppFeedbackNote>?> MyAppFeedbackAsync(int limit = 50, CancellationToken ct = default)
    {
        JsonNode? node;
        try { node = await rest.CallAsync(MyFeedbackRpc, Args(("p_limit", limit)), ct); }
        catch (ArmoryRpcException error) when (error.IsFunctionMissing) { return null; }
        return Array(node).Select(n => new AppFeedbackNote(Guid.Parse(n["id"]!.GetValue<string>()), Time(n["created_at"])!.Value,
            n["kind"]!.GetValue<string>(), n["body"]!.GetValue<string>(), TextOf(n["tried"]), TextOf(n["area"]),
            n["has_screenshot"] is JsonValue shot && shot.GetValue<bool>(), TextOf(n["app_version"]) ?? "", TextOf(n["device_name"]),
            FeedbackStatus(TextOf(n["status"])), Time(n["reviewed_at"]))).ToArray();
    }
    // kind: crash, slowAction, slowPass, repeatedFailure, repairedCheckout, readOnlyBroken or userReport.
    public async Task<Guid> SubmitAppIncidentAsync(string kind, string summary, string appVersion, string? deviceName, Guid? project, JsonObject report,
        Guid? feedback, CancellationToken ct = default)
        => GuidOf(await rest.CallAsync(SubmitIncidentRpc, Args(("p_kind", kind), ("p_summary", summary), ("p_app_version", appVersion), ("p_device_name", deviceName),
            ("p_project", project), ("p_report", report), ("p_feedback", feedback)), ct));

    public async Task<IReadOnlyList<RemoteProject>> MyProjectsAsync(CancellationToken ct = default)
        => Array(await rest.CallAsync("armory_my_projects", Args(), ct)).Select(p => new RemoteProject(
            Guid.Parse(p["id"]!.GetValue<string>()), p["name"]!.GetValue<string>(), p["season"]?.GetValue<int>(), ParseRole(p["role"]!.GetValue<string>()),
            p["pinned_release"]!.GetValue<int>(), p["release_gate"]!.GetValue<string>() == "enforce" ? ProjectReleaseGate.Enforce : ProjectReleaseGate.Warn,
            p["archived"]?.GetValue<bool>() ?? false, p["can_take_back"] is JsonValue take ? take.GetValue<bool>() : null)).ToArray();

    // v0.3 (0233). When the project was deleted forever, or null (it exists, or it never did,
    // or the caller was only removed from it). Any signed-in user may ask.
    public async Task<DateTimeOffset?> ProjectPurgedAsync(Guid project, CancellationToken ct = default)
        => Time(await rest.CallAsync("armory_project_purged", Args(("p_project", project)), ct));

    // v0.3 (0233). Stamps this computer's last_seen, and its version and state when given (null
    // or empty keeps what the server has). A call within 20 seconds that changes nothing writes
    // nothing on the server, so callers add no suppression of their own.
    public async Task HeartbeatAsync(Guid device, string? appVersion, string? state, CancellationToken ct = default)
        => await rest.CallAsync("armory_heartbeat", Args(("p_device", device), ("p_app_version", appVersion), ("p_state", state)), ct);

    // v0.3 (0233, 0234): at most this many distinct files per armory_lock_files, armory_release_locks
    // or armory_break_locks.
    public const int MaximumBatchFiles = 500;

    // v0.3 (0233). armory_acquire_lock per file, in id order, in one call. 1 to 500 distinct
    // files (Chunk anything larger); a replayed operation answers the first time.
    public async Task<BatchResult> LockFilesAsync(IReadOnlyCollection<Guid> files, Guid device, Guid operation, CancellationToken ct = default)
        => BatchOf(await rest.CallAsync("armory_lock_files", Args(("p_files", BatchFiles(files)), ("p_device", device), ("p_operation", operation)), ct), "acquired");

    // v0.3 (0233). The same over armory_release_lock.
    public async Task<BatchResult> ReleaseLocksAsync(IReadOnlyCollection<Guid> files, Guid device, Guid operation, CancellationToken ct = default)
        => BatchOf(await rest.CallAsync("armory_release_locks", Args(("p_files", BatchFiles(files)), ("p_device", device), ("p_operation", operation)), ct), "released");

    // v0.3.1 (idea-app 0234). Force check in: armory_break_lock per file, in id order, in one call,
    // each file under an operation id the server derives from this one and the file. 1 to 500
    // distinct files (Chunk anything larger). Done is broken: false means nobody had it checked
    // out any more. A replayed operation answers the first time and writes nothing. A site before
    // 0234 answers 404 PGRST202: the caller sends one BreakLockAsync per file instead.
    public const string BreakLocksRpc = "armory_break_locks";
    public async Task<BatchResult> BreakLocksAsync(IReadOnlyCollection<Guid> files, Guid device, Guid operation, CancellationToken ct = default)
        => BatchOf(await rest.CallAsync(BreakLocksRpc, Args(("p_files", BatchFiles(files)), ("p_device", device), ("p_operation", operation)), ct), "broken");

    // Files in batches the server takes: distinct, in id order, at most MaximumBatchFiles each.
    public static IReadOnlyList<Guid[]> Chunk(IEnumerable<Guid> files)
        => files.Distinct().Order().Chunk(MaximumBatchFiles).ToArray();
    // Contract v2 (C7): every live, unbroken check out on a live file of the project, for members.
    public async Task<IReadOnlyList<RemoteCheckout>> ProjectCheckoutsAsync(Guid project, CancellationToken ct = default)
        => Array(await rest.CallAsync("armory_project_checkouts", Args(("p_project", project)), ct)).Select(c => new RemoteCheckout(
            Guid.Parse(c["file_id"]!.GetValue<string>()), c["folder"]!.GetValue<string>(), c["name"]!.GetValue<string>(), c["holder_email"]!.GetValue<string>(),
            c["holder_name"]?.GetValue<string>(), c["device_name"]?.GetValue<string>(), Time(c["since"])!.Value)).ToArray();
    public async Task<IReadOnlyList<RemoteFile>> ProjectFilesAsync(Guid project, CancellationToken ct = default)
        => Array(await rest.CallAsync("armory_project_files", Args(("p_project", project)), ct)).Select(f => new RemoteFile(
            Guid.Parse(f["id"]!.GetValue<string>()), f["folder"]!.GetValue<string>(), f["name"]!.GetValue<string>(), f["deleted"]!.GetValue<bool>(),
            Time(f["created_at"])!.Value, f["current"] is JsonObject v ? new RemoteVersion(Guid.Parse(v["id"]!.GetValue<string>()), v["hash"]!.GetValue<string>(),
                v["bytes"]!.GetValue<long>(), v["author"]!.GetValue<string>(), Time(v["created_at"])!.Value, v["saved_release"]?.GetValue<int>(), v["release_checked"]?.GetValue<bool>()) : null,
            f["lock"] is JsonObject l ? new RemoteLock(l["holder_email"]!.GetValue<string>(), Guid.Parse(l["holder_device_id"]!.GetValue<string>()), l["holder_device_name"]?.GetValue<string>(),
                Time(l["acquired_at"])!.Value, Time(l["broken_at"]), l["broken_by"]?.GetValue<string>(), l["broken_holder_email"]?.GetValue<string>(),
                l["broken_holder_device_id"] is JsonValue b ? Guid.Parse(b.GetValue<string>()) : null) : null)).ToArray();
    public async Task<IReadOnlyList<RemoteHistoryEntry>> FileHistoryAsync(Guid file, CancellationToken ct = default)
        => Array(await rest.CallAsync("armory_file_history", Args(("p_file", file)), ct)).Select(h => new RemoteHistoryEntry(
            Guid.Parse(h["id"]!.GetValue<string>()), h["kind"]!.GetValue<string>(), h["author"]!.GetValue<string>(), Time(h["created_at"])!.Value,
            h["bytes"]!.GetValue<long>(), h["hash"]?.GetValue<string>(), h["parent"] is JsonValue p ? Guid.Parse(p.GetValue<string>()) : null,
            h["reason"]?.GetValue<string>(), h["saved_release"]?.GetValue<int>(), h["release_checked"]?.GetValue<bool>())).ToArray();

    internal static string RoleName(MemberRole role) => role switch
    {
        MemberRole.Student => "student", MemberRole.CadLead => "cad_lead", MemberRole.Mentor => "mentor", MemberRole.Instructor => "instructor",
        _ => throw new ArgumentOutOfRangeException(nameof(role)),
    };
    internal static MemberRole ParseRole(string role) => role switch
    {
        "student" => MemberRole.Student, "cad_lead" => MemberRole.CadLead, "mentor" => MemberRole.Mentor, "instructor" => MemberRole.Instructor,
        _ => throw new InvalidDataException($"Unknown member role {role}."),
    };
    private static Dictionary<string, object?> Args(params (string Name, object? Value)[] values) => values.ToDictionary(v => v.Name, v => v.Value);
    // A blank optional text goes as null (the site stores a blank one as nothing too).
    private static string? Blank(string? text) => string.IsNullOrWhiteSpace(text) ? null : text;
    private static string? TextOf(JsonNode? node) => node is JsonValue v && v.TryGetValue<string>(out var text) ? text : null;
    // new, seen, resolved or closed; spam is never shown as such (the site already says closed).
    private static string FeedbackStatus(string? status) => status switch
    {
        AppFeedbackNote.Seen or AppFeedbackNote.Resolved or AppFeedbackNote.Closed => status,
        "spam" => AppFeedbackNote.Closed,
        _ => AppFeedbackNote.New,
    };
    private static Guid[] BatchFiles(IReadOnlyCollection<Guid> files)
    {
        var distinct = files.Distinct().Order().ToArray();
        if (distinct.Length == 0 || distinct.Length != files.Count || distinct.Length > MaximumBatchFiles)
            throw new ArgumentException($"A batch takes 1 to {MaximumBatchFiles} distinct files; use ArmoryApi.Chunk.", nameof(files));
        return distinct;
    }
    private static BatchResult BatchOf(JsonNode? node, string done)
    {
        var o = node as JsonObject ?? throw new InvalidDataException("Armory answered a batch without a result.");
        var results = new List<BatchFileResult>();
        if (o["results"] is JsonArray list)
            foreach (var r in list.OfType<JsonObject>())
            {
                var ok = r["ok"] is JsonValue okValue && okValue.GetValue<bool>();
                results.Add(new(Guid.Parse(r["file_id"]!.GetValue<string>()), ok, ok && r[done] is JsonValue d && d.GetValue<bool>(),
                    r["code"]?.ToString(), r["message"] is JsonValue m && m.TryGetValue<string>(out var text) ? text : null));
            }
        return new(Number(o, "total") ?? results.Count, Number(o, "succeeded") ?? results.Count(r => r.Ok), Number(o, "refused") ?? results.Count(r => !r.Ok), results);
        static int? Number(JsonObject o, string name) => o[name] is JsonValue v && v.TryGetValue<int>(out var n) ? n : null;
    }
    private static Guid GuidOf(JsonNode? node) => node is JsonValue v && Guid.TryParse(v.GetValue<string>(), out var id) ? id : throw new InvalidDataException("Armory answered without an id.");
    private static bool BoolOf(JsonNode? node) => node is JsonValue v ? v.GetValue<bool>() : throw new InvalidDataException("Armory answered without a result.");
    private static int IntOf(JsonNode? node) => node is JsonValue v ? v.GetValue<int>() : throw new InvalidDataException("Armory answered without a count.");
    private static JsonObject FirstRow(JsonNode? node) => node is JsonArray { Count: > 0 } a && a[0] is JsonObject o ? o : node as JsonObject ?? throw new InvalidDataException("Armory answered without a row.");
    private static CommitResult CommitOf(JsonNode? node)
    {
        var row = FirstRow(node);
        return new(Guid.Parse(row["version_id"]!.GetValue<string>()), row["advanced"]!.GetValue<bool>());
    }
    private static IEnumerable<JsonObject> Array(JsonNode? node) => node is JsonArray a ? a.OfType<JsonObject>() : node is null ? [] : throw new InvalidDataException("Armory answered without a list.");
    private static DateTimeOffset? Time(JsonNode? node) => node is JsonValue v ? DateTimeOffset.Parse(v.GetValue<string>(), System.Globalization.CultureInfo.InvariantCulture) : null;
}
