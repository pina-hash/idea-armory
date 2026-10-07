using System.Text.Json;
using System.Text.Json.Nodes;

namespace Armory.Client;

public enum MemberRole { Student, CadLead, Mentor, Instructor }
public enum ProjectReleaseGate { Warn, Enforce }

// Season is null for a project made without one (contract v2, C1). Archived is false when the
// server does not send the flag (a server older than 0232).
public sealed record RemoteProject(Guid Id, string Name, int? Season, MemberRole Role, int PinnedRelease, ProjectReleaseGate ReleaseGate, bool Archived);
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
    // kind: crash, slowAction, slowPass, repeatedFailure, repairedCheckout, readOnlyBroken or userReport.
    public async Task<Guid> SubmitAppIncidentAsync(string kind, string summary, string appVersion, string? deviceName, Guid? project, JsonObject report,
        Guid? feedback, CancellationToken ct = default)
        => GuidOf(await rest.CallAsync(SubmitIncidentRpc, Args(("p_kind", kind), ("p_summary", summary), ("p_app_version", appVersion), ("p_device_name", deviceName),
            ("p_project", project), ("p_report", report), ("p_feedback", feedback)), ct));

    public async Task<IReadOnlyList<RemoteProject>> MyProjectsAsync(CancellationToken ct = default)
        => Array(await rest.CallAsync("armory_my_projects", Args(), ct)).Select(p => new RemoteProject(
            Guid.Parse(p["id"]!.GetValue<string>()), p["name"]!.GetValue<string>(), p["season"]?.GetValue<int>(), ParseRole(p["role"]!.GetValue<string>()),
            p["pinned_release"]!.GetValue<int>(), p["release_gate"]!.GetValue<string>() == "enforce" ? ProjectReleaseGate.Enforce : ProjectReleaseGate.Warn,
            p["archived"]?.GetValue<bool>() ?? false)).ToArray();
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
