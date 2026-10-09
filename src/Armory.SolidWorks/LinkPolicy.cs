using Armory.Agent.Engine;

namespace Armory.SolidWorks;

// The link's decisions about SolidWorks' events, kept apart from COM so they are tested on any
// platform (docs/agent/SOLIDWORKS.md, "Events"): which documents get document events (vault
// documents, and new documents until they are saved somewhere else), which are the student's
// own opens, and what each event becomes on the engine's channel.
internal static class LinkPolicy
{
    private static readonly string[] Extensions = [".sldprt", ".sldasm", ".slddrw"];

    // A SolidWorks document under the vault root (full Windows paths, any case).
    internal static bool IsVaultDocument(string? fullPath, string vaultRoot)
    {
        if (string.IsNullOrWhiteSpace(fullPath) || string.IsNullOrWhiteSpace(vaultRoot)) return false;
        var root = Normalize(vaultRoot).TrimEnd('\\') + "\\";
        var path = Normalize(fullPath);
        return path.Length > root.Length && path.StartsWith(root, StringComparison.OrdinalIgnoreCase) && Extensions.Any(e => path.EndsWith(e, StringComparison.OrdinalIgnoreCase));
    }

    // A document with no path yet (File > New): watched until it is saved, in case that is into the vault.
    internal static bool IsUnsaved(string? fullPath) => string.IsNullOrWhiteSpace(fullPath);

    internal static bool Watches(string? fullPath, string vaultRoot) => IsUnsaved(fullPath) || IsVaultDocument(fullPath, vaultRoot);

    // The document type from its name, when SolidWorks can't say (swDocumentTypes_e).
    internal static int DocTypeOf(string? fullPath) => Path.GetExtension(fullPath ?? "").ToLowerInvariant() switch
    {
        ".sldprt" => SwDocTypes.Part,
        ".sldasm" => SwDocTypes.Assembly,
        ".slddrw" => SwDocTypes.Drawing,
        _ => SwDocTypes.None,
    };

    // The student opened it: it was SolidWorks' active document when its open finished
    // (FileOpenPostNotify), or it is visible in its own window when the link attached to a
    // SolidWorks already running. A reference an assembly or a drawing loaded is neither.
    internal static bool StudentOpened(string? activePath, string openedPath, bool visibleAtAttach = false)
        => visibleAtAttach || (activePath is not null && SamePath(activePath, openedPath));

    internal static bool SamePath(string? a, string? b)
        => a is not null && b is not null && string.Equals(Normalize(a), Normalize(b), StringComparison.OrdinalIgnoreCase);

    internal static string Normalize(string path) => path.Replace('/', '\\').Trim();

    // The key a document is kept under in a session.
    internal static string Key(string fullPath) => Normalize(fullPath).ToUpperInvariant();

    // What a save of one document wrote, by swFileSaveTypes_e: Save and Save As write the
    // document's own path (FileName); Save As Copy writes another file and the document's own
    // bytes stay as they were.
    internal static bool SavedOwnPath(int saveType) => saveType is SwConstants.SaveTypeSave or SwConstants.SaveTypeSaveAs;

    // DestroyNotify2: a document closed, or only hidden (still loaded by an assembly, so still open).
    internal static bool Closed(int destroyType) => destroyType == SwConstants.DestroyTypeDestroy;

    // The mode of a save, from what Save to Version is set to now (research 3.4 step 5).
    // optionOn and targetYear: the option as set for this save; pinned: the document's project's
    // pinned year (null: not a vault document of a project).
    internal static LinkSaveMode SaveMode(bool optionOn, int targetYear, int runningYear, int? pinned)
    {
        if (pinned is not { } pin || pin >= runningYear) return LinkSaveMode.Current;
        return optionOn && targetYear <= pin ? LinkSaveMode.SaveDown : LinkSaveMode.PrivateDraft;
    }
}
