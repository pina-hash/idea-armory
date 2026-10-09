using System.Threading.Channels;
using Armory.Agent.Engine;
using Armory.Core;

namespace Armory.EndToEnd.Tests;

// An in-memory SolidWorks link (docs/agent/SOLIDWORKS.md) for engine tests: a test plays
// SolidWorks (Open, Activate, Modify, Saving, Saved, Close) and reads back every command the
// engine gave (Calls). MakeWritableAsync carries out Armory.Core.WritablePlan step by step on the
// fake document, as the real link does, recording each SolidWorks call it would make, so a test
// can prove no call ever closed a document or discarded a change.
internal sealed class FakeSolidWorksLink : ISolidWorksLink
{
    private readonly Channel<LinkRecord> records = Channel.CreateUnbounded<LinkRecord>(new UnboundedChannelOptions { SingleReader = true });
    private readonly object gate = new();
    private readonly Dictionary<string, Doc> docs = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<string> calls = [];

    // One document the fake SolidWorks has open. InPlaceWorks: SetReadOnlyState(false) makes it
    // writable; ReopenWorks: the reload (or a drawing's close and reopen) does.
    internal sealed class Doc
    {
        public required string FullPath { get; init; }
        public int DocType { get; init; } = SolidWorksDocTypes.Part;
        public bool ReadOnly { get; set; } = true;
        public bool Changed { get; set; }
        public bool InPlaceWorks { get; set; } = true;
        public bool ReopenWorks { get; set; } = true;
    }

    public FakeSolidWorksLink(string root) => Root = root;

    public string Root { get; }
    public int Pid { get; set; } = 4120;
    public DateTimeOffset Now { get; set; } = new(2026, 10, 9, 15, 0, 0, TimeSpan.Zero);
    public ChannelReader<LinkRecord> Records => records.Reader;
    public IReadOnlyList<ProjectPin> Pins { get; private set; } = [];
    // What SaveInPinnedReleaseAsync answers.
    public SaveDownOutcome SaveDownAnswer { get; set; } = SaveDownOutcome.Saved;
    public List<string> Calls { get { lock (gate) return [.. calls]; } }

    public string Full(string vaultPath) => Root + "\\" + vaultPath.Replace('/', '\\');
    public Doc this[string vaultPath] { get { lock (gate) return docs[Full(vaultPath)]; } }

    // ---- SolidWorks, as the test plays it -------------------------------------------------------

    public void Attach(string revision = "34.4.1", SaveToVersionSupport support = SaveToVersionSupport.Available)
        => Send(new LinkAttached(Pid, revision, SolidWorksRevision.Parse(revision)!.Value.Year, support));

    public void Refuse(int pid) => Send(new LinkRefused(pid, "elevated"));

    public void Detach(string reason = "SolidWorks closed")
    {
        lock (gate) docs.Clear();
        Send(new LinkDetached(Pid, reason));
    }

    // topLevel: the student opened it (SolidWorks' active document); false: a reference an
    // assembly loaded.
    public Doc Open(string vaultPath, bool topLevel = true, bool readOnly = true, int docType = SolidWorksDocTypes.Part, bool futureVersion = false, double seconds = 0)
    {
        var doc = new Doc { FullPath = Full(vaultPath), DocType = docType, ReadOnly = readOnly };
        lock (gate) docs[doc.FullPath] = doc;
        Send(new LinkOpened(Pid, doc.FullPath, docType, readOnly, futureVersion, topLevel, Now.AddSeconds(seconds)));
        return doc;
    }

    public void Activate(string vaultPath, double seconds = 0) => Send(new LinkActivated(Pid, Full(vaultPath), Now.AddSeconds(seconds)));

    public void Modify(string vaultPath)
    {
        lock (gate) docs[Full(vaultPath)].Changed = true;
        Send(new LinkModified(Pid, Full(vaultPath)));
    }

    public Guid Saving(string vaultPath, LinkSaveMode mode = LinkSaveMode.SaveDown)
    {
        var id = Guid.NewGuid();
        Send(new LinkSaving(Pid, Full(vaultPath), id, mode));
        return id;
    }

    public void Saved(string vaultPath, Guid saveId, ReleaseStamp? stamp)
    {
        lock (gate) if (docs.TryGetValue(Full(vaultPath), out var doc)) doc.Changed = false;
        Send(new LinkSaved(Pid, Full(vaultPath), saveId, 1, stamp));
    }

    public void SaveCanceled(string vaultPath, Guid saveId, LinkSaveMode mode = LinkSaveMode.SaveDown) => Send(new LinkSaveCanceled(Pid, Full(vaultPath), saveId, mode));

    public void Compatibility(string vaultPath, int target, IReadOnlyList<CompatibilityItem>? blocked = null, IReadOnlyList<DropItem>? drops = null)
        => Send(new LinkCompatibility(Pid, Full(vaultPath), target, true, blocked ?? [], drops ?? []));

    public void Stamped(string vaultPath, ReleaseStamp stamp) => Send(new LinkStamped(Pid, Full(vaultPath), stamp));

    public void Close(string vaultPath)
    {
        lock (gate) docs.Remove(Full(vaultPath));
        Send(new LinkClosed(Pid, Full(vaultPath)));
    }

    public void Send(LinkRecord record) => records.Writer.TryWrite(record);

    // ---- The engine's commands ------------------------------------------------------------------

    public Task<WritableOutcome> MakeWritableAsync(string fullPath, CancellationToken cancellationToken)
    {
        lock (gate)
        {
            calls.Add("MakeWritable " + fullPath);
            if (!docs.TryGetValue(fullPath, out var doc)) return Task.FromResult(WritableOutcome.NotOpen);
            var state = new WritableState(true, doc.ReadOnly, doc.DocType == SolidWorksDocTypes.Drawing, doc.Changed, false, false);
            var started = doc.ReadOnly;
            var reopened = false;
            while (true)
            {
                switch (WritablePlan.Next(state))
                {
                    case WritableStep.Done:
                        return Task.FromResult(!started ? WritableOutcome.AlreadyWritable : reopened ? WritableOutcome.Reopened : WritableOutcome.MadeWritableInPlace);
                    case WritableStep.SetReadOnlyStateFalse:
                        calls.Add("SetReadOnlyState(false) " + fullPath);
                        if (doc.InPlaceWorks) doc.ReadOnly = false;
                        state = state with { TriedInPlace = true, ReadOnly = doc.ReadOnly };
                        break;
                    case WritableStep.Reload:
                        calls.Add($"ReloadOrReplace({WritablePlan.ReloadReadOnly}, null, DiscardChanges: {WritablePlan.ReloadDiscardChanges}) " + fullPath);
                        if (doc.ReopenWorks && !doc.Changed) { doc.ReadOnly = false; reopened = true; }
                        state = state with { TriedReopen = true, ReadOnly = doc.ReadOnly };
                        break;
                    case WritableStep.CloseAndReopenDrawing:
                        calls.Add($"CloseAndReopen(options: {WritablePlan.CloseAndReopenOptions}) " + fullPath);
                        if (doc.ReopenWorks && !doc.Changed) { doc.ReadOnly = false; reopened = true; }
                        state = state with { TriedReopen = true, ReadOnly = doc.ReadOnly };
                        break;
                    case WritableStep.StopUnsaved:
                        return Task.FromResult(WritableOutcome.HasUnsavedChanges);
                    case WritableStep.NotOpen:
                        return Task.FromResult(WritableOutcome.NotOpen);
                    default:
                        return Task.FromResult(WritableOutcome.Refused);
                }
            }
        }
    }

    public Task<SaveDownOutcome> SaveInPinnedReleaseAsync(string fullPath, CancellationToken cancellationToken)
    {
        lock (gate) calls.Add("SaveInPinnedRelease " + fullPath);
        return Task.FromResult(SaveDownAnswer);
    }

    public void SetPins(IReadOnlyList<ProjectPin> pins)
    {
        lock (gate) Pins = pins;
    }

    public void KeepLocal(string fullPath, bool keep)
    {
        lock (gate) calls.Add($"KeepLocal({keep}) " + fullPath);
    }

    public void StatusText(string text)
    {
        lock (gate) calls.Add("StatusText " + text);
    }

    // The engine made with this link reads every record from the start: nothing to send again.
    public void Replay() { }
}
