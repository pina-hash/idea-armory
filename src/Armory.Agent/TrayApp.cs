using Armory.Agent.Engine.View;
using Microsoft.Win32;

namespace Armory.Agent;

// The notification-area icon and its menu. The window is created the first time it opens;
// "--background" starts with the icon only. Quit stops the engine and exits cleanly.
internal sealed class TrayApp : ApplicationContext
{
    // NotifyIcon.Text refuses longer text on some Windows Forms versions.
    private const int TooltipLimit = 63;
    private readonly AgentHost host;
    private readonly AgentPaths paths;
    private readonly AgentLog log;
    private readonly Icon appIcon;
    private readonly Icon trayIcon;
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
        appIcon = LoadIcon(new Size(32, 32));
        trayIcon = LoadIcon(SystemInformation.SmallIconSize);

        menu = new ContextMenuStrip();
        var openItem = new ToolStripMenuItem("Open Armory", null, (_, _) => OpenWindow()) { Font = new Font(menu.Font, FontStyle.Bold) };
        var vaultItem = new ToolStripMenuItem("Open vault folder", null, (_, _) => OpenVault());
        pauseItem = new ToolStripMenuItem("Pause sync", null, (_, _) => TogglePause());
        accountItem = new ToolStripMenuItem("Connect this computer", null, (_, _) => ConnectOrSignOut());
        var quitItem = new ToolStripMenuItem("Quit", null, (_, _) => Quit());
        menu.Items.AddRange([openItem, vaultItem, pauseItem, accountItem, quitItem]);
        menu.Opening += (_, _) => UpdateMenu(host.View);

        notify = new NotifyIcon
        {
            Icon = trayIcon,
            Text = "IDEA Armory",
            ContextMenuStrip = menu,
            Visible = true,
        };
        notify.DoubleClick += (_, _) => OpenWindow();

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

    private static Icon LoadIcon(Size size)
    {
        using var stream = typeof(TrayApp).Assembly.GetManifestResourceStream("armory.ico")
            ?? throw new InvalidOperationException("The Armory icon is missing from the app.");
        return new Icon(stream, size);
    }

    private void Post(Action action)
    {
        if (marshal.IsDisposed) return;
        try { marshal.BeginInvoke(action); }
        catch (InvalidOperationException) { }
    }

    private void OnViewChanged(AgentView view) => Post(() => UpdateMenu(view));

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
        pauseItem.Text = host.IsPaused ? "Resume sync" : "Pause sync";
        accountItem.Text = signedIn ? "Sign out" : "Connect this computer";
        var line = string.IsNullOrWhiteSpace(view.Sync.Line) ? "IDEA Armory" : view.Sync.Line.Trim();
        notify.Text = line.Length <= TooltipLimit ? line : line[..(TooltipLimit - 3)] + "...";
    }

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
        var answer = MessageBox.Show("Sign out of Armory on this computer? Your files stay in the vault folder.", "IDEA Armory",
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
            trayIcon.Dispose();
        }
        base.Dispose(disposing);
    }
}
