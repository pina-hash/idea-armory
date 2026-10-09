using System.ComponentModel;
using System.Diagnostics;
using Armory.Agent.Engine.View;
using Armory.Core;
using Armory.Platform.Windows;

namespace Armory.Agent;

// The host's half of File Explorer and Windows notifications (docs/agent/EXPLORER.md): the
// right-click items kept in step with the vault root and the account, the badge table published
// from the engine's facts, the badges' health for Settings and its Turn on, and what the shell
// desk (ShellDesk) runs on the engine.
internal sealed partial class AgentHost : IShellHost
{
    private static readonly TimeSpan BadgeHealthEvery = TimeSpan.FromMinutes(10);
    private readonly TaskCompletionSource shellStarted = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly object shellGate = new();
    // Held while the badge table is built and published (never on the engine's thread).
    private readonly object badgeGate = new();
    private readonly PublishPace badgePace = new(PublishPace.Every);
    private System.Threading.Timer? badgeHealthTimer;
    private System.Threading.Timer? badgeTimer;
    private BadgePublisher? badgePublisher;
    private IReadOnlyList<BadgeEntry> badgesPublished = [];
    private string? badgesRoot;
    private BadgesView? badges;
    private bool badgesFailed;
    private bool shellRegisters;
    private bool shellStopped;
    private MenuWanted? menuWanted;
    private MenuWanted? menuLast;
    private bool menuWriting;

    // The right-click items wanted now: the vault root and the force-check-in folders, one per line.
    private sealed record MenuWanted(string Root, string Folders);

    Task IShellHost.Started => shellStarted.Task;
    string IShellHost.VaultRoot => Settings.VaultRoot;
    AgentView IShellHost.View => View;
    Task<ActionResult> IShellHost.CheckOutAsync(IReadOnlyList<string> paths, bool open) => CheckOutAsync(paths, open);
    Task<ActionResult> IShellHost.CheckInAsync(IReadOnlyList<string> paths) => CheckInAsync(paths);
    Task<ActionResult> IShellHost.UndoCheckOutAsync(IReadOnlyList<string> paths) => UndoCheckOutAsync(paths);
    Task<ActionResult> IShellHost.TakeBackAsync(IReadOnlyList<Guid> fileIds) => TakeBackAsync(fileIds);
    Task<ActionResult> IShellHost.CheckOutAndReopenAsync(IReadOnlyList<string> paths) => CheckOutAndReopenAsync(paths);
    bool IShellHost.PickerShowing => PickerShowing;

    // The files SolidWorks has open that this computer has not checked out, for the notifications
    // that ask about them (TrayApp.Shell.cs).
    // seam: repointed to the engine's OpenPrompts/CheckOutAndReopenAsync when the SolidWorks link lands
    internal IReadOnlyList<OpenPromptInfo> CurrentOpenPrompts() => HostShell.OpenPrompts(View, OpenWithoutCheckOut);

    // A notification's "Check out and reopen". Never called because a file was opened: only for
    // the button of a notification Armory showed (ShellDesk, ToastTokens).
    // seam: repointed to the engine's OpenPrompts/CheckOutAndReopenAsync when the SolidWorks link lands
    internal Task<ActionResult> CheckOutAndReopenAsync(IReadOnlyList<string> paths) => CheckOutAsync(paths, open: true);

    // The settings the window shows, with the badges' health.
    private SettingsView SettingsNow()
    {
        lock (gate) return settings.ToView() with { Badges = Volatile.Read(ref badges) };
    }

    // At the end of StartAsync: the registrations of the installed copy, the badges' health now
    // and every 10 minutes, and a look at each new view.
    private void StartShell()
    {
        shellRegisters = ShellIdentity.IsInstalledCopy(paths);
        if (shellRegisters)
        {
            // The shell's property store wants a thread of its own apartment.
            var repair = new Thread(() => ShellIdentity.Repair(AgentPaths.AppFolder, log.Info)) { IsBackground = true, Name = "Armory shell identity" };
            repair.SetApartmentState(ApartmentState.STA);
            repair.Start();
        }
        else log.Info("shell: not the installed copy, so the right-click items and the notification registration are left as they are");
        badgeTimer = new System.Threading.Timer(_ => _ = PublishBadgesAsync(), null, Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
        badgeHealthTimer = new System.Threading.Timer(_ => CheckBadgeHealth(), null, TimeSpan.Zero, BadgeHealthEvery);
        ViewChanged += OnShellView;
        OnShellView(View);
        shellStarted.TrySetResult();
    }

    // Quit: the badges go (generation 0) and nothing more is written.
    private void StopShell()
    {
        lock (shellGate)
        {
            if (shellStopped) return;
            shellStopped = true;
        }
        ViewChanged -= OnShellView;
        badgeHealthTimer?.Dispose();
        badgeTimer?.Dispose();
        lock (badgeGate)
        {
            try { badgePublisher?.Dispose(); }
            catch (Exception error) when (error is not OutOfMemoryException) { log.Error("could not take the badges down", error); }
            badgePublisher = null;
        }
        shellStarted.TrySetResult();
    }

    // Any thread, every view: cheap, and the slow parts go elsewhere.
    private void OnShellView(AgentView view)
    {
        if (shellRegisters) WantMenu(view);
        RequestBadges();
    }

    // ---- The right-click items ---------------------------------------------------------------

    private void WantMenu(AgentView view)
    {
        var wanted = new MenuWanted(Settings.VaultRoot, string.Join("\n", HostShell.ForceCheckInFolders(view)));
        lock (shellGate)
        {
            if (shellStopped || wanted == menuLast) return;
            menuLast = wanted;
            menuWanted = wanted;
            if (menuWriting) return;
            menuWriting = true;
        }
        _ = Task.Run(WriteMenus);
    }

    // One writer at a time, always the newest wish.
    private void WriteMenus()
    {
        while (true)
        {
            MenuWanted wanted;
            lock (shellGate)
            {
                if (menuWanted is null || shellStopped) { menuWriting = false; return; }
                wanted = menuWanted;
                menuWanted = null;
            }
            try
            {
                if (!File.Exists(Path.Combine(AgentPaths.AppFolder, "ArmoryShell.exe")))
                {
                    log.Info("shell: ArmoryShell.exe is missing from the app folder, so the right-click items are not written");
                    continue;
                }
                var folders = wanted.Folders.Length == 0 ? [] : wanted.Folders.Split('\n');
                if (ShellVerbs.Apply(wanted.Root, AgentPaths.AppFolder, folders))
                    log.Info("shell: wrote the right-click items for " + wanted.Root + (folders.Length == 0 ? "" : ", Force check in in " + folders.Length + " projects"));
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException or System.Security.SecurityException)
            {
                log.Error("could not write the right-click items", error);
            }
        }
    }

    // ---- Badges -------------------------------------------------------------------------------

    private void RequestBadges()
    {
        DateTimeOffset? due;
        lock (shellGate)
        {
            // Nothing before StartShell (the window may check the badges' health first) or
            // before the health is known.
            if (shellStopped || badgeTimer is null || badges is null) return;
            due = badgePace.Ask(DateTimeOffset.UtcNow);
        }
        if (due is { } at) Arm(at);
    }

    private void Arm(DateTimeOffset at)
    {
        var wait = at - DateTimeOffset.UtcNow;
        try { badgeTimer?.Change(wait < TimeSpan.Zero ? TimeSpan.Zero : wait, Timeout.InfiniteTimeSpan); }
        catch (ObjectDisposedException) { }
    }

    // Publishes the badge table for the engine's facts, then tells Explorer what changed. Only
    // while the badges are installed (BadgeHealth is not off) and an account is signed in.
    private async Task PublishBadgesAsync()
    {
        lock (shellGate)
        {
            if (shellStopped) return;
            badgePace.Started(DateTimeOffset.UtcNow);
        }
        try
        {
            var current = Volatile.Read(ref runtime);
            var signedIn = View.Connection == Connections.SignedIn;
            if (current is null || !signedIn || Volatile.Read(ref badges)?.State is null or BadgesStates.Off) ClearBadges();
            else
            {
                // An engine that is stopping may never answer: the next view asks again.
                var facts = await current.Engine.BadgeFactsAsync().WaitAsync(TimeSpan.FromSeconds(30)).ConfigureAwait(false);
                var entries = BadgeRules.Entries(facts);
                var root = current.Files.Root;
                ShellChangePlan[] plans;
                lock (badgeGate)
                {
                    if (Volatile.Read(ref shellStopped)) return;
                    badgePublisher ??= new BadgePublisher(BadgePublisher.DefaultHeaderName(paths.InstanceSuffix));
                    badgePublisher.Publish(root, entries);
                    // A new vault root: the old one's badges go, the new one's come.
                    plans = badgesRoot is { } before && !string.Equals(before, root, StringComparison.OrdinalIgnoreCase)
                        ? [ShellChangePlan.For(before, badgesPublished, []), ShellChangePlan.For(root, [], entries)]
                        : [ShellChangePlan.For(root, badgesPublished, entries)];
                    badgesPublished = entries;
                    badgesRoot = root;
                }
                // SHChangeNotify can wait on Explorer: never on the engine's or the window's thread.
                if (plans.Any(p => p.Kind != ShellChangeKind.Nothing)) _ = Task.Run(() => NotifyExplorer(plans));
            }
        }
        catch (Exception error) when (error is not OutOfMemoryException)
        {
            if (!badgesFailed) log.Error("could not publish the badges", error);
            badgesFailed = true;
        }
        finally
        {
            DateTimeOffset? again;
            lock (shellGate) again = shellStopped ? null : badgePace.Finished(DateTimeOffset.UtcNow);
            if (again is { } at) Arm(at);
        }
    }

    // Signed out, or the badges are off: no badge stays.
    private void ClearBadges()
    {
        ShellChangePlan? plan = null;
        lock (badgeGate)
        {
            if (Volatile.Read(ref shellStopped) || badgePublisher is null || badgesRoot is null) return;
            badgePublisher.Clear();
            plan = ShellChangePlan.For(badgesRoot, badgesPublished, []);
            badgesPublished = [];
            badgesRoot = null;
        }
        if (plan.Kind != ShellChangeKind.Nothing) _ = Task.Run(() => NotifyExplorer([plan]));
    }

    private void NotifyExplorer(IEnumerable<ShellChangePlan> plans)
    {
        try { foreach (var plan in plans) ShellNotify.Send(plan); }
        catch (Exception error) when (error is not OutOfMemoryException) { log.Error("could not tell File Explorer about the badges", error); }
    }

    // BadgeHealth now: at start, every 10 minutes, when the window opens and after Turn on.
    internal void CheckBadgeHealth()
    {
        BadgeHealthReport report;
        try { report = BadgeHealth.Check(); }
        catch (Exception error) when (error is not OutOfMemoryException)
        {
            log.Error("could not check the badges", error);
            return;
        }
        var next = new BadgesView(report.Key, report.Line);
        BadgesView? before;
        lock (shellGate)
        {
            if (shellStopped) return;
            before = badges;
            badges = next;
        }
        if (before == next) return;
        log.Info("badges: " + report.Key + (report.Detail is { Length: > 0 } detail ? " (" + detail + ")" : ""));
        RaiseView();
        if (report.State == BadgeHealthState.Off) ClearBadges();
        else RequestBadges();
    }

    // Settings' Turn on: the badges setup bundled beside Armory, as an administrator. A canceled
    // password prompt changes nothing and says so.
    internal async Task<ActionResult> TurnOnBadgesAsync()
    {
        var setup = Path.Combine(AgentPaths.AppFolder, "badges", "IDEA-Armory-Badges-Setup.exe");
        if (!File.Exists(setup)) return new ActionResult(false, HostShell.NoBadgesSetup);
        int exit;
        try
        {
            using var process = Process.Start(new ProcessStartInfo(setup)
            {
                UseShellExecute = true,
                Verb = "runas",
                Arguments = "/SILENT /SUPPRESSMSGBOXES /NORESTART",
                WorkingDirectory = Path.GetDirectoryName(setup)!,
            });
            if (process is null) return new ActionResult(false, HostShell.BadgesNotStarted);
            log.Info("badges: the badges setup started as an administrator");
            await process.WaitForExitAsync().ConfigureAwait(false);
            exit = process.ExitCode;
        }
        catch (Win32Exception error) when (error.NativeErrorCode == HostShell.Canceled)
        {
            log.Info("badges: the administrator's password prompt was canceled");
            return new ActionResult(false, HostShell.PasswordNeeded);
        }
        catch (Exception error) when (error is Win32Exception or InvalidOperationException or IOException)
        {
            log.Error("could not start the badges setup", error);
            return new ActionResult(false, HostShell.BadgesNotStarted);
        }
        log.Info("badges: the badges setup ended with " + exit);
        CheckBadgeHealth();
        if (exit != 0) return new ActionResult(false, HostShell.BadgesSetupStopped);
        return new ActionResult(true, Volatile.Read(ref badges)?.Line ?? BadgeHealth.AfterSignInLine);
    }
}

// The host's shell rules that need no Windows, tested on every host.
internal static class HostShell
{
    // ERROR_CANCELLED: the person said no to the administrator's password prompt.
    internal const int Canceled = 1223;
    internal const string PasswordNeeded = "Nothing changed. This one step needs an administrator's password.";
    internal const string NoBadgesSetup = "The badges setup isn't in Armory's folder on this computer. Ask an administrator to run IDEA-Armory-Badges-Setup.";
    internal const string BadgesNotStarted = "Armory couldn't start the badges setup. Try again in a moment.";
    internal const string BadgesSetupStopped = "The badges setup stopped before it finished, so nothing changed.";

    // The vault-relative folders of the projects where this account may force a check in
    // (ProjectView.CanTakeBack), sorted, each once; none while signed out. A project's folder is
    // the first part of its files' paths, or its name while it has none (they are the same but
    // during a rename the server has not finished).
    internal static IReadOnlyList<string> ForceCheckInFolders(AgentView view)
    {
        if (view.Connection != Connections.SignedIn) return [];
        return view.Projects.Where(p => p.CanTakeBack).Select(FolderOf).Distinct(StringComparer.OrdinalIgnoreCase).Order(StringComparer.OrdinalIgnoreCase).ToArray();
    }

    private static string FolderOf(ProjectView project)
    {
        var path = project.Folders.SelectMany(f => f.Files).Select(r => r.Path).FirstOrDefault();
        var slash = path?.IndexOf('/') ?? -1;
        return slash > 0 ? path![..slash] : project.Name;
    }

    // The open files without a check out here, as the notifications name them; a file the view
    // does not list (yet) is left out.
    internal static IReadOnlyList<OpenPromptInfo> OpenPrompts(AgentView view, IReadOnlyCollection<string> open)
    {
        if (open.Count == 0) return [];
        var wanted = new HashSet<string>(open, StringComparer.OrdinalIgnoreCase);
        List<OpenPromptInfo> found = [];
        foreach (var row in view.Projects.SelectMany(p => p.Folders).SelectMany(f => f.Files))
        {
            if (!wanted.Remove(row.Path)) continue;
            var checkout = row.Checkout;
            var by = checkout.State switch
            {
                CheckoutStates.Other => (checkout.Name ?? "someone else") + " on " + (checkout.Device ?? "another computer"),
                CheckoutStates.MyOtherComputer => "you on " + (checkout.Device ?? "another computer"),
                _ => null,
            };
            // A file checked out here asks nothing.
            if (checkout.State == CheckoutStates.Mine) continue;
            found.Add(new OpenPromptInfo(row.Path, row.Name, checkout.State == CheckoutStates.Available, by));
        }
        return found;
    }
}

// At most one badge publication every 500 ms (docs/agent/EXPLORER.md 2.4): a view change asks,
// the pace says when to start, and a change while one runs starts one more after it. Pure: time
// comes in with each call. Not thread safe (AgentHost holds its lock around it).
internal sealed class PublishPace(TimeSpan every)
{
    internal static readonly TimeSpan Every = TimeSpan.FromMilliseconds(500);
    private DateTimeOffset? last;
    private bool waiting;
    private bool running;
    private bool again;

    // A change at now: when the next publication should start, or null when one already waits
    // or runs (it picks this change up).
    internal DateTimeOffset? Ask(DateTimeOffset now)
    {
        if (running) { again = true; return null; }
        if (waiting) return null;
        waiting = true;
        return Next(now);
    }

    internal void Started(DateTimeOffset now)
    {
        waiting = false;
        running = true;
        last = now;
    }

    // Done: when to start again for a change that came meanwhile, or null.
    internal DateTimeOffset? Finished(DateTimeOffset now)
    {
        running = false;
        if (!again) return null;
        again = false;
        waiting = true;
        return Next(now);
    }

    private DateTimeOffset Next(DateTimeOffset now) => last is { } at && now - at < every ? at + every : now;
}
