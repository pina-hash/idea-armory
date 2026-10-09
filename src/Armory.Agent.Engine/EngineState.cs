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

// The engine's durable document, replaced atomically on every save. A save is due before every
// server write (the in-flight record, through the group commit in SyncEngine.Persistence.cs) and
// before anything durable carries one of its ids; other changes are saved at the end of each
// phase of a pass. Only the file records that changed since the last save are serialized again
// (StateSerializer).
internal sealed class EngineState
{
    // 1 is 0.1.0. 2 is v2 check out (decision D15): see Migrate.
    internal const int CurrentSchema = 2;
    public int Schema { get; set; } = CurrentSchema;
    public string? Email { get; set; }
    public Guid? DeviceId { get; set; }
    public long Sequence { get; set; }
    public Dictionary<Guid, ProjectState> Projects { get; set; } = [];
    private FileTable files = new();
    public FileTable Files
    {
        get => files;
        set
        {
            files = value ?? new();
            files.Owner = this;
            serializer = null;
            foreach (var st in files.Values) Track(st);
        }
    }
    public IdSet Completed { get; set; } = new();
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
    // Folders inside a project (vault-relative, "Robot 2027/Drivetrain/Gearbox") that held files
    // the server has, on this computer. Only these are removed when nothing is left in them
    // (decision D17), and only these, gone from the disk, can be a deleted folder: a folder a
    // student made stays.
    public HashSet<string> KnownFolders { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    // Consecutive scans that did not find a known folder: a folder removal is sent only after
    // two, like a file's.
    public Dictionary<string, int> AbsentFolders { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    // Folder renames and removals made on this disk, each sent as one server call with its
    // operation id (durable before the call), and refused renames waiting to be put back.
    public List<PendingFolderOp> FolderOps { get; set; } = [];
    // Folder renames the server announced (folder_renamed), not yet moved here in one step.
    public List<RemoteFolderRename> RemoteFolderRenames { get; set; } = [];
    // A folder this engine is moving on this disk right now (the team's rename, the window's
    // rename, a project renamed on the site, a folder put back). Saved before the move and
    // cleared, with the records following, in the same save after it: a stop in between is
    // finished from what the disk shows on the next start, before the scan's files are read,
    // so the moved files are never taken for missing ones and new ones.
    public List<MovingFolder> MovingFolders { get; set; } = [];
    // Bulk adds (an unzip, a paste, a Pack and Go, Add files): one import summary each.
    public List<ImportRecord> Imports { get; set; } = [];
    // Force check in of many files (armory_break_locks, v0.3.1): one record per call, saved before
    // it is sent and dropped once its answer is applied. A stop in between leaves it here, and the
    // next online pass sends it again with the same id only while every file still has the very
    // check out it named (SyncEngine.Batches.cs, ResumeForceCheckInsAsync).
    public List<PendingForceCheckIn> ForceCheckIns { get; set; } = [];

    internal bool IsMine(Guid device) => device == DeviceId || FormerDevices.Contains(device);

    // Ids (captures, removals, check outs, folder operations) are handed out from blocks:
    // Sequence, as saved, is the end of the block in use, and it is saved before any id of a new
    // block is used, so an id is never handed out twice, even after a crash, and a run of 5,000
    // captures needs a save per block, not per id. The engine reserves a block with its save
    // (ReserveIds(save)) before it takes one; a block whose save fails is given back, so no id
    // comes from a block the disk never had.
    internal const long IdBlock = 1024;
    private long issued = -1;
    internal bool IdsRunOut => Issued >= Sequence;
    private long Issued => issued < 0 ? Sequence : issued;
    internal void ReserveIds()
    {
        issued = Issued;
        Sequence = issued + IdBlock;
    }
    internal void ReserveIds(Action save)
    {
        var (sequence, before) = (Sequence, issued);
        ReserveIds();
        try { save(); }
        catch
        {
            (Sequence, issued) = (sequence, before);
            throw;
        }
    }
    internal string NextId(string kind)
    {
        if (IdsRunOut) throw new InvalidOperationException("No id is reserved: reserve a block and save it first.");
        issued = Issued + 1;
        return $"{DeviceId}:{kind}:{issued}";
    }

    public EngineState() => files.Owner = this;

    // File records by FileId, kept as FileId changes (FileState.FileId tells its owner). A record
    // taken out of Files may stay in a list; lookups answer only records that are in Files.
    private readonly Dictionary<Guid, List<FileState>> byFileId = [];
    private void Track(FileState st)
    {
        st.Owner = this;
        if (st.FileId is { } id) Index(st, id);
    }

    // Files tells its document of every record that comes and goes, and each record of every
    // change (FileState.Changed): the FileId index and the serializer's blocks follow.
    internal void FileAdded(string key, FileState st)
    {
        Track(st);
        serializer?.Added(key, st);
    }
    internal void FileRemoved(FileState st) => serializer?.Removed(st);
    internal void RecordChanged(FileState st) => serializer?.Changed(st);
    internal void FileIdChanged(FileState st, Guid? old)
    {
        if (old is { } before && byFileId.TryGetValue(before, out var list))
        {
            list.Remove(st);
            if (list.Count == 0) byFileId.Remove(before);
        }
        if (st.FileId is { } now) Index(st, now);
    }
    private void Index(FileState st, Guid id)
    {
        if (!byFileId.TryGetValue(id, out var list)) byFileId[id] = list = [];
        if (!list.Contains(st)) list.Add(st);
    }
    private bool Holds(FileState st) => Files.TryGetValue(st.Path, out var current) && ReferenceEquals(current, st);
    // The records with this FileId (almost always one), other than except.
    internal List<FileState> WithFileId(Guid id, FileState? except = null)
    {
        List<FileState> found = [];
        if (byFileId.TryGetValue(id, out var list))
            foreach (var st in list)
                if (!ReferenceEquals(st, except) && Holds(st)) found.Add(st);
        return found;
    }
    internal FileState? FirstWithFileId(Guid id, FileState? except = null)
    {
        if (!byFileId.TryGetValue(id, out var list)) return null;
        foreach (var st in list)
            if (!ReferenceEquals(st, except) && Holds(st)) return st;
        return null;
    }

    internal static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        // Set, not left to the first call, so StateSerializer can read the document's properties first.
        TypeInfoResolver = new System.Text.Json.Serialization.Metadata.DefaultJsonTypeInfoResolver(),
    };
    private StateSerializer? serializer;
    // The whole document, with only what changed since the last call serialized again.
    internal byte[] Serialize() => StateSerializer.Join(SerializeParts(whole: true)!);
    // The same as pieces to write one after the other (none of them is changed afterwards), or
    // null when nothing in the document changed since the last call (unless whole).
    internal IReadOnlyList<ReadOnlyMemory<byte>>? SerializeParts(bool whole) => (serializer ??= new StateSerializer(this)).Serialize(this, whole);
    // The same document by the reflection serializer alone (tests compare the two).
    internal byte[] SerializeWhole() => JsonSerializer.SerializeToUtf8Bytes(this, Options);
    internal static EngineState Load(IEngineStateStore store)
    {
        var bytes = store.Load();
        if (bytes is null || bytes.Length == 0) return new();
        var state = JsonSerializer.Deserialize<EngineState>(bytes, Options) ?? new();
        // JSON does not preserve the comparers (Files keeps its own).
        state.Completed ??= new();
        state.Dismissed = new(state.Dismissed, StringComparer.Ordinal);
        state.Revivals ??= [];
        state.KnownFolders = new(state.KnownFolders ?? [], StringComparer.OrdinalIgnoreCase);
        state.AbsentFolders = new(state.AbsentFolders ?? [], StringComparer.OrdinalIgnoreCase);
        state.FolderOps ??= [];
        state.RemoteFolderRenames ??= [];
        state.MovingFolders ??= [];
        state.Imports ??= [];
        state.ForceCheckIns ??= [];
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
    // The project's folder on this computer, the first segment of every local path in it. It
    // is the project's name, except while a rename on the site waits for a file to close
    // (then the folder is moved in place, never copied).
    public string Folder { get; set; } = "";
    // The top-level folder a student renamed this project's folder to in Explorer, while it
    // waits to be put back (decision D16). The project's own folder is never made again
    // beside it, and nothing in the project is planned meanwhile.
    public string? PutBackFrom { get; set; }
    public long Cursor { get; set; }
    public int PinnedRelease { get; set; } = 2025;
    public bool Enforce { get; set; }
    public string Role { get; set; } = "student";
    public bool Usable { get; set; } = true;
    public bool Archived { get; set; }
    // The server's can_take_back (v0.3, 0233: mentor, CAD lead, or site admin), or null from a
    // server that does not send it.
    public bool? TakeBack { get; set; }
    // v0.3: when the project was deleted forever (armory_project_purged). Its folder and records
    // leave this computer (its files to Armory's recovery folder), then the project is forgotten.
    public DateTimeOffset? PurgedAt { get; set; }
    // No longer in armory_my_projects, and armory_project_purged answered null: this person was
    // removed from it. Handled as before 0.3 (not synced, its folder left as it is); asked again
    // once per start, in case it is deleted forever later.
    public bool Departed { get; set; }

    // Who may force a check in (armory_break_lock): exactly what the server says, or, from a
    // server older than 0233, a mentor or CAD lead.
    [JsonIgnore] public bool CanTakeBack => TakeBack ?? Role is "mentor" or "cad_lead";

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
    public FileState()
    {
        entries.Owner = this;
        sides.Owner = this;
        drafts.Owner = this;
    }

    // Moves on every change of a serialized field (a list's contents included), so the state
    // document serializes this record again only when it changed (StateSerializer). A unit test
    // sets each public property in turn and checks that it moves.
    internal long Version { get; private set; }
    internal void Changed()
    {
        Version++;
        Owner?.RecordChanged(this);
    }
    // The serializer's block this record is written in.
    internal StateSerializer.Block? Block;
    // This record's entry in the files object as last written ("key":{...}), and for which key and version.
    internal byte[]? Json;
    internal string? JsonKey;
    internal long JsonVersion = -1;

    private string path = "";
    private Guid projectId;
    private string? baseId;
    private string? baseHash;
    private string? preserved;
    private string? lastCaptured;
    private ChangeList<string> entries = new();
    private string? createEntry;
    private string? deleteEntry;
    private string? markerEntry;
    private int attempt;
    private bool breakNotice;
    private string? refusal;
    private string? refusalKind;
    private bool releaseNotChecked;
    private bool newerWaiting;
    private string? newerAuthor;
    private bool removedWaiting;
    private LockOwnership? appliedOwnership;
    private KnownLock? holder;
    private ChangeList<SideRecord> sides = new();
    private Inflight? inflight;
    private int absentScans;
    private string? localMoveTo;
    private ChangeList<string> drafts = new();
    private string? checkOut;
    private CheckoutRequest request;
    private bool autoCheckIn;
    private bool transientLock;
    private bool purged;

    private void Set<T>(ref T field, T value)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return;
        field = value;
        Changed();
    }

    private ChangeList<T> Owned<T>(ChangeList<T>? list)
    {
        list ??= new();
        list.Owner = this;
        Changed();
        return list;
    }

    public string Path { get => path; set => Set(ref path, value); }
    public Guid ProjectId { get => projectId; set => Set(ref projectId, value); }
    private Guid? fileId;
    public Guid? FileId
    {
        get => fileId;
        set
        {
            if (fileId == value) return;
            var old = fileId;
            fileId = value;
            Changed();
            Owner?.FileIdChanged(this, old);
        }
    }
    // The state document this record belongs to, which keeps it findable by FileId.
    internal EngineState? Owner { get; set; }
    // BASE: the remote revision the local file last matched. A tombstone base has a null hash.
    public string? BaseId { get => baseId; set => Set(ref baseId, value); }
    public string? BaseHash { get => baseHash; set => Set(ref baseHash, value); }
    public string? Preserved { get => preserved; set => Set(ref preserved, value); }
    public string? LastCaptured { get => lastCaptured; set => Set(ref lastCaptured, value); }
    public ChangeList<string> Entries { get => entries; set => entries = Owned(value); }
    public string? CreateEntry { get => createEntry; set => Set(ref createEntry, value); }
    public string? DeleteEntry { get => deleteEntry; set => Set(ref deleteEntry, value); }
    // 0.1.0 only: the journal intent of a lock its ~$ marker took. v2 markers never lock;
    // Migrate completes it.
    public string? MarkerEntry { get => markerEntry; set => Set(ref markerEntry, value); }
    public int Attempt { get => attempt; set => Set(ref attempt, value); }
    public bool BreakNotice { get => breakNotice; set => Set(ref breakNotice, value); }
    public string? Refusal { get => refusal; set => Set(ref refusal, value); }
    public string? RefusalKind { get => refusalKind; set => Set(ref refusalKind, value); }
    public bool ReleaseNotChecked { get => releaseNotChecked; set => Set(ref releaseNotChecked, value); }
    public bool NewerWaiting { get => newerWaiting; set => Set(ref newerWaiting, value); }
    public string? NewerAuthor { get => newerAuthor; set => Set(ref newerAuthor, value); }
    // The team removed the file and it is open here: it goes aside once it is closed.
    public bool RemovedWaiting { get => removedWaiting; set => Set(ref removedWaiting, value); }
    // The ownership the read-only rule was last applied from: this computer's last knowledge
    // of who holds the file, used again while offline.
    public LockOwnership? AppliedOwnership { get => appliedOwnership; set => Set(ref appliedOwnership, value); }
    // The file's live check out as this computer last knew it (from the server, or from its own
    // check out and let go), so the window still says who has it while offline.
    public KnownLock? Holder { get => holder; set => Set(ref holder, value); }
    public ChangeList<SideRecord> Sides { get => sides; set => sides = Owned(value); }
    public Inflight? Inflight { get => inflight; set => Set(ref inflight, value); }
    // Consecutive scans that did not find a file this computer had: a deletion is planned
    // only after two, so one bad scan never deletes for the team.
    public int AbsentScans { get => absentScans; set => Set(ref absentScans, value); }
    // An Explorer rename or move of this file to another path, being sent as a server move.
    public string? LocalMoveTo { get => localMoveTo; set => Set(ref localMoveTo, value); }
    // Journaled saves the release gate refused: private drafts kept on this computer. They
    // never hold the lock and are offered again whenever the gate would allow them.
    public ChangeList<string> Drafts { get => drafts; set => drafts = Owned(value); }
    // v2 check out (docs/agent/ENGINE.md). Each request is durable here before any server call,
    // so a crash finishes it on the next pass.
    // CheckOut: the student asked to check this file out; the lock's operation id derives from it.
    public string? CheckOut { get => checkOut; set => Set(ref checkOut, value); }
    // Check in or Undo check out, asked for a file this computer has checked out.
    public CheckoutRequest Request { get => request; set => Set(ref request, value); }
    // A file added while open stays checked out to its creator and is checked in when it
    // closes (decision D2). A closed add is checked in by the same pass.
    public bool AutoCheckIn { get => autoCheckIn; set => Set(ref autoCheckIn, value); }
    // The lock was taken only for a move or a removal, and is let go once that is done.
    public bool TransientLock { get => transientLock; set => Set(ref transientLock, value); }
    // v0.3: the server deleted this file and its history forever (folder_purged, or its project
    // deleted forever). Never planned or sent again; its copy here goes to Armory's recovery
    // folder once closed, then the record is dropped (SyncEngine.Purge.cs).
    public bool Purged { get => purged; set => Set(ref purged, value); }

    [JsonIgnore] public Revision? Base => BaseId is null ? null : new(BaseId, BaseHash, "");
    public void SetBase(Revision? revision) { BaseId = revision?.Id; BaseHash = revision?.Hash; }
}

internal sealed record KnownLock(string Email, Guid Device, string? DeviceName, DateTimeOffset Since);

// The file records by vault path, compared as Windows compares paths. Every record added,
// replaced or removed is told to the state document (EngineState.FileAdded, FileRemoved).
internal sealed class FileTable : IDictionary<string, FileState>, IReadOnlyDictionary<string, FileState>
{
    private readonly Dictionary<string, FileState> items = new(StringComparer.OrdinalIgnoreCase);
    internal EngineState? Owner { get; set; }

    public FileState this[string key]
    {
        get => items[key];
        set
        {
            if (items.TryGetValue(key, out var old))
            {
                if (ReferenceEquals(old, value)) return;
                items.Remove(key);
                Owner?.FileRemoved(old);
            }
            items[key] = value;
            Owner?.FileAdded(key, value);
        }
    }
    public int Count => items.Count;
    public bool IsReadOnly => false;
    public Dictionary<string, FileState>.KeyCollection Keys => items.Keys;
    public Dictionary<string, FileState>.ValueCollection Values => items.Values;
    ICollection<string> IDictionary<string, FileState>.Keys => items.Keys;
    ICollection<FileState> IDictionary<string, FileState>.Values => items.Values;
    IEnumerable<string> IReadOnlyDictionary<string, FileState>.Keys => items.Keys;
    IEnumerable<FileState> IReadOnlyDictionary<string, FileState>.Values => items.Values;
    public void Add(string key, FileState value)
    {
        items.Add(key, value);
        Owner?.FileAdded(key, value);
    }
    public bool Remove(string key)
    {
        if (!items.Remove(key, out var old)) return false;
        Owner?.FileRemoved(old);
        return true;
    }
    public bool ContainsKey(string key) => items.ContainsKey(key);
    public bool TryGetValue(string key, [System.Diagnostics.CodeAnalysis.MaybeNullWhen(false)] out FileState value) => items.TryGetValue(key, out value);
    public void Clear()
    {
        var old = items.Values.ToArray();
        items.Clear();
        foreach (var st in old) Owner?.FileRemoved(st);
    }
    public Dictionary<string, FileState>.Enumerator GetEnumerator() => items.GetEnumerator();
    IEnumerator<KeyValuePair<string, FileState>> IEnumerable<KeyValuePair<string, FileState>>.GetEnumerator() => items.GetEnumerator();
    System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => items.GetEnumerator();
    void ICollection<KeyValuePair<string, FileState>>.Add(KeyValuePair<string, FileState> item) => Add(item.Key, item.Value);
    bool ICollection<KeyValuePair<string, FileState>>.Contains(KeyValuePair<string, FileState> item) => ((ICollection<KeyValuePair<string, FileState>>)items).Contains(item);
    void ICollection<KeyValuePair<string, FileState>>.CopyTo(KeyValuePair<string, FileState>[] array, int arrayIndex) => ((ICollection<KeyValuePair<string, FileState>>)items).CopyTo(array, arrayIndex);
    bool ICollection<KeyValuePair<string, FileState>>.Remove(KeyValuePair<string, FileState> item) => TryGetValue(item.Key, out var value) && ReferenceEquals(value, item.Value) && Remove(item.Key);
}

// A FileState's list: a change to its contents is a change of the record (FileState.Version).
internal sealed class ChangeList<T> : System.Collections.ObjectModel.Collection<T>
{
    internal FileState? Owner { get; set; }
    protected override void InsertItem(int index, T item) { base.InsertItem(index, item); Owner?.Changed(); }
    protected override void RemoveItem(int index) { base.RemoveItem(index); Owner?.Changed(); }
    protected override void SetItem(int index, T item) { base.SetItem(index, item); Owner?.Changed(); }
    protected override void ClearItems() { base.ClearItems(); Owner?.Changed(); }
    internal void RemoveRange(int index, int count)
    {
        for (var i = 0; i < count; i++) base.RemoveItem(index);
        if (count > 0) Owner?.Changed();
    }
}

// The ids of completed journal entries: only ever added to, and kept in the order they were
// added, so the state document writes again only the ids added since its last save.
internal sealed class IdSet : ICollection<string>, IReadOnlyCollection<string>
{
    private readonly HashSet<string> set = new(StringComparer.Ordinal);
    private readonly List<string> order = [];
    public int Count => order.Count;
    public bool IsReadOnly => false;
    // Moves on any removal, so a cached copy of the list knows to start again.
    internal int Removals { get; private set; }
    internal IReadOnlyList<string> InOrder => order;
    public void Add(string item)
    {
        if (set.Add(item)) order.Add(item);
    }
    public bool Contains(string item) => set.Contains(item);
    public bool Remove(string item)
    {
        if (!set.Remove(item)) return false;
        order.Remove(item);
        Removals++;
        return true;
    }
    public void Clear()
    {
        set.Clear();
        order.Clear();
        Removals++;
    }
    public void CopyTo(string[] array, int arrayIndex) => order.CopyTo(array, arrayIndex);
    public IEnumerator<string> GetEnumerator() => order.GetEnumerator();
    System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => GetEnumerator();
}
// ItemDetail is the item's own sentence in a card of several (who has its files checked out,
// why it went back); ReasonKind and Who let such a card name everyone in its title.
internal sealed record RememberedNotice(string Kind, Guid? FileId, string Path, string Title, string Detail, DateTimeOffset At,
    string? ItemDetail = null, string? ReasonKind = null, string? Who = null);
internal sealed record SideRecord(Guid VersionId, string Hash, string Reason, DateTimeOffset At);

// One server write that was about to be sent. Re-sent with the same operation id on the
// next pass if the engine stopped before recording its answer.
internal sealed record Inflight(string Kind, Guid Operation, string? EntryId = null, Guid? ProjectId = null, Guid? FileId = null,
    string? Folder = null, string? Name = null, string? ParentId = null, string? Hash = null, long Bytes = 0, string? SnapshotId = null,
    int? SavedRelease = null, string? Reason = null, bool ReleaseNotChecked = false, Guid? Device = null);

// A rename sent through armory_move_file: requested with MoveAsync, or an Explorer rename the
// engine detected (Local), whose bytes already sit at To.
internal sealed record PendingMove(Guid Operation, Guid FileId, string From, string To, bool Local = false);

// One armory_break_locks call of a Force check in (EngineState.ForceCheckIns): its operation id,
// the device it was sent as, and each file with the check out it was asked to end (the holder's
// device and when they checked it out), in id order.
internal sealed record PendingForceCheckIn(Guid Operation, Guid Device, ForcedCheckOut[] Files);
internal sealed record ForcedCheckOut(Guid FileId, Guid HolderDevice, DateTimeOffset AcquiredAt);

// A folder change (vault-relative folders), durable before its server call. Rename and delete
// operations keep the paths they had when they happened and are sent in order, the server read
// again between them, so a chain or a swap goes through its temporary name as it did on disk.
// "rename": the student moved LocalFrom to LocalTo; the file states already follow the disk,
// and armory_rename_folder is sent once (contract C5). "delete": the known folder LocalFrom is
// gone; armory_delete_folder is sent once (C6). "appRename" and "appDelete": the window's Rename
// folder and Delete folder, sent with this id (a lost answer is replayed from the server's
// receipt), then finished here (the folder moved in one step, or its files moved aside).
// "putBack": a folder waiting to move from LocalTo back to LocalFrom (a refused rename, a folder
// moved out of its project); with AtHome its records stayed at LocalFrom while it sits at
// LocalTo, otherwise they follow the disk. Reason is why ("Maria Lopez has 2 of its files
// checked out"), ReasonKind its kind and Who the people it names.
internal sealed record PendingFolderOp(string Kind, Guid Operation, Guid ProjectId, string LocalFrom, string LocalTo, string? Reason = null,
    string? ReasonKind = null, string? Who = null, bool AtHome = false);

// A local folder move in progress (EngineState.MovingFolders). Kind says what follows it: "team",
// "app" and "project" move the records with the folder; "putBack" and "projectPutBack" put a
// folder back where it was (with RecordsStay, its records never left there).
internal sealed record MovingFolder(string Kind, string From, string To, Guid? ProjectId = null, bool RecordsStay = false);

// folder_renamed from the change feed, in the server's spelling (project-relative).
internal sealed record RemoteFolderRename(Guid ProjectId, string From, string To);

// One bulk add: the folder it landed in (vault-relative) and every file it brought. The
// summary counts what is in Armory now, what shares a name, and the rest.
internal sealed record ImportRecord(Guid Id, Guid ProjectId, string Folder, IReadOnlyList<string> Paths, DateTimeOffset At);
