using Armory.Agent.Engine.View;
using Microsoft.Win32;

namespace Armory.Agent;

// The notification-area icon and its menu. The window is created the first time it opens;
// "--background" starts with the icon only. Quit stops the engine and exits cleanly. The icon
// follows the sync state (view.Sync.State) with a badge whose shape, not only its color, says
// syncing, paused, offline or attention (tools/agent-icon/make_icon.py draws them).
internal sealed class TrayApp : ApplicationContext
{
    // NotifyIcon.Text refuses longer text on some Windows Forms versions.
    private const int TooltipLimit = 63;
    private readonly AgentHost host;
    private readonly AgentPaths paths;
    private readonly AgentLog log;
    private readonly Icon appIcon;
    private readonly Dictionary<string, Icon> trayIcons = new(StringComparer.Ordinal);
    private readonly CheckOutPrompts prompts = new();
    private readonly NotifyIcon notify;
    private readonly ContextMenuStrip menu;
    private readonly ToolStripMenuItem pauseItem;
    private readonly ToolStripMenuItem accountItem;
    private readonly Control marshal;
    private readonly RegisteredWaitHandle showWait;
    private readonly RegisteredWaitHandle quitWait;
    private MainWindow? window;
    private bool quitting;
    private bool signedIn;

    internal TrayApp(AgentHost host, AgentPaths paths, AgentLog log, SingleInstance instance, bool background)
    {
        this.host = host;
        this.paths = paths;
        this.log = log;
        marshal = new Control();
        marshal.CreateControl();
        _ = marshal.Handle;
        appIcon = LoadIcon("armory.ico", new Size(32, 32));
        foreach (var state in new[] { SyncStates.Synced, SyncStates.Syncing, SyncStates.Paused, SyncStates.Offline, SyncStates.Attention })
            trayIcons[state] = LoadIcon("tray-" + state + ".ico", SystemInformation.SmallIconSize);

        menu = new ContextMenuStrip();
        var openItem = new ToolStripMenuItem("Open Armory", null, (_, _) => OpenWindow()) { Font = new Font(menu.Font, FontStyle.Bold) };
        var vaultItem = new ToolStripMenuItem("Open Armory folder", null, (_, _) => OpenVault());
        pauseItem = new ToolStripMenuItem("Pause", null, (_, _) => TogglePause());
        accountItem = new ToolStripMenuItem("Connect this computer", null, (_, _) => ConnectOrSignOut());
        var quitItem = new ToolStripMenuItem("Quit", null, (_, _) => Quit());
        menu.Items.AddRange([openItem, vaultItem, pauseItem, accountItem, quitItem]);
        menu.Opening += (_, _) => UpdateMenu(host.View);

        notify = new NotifyIcon
        {
            Icon = trayIcons[SyncStates.Synced],
            Text = "IDEA Armory",
            ContextMenuStrip = menu,
            Visible = true,
        };
        notify.DoubleClick += (_, _) => OpenWindow();
        notify.BalloonTipClicked += (_, _) => OpenWindow();

        host.ViewChanged += OnViewChanged;
        SystemEvents.UserPreferenceChanged += OnUserPreferenceChanged;
        SystemEvents.SessionEnding += OnSessionEnding;
        showWait = ThreadPool.RegisterWaitForSingleObject(instance.ShowSignal, (_, _) => Post(OpenWindow), null, Timeout.Infinite, executeOnlyOnce: false);
        quitWait = ThreadPool.RegisterWaitForSingleObject(instance.QuitSignal, (_, _) => Post(Quit), null, Timeout.Infinite, executeOnlyOnce: false);

        _ = StartAsync(background);
    }

    private async Task StartAsync(bool background)
    {
        if (!background) OpenWindow();
        try { await host.StartAsync(); }
        catch (Exception error) when (error is not OutOfMemoryException) { log.Error("start failed", error); }
        UpdateMenu(host.View);
    }

    private static Icon LoadIcon(string name, Size size)
    {
        using var stream = typeof(TrayApp).Assembly.GetManifestResourceStream(name)
            ?? throw new InvalidOperationException("The Armory icon " + name + " is missing from the app.");
        return new Icon(stream, size);
    }

    private void Post(Action action)
    {
        if (marshal.IsDisposed) return;
        try { marshal.BeginInvoke(action); }
        catch (InvalidOperationException) { }
    }

    private void OnViewChanged(AgentView view)
    {
        // The quiet check-out question outside the window (decision D13): files that closed may
        // ask again when reopened, and the newest question gets one balloon while hidden.
        var stillOpen = host.OpenWithoutCheckOut;
        Post(() => UpdateMenu(view));
        KeepCheckOutPromptsFor(stillOpen);
        if (view.Prompt is { } prompt)
            OfferCheckOut(prompt.Path, prompt.Name, prompt.CanCheckOut ? null
                : prompt.Checkout.State == CheckoutStates.MyOtherComputer ? "you on " + (prompt.Checkout.Device ?? "another computer")
                : (prompt.Checkout.Name ?? "someone else") + " on " + (prompt.Checkout.Device ?? "another computer"));
    }

    private void OnUserPreferenceChanged(object? sender, UserPreferenceChangedEventArgs e)
    {
        if (e.Category is UserPreferenceCategory.General or UserPreferenceCategory.Color or UserPreferenceCategory.VisualStyle)
            host.RefreshSystemTheme();
    }

    // Sign-out or shutdown: stop the engine the same way Quit does, while Windows waits.
    private void OnSessionEnding(object? sender, SessionEndingEventArgs e) => Post(Quit);

    private void UpdateMenu(AgentView view)
    {
        if (quitting) return;
        signedIn = view.Connection is Connections.SignedIn or Connections.VaultOwnedByOther;
        var paused = host.IsPaused;
        pauseItem.Text = paused ? "Resume" : "Pause";
        accountItem.Text = signedIn ? "Sign out" : "Connect this computer";
        var line = string.IsNullOrWhiteSpace(view.Sync.Line) ? "IDEA Armory" : view.Sync.Line.Trim();
        notify.Text = line.Length <= TooltipLimit ? line : line[..(TooltipLimit - 3)] + "...";
        var state = paused ? SyncStates.Paused : view.Sync.State;
        var icon = trayIcons.TryGetValue(state ?? string.Empty, out var found) ? found : trayIcons[SyncStates.Synced];
        if (!ReferenceEquals(notify.Icon, icon)) notify.Icon = icon;
    }

    // Decision D13, for when SolidWorks opened a file this computer has not checked out (its ~$
    // marker appeared). While the window shows, its own prompt card asks; while it is hidden,
    // one quiet balloon per opened file, and clicking it opens the window on that card.
    // checkedOutBy is "Maria Lopez on LAB-PC-07" when someone else has it. Called whenever the
    // view changes, with the engine's newest question and the files still open.
    internal void OfferCheckOut(string path, string name, string? checkedOutBy) => Post(() =>
    {
        if (quitting) return;
        var showing = window is { IsDisposed: false, Visible: true } && window.WindowState != FormWindowState.Minimized;
        if (!prompts.ShouldOffer(path, showing)) return;
        var (title, text) = CheckOutPrompts.Words(name, checkedOutBy);
        notify.BalloonTipTitle = title;
        notify.BalloonTipText = text;
        notify.BalloonTipIcon = ToolTipIcon.None;
        notify.ShowBalloonTip(10000);
    });

    // The files SolidWorks still has open; any other file may ask again when reopened.
    internal void KeepCheckOutPromptsFor(IReadOnlyCollection<string> stillOpen) => Post(() => prompts.KeepOnly(stillOpen));

    internal void OpenWindow()
    {
        if (quitting) return;
        if (window is null || window.IsDisposed) window = new MainWindow(host, paths, log, appIcon);
        window.Open();
    }

    private void OpenVault()
    {
        try
        {
            var root = host.Settings.VaultRoot;
            Directory.CreateDirectory(root);
            Shell.OpenFolder(root);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception)
        {
            log.Error("could not open the vault folder", error);
        }
    }

    private void TogglePause()
    {
        if (host.IsPaused) host.Resume();
        else host.Pause();
        UpdateMenu(host.View);
    }

    private void ConnectOrSignOut()
    {
        if (!signedIn)
        {
            OpenWindow();
            _ = host.ConnectAsync();
            return;
        }
        var answer = MessageBox.Show("Sign out of Armory on this computer? Your files stay in the Armory folder.", "IDEA Armory",
            MessageBoxButtons.YesNo, MessageBoxIcon.Question, MessageBoxDefaultButton.Button2);
        if (answer == DialogResult.Yes) host.SignOut();
    }

    internal async void Quit()
    {
        if (quitting) return;
        quitting = true;
        log.Info("quitting");
        host.ViewChanged -= OnViewChanged;
        notify.Visible = false;
        window?.CloseForQuit();
        try { await host.DisposeAsync(); }
        catch (Exception error) when (error is not OutOfMemoryException) { log.Error("stop failed", error); }
        ExitThread();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            SystemEvents.UserPreferenceChanged -= OnUserPreferenceChanged;
            SystemEvents.SessionEnding -= OnSessionEnding;
            showWait.Unregister(null);
            quitWait.Unregister(null);
            host.ViewChanged -= OnViewChanged;
            notify.Dispose();
            menu.Dispose();
            window?.Dispose();
            marshal.Dispose();
            appIcon.Dispose();
            foreach (var icon in trayIcons.Values) icon.Dispose();
        }
        base.Dispose(disposing);
    }
}
