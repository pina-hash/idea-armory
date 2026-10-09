using System.Threading.Channels;
using Armory.Core;

namespace Armory.Agent.Engine;

// The SolidWorks link as the engine sees it (docs/agent/SOLIDWORKS.md). Windows:
// Armory.SolidWorks.SolidWorksLink, attached out of process to every SolidWorks of this Windows
// user. Tests: an in-memory fake. What SolidWorks does arrives as records on one channel, in the
// order it happened; the engine answers with the commands below. Paths are full Windows paths,
// as SolidWorks names them; the link reports only documents under the vault root.
public interface ISolidWorksLink
{
    ChannelReader<LinkRecord> Records { get; }
    // After a check out: makes the document SolidWorks has open read-only editable where it is
    // (Armory.Core.WritablePlan). It never closes a document and never discards a change.
    Task<WritableOutcome> MakeWritableAsync(string fullPath, CancellationToken cancellationToken);
    // Saves an open document again, in its project's pinned release (Save to Version, then Save).
    Task<SaveDownOutcome> SaveInPinnedReleaseAsync(string fullPath, CancellationToken cancellationToken);
    // Which project folders save down, and to what year. The engine sends them after each read
    // of the server that changed them.
    void SetPins(IReadOnlyList<ProjectPin> pins);
    // The student chose "Keep this file on this computer only" for an open document (or no
    // longer): its saves write the running release and stay a private draft.
    void KeepLocal(string fullPath, bool keep);
    // One line on SolidWorks' status bar.
    void StatusText(string text);
    // Sends again what is true now (each attached SolidWorks and the vault documents it has
    // open), for an engine that just started: the engine before it may have read the earlier
    // records.
    void Replay();
}

public enum WritableOutcome { NotOpen, AlreadyWritable, MadeWritableInPlace, Reopened, HasUnsavedChanges, Refused, LinkGone }

public enum SaveDownOutcome { Saved, NotOpen, ReadOnly, NotNeeded, CantSaveDown, Failed, LinkGone }

// FullFolder: the project's folder, a full path. Enforce: the project's release gate.
public sealed record ProjectPin(string FullFolder, int PinnedRelease, bool Enforce);

// What the link's save of a document set Save to Version to: down to the pinned release, the
// running release (no save down was needed), or the running release kept as a private draft
// (blocked, kept on this computer, or this SolidWorks can't save down).
public enum LinkSaveMode { SaveDown, Current, PrivateDraft }

// swDocumentTypes_e.
public static class SolidWorksDocTypes
{
    public const int Part = 1, Assembly = 2, Drawing = 3;
}

// Records, link to engine (docs/agent/SOLIDWORKS.md, "Messages").
public abstract record LinkRecord(int ProcessId);
// Attached to a SolidWorks that finished starting: its RevisionNumber ("34.4.1") and whether it
// can save down in place. Sent again when that changes (a save down that didn't take, B4).
public sealed record LinkAttached(int ProcessId, string Revision, int RunningYear, SaveToVersionSupport SaveDown) : LinkRecord(ProcessId);
// A SolidWorks of this user the link can't attach to: started as administrator (another
// integrity level, which COM does not cross). Sent once per process.
public sealed record LinkRefused(int ProcessId, string Reason) : LinkRecord(ProcessId);
// A vault document opened (FileOpenPostNotify, FileNewNotify2, or found open at attach).
// TopLevel: it was SolidWorks' active document then (the student opened it in its own window),
// never a reference an assembly or a drawing loaded. FutureVersion: saved in a newer SolidWorks
// than this one (IModelDocExtension.IsFutureVersion).
public sealed record LinkOpened(int ProcessId, string Path, int DocType, bool ReadOnly, bool FutureVersion, bool TopLevel, DateTimeOffset At) : LinkRecord(ProcessId);
// A document became SolidWorks' active one (ActiveModelDocChangeNotify): the student is working
// in it, so it counts as opened by the student from now on.
public sealed record LinkActivated(int ProcessId, string Path, DateTimeOffset At) : LinkRecord(ProcessId);
// The student changed it (ModifyNotify: the first change since it was opened or saved).
public sealed record LinkModified(int ProcessId, string Path) : LinkRecord(ProcessId);
// SolidWorks is about to save it (FileSaveNotify, FileSaveAsNotify2). The engine holds that
// path, deciding no upload, until LinkSaved, LinkSaveCanceled or two minutes.
public sealed record LinkSaving(int ProcessId, string Path, Guid SaveId, LinkSaveMode Mode) : LinkRecord(ProcessId);
// Saved (FileSavePostNotify, swFileSaveTypes_e SaveType). Stamp: what SolidWorks read in the
// bytes on disk right after (null when the file changed while it was read).
public sealed record LinkSaved(int ProcessId, string Path, Guid SaveId, int SaveType, ReleaseStamp? Stamp) : LinkRecord(ProcessId);
// The save did not happen (FileSavePostCancelNotify), for example SolidWorks' own Previous
// Release Check stopped a save down. The work is still in SolidWorks.
public sealed record LinkSaveCanceled(int ProcessId, string Path, Guid SaveId, LinkSaveMode Mode) : LinkRecord(ProcessId);
// What a save down of this document would do (IModelDocExtension.CheckVersionCompatibility and
// the link's inventory), checked after it opened, after changes settle and after each save.
// Checked false: SolidWorks couldn't check it (before 2026 SP3, or it failed).
public sealed record LinkCompatibility(int ProcessId, string Path, int TargetYear, bool Checked, IReadOnlyList<CompatibilityItem> Blocked,
    IReadOnlyList<DropItem> Drops) : LinkRecord(ProcessId);
// The link read the bytes of a document it saw open (B5): SolidWorks' year for them.
public sealed record LinkStamped(int ProcessId, string Path, ReleaseStamp Stamp) : LinkRecord(ProcessId);
public sealed record LinkClosed(int ProcessId, string Path) : LinkRecord(ProcessId);
// SolidWorks closed, Armory quit, or the connection broke. The student's own Save to Version
// setting was put back first.
public sealed record LinkDetached(int ProcessId, string Reason) : LinkRecord(ProcessId);

// IVersionCompatibilityItem: SolidWorks' own words for something the pinned release doesn't
// have, and what it suggests.
public sealed record CompatibilityItem(string Message, string? Action, string? ObjectName);

// Something a save down drops (research section 3.2): a count of a kind ("appearances",
// "decals", "lights", "customProperties", "explodeSteps", "simulationStudies"), or SolidWorks'
// own warning ("warning", Count 1, Text its message). Count 0 with kind simulationStudies:
// unknown (the Simulation add-in isn't loaded), said as "simulation studies, if any".
public sealed record DropItem(string Kind, int Count, string? Text = null)
{
    public const string Appearances = "appearances", Decals = "decals", Lights = "lights", CustomProperties = "customProperties",
        ExplodeSteps = "explodeSteps", SimulationStudies = "simulationStudies", Warning = "warning";
}

// A question the engine asks because SolidWorks opened a file this computer has not checked out
// (C5), for Windows notifications (docs/agent/BRIDGE.md, "Host-facing"). One per document the
// student opened; prompts with the same Group are one notification. ViaSolidWorks: the link saw
// the open (the button can make it editable in place); otherwise SolidWorks' "~$" marker did.
// CheckedOutBy: who has it ("Maria Lopez on LAB-PC-07"), when Kind is HeldByOther or
// HeldOnMyOtherComputer.
public sealed record OpenPrompt(string Path, string Name, Guid FileId, string? CheckedOutBy, bool ViaSolidWorks, long Group, OpenAskKind Kind);

// The question before a save down that drops something (research section 3.4 step 4), for a
// Windows notification with two buttons: "Save in {Pinned}" (AnswerSaveDownAsync with keepLocal
// false, or nothing: the team's rule) and "Keep this file on this computer only" (keepLocal true).
public sealed record SaveDownPrompt(string Key, string Path, string Name, string Title, string Text, int PinnedRelease);
