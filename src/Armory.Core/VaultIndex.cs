using System.Text;

namespace Armory.Core;

public sealed record NameCollision(string Name, IReadOnlyList<VaultPath> Paths);
public sealed record InvalidServerPath(string Path, string Problem);
public sealed record ImportReport(IReadOnlyList<NameCollision> Collisions, IReadOnlyList<InvalidServerPath> InvalidPaths);

public sealed class VaultIndex
{
    private readonly Dictionary<string, VaultPath> names = new(VaultPath.Comparer);
    public VaultPath? Find(string name) => VaultPath.TryValidateName(name, out _) && names.TryGetValue(name.Normalize(NormalizationForm.FormC), out var path) ? path : null;
    public bool IsFree(string name) => VaultPath.TryValidateName(name, out _) && Find(name) is null;
    public bool TryAdd(VaultPath path, out string? problem)
    {
        if (!path.IsValid) { problem = "The server path is invalid; a lead must fix it."; return false; }
        if (names.TryGetValue(path.Name, out var existing))
        { problem = $"'{path.Name}' already lives at '{existing}'."; return false; }
        names.Add(path.Name, path);
        problem = null;
        return true;
    }

    public IReadOnlyList<NameCollision> FindImportCollisions(IEnumerable<VaultPath> imported)
        => names.Values.Concat(imported).Where(p => p.IsValid)
            .GroupBy(p => p.Name, VaultPath.Comparer).Where(g => g.Count() > 1)
            .OrderBy(g => g.Key, VaultPath.Comparer)
            .Select(g => new NameCollision(g.Key, g.Order().ToArray())).ToArray();

    public ImportReport AnalyzeImport(IEnumerable<string> serverPaths)
    {
        List<VaultPath> valid = [];
        List<InvalidServerPath> invalid = [];
        foreach (var raw in serverPaths)
        {
            if (VaultPath.TryCreate(raw, out var path, out var problem)) valid.Add(path);
            else invalid.Add(new(raw, $"A lead must fix this server name: {problem}"));
        }
        return new(FindImportCollisions(valid), invalid.OrderBy(p => p.Path, StringComparer.Ordinal).ToArray());
    }
}
