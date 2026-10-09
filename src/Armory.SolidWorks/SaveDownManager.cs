using Armory.Agent.Engine;
using Armory.Core;

namespace Armory.SolidWorks;

// What the save-down manager asks of one SolidWorks session (LinkSession carries these out on
// the link's thread with late-bound calls; tests give it a fake). Paths are full paths of
// documents open in that session.
internal interface ISaveDownCalls
{
    bool? GetToggle(int preference);
    bool SetToggle(int preference, bool value);
    int? GetInteger(int preference);
    bool SetInteger(int preference, int value);
    // IModelDocExtension.CheckVersionCompatibility(version, NeverShow): null when it couldn't run.
    CompatibilityResult? CheckCompatibility(string path, int saveToVersion);
    // What a save down drops that the check may not list (research section 3.3).
    IReadOnlyList<DropItem> Inventory(string path, int docType);
    // ISldWorks.VersionHistory(path): read from the file on disk.
    IReadOnlyList<string>? VersionHistory(string path);
    // IModelDoc2.IsOpenedReadOnly; null when it isn't open.
    bool? IsReadOnly(string path);
    // IModelDoc2.SetSaveFlag, then Save3(Silent): true when SolidWorks saved it.
    bool Save(string path);
}

internal sealed record CompatibilityResult(int Result, IReadOnlyList<CompatibilityItem> Incompatible, IReadOnlyList<string> Warnings);

// B2, saving down to the project's pinned year (research section 3.4), for one SolidWorks
// session. At attach it keeps the student's own Save to Version setting (in LinkSettings, so a
// crash or a SolidWorks that closed first puts it back at the next attach) and checks that the
// option can be set and read back. While a vault document of a project pinned below the running
// year is the active one and SolidWorks says it can go back, the option is set to that year
// (SaveDown.Choose); for a document that can't (blocked), or that the student keeps on this
// computer, it is off, so the save writes this release and stays a private draft; everywhere
// else the student's own setting is back. After each save the bytes are stamped with the year
// SolidWorks itself reads in them. Nothing here blocks a save or discards anything.
internal sealed class SaveDownManager
{
    private readonly int pid;
    private readonly SolidWorksRevision running;
    private readonly ISaveDownCalls calls;
    private readonly SaveToVersionIds? ids;
    private readonly LinkSettings settings;
    private readonly Action<LinkRecord> emit;
    private readonly Action<SaveToVersionSupport> supportChanged;
    private readonly Func<string, string?> hash;
    private readonly TimeProvider clock;
    private readonly Action<string>? log;
    private IReadOnlyList<ProjectPin> pins = [];
    // The student's own setting (null until read) and what Armory set now (null: the student's).
    private (bool Enable, int Value)? student;
    private (bool Enable, int Value)? applied;
    private bool readBack = true, saveDownFailed;
    private string? active;
    private readonly HashSet<string> blocked = [];
    private readonly HashSet<string> keepLocal = [];
    // What the option was for each save in progress: on, and the year it saves in.
    private readonly Dictionary<string, (bool On, int Target)> saving = [];

    internal SaveDownManager(int pid, SolidWorksRevision running, ISaveDownCalls calls, SaveToVersionIds? ids, LinkSettings settings, Action<LinkRecord> emit,
        Action<SaveToVersionSupport> supportChanged, Func<string, string?> hash, TimeProvider? clock = null, Action<string>? log = null)
    {
        this.pid = pid;
        this.running = running;
        this.calls = calls;
        this.ids = ids;
        this.settings = settings;
        this.emit = emit;
        this.supportChanged = supportChanged;
        this.hash = hash;
        this.clock = clock ?? TimeProvider.System;
        this.log = log;
    }

    internal SaveToVersionSupport Support => SaveDown.Support(running, ids is not null, readBack && student is not null, saveDownFailed);
    // What Save to Version is set to now (the student's own when Armory changed nothing).
    internal (bool Enable, int Value)? Option => applied ?? student;
    internal bool Changed => applied is not null;

    // At attach. restoreLeftover: no other session of this release is linked in this Armory, so
    // a value an earlier run left set is this release's to put back.
    internal void Attach(bool restoreLeftover = true)
    {
        if (ids is null || !SaveDown.HasSaveToVersion(running)) return; // nothing Armory may read or touch
        try
        {
            if (restoreLeftover && settings.Student(running.Major) is { Changed: true } left)
            {
                log?.Invoke($"solidworks: putting back the student's Save to Version setting an earlier run left changed ({left.Enable}, {left.Value})");
                calls.SetToggle(ids.EnableToggle, left.Enable);
                calls.SetInteger(ids.VersionValue, left.Value);
                settings.SetStudent(running.Major, left with { Changed = false });
            }
            var enable = calls.GetToggle(ids.EnableToggle);
            var value = calls.GetInteger(ids.VersionValue);
            if (enable is not { } e || value is not { } v)
            {
                readBack = false;
                return;
            }
            student = (e, v);
            settings.SetStudent(running.Major, new LinkSettings.StudentSetting(e, v, false));
            // Each set to its own value must read back unchanged: proof the option takes here.
            readBack = calls.SetToggle(ids.EnableToggle, e) && calls.SetInteger(ids.VersionValue, v) && calls.GetToggle(ids.EnableToggle) == e && calls.GetInteger(ids.VersionValue) == v;
            if (!readBack) log?.Invoke("solidworks: Save to Version didn't read back; no save down on this computer");
        }
        catch (Exception error) when (Com.IsComFailure(error))
        {
            readBack = false;
            log?.Invoke("solidworks: couldn't read Save to Version: " + error.Message);
        }
    }

    internal void SetPins(IReadOnlyList<ProjectPin> newPins)
    {
        pins = newPins;
        Apply();
    }

    internal void ActiveChanged(string? path)
    {
        active = path;
        Apply();
    }

    internal void KeepLocal(string path, bool keep)
    {
        if (keep) keepLocal.Add(LinkPolicy.Key(path)); else keepLocal.Remove(LinkPolicy.Key(path));
        Apply();
    }

    internal bool IsBlocked(string path) => blocked.Contains(LinkPolicy.Key(path));

    // The project a document is in (the longest folder that holds it), or null.
    internal ProjectPin? PinFor(string? path)
    {
        if (path is null) return null;
        var full = LinkPolicy.Normalize(path);
        return pins.Where(p => full.StartsWith(LinkPolicy.Normalize(p.FullFolder).TrimEnd('\\') + "\\", StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(p => p.FullFolder.Length).FirstOrDefault();
    }

    internal SaveDownPlan PlanFor(string? path) => PinFor(path) is { } pin ? SaveDown.Plan(running, pin.PinnedRelease) : SaveDownPlan.NotNeeded;

    internal SaveToVersionChoice ChoiceFor(string? path)
        => SaveDown.Choose(PlanFor(path), Support == SaveToVersionSupport.Available, path is not null && PinFor(path) is not null,
            path is not null && blocked.Contains(LinkPolicy.Key(path)), path is not null && keepLocal.Contains(LinkPolicy.Key(path)));

    // Sets Save to Version for the active document (research 3.4 step 1).
    internal void Apply()
    {
        if (ids is null || student is not { } own) return;
        switch (ChoiceFor(active))
        {
            case SaveToVersionChoice.SaveDown:
                Want(true, PlanFor(active).SaveToVersionValue()!.Value);
                break;
            case SaveToVersionChoice.Off:
                Want(false, own.Value);
                break;
            default:
                Restore();
                break;
        }
    }

    // Armory's value, the intent kept first (a crash after this still puts the student's back).
    private void Want(bool enable, int value)
    {
        // Already so: Armory's own value, or the student's own that is the same.
        if (ids is null || student is not { } own || applied == (enable, value) || (applied is null && own == (enable, value))) return;
        settings.SetStudent(running.Major, new LinkSettings.StudentSetting(own.Enable, own.Value, true));
        bool set;
        try { set = calls.SetToggle(ids.EnableToggle, enable) && calls.SetInteger(ids.VersionValue, value) && calls.GetToggle(ids.EnableToggle) == enable && calls.GetInteger(ids.VersionValue) == value; }
        catch (Exception error) when (Com.IsComFailure(error)) { set = false; log?.Invoke("solidworks: couldn't set Save to Version: " + error.Message); }
        applied = (enable, value);
        if (set) return;
        // It didn't take: no save down here (B4), and the student's own setting back.
        readBack = false;
        Restore();
        supportChanged(Support);
    }

    // The student's own setting back (at detach, and wherever Armory shouldn't touch it).
    internal void Restore()
    {
        if (ids is null || student is not { } own || applied is null) return;
        try
        {
            calls.SetToggle(ids.EnableToggle, own.Enable);
            calls.SetInteger(ids.VersionValue, own.Value);
            applied = null;
            settings.SetStudent(running.Major, new LinkSettings.StudentSetting(own.Enable, own.Value, false));
        }
        catch (Exception error) when (Com.IsComFailure(error))
        {
            // Kept as changed: the next attach puts it back.
            log?.Invoke("solidworks: couldn't put back the student's Save to Version setting: " + error.Message);
        }
    }

    // FileSaveNotify, from memory only: what this save writes (research 3.4 step 5).
    internal LinkSaveMode Saving(string path)
    {
        var (enable, value) = Option ?? (false, 0);
        var on = enable && value > 0;
        var target = on ? running.Year - value : running.Year;
        saving[LinkPolicy.Key(path)] = (on, target);
        return LinkPolicy.SaveMode(on, target, running.Year, PinFor(path)?.PinnedRelease);
    }

    // After FileSavePostNotify: the stamp of the bytes on disk (research section 2): their
    // SHA-256, read twice around SolidWorks' own reading of their history, and the year that
    // history names when it is the year this save meant. A save down that still wrote this
    // release means no save down here (B4).
    internal ReleaseStamp? Saved(string path, LinkSaveMode mode)
    {
        if (!saving.Remove(LinkPolicy.Key(path), out var option)) option = Option is { } o && o.Enable && o.Value > 0 ? (true, running.Year - o.Value) : (false, running.Year);
        var first = hash(path);
        if (first is null) return null;
        IReadOnlyList<string>? history;
        try { history = calls.VersionHistory(path); }
        catch (Exception error) when (Com.IsComFailure(error)) { history = null; }
        if (hash(path) != first) return null; // written again meanwhile: no stamp for bytes that may not be these
        var written = VersionHistory.LastYear(history);
        var intended = option.On ? option.Target : running.Year;
        if (mode == LinkSaveMode.SaveDown && written is { } year && year > option.Target && !saveDownFailed)
        {
            log?.Invoke($"solidworks: a save down to {option.Target} wrote {year}: no save down on this computer");
            saveDownFailed = true;
            Restore();
            supportChanged(Support);
        }
        return new ReleaseStamp(first, SavedReleaseRule.StampYear(written, intended), Revision, running.Year, option.On ? option.Target : null, clock.GetUtcNow());
    }

    // FileSavePostCancelNotify: a save down SolidWorks stopped (its own Previous Release Check)
    // is treated as blocked until a check says otherwise, so the next Save keeps it here.
    internal void SaveCanceled(string path, LinkSaveMode mode)
    {
        saving.Remove(LinkPolicy.Key(path));
        if (mode != LinkSaveMode.SaveDown) return;
        blocked.Add(LinkPolicy.Key(path));
        Apply();
    }

    // The bytes on disk as SolidWorks sees them, when it opens a vault document (B5).
    internal ReleaseStamp? StampOnOpen(string path)
    {
        var first = hash(path);
        if (first is null) return null;
        IReadOnlyList<string>? history;
        try { history = calls.VersionHistory(path); }
        catch (Exception error) when (Com.IsComFailure(error)) { return null; }
        if (hash(path) != first || VersionHistory.LastYear(history) is not { } year) return null;
        return new ReleaseStamp(first, SavedReleaseRule.StampYear(year, null), Revision, running.Year, null, clock.GetUtcNow());
    }

    // After an open, after changes settle and after each save (research 3.4 step 2): what a save
    // down of this document would block on and drop, for the engine's words, kept here so the
    // option is right before the next save.
    internal LinkCompatibility? CheckCompatibility(string path, int docType)
    {
        var plan = PlanFor(path);
        if (!plan.CanSave() || Support != SaveToVersionSupport.Available) return null;
        var value = plan.SaveToVersionValue()!.Value;
        CompatibilityResult? result;
        try { result = calls.CheckCompatibility(path, value); }
        catch (Exception error) when (Com.IsComFailure(error)) { result = null; log?.Invoke($"solidworks: couldn't check {path}: {error.Message}"); }
        IReadOnlyList<DropItem> inventory;
        try { inventory = calls.Inventory(path, docType); }
        catch (Exception error) when (Com.IsComFailure(error)) { inventory = []; }
        var items = result?.Incompatible ?? [];
        var key = LinkPolicy.Key(path);
        if (items.Count > 0) blocked.Add(key);
        else if (result is { Result: SwConstants.CompatibilityCompleted }) blocked.Remove(key);
        var drops = inventory.Concat((result?.Warnings ?? []).Where(w => !string.IsNullOrWhiteSpace(w)).Select(w => new DropItem(DropItem.Warning, 1, w))).ToList();
        var record = new LinkCompatibility(pid, path, running.Year - value, result is { Result: SwConstants.CompatibilityCompleted } || items.Count > 0, items, drops);
        if (LinkPolicy.SamePath(path, active)) Apply();
        return record;
    }

    // "Save it in 2025 now" (research 3.4 step 8, B5): the document's own pinned year, then a
    // plain Save of it, then the active document's setting again.
    internal SaveDownOutcome SaveInPinnedRelease(string path)
    {
        bool? readOnly;
        try { readOnly = calls.IsReadOnly(path); }
        catch (Exception error) when (Com.IsComFailure(error)) { return Com.IsGone(error) ? SaveDownOutcome.LinkGone : SaveDownOutcome.Failed; }
        if (readOnly is null) return SaveDownOutcome.NotOpen;
        if (readOnly == true) return SaveDownOutcome.ReadOnly;
        var plan = PlanFor(path);
        if (plan == SaveDownPlan.NotNeeded) return SaveDownOutcome.NotNeeded;
        if (!plan.CanSave() || Support != SaveToVersionSupport.Available || blocked.Contains(LinkPolicy.Key(path))) return SaveDownOutcome.CantSaveDown;
        keepLocal.Remove(LinkPolicy.Key(path));
        Want(true, plan.SaveToVersionValue()!.Value);
        if (Support != SaveToVersionSupport.Available) return SaveDownOutcome.CantSaveDown;
        bool saved;
        try { saved = calls.Save(path); }
        catch (Exception error) when (Com.IsComFailure(error)) { saved = false; log?.Invoke($"solidworks: couldn't save {path}: {error.Message}"); }
        Apply();
        return saved ? SaveDownOutcome.Saved : SaveDownOutcome.Failed;
    }

    private string Revision => $"{running.Major}.{running.Minor}.{running.Hotfix}";
}
