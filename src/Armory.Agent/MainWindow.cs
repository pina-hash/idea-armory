using Armory.Agent.Engine.View;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;

namespace Armory.Agent;

// The Armory window: WebView2 showing wwwroot (served as https://armory.local, nothing from
// the network), talking to the engine only through the bridge. Closing it hides it to the
// tray; Quit in the tray exits.
internal sealed class MainWindow : Form, IBridgeWindow
{
    internal const string HostName = "armory.local";
    internal static readonly Uri StartPage = new("https://" + HostName + "/index.html");
    internal static readonly Uri RuntimeDownload = new("https://go.microsoft.com/fwlink/p/?LinkId=2124703");
    private readonly AgentHost host;
    private readonly AgentPaths paths;
    private readonly AgentLog log;
    private readonly Bridge bridge;
    private WebView2? web;
    private bool initialized;
    private bool allowClose;
    private string? pendingView;
    private bool viewPostScheduled;
    private readonly object viewGate = new();

    internal MainWindow(AgentHost host, AgentPaths paths, AgentLog log, Icon icon)
    {
        this.host = host;
        this.paths = paths;
        this.log = log;
        bridge = new Bridge(host, this, log);
        Text = "IDEA Armory";
        Icon = icon;
        StartPosition = FormStartPosition.CenterScreen;
        ClientSize = new Size(1120, 760);
        MinimumSize = new Size(720, 520);
        BackColor = Background(host.EffectiveTheme);
        host.ViewChanged += OnViewChanged;
    }

    internal void Open()
    {
        if (!Visible) Show();
        if (WindowState == FormWindowState.Minimized) WindowState = FormWindowState.Normal;
        Activate();
        BringToFront();
        if (!initialized)
        {
            initialized = true;
            _ = InitializeAsync();
        }
    }

    internal void CloseForQuit()
    {
        allowClose = true;
        Close();
    }

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        if (!allowClose && e.CloseReason == CloseReason.UserClosing)
        {
            e.Cancel = true;
            Hide();
            return;
        }
        base.OnFormClosing(e);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            host.ViewChanged -= OnViewChanged;
            web?.Dispose();
        }
        base.Dispose(disposing);
    }

    private async Task InitializeAsync()
    {
        string runtime;
        try { runtime = CoreWebView2Environment.GetAvailableBrowserVersionString(); }
        catch (Exception error) when (error is WebView2RuntimeNotFoundException or DllNotFoundException or BadImageFormatException)
        {
            log.Error("WebView2 runtime is missing", error);
            ShowRuntimeMissing();
            return;
        }
        try
        {
            log.Info("WebView2 runtime " + runtime);
            var control = new WebView2 { Dock = DockStyle.Fill, DefaultBackgroundColor = Background(host.EffectiveTheme) };
            Controls.Add(control);
            web = control;
            Directory.CreateDirectory(paths.WebView2Folder);
            var environment = await CoreWebView2Environment.CreateAsync(null, paths.WebView2Folder, new CoreWebView2EnvironmentOptions());
            await control.EnsureCoreWebView2Async(environment);
            var core = control.CoreWebView2;
            var debug = IsDebugBuild;
            core.Settings.AreDevToolsEnabled = debug;
            core.Settings.AreDefaultContextMenusEnabled = debug;
            core.Settings.AreBrowserAcceleratorKeysEnabled = debug;
            core.Settings.IsStatusBarEnabled = false;
            core.Settings.IsZoomControlEnabled = false;
            core.Settings.IsPasswordAutosaveEnabled = false;
            core.Settings.IsGeneralAutofillEnabled = false;
            core.Settings.AreHostObjectsAllowed = false;
            core.Settings.IsWebMessageEnabled = true;
            core.SetVirtualHostNameToFolderMapping(HostName, AgentPaths.WebRoot, CoreWebView2HostResourceAccessKind.Deny);
            core.NavigationStarting += (_, args) => KeepInsideApp(args.Uri, () => args.Cancel = true);
            // A frame never leaves the app and never opens the browser by itself.
            core.FrameNavigationStarting += (_, args) => { if (!IsAppUri(args.Uri)) args.Cancel = true; };
            core.NewWindowRequested += (_, args) =>
            {
                args.Handled = true;
                OpenOutside(args.Uri);
            };
            // The page downloads nothing and needs no camera, microphone, location or clipboard.
            core.DownloadStarting += (_, args) => { args.Cancel = true; args.Handled = true; };
            core.PermissionRequested += (_, args) => { args.State = CoreWebView2PermissionState.Deny; args.Handled = true; };
            core.WebMessageReceived += async (_, args) =>
            {
                if (!IsAppUri(args.Source)) return;
                string json;
                try { json = args.WebMessageAsJson; }
                catch (ArgumentException) { return; }
                await bridge.HandleAsync(json);
            };
            core.ProcessFailed += (_, args) =>
            {
                log.Error("WebView2 process failed: " + args.ProcessFailedKind);
                if (IsDisposed || allowClose) return;
                if (args.ProcessFailedKind == CoreWebView2ProcessFailedKind.BrowserProcessExited)
                {
                    // The control is dead with its browser process: build a new one.
                    BeginInvoke(() =>
                    {
                        if (web is not null) { Controls.Remove(web); web.Dispose(); web = null; }
                        initialized = false;
                        if (Visible) Open();
                    });
                    return;
                }
                try { core.Reload(); }
                catch (Exception error) when (error is InvalidOperationException or System.Runtime.InteropServices.COMException) { }
            };
            core.Navigate(StartPage.AbsoluteUri);
        }
        catch (Exception error) when (error is WebView2RuntimeNotFoundException)
        {
            log.Error("WebView2 runtime is missing", error);
            if (!IsDisposed && !allowClose) ShowRuntimeMissing();
        }
        catch (Exception error) when (error is not OutOfMemoryException)
        {
            // Quit can close the window while WebView2 is still starting; that is not a fault.
            if (IsDisposed || allowClose) return;
            log.Error("the window could not start WebView2", error);
            ShowRuntimeMissing();
        }
    }

#if DEBUG
    private const bool IsDebugBuild = true;
#else
    private const bool IsDebugBuild = false;
#endif

    internal static bool IsAppUri(string? uri)
        => Uri.TryCreate(uri, UriKind.Absolute, out var parsed) && parsed.Scheme == Uri.UriSchemeHttps &&
           parsed.IsDefaultPort && string.Equals(parsed.Host, HostName, StringComparison.OrdinalIgnoreCase);

    // Any navigation away from https://armory.local is canceled; web links open in the
    // default browser instead, and everything else (file:, javascript:, ...) goes nowhere.
    private void KeepInsideApp(string uri, Action cancel)
    {
        if (IsAppUri(uri) || uri == "about:blank") return;
        cancel();
        OpenOutside(uri);
    }

    private void OpenOutside(string uri)
    {
        if (!Uri.TryCreate(uri, UriKind.Absolute, out var parsed) || !Shell.IsWebUrl(parsed)) return;
        try { Shell.OpenUrl(parsed); }
        catch (Exception error) when (error is System.ComponentModel.Win32Exception or InvalidOperationException)
        { log.Error("could not open a link in the browser", error); }
    }

    // Engine views arrive on any thread; only the newest one is posted, on the UI thread.
    private void OnViewChanged(AgentView view)
    {
        var json = BridgeMessages.ViewMessage(view);
        lock (viewGate)
        {
            pendingView = json;
            if (viewPostScheduled) return;
            viewPostScheduled = true;
        }
        if (!IsHandleCreated || IsDisposed)
        {
            lock (viewGate) viewPostScheduled = false;
            return;
        }
        try { BeginInvoke(FlushView); }
        catch (InvalidOperationException) { lock (viewGate) viewPostScheduled = false; }
    }

    private void FlushView()
    {
        string? json;
        lock (viewGate)
        {
            json = pendingView;
            pendingView = null;
            viewPostScheduled = false;
        }
        if (json is null) return;
        if (web?.CoreWebView2 is { } core)
        {
            try { core.PostWebMessageAsJson(json); }
            catch (Exception error) when (error is InvalidOperationException or System.Runtime.InteropServices.COMException) { }
        }
        var theme = host.EffectiveTheme;
        BackColor = Background(theme);
        if (web is not null) web.DefaultBackgroundColor = Background(theme);
    }

    void IBridgeWindow.Post(string json)
    {
        if (web?.CoreWebView2 is not { } core) return;
        try { core.PostWebMessageAsJson(json); }
        catch (Exception error) when (error is InvalidOperationException or System.Runtime.InteropServices.COMException) { }
    }

    string? IBridgeWindow.ChooseFolder(string current)
    {
        using var dialog = new FolderBrowserDialog
        {
            Description = "Choose the folder where Armory keeps your team's CAD files.",
            UseDescriptionForTitle = true,
            ShowNewFolderButton = true,
            InitialDirectory = Directory.Exists(current) ? current : Path.GetDirectoryName(current) ?? "",
        };
        return dialog.ShowDialog(this) == DialogResult.OK ? dialog.SelectedPath : null;
    }

    void IBridgeWindow.ShowProblem(string message)
        => MessageBox.Show(this, message, "IDEA Armory", MessageBoxButtons.OK, MessageBoxIcon.Information);

    // The surface-0 color of each theme (wwwroot/app.css), so nothing flashes while loading.
    private static Color Background(string theme) => theme == Themes.SpaceWhite ? Color.FromArgb(0xe8, 0xec, 0xeb) : Color.FromArgb(0x0a, 0x0c, 0x0b);

    private void ShowRuntimeMissing()
    {
        if (web is not null)
        {
            Controls.Remove(web);
            web.Dispose();
            web = null;
        }
        Controls.Clear();
        BackColor = SystemColors.Window;
        var panel = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 4,
            Padding = new Padding(48),
            BackColor = SystemColors.Window,
        };
        panel.RowStyles.Add(new RowStyle(SizeType.Percent, 50));
        panel.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        panel.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        panel.RowStyles.Add(new RowStyle(SizeType.Percent, 50));
        var message = new Label
        {
            Text = "Armory needs Microsoft Edge WebView2 Runtime. Ask Mr. Pina to install it.",
            AutoSize = true,
            MaximumSize = new Size(560, 0),
            Font = new Font("Segoe UI", 14f),
            ForeColor = SystemColors.WindowText,
            Anchor = AnchorStyles.None,
            Margin = new Padding(0, 0, 0, 24),
        };
        var button = new Button
        {
            Text = "Get WebView2 Runtime",
            AutoSize = true,
            MinimumSize = new Size(220, 44),
            Height = 44,
            Font = new Font("Segoe UI", 11f),
            Anchor = AnchorStyles.None,
        };
        button.Click += (_, _) => OpenOutside(RuntimeDownload.AbsoluteUri);
        panel.Controls.Add(new Panel { Height = 1 }, 0, 0);
        panel.Controls.Add(message, 0, 1);
        panel.Controls.Add(button, 0, 2);
        Controls.Add(panel);
        AcceptButton = button;
    }
}
