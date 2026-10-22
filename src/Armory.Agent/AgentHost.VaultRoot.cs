using Armory.Agent.Engine.View;

namespace Armory.Agent;

// The Armory folder is never a project folder or inside one (VaultRoot.cs, 0.4.2). Settings
// corrects or refuses such a folder with one sentence; a computer already in that state runs no
// engine there (it would only download the projects again inside a project and leave the files
// already there unsent) and shows one card, "Armory's folder is inside a project folder", with
// Fix it (RepairVaultRootAsync).
internal sealed partial class AgentHost
{
    // The verdict on the folder Armory was asked to run in, while it is not a usable one.
    private VaultRootVerdict? vaultRepair;
    private string? vaultRepairFolder;
    // The projects as the last engine view had them (the names at the top of each), so a folder
    // is judged by the account's own projects even while no engine runs.
    private IReadOnlyList<ProjectShape> knownShapes = [];
    private int repairing;

    internal const string RepairWords = "Fix it";
    internal const string RepairNow = "Nothing moves until Armory's folder is fixed.";

    // A folder judged against the account's projects (the running engine's, and the folder's own
    // state document's) and the disk.
    private VaultRootVerdict JudgeFolder(string folder)
    {
        IReadOnlyList<ProjectShape> shapes;
        lock (gate) shapes = knownShapes;
        return VaultRootRule.Judge(folder, VaultRootRule.Merge(shapes, parts.StateFile(folder).Projects), parts.FolderFacts);
    }

    // Under the lifecycle gate, before a runtime starts in a folder: false (and the card) when the
    // folder can't be Armory's.
    private bool FolderUsable(string folder)
    {
        var verdict = JudgeFolder(folder);
        lock (gate)
        {
            vaultRepair = verdict.Ok ? null : verdict;
            vaultRepairFolder = verdict.Ok ? null : folder;
        }
        if (verdict.Ok) return true;
        log.Info("the Armory folder " + folder + " is not usable: " + verdict.Words);
        runtimeProblem = "Armory is waiting for you to fix its folder.";
        return false;
    }

    // The engine learned the account's projects: a folder that turns out to be a project folder
    // (its own projects were not known when it started) stops at once and offers Fix it.
    private void LearnShapes(AgentView view)
    {
        if (view.Projects.Count == 0) return;
        var shapes = VaultRootRule.Shapes(view.Projects);
        bool changed;
        lock (gate)
        {
            changed = !SameShapes(knownShapes, shapes);
            if (changed) knownShapes = shapes;
        }
        if (!changed || Volatile.Read(ref runtime) is not { } current) return;
        if (JudgeFolder(current.Root).Ok) return;
        _ = Task.Run(async () =>
        {
            try { await RestartRuntimeAsync(current.Root); }
            catch (Exception error) when (error is not OutOfMemoryException) { log.Error("stopping in a project folder failed", error); }
        });
    }

    private static bool SameShapes(IReadOnlyList<ProjectShape> a, IReadOnlyList<ProjectShape> b)
        => a.Count == b.Count && a.Zip(b).All(p => VaultRootRule.Same(p.First.Folder, p.Second.Folder) && p.First.TopNames.SetEquals(p.Second.TopNames));

    // Settings' folder, corrected or refused by the rule: (the folder to use, a sentence to show).
    private (string? Folder, string? Words) CorrectedFolder(string folder)
    {
        var verdict = JudgeFolder(folder);
        if (verdict.Ok) return (folder, null);
        if (verdict.Refused || !AgentSettings.TryNormalizeVaultRoot(verdict.UseInstead, out var corrected, out _)) return (null, verdict.Words);
        log.Info("settings: " + verdict.Words);
        return (corrected, verdict.Words);
    }

    // The card while the folder waits to be fixed: the first notice, and the only one about it.
    private AgentView WithVaultRepair(AgentView view)
    {
        VaultRootVerdict? verdict;
        string? folder;
        lock (gate) { verdict = vaultRepair; folder = vaultRepairFolder; }
        if (verdict is null || folder is null) return view;
        var title = verdict.Project is { } project ? $"Armory's folder is inside the {project} project folder" : "Armory's folder is inside another Armory folder";
        var detail = verdict.Refused
            ? verdict.Words + " Then Armory starts again by itself."
            : $"Fix it moves Armory back to {verdict.UseInstead} and puts every file where it belongs, without uploading anything twice or losing a change.";
        var card = new NoticeGroupView("vaultRoot", NoticeKinds.VaultRoot, NoticeTones.Bad, title, detail, 1,
            verdict.Refused ? new NoticeActionView("Choose a folder", BridgeMessages.ChooseVaultRoot, []) : new NoticeActionView(RepairWords, BridgeMessages.RepairVaultRoot, []),
            [new NoticeItemView(null, "", Path.GetFileName(folder.TrimEnd('\\', '/')), folder)]);
        return view with
        {
            Notices = [card, .. view.Notices.Where(n => n.Kind != NoticeKinds.OutsideProject)],
            Sync = view.Sync with { Now = RepairNow },
        };
    }

    // Fix it: the engine stays stopped while the wrong folder folds into the right one
    // (VaultRootRepair), then Armory runs in the right one. Stopped part way, nothing runs and the
    // card stays, with the sentence that says what to close; clicking it again goes on.
    internal async Task<ActionResult> RepairVaultRootAsync()
    {
        VaultRootVerdict? verdict;
        string? folder;
        lock (gate) { verdict = vaultRepair; folder = vaultRepairFolder; }
        if (verdict is null || folder is null) return new ActionResult(true, "Armory's folder is fine. Nothing to fix.");
        if (verdict.Refused || verdict.UseInstead is not { } target) return new ActionResult(false, verdict.Words);
        if (SharedComputer) return new ActionResult(false, "Turn off \"This computer is shared by several students\" in Settings, then click Fix it.");
        if (Interlocked.Exchange(ref repairing, 1) == 1) return new ActionResult(false, "Armory is already fixing its folder.");
        try
        {
            log.Info($"fix the Armory folder: {folder} to {target}");
            await lifecycle.WaitAsync();
            VaultRepairResult result;
            try
            {
                // Nothing runs in either folder while their files move.
                var old = Interlocked.Exchange(ref runtime, null);
                if (old is not null) await StopRuntimeAsync(old);
                runtimeProblem = "Armory is fixing its folder.";
                RaiseView();
                result = await Task.Run(() => parts.RepairFolder(folder, target, Now, log.Info));
                if (!result.Done)
                {
                    runtimeProblem = "Armory is waiting for you to fix its folder.";
                    return new ActionResult(false, result.Problem!);
                }
                AgentSettings next;
                lock (gate)
                {
                    next = settings with { VaultRoot = target };
                    settings = next;
                    vaultRepair = null;
                    vaultRepairFolder = null;
                }
                try { settingsStore.Save(next); }
                catch (Exception error) when (error is IOException or UnauthorizedAccessException) { log.Error("could not save settings", error); }
                log.Info($"settings saved: vault {next.VaultRoot}, start at sign-in {next.StartAtSignIn}, theme {next.Theme}");
                if (RuntimeFolder(next) is { } use) await StartRuntimeAsync(use);
            }
            finally { lifecycle.Release(); }
            RaiseView();
            var words = $"Fixed. Armory uses {target} again, and every file is where it belongs.";
            if (result.KeptCopies > 0)
                words += $" {(result.KeptCopies == 1 ? "1 file had" : $"{result.KeptCopies:N0} files had")} changes in both places: the older {(result.KeptCopies == 1 ? "copy is" : "copies are")} kept in {result.KeptFolder}.";
            return new ActionResult(true, words);
        }
        finally
        {
            Volatile.Write(ref repairing, 0);
            RaiseView();
        }
    }
}
