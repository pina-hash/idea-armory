using System.Security.Cryptography;
using Armory.Agent.Engine;
using Armory.Core;
using Armory.Platform.Windows;

namespace Armory.EndToEnd.Tests;

// A real temp folder standing in for C:\IDEA\Armory on Linux. It applies the platform
// layer's own ignore list (VaultIgnore) and reports SolidWorks ~$ markers the way the
// Windows adapter does. "Open in SolidWorks" is simulated: Open() adds the path to a set
// and writes the ~$ marker. This double deliberately does NOT refuse writes to an open
// file, so the engine's own open-file check is what the proof exercises; every write to
// an open file is recorded as a violation for the oracle instead.
internal sealed class PortableVaultFileSystem : IVaultFileSystem
{
    private readonly object gate = new();
    private readonly HashSet<string> open = new(StringComparer.OrdinalIgnoreCase);
    private int recoveries;
    public PortableVaultFileSystem(string root) { Root = root; Directory.CreateDirectory(root); }
    public string Root { get; }
    public bool WriteMarkers { get; set; } = true;
    public List<string> OpenWriteViolations { get; } = [];
    // The oracle for "never overwrite unpreserved bytes": before any replace or recovery move,
    // the bytes being displaced must already be in server history.
    public Func<string, bool>? IsPreserved { get; set; }
    public List<string> UnpreservedOverwrites { get; } = [];
    public int Moves { get; private set; }
    public int Replaces { get; private set; }
    public Dictionary<string, LockOwnership> Attributes { get; } = new(StringComparer.OrdinalIgnoreCase);
    public List<string> Recovered { get; } = [];

    public string Full(string relative) => Path.Combine(Root, relative.Replace('/', Path.DirectorySeparatorChar));
    public static VaultPath P(string relative) => VaultPath.TryCreate(relative, out var path, out var problem) ? path : throw new ArgumentException(problem);

    public void Open(string relative)
    {
        lock (gate) open.Add(P(relative).Value);
        var marker = MarkerFor(relative);
        if (WriteMarkers && File.Exists(Full(relative))) File.WriteAllBytes(Full(marker), [0]);
    }
    public void Close(string relative)
    {
        lock (gate) open.Remove(P(relative).Value);
        var marker = Full(MarkerFor(relative));
        if (File.Exists(marker)) File.Delete(marker);
    }
    // SolidWorks stopped without closing the document: no longer open, marker left behind.
    public void CrashApp(string relative) { lock (gate) open.Remove(P(relative).Value); }
    public bool IsOpenNow(string relative) { lock (gate) return open.Contains(P(relative).Value); }
    public IReadOnlyList<string> OpenFiles() { lock (gate) return open.ToArray(); }
    private static string MarkerFor(string relative)
    {
        var slash = relative.LastIndexOf('/');
        return relative[..(slash + 1)] + "~$" + relative[(slash + 1)..];
    }

    public VaultScan Scan()
    {
        List<LocalFile> files = [];
        List<string> markers = [];
        List<string> problems = [];
        foreach (var full in Directory.EnumerateFiles(Root, "*", SearchOption.AllDirectories))
        {
            var relative = Path.GetRelativePath(Root, full).Replace(Path.DirectorySeparatorChar, '/');
            if (VaultIgnore.IsIgnored(relative))
            {
                var name = relative[(relative.LastIndexOf('/') + 1)..];
                if (name.StartsWith("~$", StringComparison.Ordinal) && !relative.Split('/').Contains(".armory", StringComparer.OrdinalIgnoreCase)) markers.Add(relative);
                continue;
            }
            if (!VaultPath.TryCreate(relative, out var path, out var problem)) { problems.Add($"{relative}: {problem}"); continue; }
            try
            {
                var bytes = File.ReadAllBytes(full);
                files.Add(new LocalFile(path, Convert.ToHexStringLower(SHA256.HashData(bytes)), bytes.Length));
            }
            catch (IOException error) { problems.Add($"{relative}: {error.Message}"); }
        }
        return new(files.OrderBy(f => f.Path).ToArray(), markers, problems);
    }

    public bool IsOpen(VaultPath path) { lock (gate) return open.Contains(path.Value); }
    public Stream OpenRead(VaultPath path) => new FileStream(Full(path.Value), FileMode.Open, FileAccess.Read, FileShare.Read);

    private string? HashOf(string full) => File.Exists(full) ? Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(full))) : null;
    private void RecordIfUnpreserved(VaultPath path, string? hash, string what)
    {
        if (hash is null || IsPreserved is null || IsPreserved(hash)) return;
        lock (gate) UnpreservedOverwrites.Add($"{what} {path} {hash[..8]}");
    }
    private void RecordIfOpen(VaultPath path, string what)
    {
        lock (gate) if (open.Contains(path.Value)) OpenWriteViolations.Add($"{what} {path}");
    }

    public ReplaceOutcome Replace(VaultPath path, string? expectedHash, Stream content)
    {
        var full = Full(path.Value);
        if (HashOf(full) != expectedHash) return ReplaceOutcome.Refused("Destination changed since the plan was made.");
        RecordIfOpen(path, "replace");
        RecordIfUnpreserved(path, expectedHash, "replace");
        Replaces++;
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        var temp = Full(".armory/staging/" + Guid.NewGuid().ToString("N") + ".pending");
        Directory.CreateDirectory(Path.GetDirectoryName(temp)!);
        using (var output = File.Create(temp)) content.CopyTo(output);
        File.Move(temp, full, overwrite: true);
        return ReplaceOutcome.Done;
    }

    public ReplaceOutcome MoveToRecovery(VaultPath path, string expectedHash)
    {
        var full = Full(path.Value);
        if (HashOf(full) != expectedHash) return ReplaceOutcome.Refused("File changed.");
        RecordIfOpen(path, "recovery");
        RecordIfUnpreserved(path, expectedHash, "recovery");
        var target = Full($".armory/recovery/{Interlocked.Increment(ref recoveries)}/{path.Value}");
        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        File.Move(full, target);
        lock (gate) Recovered.Add(path.Value);
        return ReplaceOutcome.Done;
    }

    public ReplaceOutcome Move(VaultPath from, VaultPath to, string expectedHash)
    {
        var source = Full(from.Value);
        var target = Full(to.Value);
        if (HashOf(source) != expectedHash) return ReplaceOutcome.Refused("File changed.");
        if (File.Exists(target) && !string.Equals(Path.GetFullPath(source), Path.GetFullPath(target), StringComparison.OrdinalIgnoreCase)) return ReplaceOutcome.Refused("Destination exists.");
        RecordIfOpen(from, "move");
        Moves++;
        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        File.Move(source, target);
        return ReplaceOutcome.Done;
    }

    public void ApplyLockAttribute(VaultPath path, LockOwnership ownership) { lock (gate) Attributes[path.Value] = ownership; }
    public void EnsureFolder(string vaultRelativeFolder) => Directory.CreateDirectory(Full(vaultRelativeFolder));
    public Stream CreateStaging(out string stagingName)
    {
        stagingName = ".armory/staging/" + Guid.NewGuid().ToString("N") + ".download";
        var full = Full(stagingName);
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        return new FileStream(full, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None);
    }
    public void DeleteStaging(string stagingName) { var full = Full(stagingName); if (File.Exists(full)) File.Delete(full); }
}
