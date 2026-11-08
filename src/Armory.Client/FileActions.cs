using System.Text.Json.Nodes;

namespace Armory.Client;

public sealed partial class ArmoryApi
{
    private DateTimeOffset folderPathsMissingUntil;
    private readonly Dictionary<Guid, IReadOnlyList<string>> knownFolderPaths = [];

    private Task<int?> EditKnownFolderAsync(Guid project, string action, string from, string? to, Guid device, Guid operation, CancellationToken ct)
        => knownFolderPaths.TryGetValue(project, out var paths) && paths.Any(p => p.Equals(from, StringComparison.OrdinalIgnoreCase) || p.StartsWith(from + "/", StringComparison.OrdinalIgnoreCase))
            ? EditFolderAsync(project, action, from, to, device, operation, ct) : Task.FromResult<int?>(null);

    // Null means an older website. Do not repeatedly ask it for a missing capability.
    public async Task<IReadOnlyList<string>?> FolderPathsAsync(Guid project, CancellationToken ct = default)
    {
        if (DateTimeOffset.UtcNow < folderPathsMissingUntil) return null;
        try
        {
            var result = await rest.CallAsync("armory_list_folder_paths", Args(("p_project", project)), ct);
            if (result is not JsonArray paths) { folderPathsMissingUntil = DateTimeOffset.UtcNow.AddHours(6); return null; }
            var answer = paths.Select(p => p!.GetValue<string>()).ToArray();
            knownFolderPaths[project] = answer;
            return answer;
        }
        catch (ArmoryRpcException error) when (error.IsFunctionMissing)
        { folderPathsMissingUntil = DateTimeOffset.UtcNow.AddHours(6); return null; }
    }

    private async Task<int?> EditFolderAsync(Guid project, string action, string from, string? to, Guid device, Guid operation, CancellationToken ct)
    {
        if (DateTimeOffset.UtcNow < folderPathsMissingUntil) return null;
        try
        {
            return IntOf(await rest.CallAsync("armory_edit_folder", Args(("p_project", project), ("p_action", action), ("p_from", from),
                ("p_to", to), ("p_device", device), ("p_operation", operation)), ct));
        }
        catch (ArmoryRpcException error) when (error.IsFunctionMissing)
        { folderPathsMissingUntil = DateTimeOffset.UtcNow.AddHours(6); return null; }
    }

    public async Task<bool> CreateFolderAsync(Guid project, string folder, Guid device, Guid operation, CancellationToken ct = default)
    {
        if (await EditFolderAsync(project, "create", folder, null, device, operation, ct) is null) return false;
        knownFolderPaths[project] = knownFolderPaths.GetValueOrDefault(project, []).Append(folder).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        return true;
    }

    // The website's existing Trash action. Its membership, class-folder and checkout rules
    // are authoritative. Removed files keep their versions and are restorable for 30 days.
    public async Task<bool> TrashFileAsync(Guid file, bool restore, Guid operation, CancellationToken ct = default)
        => BoolOf(await rest.CallAsync("armory_web_trash", Args(("p_file", file), ("p_restore", restore), ("p_operation", operation)), ct));
}
