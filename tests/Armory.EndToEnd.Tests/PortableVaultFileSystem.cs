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
//
// The read-only bit has Windows semantics but is modeled here, per vault path: these tests
// run as root on Linux, where a cleared write permission stops nobody. ApplyLockAttribute(s)
// set it by the v2 rule (read-only unless THIS device holds the check out, Armory.Core's
// CheckoutRules.IsReadOnlyOnDisk), Replace(readOnly) sets it before the new bytes appear, it
// travels with Move, MoveFolder and a student's folder rename, and Computer.Save refuses a
// file that has it, the way SolidWorks cannot save over a read-only file.
internal sealed class PortableVaultFileSystem : IVaultFileSystem
{
    // Decision D14, as the Windows adapter's LaunchPolicy (without the computer's PATHEXT).
    private static readonly string[] RefusedLaunchTypes =
    [
        ".exe", ".com", ".bat", ".cmd", ".ps1", ".psm1", ".vbs", ".vbe", ".js", ".jse", ".wsf", ".wsh", ".hta", ".msi", ".msp", ".scr",
        ".lnk", ".url", ".reg", ".cpl", ".jar", ".appref-ms",
        ".pif", ".scf", ".website", ".settingcontent-ms", ".theme", ".themepack", ".deskthemepack",
        ".application", ".appinstaller", ".appx", ".appxbundle", ".msix", ".msixbundle", ".xbap", ".vsto", ".jnlp", ".diagcab",
        ".wsc", ".sct", ".ws", ".msc", ".gadget", ".inf", ".shb", ".shs", ".chm", ".xll", ".ade", ".adp",
        ".py", ".pyw", ".pyz", ".pyzw", ".pyc", ".pyo", ".sh", ".pl", ".rb", ".ahk", ".au3",
        ".psd1", ".ps1xml", ".psc1", ".psc2", ".ps2", ".ps2xml", ".msh", ".msh1", ".msh2", ".mshxml", ".msh1xml", ".msh2xml",
    ];
    private readonly object gate = new();
    private readonly HashSet<string> open = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> readOnlyBits = new(StringComparer.OrdinalIgnoreCase);
    // Feedback N4: SolidWorks holds a part it opened while it was writable with a write handle,
    // which every reader that does not share writing conflicts with. The Windows scan
    // (LocalChangeDetector) then can't open it: it reports "being used by another process" and
    // carries the previous scan's entry over (hash, size, read-only bit) marked Unread, and every
    // read (OpenRead, and the hash Replace, Move and MoveToRecovery check) fails. SolidWorks
    // keeps saving through its handle whatever the read-only bit says afterwards (HeldForWriting:
    // Computer.Save lets those saves through). Hold is SolidWorks (open, with its ~$ marker);
    // HoldUnreadable is another program holding it the same way (a backup or a virus scan), which
    // the open-file check does not see.
    private readonly HashSet<string> held = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> heldForWriting = new(StringComparer.OrdinalIgnoreCase);
    private Dictionary<string, LocalFile> lastScan = new(StringComparer.OrdinalIgnoreCase);
    public void Hold(string relative)
    {
        Open(relative);
        lock (gate)
        {
            held.Add(P(relative).Value);
            if (!readOnlyBits.Contains(P(relative).Value)) heldForWriting.Add(P(relative).Value);
        }
    }
    public void HoldUnreadable(string relative) { lock (gate) held.Add(P(relative).Value); }
    // Closed (SolidWorks), or let go (the other program).
    public void Unhold(string relative)
    {
        bool wasOpen;
        lock (gate)
        {
            held.Remove(P(relative).Value);
            heldForWriting.Remove(P(relative).Value);
            wasOpen = open.Contains(P(relative).Value);
        }
        if (wasOpen) Close(relative);
    }
    public bool IsHeld(string relative) { lock (gate) return held.Contains(P(relative).Value); }
    // SolidWorks opened it while it was writable: its saves go through its handle.
    public bool HeldForWriting(string relative) { lock (gate) return heldForWriting.Contains(P(relative).Value); }
    private static IOException SharingViolation(string full) => new($"The process cannot access the file '{full}' because it is being used by another process.");
    private readonly List<FolderMove> studentFolderMoves = [];
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
    // The lock ownership the engine last applied to each path: a record of the engine's calls
    // for the oracles. The bit itself (readOnlyBits) stays with its file, as on Windows, where
    // the read-only manifest keeps an intent only while the same file (NTFS id) is at its path
    // and a restart applies it to that file alone, so a new file at an old path never inherits
    // a bit there either.
    public Dictionary<string, LockOwnership> Attributes { get; } = new(StringComparer.OrdinalIgnoreCase);
    public int AttributeBatches { get; private set; }
    public List<string> Recovered { get; } = [];
    public List<FolderMove> MovedFolders { get; } = [];
    public List<string> DeletedFolders { get; } = [];
    public List<string> CopiedIn { get; } = [];
    public List<string> Launched { get; } = [];
    // False stands for a platform without directory identity: Scan reports FolderMoves as null
    // ("could not tell"), and the engine falls back to its own evidence.
    public bool ReportsFolderMoves { get; set; } = true;

    public string Full(string relative) => Path.Combine(Root, relative.Replace('/', Path.DirectorySeparatorChar));
    public static VaultPath P(string relative) => VaultPath.TryCreate(relative, out var path, out var problem) ? path : throw new ArgumentException(problem);
    // The v2 rule: Armory.Core's CheckoutRules.IsReadOnlyOnDisk, which the Windows ReadOnlyPolicy also calls.
    public static bool IsReadOnlyByRule(LockOwnership ownership) => CheckoutRules.IsReadOnlyOnDisk(ownership);

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

    // The modeled read-only bit.
    public bool IsReadOnly(string relative) { lock (gate) return readOnlyBits.Contains(P(relative).Value); }
    // Someone cleared the bit by hand (Explorer, Properties, Read-only unchecked).
    public void ClearReadOnly(string relative) { lock (gate) readOnlyBits.Remove(P(relative).Value); }
    // A brand-new file at a path never inherits an old file's bit.
    public void ForgetReadOnly(string relative) { lock (gate) readOnlyBits.Remove(P(relative).Value); }

    // A student renames a folder in Explorer: Windows refuses while a file inside is open in
    // SolidWorks; otherwise the folder moves, the read-only bits travel with its files, and
    // the next Scan reports the move once (as the Windows adapter proves it by directory id).
    public void RenameFolderAsStudent(string from, string to)
    {
        from = Folder(from);
        to = Folder(to);
        lock (gate)
        {
            var held = open.FirstOrDefault(path => Under(path, from));
            if (held is not null) throw new IOException($"The action can't be completed because {Name(held)} is open in SOLIDWORKS.");
        }
        Directory.Move(Full(from), Full(to));
        lock (gate)
        {
            Rekey(readOnlyBits, from, to);
            studentFolderMoves.Add(new FolderMove(from, to));
        }
    }

    // How long each Scan took (it reads and hashes every file, every time: the Windows adapter
    // hashes again only what changed).
    public List<TimeSpan> ScanTimes { get; } = [];
    // As the Windows scan (LocalChangeDetector): a file's hash is used again while its size and
    // last-write time are what they were and it was hashed outside the racy window. Off by default,
    // so every other test still reads every file every scan; the large-vault measurements turn it
    // on, so their scans cost what a Windows scan does.
    public bool ReuseHashes { get; set; }
    private Dictionary<string, (long Size, DateTime Written, DateTimeOffset HashedAt, string Hash)> hashed = new(StringComparer.OrdinalIgnoreCase);

    public VaultScan Scan() => Scan(CancellationToken.None);

    // Stops between files once the token is canceled, as the Windows scan does (0.3.3).
    public VaultScan Scan(CancellationToken cancellationToken)
    {
        var started = System.Diagnostics.Stopwatch.GetTimestamp();
        try { return ScanAll(cancellationToken); }
        finally { lock (gate) ScanTimes.Add(System.Diagnostics.Stopwatch.GetElapsedTime(started)); }
    }

    private VaultScan ScanAll(CancellationToken cancellationToken)
    {
        List<LocalFile> files = [];
        List<string> markers = [];
        List<string> problems = [];
        foreach (var full in Directory.EnumerateFiles(Root, "*", SearchOption.AllDirectories))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var relative = Path.GetRelativePath(Root, full).Replace(Path.DirectorySeparatorChar, '/');
            if (VaultIgnore.IsIgnored(relative))
            {
                var name = relative[(relative.LastIndexOf('/') + 1)..];
                if (name.StartsWith("~$", StringComparison.Ordinal) && !relative.Split('/').Contains(".armory", StringComparer.OrdinalIgnoreCase)) markers.Add(relative);
                continue;
            }
            if (!VaultPath.TryCreate(relative, out var path, out var problem)) { problems.Add($"{relative}: {problem}"); continue; }
            bool isHeld;
            lock (gate) isHeld = held.Contains(path.Value);
            if (isHeld)
            {
                // As LocalChangeDetector: never a deletion, and never the old entry passed off as read.
                problems.Add($"{relative}: {SharingViolation(full).Message}");
                if (lastScan.TryGetValue(path.Value, out var previous)) files.Add(previous with { Unread = true });
                continue;
            }
            try
            {
                var info = new FileInfo(full);
                var written = info.LastWriteTimeUtc;
                bool bit;
                lock (gate) bit = readOnlyBits.Contains(path.Value);
                if (ReuseHashes && hashed.TryGetValue(path.Value, out var known) && known.Size == info.Length && known.Written == written &&
                    (known.HashedAt.UtcDateTime - written).Duration() >= RacyWindow)
                {
                    files.Add(new LocalFile(path, known.Hash, known.Size, bit, Stamp: new FileStamp(path.Value, written, known.HashedAt)));
                    continue;
                }
                var bytes = File.ReadAllBytes(full);
                var hash = Convert.ToHexStringLower(SHA256.HashData(bytes));
                var now = DateTimeOffset.UtcNow;
                if (ReuseHashes) hashed[path.Value] = (bytes.Length, written, now, hash);
                files.Add(new LocalFile(path, hash, bytes.Length, bit, Stamp: new FileStamp(path.Value, written, now)));
            }
            catch (IOException error) { problems.Add($"{relative}: {error.Message}"); }
        }
        List<string> folders = [];
        foreach (var full in Directory.EnumerateDirectories(Root, "*", SearchOption.AllDirectories))
        {
            var relative = Path.GetRelativePath(Root, full).Replace(Path.DirectorySeparatorChar, '/');
            if (!VaultIgnore.IsIgnored(relative) && VaultPath.TryCreate(relative, out var folder, out _)) folders.Add(folder.Value);
        }
        folders.Sort(StringComparer.OrdinalIgnoreCase);
        FolderMove[] moves;
        lock (gate)
        {
            // A bit stays with its file: a path with no file any more has no bit.
            var present = files.Select(f => f.Path.Value).ToHashSet(StringComparer.OrdinalIgnoreCase);
            readOnlyBits.RemoveWhere(path => !present.Contains(path));
            moves = [.. studentFolderMoves];
            studentFolderMoves.Clear();
            lastScan = files.ToDictionary(f => f.Path.Value, StringComparer.OrdinalIgnoreCase);
        }
        return new(files.OrderBy(f => f.Path).ToArray(), markers, problems, null, folders, ReportsFolderMoves ? moves : null);
    }

    public bool IsOpen(VaultPath path) { lock (gate) { IsOpenCalls++; return open.Contains(path.Value); } }
    // Many files at once, answered as IsOpen answers each. The counts let a test see that a
    // pass asks once for its files (on Windows a question per file cost a Restart Manager
    // session each, 40 seconds a pass for 1,500 files), and only about the files whose plan
    // depends on it (0.3.3).
    public IReadOnlySet<string> OpenAmong(IReadOnlyCollection<VaultPath> paths)
    {
        // The Windows adapter's synchronous question pays the same cost, up to the same budget.
        var cost = Asked(paths);
        if (cost > TimeSpan.Zero) Thread.Sleep(cost < SyncEngine.OpenBudget ? cost : SyncEngine.OpenBudget);
        return OpenOf(paths);
    }

    // What the question costs on Windows (0.3.2's field data: about 14.7 ms per read-only file
    // for Restart Manager, so 1,467 files took 21 seconds and always hit the budget, and about a
    // fifth of that per writable one, a file checked out here): a test gives it for the number of
    // read-only and of writable files asked. Past the budget the answer comes at the budget, from
    // the quicker check (the open files here are SolidWorks', which the exclusive-open probe
    // finds), and says it gave up. A canceled token ends the wait at once.
    public Func<int, int, TimeSpan>? OpenAmongCost { get; set; }
    public async Task<OpenFilesAnswer> OpenAmongAsync(IReadOnlyCollection<VaultPath> paths, TimeSpan budget, CancellationToken cancellationToken, bool fresh = false)
    {
        var cost = Asked(paths);
        var timedOut = cost > budget;
        if (cost > TimeSpan.Zero) await Task.Delay(timedOut ? budget : cost, cancellationToken);
        return new OpenFilesAnswer(OpenOf(paths), timedOut);
    }

    private TimeSpan Asked(IReadOnlyCollection<VaultPath> paths)
    {
        int readOnly;
        lock (gate)
        {
            OpenAmongCalls++;
            OpenAmongSizes.Add(paths.Count);
            readOnly = paths.Count(p => readOnlyBits.Contains(p.Value));
        }
        return OpenAmongCost?.Invoke(readOnly, paths.Count - readOnly) ?? TimeSpan.Zero;
    }

    private HashSet<string> OpenOf(IReadOnlyCollection<VaultPath> paths)
    {
        lock (gate) return paths.Where(p => open.Contains(p.Value)).Select(p => p.Value).ToHashSet(StringComparer.OrdinalIgnoreCase);
    }

    public int IsOpenCalls { get; private set; }
    public int OpenAmongCalls { get; private set; }
    // How many files each open-files question named, in order.
    public List<int> OpenAmongSizes { get; } = [];
    // Files the engine opened for reading (a check out's or a check in's read, a capture), not the scan.
    public int OpenReads { get; private set; }
    public Stream OpenRead(VaultPath path)
    {
        lock (gate)
        {
            if (held.Contains(path.Value)) throw SharingViolation(Full(path.Value));
            OpenReads++;
        }
        return new FileStream(Full(path.Value), FileMode.Open, FileAccess.Read, FileShare.Read);
    }

    // As the Windows adapter: the same last-write time and size as when the scan hashed it, and
    // hashed outside the window where a write can keep its time (LocalChangeDetector.RacyWindow).
    public TimeSpan RacyWindow { get; set; } = TimeSpan.FromSeconds(2);
    public bool UnchangedSinceScan(LocalFile file)
    {
        if (file.Stamp is not { } stamp || file.Unread) return false;
        var full = Full(file.Path.Value);
        lock (gate) if (held.Contains(file.Path.Value)) return false;
        var info = new FileInfo(full);
        return info.Exists && info.Length == file.Size && info.LastWriteTimeUtc == stamp.LastWriteUtc &&
               (stamp.HashedAt.UtcDateTime - stamp.LastWriteUtc).Duration() >= RacyWindow;
    }

    // A marker's last-write time and size (Linux has no NTFS id to add).
    public string? MarkerStamp(string marker)
    {
        var info = new FileInfo(Full(marker));
        return info.Exists ? info.LastWriteTimeUtc.Ticks + ":" + info.Length : null;
    }
    // The destination's hash, read as the Windows adapter reads it: a held file can't be read.
    private bool Unreadable(VaultPath path) { lock (gate) return held.Contains(path.Value); }

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

    // Windows: a read-only destination is replaced and stays read-only; readOnly makes the new
    // bytes read-only from their first moment.
    public ReplaceOutcome Replace(VaultPath path, string? expectedHash, Stream content, bool readOnly = false)
    {
        var full = Full(path.Value);
        if (Unreadable(path)) return ReplaceOutcome.Refused(SharingViolation(full).Message);
        if (HashOf(full) != expectedHash) return ReplaceOutcome.Refused("Destination changed since the plan was made.");
        RecordIfOpen(path, "replace");
        RecordIfUnpreserved(path, expectedHash, "replace");
        Replaces++;
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        var temp = Full(".armory/staging/" + Guid.NewGuid().ToString("N") + ".pending");
        Directory.CreateDirectory(Path.GetDirectoryName(temp)!);
        using (var output = File.Create(temp)) content.CopyTo(output);
        lock (gate)
        {
            var wasReadOnly = expectedHash is not null && readOnlyBits.Contains(path.Value);
            File.Move(temp, full, overwrite: true);
            if (readOnly || wasReadOnly) readOnlyBits.Add(path.Value);
            else readOnlyBits.Remove(path.Value);
        }
        return ReplaceOutcome.Done;
    }

    public ReplaceOutcome MoveToRecovery(VaultPath path, string expectedHash)
    {
        var full = Full(path.Value);
        if (Unreadable(path)) return ReplaceOutcome.Refused(SharingViolation(full).Message);
        if (HashOf(full) != expectedHash) return ReplaceOutcome.Refused("File changed.");
        RecordIfOpen(path, "recovery");
        RecordIfUnpreserved(path, expectedHash, "recovery");
        var target = Full($".armory/recovery/{Interlocked.Increment(ref recoveries)}/{path.Value}");
        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        File.Move(full, target);
        lock (gate)
        {
            Recovered.Add(path.Value);
            readOnlyBits.Remove(path.Value);
        }
        return ReplaceOutcome.Done;
    }

    public ReplaceOutcome Move(VaultPath from, VaultPath to, string expectedHash)
    {
        var source = Full(from.Value);
        var target = Full(to.Value);
        if (Unreadable(from)) return ReplaceOutcome.Refused(SharingViolation(source).Message);
        if (HashOf(source) != expectedHash) return ReplaceOutcome.Refused("File changed.");
        if (File.Exists(target) && !string.Equals(Path.GetFullPath(source), Path.GetFullPath(target), StringComparison.OrdinalIgnoreCase)) return ReplaceOutcome.Refused("Destination exists.");
        RecordIfOpen(from, "move");
        Moves++;
        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        File.Move(source, target);
        lock (gate) if (readOnlyBits.Remove(from.Value)) readOnlyBits.Add(to.Value);
        return ReplaceOutcome.Done;
    }

    // Windows refuses while a file inside is open, never merges into an existing folder (a
    // case-only rename is allowed), and the agent's own move is not reported by Scan.
    public ReplaceOutcome MoveFolder(string from, string to)
    {
        from = Folder(from);
        to = Folder(to);
        if (from.Length == 0 || to.Length == 0) return ReplaceOutcome.Refused("That is the whole vault, not a folder in it.");
        if (string.Equals(from, to, StringComparison.Ordinal)) return ReplaceOutcome.Done;
        var caseOnly = string.Equals(from, to, StringComparison.OrdinalIgnoreCase);
        if (!Directory.Exists(Full(from))) return ReplaceOutcome.Refused($"The folder {Name(from)} is not there any more.");
        if (!caseOnly && to.StartsWith(from + "/", StringComparison.OrdinalIgnoreCase)) return ReplaceOutcome.Refused($"{Name(from)} cannot be moved into itself.");
        if (!caseOnly && ExistsIgnoringCase(to)) return ReplaceOutcome.Refused($"Something named {Name(to)} is already there.");
        lock (gate)
        {
            var held = open.FirstOrDefault(path => Under(path, from));
            if (held is not null) return ReplaceOutcome.Refused($"{Name(held)} is open in SOLIDWORKS. Close it, then try again.");
        }
        foreach (var file in Directory.EnumerateFiles(Full(from), "*", SearchOption.AllDirectories))
        {
            var moved = to + "/" + Path.GetRelativePath(Full(from), file).Replace(Path.DirectorySeparatorChar, '/');
            if (!VaultIgnore.IsIgnored(moved) && !VaultPath.TryCreate(moved, out _, out _))
                return ReplaceOutcome.Refused($"{Path.GetFileName(file)} would have too long a path in {Name(to)}. Choose a shorter name.");
        }
        Directory.CreateDirectory(Path.GetDirectoryName(Full(to))!);
        Directory.Move(Full(from), Full(to));
        lock (gate)
        {
            Rekey(readOnlyBits, from, to);
            foreach (var pair in Attributes.Where(p => Under(p.Key, from)).ToArray())
            {
                Attributes.Remove(pair.Key);
                Attributes[to + pair.Key[from.Length..]] = pair.Value;
            }
            MovedFolders.Add(new FolderMove(from, to));
        }
        return ReplaceOutcome.Done;
    }

    // Only when nothing but ignored metadata (desktop.ini, Thumbs.db, ~$ markers) is inside.
    public bool DeleteEmptyFolder(string folder)
    {
        folder = Folder(folder);
        if (folder.Length == 0) return false;
        var full = Full(folder);
        if (!Directory.Exists(full)) return !File.Exists(full);
        var files = Directory.EnumerateFiles(full, "*", SearchOption.AllDirectories).ToArray();
        if (files.Any(f => !VaultIgnore.IsIgnored(Path.GetFileName(f)))) return false;
        if (Directory.EnumerateDirectories(full, "*", SearchOption.AllDirectories).Any(d => Path.GetFileName(d).Equals(".armory", StringComparison.OrdinalIgnoreCase))) return false;
        foreach (var file in files) File.Delete(file);
        foreach (var directory in Directory.EnumerateDirectories(full, "*", SearchOption.AllDirectories).OrderByDescending(d => d.Length).Append(full))
            Directory.Delete(directory, recursive: false);
        lock (gate) DeletedFolders.Add(folder);
        return true;
    }

    // A staged copy renamed into place; never overwrites (names compare as NTFS does).
    public ReplaceOutcome CopyIn(string sourceFullPath, VaultPath to)
    {
        if (!Path.IsPathFullyQualified(sourceFullPath)) return ReplaceOutcome.Refused("Armory can only add a file from a folder on this computer.");
        if (Directory.Exists(sourceFullPath)) return ReplaceOutcome.Refused($"{Path.GetFileName(sourceFullPath)} is a folder. Add the files in it.");
        var source = new FileInfo(sourceFullPath);
        if (!source.Exists) return ReplaceOutcome.Refused($"{source.Name} is not there any more.");
        if (source.LinkTarget is not null) return ReplaceOutcome.Refused($"{source.Name} is a link to another file. Add the file itself.");
        if (Path.GetFullPath(sourceFullPath).StartsWith(Path.GetFullPath(Full(".armory")) + Path.DirectorySeparatorChar, StringComparison.Ordinal))
            return ReplaceOutcome.Refused("Armory does not add its own private files.");
        if (VaultIgnore.IsIgnored(to.Value)) return ReplaceOutcome.Refused($"Armory does not keep {to.Name} files.");
        if (ExistsIgnoringCase(to.Value)) return ReplaceOutcome.Refused($"Something named {to.Name} is already in that folder.");
        var temp = Full(".armory/staging/" + Guid.NewGuid().ToString("N") + ".copy");
        Directory.CreateDirectory(Path.GetDirectoryName(temp)!);
        File.Copy(sourceFullPath, temp);
        Directory.CreateDirectory(Path.GetDirectoryName(Full(to.Value))!);
        try { File.Move(temp, Full(to.Value), overwrite: false); }
        catch (IOException error) { File.Delete(temp); return ReplaceOutcome.Refused(error.Message); }
        lock (gate)
        {
            readOnlyBits.Remove(to.Value);
            CopiedIn.Add(to.Value);
        }
        return ReplaceOutcome.Done;
    }

    // Records instead of starting a program.
    public ReplaceOutcome Launch(VaultPath path)
    {
        if (RefusedLaunchTypes.Contains(Path.GetExtension(path.Name), StringComparer.OrdinalIgnoreCase))
            return ReplaceOutcome.Refused($"Armory does not open {Path.GetExtension(path.Name)} files, because opening one runs a program.");
        if (!File.Exists(Full(path.Value))) return ReplaceOutcome.Refused($"{path.Name} is not on this computer yet.");
        lock (gate) Launched.Add(path.Value);
        return ReplaceOutcome.Done;
    }

    // A file whose read-only bit can't be changed now (another program holds it), the way
    // Windows refuses: ApplyLockAttribute throws, and a batch carries on with the others.
    public Func<string, bool>? RefuseAttribute { get; set; }

    // Bits set one file at a time (each a manifest write on Windows), not through a batch.
    public int SingleAttributeCalls { get; private set; }

    public void ApplyLockAttribute(VaultPath path, LockOwnership ownership)
    {
        lock (gate) SingleAttributeCalls++;
        SetBit(path, ownership);
    }

    private void SetBit(VaultPath path, LockOwnership ownership)
    {
        if (RefuseAttribute?.Invoke(path.Value) == true) throw new IOException($"{path.Value}: the read-only attribute can't be changed now (test).");
        lock (gate)
        {
            Attributes[path.Value] = ownership;
            // Windows changes the bit of a file that exists; a path with no file gets no bit
            // (and no intent in the Windows manifest).
            if (!File.Exists(Full(path.Value))) readOnlyBits.Remove(path.Value);
            else if (IsReadOnlyByRule(ownership)) readOnlyBits.Add(path.Value);
            else readOnlyBits.Remove(path.Value);
        }
    }

    public void ApplyLockAttributes(IReadOnlyList<(VaultPath Path, LockOwnership Ownership)> attributes)
    {
        lock (gate) AttributeBatches++;
        foreach (var (path, ownership) in attributes)
        {
            try { SetBit(path, ownership); }
            catch (IOException) { } // retried when the next scan finds the bit as it was
        }
    }

    // One batch, as the Windows adapter's single manifest write, with the refused files returned.
    public IReadOnlyList<(VaultPath Path, string Problem)> ApplyLockAttributesNow(IReadOnlyList<(VaultPath Path, LockOwnership Ownership)> attributes)
    {
        lock (gate) AttributeBatches++;
        List<(VaultPath, string)> failed = [];
        foreach (var (path, ownership) in attributes)
        {
            try { SetBit(path, ownership); }
            catch (IOException error) { failed.Add((path, error.Message)); }
        }
        return failed;
    }

    public void EnsureFolder(string vaultRelativeFolder) => Directory.CreateDirectory(Full(vaultRelativeFolder));
    public bool FolderExists(string vaultRelativeFolder) => Directory.Exists(Full(vaultRelativeFolder));
    public Stream CreateStaging(out string stagingName)
    {
        stagingName = ".armory/staging/" + Guid.NewGuid().ToString("N") + ".download";
        var full = Full(stagingName);
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        return new FileStream(full, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None);
    }
    public void DeleteStaging(string stagingName) { var full = Full(stagingName); if (File.Exists(full)) File.Delete(full); }

    private static string Folder(string folder) => folder.Replace('\\', '/').Trim('/');
    private static string Name(string path) => path[(path.LastIndexOf('/') + 1)..];
    private static bool Under(string path, string folder) => path.StartsWith(folder + "/", StringComparison.OrdinalIgnoreCase);
    private static void Rekey(HashSet<string> paths, string from, string to)
    {
        foreach (var path in paths.Where(p => Under(p, from)).ToArray())
        {
            paths.Remove(path);
            paths.Add(to + path[from.Length..]);
        }
    }
    // NTFS compares names without case; Linux does not.
    private bool ExistsIgnoringCase(string relative)
    {
        var full = Full(relative);
        var parent = Path.GetDirectoryName(full)!;
        return Directory.Exists(parent) && Directory.EnumerateFileSystemEntries(parent)
            .Any(entry => string.Equals(Path.GetFileName(entry), Path.GetFileName(full), StringComparison.OrdinalIgnoreCase));
    }
}
