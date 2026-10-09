using Armory.Agent.Engine;
using Armory.Agent.Engine.View;
using Armory.SolidWorks;

namespace Armory.Agent;

// The SolidWorks link (docs/agent/SOLIDWORKS.md) and what the window and the notifications ask
// of it. Each vault runtime has its own link: it starts right after its engine, and it lets go of
// SolidWorks (every reference released, the student's own Save to Version setting put back) when
// the runtime stops, so a vault root change or a quit never leaves SolidWorks changed.
internal sealed partial class AgentHost
{
    // The questions about files SolidWorks opened that this computer hasn't checked out (C5),
    // newest group last; raised on any thread whenever they change. The notifications show them
    // (docs/agent/BRIDGE.md, "SolidWorks").
    internal event Action<IReadOnlyList<OpenPrompt>>? OpenPromptsChanged;

    internal IReadOnlyList<OpenPrompt> OpenPrompts
    {
        get
        {
            var engine = Volatile.Read(ref runtime)?.Engine;
            try { return engine?.OpenPrompts ?? []; }
            catch (Exception error) when (error is not OutOfMemoryException) { LogEngineFailure("open prompts", error); return []; }
        }
    }

    // The save down questions to ask before a save (the window's notice cards show them too).
    internal IReadOnlyList<SaveDownPrompt> SaveDownPrompts
    {
        get
        {
            var engine = Volatile.Read(ref runtime)?.Engine;
            try { return engine?.SaveDownPrompts ?? []; }
            catch (Exception error) when (error is not OutOfMemoryException) { LogEngineFailure("save down prompts", error); return []; }
        }
    }

    // A notification's "Check out and reopen" (C5): checked out, then writable in SolidWorks
    // without closing anything.
    internal Task<ActionResult> CheckOutAndReopenAsync(IReadOnlyList<string> paths)
        => OnEngineAsync("check out and reopen " + paths.Count + " files", e => e.CheckOutAndReopenAsync(paths));

    // "Keep this file on this computer only": its saves stay in the running SolidWorks year and
    // stay private drafts until it is saved in its project's year.
    internal Task<ActionResult> KeepLocalAsync(IReadOnlyList<string> paths)
        => OnEngineAsync("keep " + paths.Count + " files on this computer", e => e.KeepLocalAsync(paths));

    // "Save it in 2025 now": SolidWorks saves the open file in its project's year.
    internal Task<ActionResult> SaveDownNowAsync(IReadOnlyList<string> paths)
        => OnEngineAsync("save " + paths.Count + " files in their project's year", e => e.SaveDownNowAsync(paths));

    // The answer to the question asked before a save ("Save in 2025" or "Keep this file on this
    // computer only").
    internal Task<ActionResult> AnswerSaveDownAsync(string path, bool keepLocal)
        => OnEngineAsync(keepLocal ? "keep a file on this computer" : "save a file in its project's year", e => e.AnswerSaveDownAsync(path, keepLocal));

    private void OnOpenPrompts(IReadOnlyList<OpenPrompt> prompts) => OpenPromptsChanged?.Invoke(prompts);

    // The link for one vault runtime: its own settings file beside settings.json keeps the
    // student's Save to Version setting while Armory changed it, so a crash puts it back on the
    // next start.
    internal static SolidWorksLink CreateSolidWorksLink(string vaultRoot, string settingsFile, AgentLog? log)
        => new(new SolidWorksLinkOptions { VaultRoot = vaultRoot, SettingsFile = settingsFile, Log = log is null ? null : log.Info });
}
