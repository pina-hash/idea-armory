namespace Armory.Core;

// "Check out and reopen" with the SolidWorks link (docs/agent/SOLIDWORKS.md): after the
// check out cleared the read-only bit, the document SolidWorks has open read-only is made
// editable where it is. The link carries out one step at a time and asks again. No step
// closes a document or discards a change: IModelDoc2.CloseDoc (which closes a changed
// document without saving) and every "discard changes" flag are never used.
public enum WritableStep
{
    // SolidWorks does not have it open: nothing to make writable (Armory opens it as usual).
    NotOpen,
    // It is writable in SolidWorks now.
    Done,
    // IModelDoc2.SetReadOnlyState(false): in place, keeping unsaved changes.
    SetReadOnlyStateFalse,
    // In place did not work and the student changed it: stop, and say how to save.
    StopUnsaved,
    // A part or assembly with no change: IModelDoc2.ReloadOrReplace(false, null, false).
    Reload,
    // A drawing with no change: ISldWorks.CloseAndReopen(doc, MatchSheet), never DiscardChanges.
    CloseAndReopenDrawing,
    // Nothing worked: it stays read-only in SolidWorks (Armory reopens it once it is closed).
    Refused,
}

// What the link knows of one open document. ChangedByStudent: a ModifyNotify arrived since it
// was opened or last saved; null when that is unknown, which counts as changed
// (IModelDoc2.GetSaveFlag can't tell: it is true for every file from an older release).
public sealed record WritableState(bool Open, bool ReadOnly, bool IsDrawing, bool? ChangedByStudent, bool TriedInPlace, bool TriedReopen);

public static class WritablePlan
{
    // The arguments of the reload: ReloadOrReplace(ReadOnly: false, ReplaceFileName: null,
    // DiscardChanges: false). With DiscardChanges false SolidWorks refuses a changed model.
    public const bool ReloadReadOnly = false;
    public const bool ReloadDiscardChanges = false;
    // swCloseReopenOption_e: MatchSheet is 4, DiscardChanges is 2. The drawing's reopen keeps
    // the sheet and never discards; without DiscardChanges SolidWorks refuses a changed drawing.
    public const int CloseReopenMatchSheet = 4;
    public const int CloseReopenDiscardChanges = 2;
    public const int CloseAndReopenOptions = CloseReopenMatchSheet;

    public static WritableStep Next(WritableState state)
    {
        if (!state.Open) return WritableStep.NotOpen;
        if (!state.ReadOnly) return WritableStep.Done;
        if (!state.TriedInPlace) return WritableStep.SetReadOnlyStateFalse;
        // Never a reload over the student's changes, nor over changes nobody can rule out.
        if (state.ChangedByStudent != false) return WritableStep.StopUnsaved;
        if (!state.TriedReopen) return state.IsDrawing ? WritableStep.CloseAndReopenDrawing : WritableStep.Reload;
        return WritableStep.Refused;
    }
}
