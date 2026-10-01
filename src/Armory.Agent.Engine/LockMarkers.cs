using Armory.Core;

namespace Armory.Agent.Engine;

// SolidWorks writes "~$<document name>" beside a document while it has it open
// (docs/spike/solidworks-lock-file.md: one observation, 2026 SP04.1). The platform's ignore
// list keeps the marker itself out of sync; the engine reads it as "this document is open".
public static class LockMarkers
{
    public static bool TryGetDocument(string markerPath, out string documentPath)
    {
        documentPath = "";
        var normalized = markerPath.Replace('\\', '/');
        var slash = normalized.LastIndexOf('/');
        var name = normalized[(slash + 1)..];
        if (!name.StartsWith("~$", StringComparison.Ordinal) || name.Length <= 2) return false;
        var candidate = normalized[..(slash + 1)] + name[2..];
        if (!VaultPath.TryCreate(candidate, out var path, out _) || !Reconciler.IsSolidWorks(path)) return false;
        documentPath = path.Value;
        return true;
    }
}
