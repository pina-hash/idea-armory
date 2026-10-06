using System.Globalization;
using System.Text.Json;

namespace Armory.Client;

// One file named in a folder refusal. 0232 sends only names; the richer fields are read when a
// server sends them (lane A's design notes proposed file objects) and are null otherwise.
public sealed record FolderRefusalFile(string Name, Guid? FileId = null, string? Folder = null, string? HolderEmail = null,
    string? HolderName = null, string? DeviceName = null, DateTimeOffset? Since = null);

// The DETAIL of a 55006 refusal from armory_rename_folder or armory_delete_folder (contract v2,
// C5 and C6). 0232 sends {"reason": "checked_out" | "target_exists", "names": [...at most 10],
// "total": n}. TryParse also reads "count" for "total", "files" (objects or strings) for
// "names" and an optional "folder", and returns null for anything that is not a JSON object.
public sealed record FolderRefusal(string Reason, string? Folder, int Count, IReadOnlyList<FolderRefusalFile> Files)
{
    public const string CheckedOutReason = "checked_out";
    public const string TargetExistsReason = "target_exists";

    // Someone else (another person, or this person on another computer) has a file in it checked out.
    public bool IsCheckedOut => Reason == CheckedOutReason;
    // The target folder already holds live files.
    public bool IsTargetExists => Reason == TargetExistsReason;

    public static FolderRefusal? TryParse(string? details)
    {
        if (string.IsNullOrWhiteSpace(details)) return null;
        try
        {
            using var document = JsonDocument.Parse(details);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return null;
            var files = new List<FolderRefusalFile>();
            foreach (var list in new[] { "files", "names" })
            {
                if (!root.TryGetProperty(list, out var items) || items.ValueKind != JsonValueKind.Array) continue;
                foreach (var item in items.EnumerateArray())
                {
                    if (item.ValueKind == JsonValueKind.String) files.Add(new(item.GetString()!));
                    else if (item.ValueKind == JsonValueKind.Object && Text(item, "name") is { } name)
                        files.Add(new(name, Id(item, "file_id"), Text(item, "folder"), Text(item, "holder_email"), Text(item, "holder_name"),
                            Text(item, "device_name"), Time(item, "since")));
                }
                break;
            }
            var count = Number(root, "total") ?? Number(root, "count") ?? files.Count;
            return new(Text(root, "reason") ?? "", Text(root, "folder"), Math.Max(count, files.Count), files);
        }
        catch (JsonException) { return null; }

        static string? Text(JsonElement e, string name) => e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
        static int? Number(JsonElement e, string name) => e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out var n) ? n : null;
        static Guid? Id(JsonElement e, string name) => Text(e, name) is { } s && Guid.TryParse(s, out var g) ? g : null;
        static DateTimeOffset? Time(JsonElement e, string name)
            => Text(e, name) is { } s && DateTimeOffset.TryParse(s, CultureInfo.InvariantCulture, DateTimeStyles.None, out var t) ? t : null;
    }
}
