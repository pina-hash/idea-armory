using System.Globalization;
using Armory.Agent.Engine.View;
using Armory.Core;
using Armory.Platform.Windows;

namespace Armory.Agent;

// What the shell desk asks of the host (AgentHost.Shell.cs): the engine's actions, the view, and
// when the host has started. Every call is safe from any thread.
internal interface IShellHost
{
    // Done when the host has started (a forwarder may have just started Armory).
    Task Started { get; }
    string VaultRoot { get; }
    AgentView View { get; }
    Task<ActionResult> CheckOutAsync(IReadOnlyList<string> paths, bool open);
    Task<ActionResult> CheckInAsync(IReadOnlyList<string> paths);
    Task<ActionResult> UndoCheckOutAsync(IReadOnlyList<string> paths);
    Task<ActionResult> TakeBackAsync(IReadOnlyList<Guid> fileIds);
    Task<ActionResult> CheckOutAndReopenAsync(IReadOnlyList<string> paths);
}

// What the shell desk asks of the window and the tray (TrayApp.Shell.cs). The calls marshal to
// the window thread themselves.
internal interface IShellSurface
{
    void OpenWindow();
    // The window on a file's detail, or Team files at a folder; "" is Home.
    void Reveal(string vaultPath);
    // A confirmation in front of everything; true for its OK button.
    Task<bool> ConfirmAsync(ShellQuestion question);
    // The action's one sentence: in the window's foot line when it shows, else as a notification.
    void Answer(ActionResult result);
}

// A question asked before an action (TaskDialog): its title, words and the OK button's words.
// Every one starts on Cancel, as the window's do, so Enter never does it by accident; Warning
// marks one that takes files from someone.
internal sealed record ShellQuestion(string Title, string Text, string Ok, bool Warning);

// The right-click items' and the notifications' requests, run against the host
// (docs/agent/EXPLORER.md 1.2). Batches come from ShellInbox on a thread-pool thread and queue
// here until the tray attaches; each then runs on its own, after the host has started, so a
// long check out never holds up a Show in Armory. Confirmations are asked one at a time.
internal sealed class ShellDesk(Action<string> log, TimeProvider? timeProvider = null)
{
    private static readonly TimeSpan StartWait = TimeSpan.FromSeconds(60);
    private readonly Lock gate = new();
    private readonly List<ShellBatch> early = [];
    private readonly SemaphoreSlim asking = new(1, 1);
    private IShellHost? host;
    private IShellSurface? surface;
    private bool closed;

    internal ToastTokens Tokens { get; } = new(timeProvider);

    // The pipe, started for the first instance right after the single-instance check. Null when it
    // could not start (another program holds its name): the right-click items and a
    // notification's buttons then only open the window (ArmoryShell.exe and LinkForwarder fall
    // back to that).
    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    internal static ShellInbox? StartInbox(AgentPaths paths, ShellDesk desk, Action<string> log)
    {
        var inbox = new ShellInbox(paths, desk.Receive, log);
        try
        {
            inbox.Start();
            log("shell: listening on " + inbox.Name);
            return inbox;
        }
        catch (Exception error) when (error is IOException or System.ComponentModel.Win32Exception or UnauthorizedAccessException or InvalidOperationException)
        {
            log("shell: the pipe could not start, so File Explorer's items and notification buttons only open the window: " + error.Message);
            inbox.Dispose();
            return null;
        }
    }

    // From the inbox (any thread, never blocks).
    internal void Receive(ShellBatch batch)
    {
        IShellHost? h;
        IShellSurface? s;
        lock (gate)
        {
            if (closed) return;
            h = host;
            s = surface;
            if (h is null || s is null)
            {
                early.Add(batch);
                return;
            }
        }
        // Off the inbox's thread at once: the inbox never waits on a batch.
        _ = Task.Run(() => RunAsync(batch, h, s));
    }

    // The tray exists: everything waiting runs, and so does everything after it.
    internal void Attach(IShellHost shellHost, IShellSurface shellSurface)
    {
        ShellBatch[] waiting;
        lock (gate)
        {
            host = shellHost;
            surface = shellSurface;
            waiting = early.ToArray();
            early.Clear();
        }
        foreach (var batch in waiting) _ = Task.Run(() => RunAsync(batch, shellHost, shellSurface));
    }

    // Armory is quitting: nothing more runs, and no notification's token answers again.
    internal void Close()
    {
        lock (gate)
        {
            closed = true;
            early.Clear();
        }
        Tokens.Clear();
    }

    // One batch, start to answer. Never throws.
    internal async Task RunAsync(ShellBatch batch, IShellHost h, IShellSurface s)
    {
        try
        {
            log("shell: " + ShellVerbNames.Name(batch.Verb) + ", " + batch.Paths.Count + (batch.Paths.Count == 1 ? " item" : " items"));
            var started = h.Started;
            if (!started.IsCompleted) await Task.WhenAny(started, Task.Delay(StartWait)).ConfigureAwait(false);
            if (batch.Verb == ShellVerb.Uri) await RunLinkAsync(batch.Paths.FirstOrDefault(), h, s).ConfigureAwait(false);
            else await RunVerbAsync(batch, h, s).ConfigureAwait(false);
        }
        catch (Exception error) when (error is not OutOfMemoryException)
        {
            log("shell: " + ShellVerbNames.Name(batch.Verb) + " failed: " + error);
            s.Answer(new ActionResult(false, "Armory couldn't do that. Try again in a moment."));
        }
    }

    private async Task RunVerbAsync(ShellBatch batch, IShellHost h, IShellSurface s)
    {
        var view = h.View;
        if (view.Connection is not (Connections.SignedIn or Connections.VaultOwnedByOther))
        {
            // The window opens on Connect, and says why.
            s.OpenWindow();
            s.Answer(new ActionResult(false, ShellWords.ConnectFirst));
            return;
        }
        List<string> paths = [];
        foreach (var full in batch.Paths)
        {
            if (!ShellPaths.TryVaultPath(h.VaultRoot, full, out var path))
            {
                s.Answer(new ActionResult(false, ShellWords.NotInVault));
                return;
            }
            if (!paths.Contains(path, StringComparer.OrdinalIgnoreCase)) paths.Add(path);
        }
        if (batch.Verb == ShellVerb.Show)
        {
            s.Reveal(paths[0]);
            return;
        }
        // The vault folder itself: only Check in (everything this computer has checked out).
        if (paths.Contains(""))
        {
            if (batch.Verb != ShellVerb.CheckIn)
            {
                s.Answer(new ActionResult(false, ShellWords.WholeVault));
                return;
            }
            var mine = view.MyFiles.Select(f => f.Path).ToArray();
            if (mine.Length == 0)
            {
                s.Answer(new ActionResult(false, ShellWords.NothingCheckedOut));
                return;
            }
            paths = [.. mine];
        }
        ActionResult result;
        switch (batch.Verb)
        {
            case ShellVerb.CheckOut:
            case ShellVerb.CheckOutAndOpen:
                var folders = ShellWords.Folders(view, paths);
                var open = batch.Verb == ShellVerb.CheckOutAndOpen && folders.Count == 0;
                if (folders.Count > 0 && ShellWords.CheckOutQuestion(view, paths, folders) is { } question && !await AskAsync(s, question).ConfigureAwait(false))
                {
                    log("shell: check out not confirmed");
                    return;
                }
                result = await h.CheckOutAsync(paths, open).ConfigureAwait(false);
                break;
            case ShellVerb.CheckIn:
                result = await h.CheckInAsync(paths).ConfigureAwait(false);
                break;
            case ShellVerb.Undo:
                result = await h.UndoCheckOutAsync(paths).ConfigureAwait(false);
                break;
            case ShellVerb.ForceCheckIn:
                var (targets, refusal) = ShellWords.ForceTargets(view, paths);
                if (refusal is not null)
                {
                    s.Answer(new ActionResult(false, refusal));
                    return;
                }
                if (!await AskAsync(s, ShellWords.ForceQuestion(targets)).ConfigureAwait(false))
                {
                    log("shell: force check in not confirmed");
                    return;
                }
                result = await h.TakeBackAsync(targets.Select(t => Guid.Parse(t.FileId!)).ToArray()).ConfigureAwait(false);
                break;
            default:
                return;
        }
        log("shell: " + ShellVerbNames.Name(batch.Verb) + (result.Ok ? " done" : " refused"));
        s.Answer(result);
    }

    // A notification's link: its token's action, once; anything else only opens the window.
    private async Task RunLinkAsync(string? text, IShellHost h, IShellSurface s)
    {
        if (!ProtocolLink.TryParse(text, out var link))
        {
            log("shell: a link that isn't one of Armory's opens the window");
            s.OpenWindow();
            return;
        }
        if (!Tokens.TryTake(link!, out var ticket))
        {
            log("shell: an unknown, used or old link opens the window");
            s.OpenWindow();
            return;
        }
        if (ticket!.Action == ProtocolLink.Show || ticket.Paths.Count == 0)
        {
            s.OpenWindow();
            return;
        }
        if (h.View.Connection is not (Connections.SignedIn or Connections.VaultOwnedByOther))
        {
            s.OpenWindow();
            s.Answer(new ActionResult(false, ShellWords.ConnectFirst));
            return;
        }
        // Only ever from the button of a notification Armory showed for these files: nothing is
        // checked out because it was opened.
        var result = await h.CheckOutAndReopenAsync(ticket.Paths).ConfigureAwait(false);
        log("shell: check out and reopen from a notification" + (result.Ok ? " done" : " refused"));
        s.Answer(result);
    }

    private async Task<bool> AskAsync(IShellSurface s, ShellQuestion question)
    {
        await asking.WaitAsync().ConfigureAwait(false);
        try { return await s.ConfirmAsync(question).ConfigureAwait(false); }
        finally { asking.Release(); }
    }
}

// A path File Explorer gave, as a vault path.
internal static class ShellPaths
{
    // "C:\IDEA\Armory\Robot 2027\Plate.SLDPRT" to "Robot 2027/Plate.SLDPRT"; the vault folder
    // itself to "". False outside the vault, for a name Armory ignores (~$ markers, .armory,
    // desktop.ini, Thumbs.db), and for anything VaultPath refuses.
    internal static bool TryVaultPath(string vaultRoot, string full, out string path)
    {
        path = string.Empty;
        var root = vaultRoot.Replace('/', '\\').TrimEnd('\\');
        var text = full.Trim().Replace('/', '\\').TrimEnd('\\');
        if (root.Length == 0 || text.Length == 0) return false;
        if (string.Equals(text, root, StringComparison.OrdinalIgnoreCase)) return true;
        if (!text.StartsWith(root + "\\", StringComparison.OrdinalIgnoreCase)) return false;
        var relative = text[(root.Length + 1)..];
        if (!VaultPath.TryCreate(relative, out var valid, out _, root) || VaultIgnore.IsIgnored(valid.Value)) return false;
        path = valid.Value;
        return true;
    }
}

// The words of the right-click items' answers and questions: the window's own (app.js askHtml,
// "checkOutAll" and "takeBack"), so a student reads the same thing from either place.
internal static class ShellWords
{
    internal const string ConnectFirst = "Connect this computer first.";
    internal const string NotInVault = "That isn't in the Armory folder.";
    internal const string WholeVault = "Pick files or folders inside a project for that.";
    internal const string NothingCheckedOut = "Nothing there is checked out by you.";
    internal const string NothingHeld = "None of those files is checked out by someone else now.";
    internal const string OnlyMentors = "Only a mentor or CAD lead can force a check in.";

    private static string Count(int n, string one, string many) => n.ToString("N0", CultureInfo.InvariantCulture) + " " + (n == 1 ? one : many);

    private static string FirstName(string name)
    {
        var n = name.Trim();
        if (System.Text.RegularExpressions.Regex.IsMatch(n, @"^(Mr|Mrs|Ms|Dr|Mx)\.?\s", System.Text.RegularExpressions.RegexOptions.IgnoreCase)) return n;
        var space = n.IndexOfAny([' ', '\t']);
        return space > 0 ? n[..space] : n;
    }

    // "Drivetrain", "Drivetrain and Arm", "Drivetrain, Arm and Plate.SLDPRT", "Drivetrain, Arm and 3 others".
    internal static string Names(IReadOnlyList<string> names) => names.Count switch
    {
        0 => "",
        1 => names[0],
        2 => names[0] + " and " + names[1],
        3 => names[0] + ", " + names[1] + " and " + names[2],
        _ => names[0] + ", " + names[1] + " and " + (names.Count - 2).ToString("N0", CultureInfo.InvariantCulture) + " others",
    };

    private static string Leaf(string path) => path[(path.LastIndexOf('/') + 1)..];

    private static IEnumerable<(FileRowView Row, ProjectView Project)> Rows(AgentView view) =>
        view.Projects.SelectMany(p => p.Folders.SelectMany(f => f.Files.Select(r => (r, p))));

    private static bool Under(string path, string folder) =>
        string.Equals(path, folder, StringComparison.OrdinalIgnoreCase) || path.StartsWith(folder + "/", StringComparison.OrdinalIgnoreCase);

    // The picked paths that are folders: no file of the view has that path, and one is under it.
    internal static IReadOnlyList<string> Folders(AgentView view, IReadOnlyList<string> paths)
    {
        var files = new HashSet<string>(Rows(view).Select(r => r.Row.Path), StringComparer.OrdinalIgnoreCase);
        return paths.Where(p => !files.Contains(p) && files.Any(f => f.StartsWith(p + "/", StringComparison.OrdinalIgnoreCase))).ToArray();
    }

    // "Check out all" for a pick with a folder in it, or null when no file there can be checked out.
    internal static ShellQuestion? CheckOutQuestion(AgentView view, IReadOnlyList<string> paths, IReadOnlyList<string> folders)
    {
        var rows = Rows(view).Where(r => r.Row.FileId is not null && paths.Any(p => Under(r.Row.Path, p))).Select(r => r.Row).ToArray();
        var count = rows.Count(r => r.Checkout.State == CheckoutStates.Available);
        if (count == 0) return null;
        var held = rows.Count(r => r.Checkout.State is CheckoutStates.Other or CheckoutStates.MyOtherComputer);
        var inside = folders.Any(f => Rows(view).Any(r => r.Row.Path.StartsWith(f + "/", StringComparison.OrdinalIgnoreCase) && r.Row.Path.IndexOf('/', f.Length + 1) > 0));
        var them = count == 1 ? "it" : "them";
        var text = "Check out " + Count(count, "file", "files") + " in " + Names(paths.Select(Leaf).ToArray()) +
            (inside ? folders.Count == 1 && paths.Count == 1 ? " and its folders" : " and their folders" : "") + "? " +
            "Nobody else can save " + them + " until you check " + them + " in." +
            (held > 0 ? " " + Count(held, "other file is", "other files are") + " checked out by someone else, and " + (held == 1 ? "stays" : "stay") + " with them." : "");
        return new ShellQuestion("Check out all", text, "Check out " + Count(count, "file", "files"), Warning: false);
    }

    // The files a Force check in of these paths takes back: each file someone else (or you on
    // another computer) has checked out, at or under a path, in a project where this account
    // may force a check in. A refusal sentence instead when there is none.
    internal static (IReadOnlyList<FileRowView> Targets, string? Refusal) ForceTargets(AgentView view, IReadOnlyList<string> paths)
    {
        var held = Rows(view).Where(r => r.Row.FileId is not null && Guid.TryParse(r.Row.FileId, out _) &&
            r.Row.Checkout.State is CheckoutStates.Other or CheckoutStates.MyOtherComputer && paths.Any(p => Under(r.Row.Path, p))).ToArray();
        if (held.Length == 0) return ([], NothingHeld);
        var allowed = held.Where(r => r.Project.CanTakeBack).Select(r => r.Row).ToArray();
        return allowed.Length == 0 ? ([], OnlyMentors) : (allowed, null);
    }

    // "Force check in" for the files ForceTargets found, naming who has them.
    internal static ShellQuestion ForceQuestion(IReadOnlyList<FileRowView> targets)
    {
        var names = targets.Select(t => t.Checkout.Name ?? "someone").Distinct(StringComparer.Ordinal).ToArray();
        var count = targets.Count;
        var holders = names.Length <= 2 ? string.Join(" and ", names) : string.Join(", ", names[..^1]) + " and " + names[^1];
        var what = count == 1 ? targets[0].Name : Count(count, "file", "files");
        var who = names.Length == 1 ? FirstName(names[0]) : "anyone";
        var whose = names.Length == 1 ? FirstName(names[0]) + "'s" : "their";
        var text = "Force check in " + what + "? " + holders + (names.Length == 1 ? " has " : " have ") + (count == 1 ? "it" : "them") + " checked out now. " +
            "Any changes " + who + " hasn't checked in are kept as " + whose + " own copy in " + (count == 1 ? "the file's history" : "each file's history") +
            ", so nothing is lost. Then anyone can check " + (count == 1 ? "it" : "them") + " out.";
        return new ShellQuestion(count == 1 ? "Force check in" : "Force check in all", text, count == 1 ? "Force check in" : "Force check in " + Count(count, "file", "files"), Warning: true);
    }
}
