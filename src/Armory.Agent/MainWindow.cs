using Armory.Agent.Engine.View;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;

namespace Armory.Agent;

// The Armory window: WebView2 showing wwwroot (served as https://armory.local, nothing from
// the network), talking to the engine only through the bridge. Closing it hides it to the
// tray; Quit in the tray exits.
internal sealed class MainWindow : Form, IBridgeWindow
{
    internal const string HostName = PageAssets.HostName;
    // ?v=<version>: a new version never shows a page cached by the old one (PageAssets).
    internal static readonly Uri StartPage = PageAssets.StartPage(AgentPaths.Version);
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
    private string? pendingActivity;
    private bool activityPostScheduled;
    private readonly object viewGate = new();
    // Messages for a page that has not said ready yet (a reveal, or an answer for File
    // Explorer's right-click): posted, in order, right after the view the ready gets.
    private readonly Queue<string> forReadyPage = new();
    private bool pageReady;
    // The last view the page got: an identical one is never posted again (N7).
    private readonly LastViewPosted lastView = new();
    // The WebView2-missing button's tooltip (N1).
    private ToolTip? runtimeTip;
    // A shared computer: a window left open overnight goes to the picker on the new day.
    private readonly System.Windows.Forms.Timer dayTimer = new() { Interval = 60_000 };

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
        host.ActivityChanged += OnActivityChanged;
        dayTimer.Tick += (_, _) => { if (Visible) host.ShowPicker(Armory.Core.PickerTrigger.Activated); };
        dayTimer.Start();
    }

    internal void Open()
    {
        // A computer shared by several students opens on the picker whenever the window was
        // hidden: closed with X, never shown, or started in the background (decision F6).
        if (!Visible) host.ShowPicker(Armory.Core.PickerTrigger.ShownFromHidden);
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

    // Brought to the front or restored: the picker only on the first open of a new day.
    protected override void OnActivated(EventArgs e)
    {
        base.OnActivated(e);
        host.ShowPicker(Armory.Core.PickerTrigger.Activated);
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
            host.ActivityChanged -= OnActivityChanged;
            dayTimer.Dispose();
            runtimeTip?.Dispose();
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
            await ForgetOldPageAsync(core);
            // index.html is served from here, never cached, with ?v=<version> on its scripts and
            // style sheets. When anything fails the folder mapping above serves it as is.
            core.AddWebResourceRequestedFilter("https://" + HostName + "/index.html*", CoreWebView2WebResourceContext.Document);
            // A file's thumbnail, as File Explorer shows it: https://armory.local/thumb/<vault path>.
            core.AddWebResourceRequestedFilter("https://" + HostName + ThumbPrefix + "*", CoreWebView2WebResourceContext.Image);
            // Send feedback's picture of this window, from memory: https://armory.local/shot/<id>.png.
            core.AddWebResourceRequestedFilter("https://" + HostName + WindowShots.Prefix + "*", CoreWebView2WebResourceContext.Image);
            core.WebResourceRequested += (_, args) =>
            {
                if (args.ResourceContext != CoreWebView2WebResourceContext.Image) ServeStartPage(core, args);
                else if (IsShotRequest(args.Request.Uri)) ServeShot(core, args);
                else ServeThumbnail(core, args);
            };
            core.NavigationStarting += (_, args) => KeepInsideApp(args.Uri, () => args.Cancel = true);
            // The page loads again: it is ready once it says so, and has no view until it gets one.
            core.NavigationStarting += (_, args) =>
            {
                if (!IsAppUri(args.Uri)) return;
                pageReady = false;
                lastView.Forget();
            };
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
                var ready = Bridge.TryRead(json, out var type, out System.Text.Json.JsonElement _) && type == BridgeMessages.Ready;
                // A page that says ready has no view yet: the one the bridge answers with always goes.
                if (ready) lastView.Forget();
                await bridge.HandleAsync(json, DroppedFiles(args));
                if (ready) PageReady();
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
                        pageReady = false;
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

    // Files the page sent along with a message (chrome.webview.postMessageWithAdditionalObjects):
    // only File objects cross, as CoreWebView2File with the full path on this computer.
    // Dropping files on the window never navigates: the page handles the drop, and a drop it
    // missed would be a file: navigation that KeepInsideApp cancels.
    private IReadOnlyList<string> DroppedFiles(CoreWebView2WebMessageReceivedEventArgs args)
    {
        try
        {
            var objects = args.AdditionalObjects;
            if (objects is null || objects.Count == 0) return [];
            List<string> files = [];
            foreach (var item in objects)
                if (item is CoreWebView2File file && !string.IsNullOrWhiteSpace(file.Path)) files.Add(file.Path);
            return files;
        }
        catch (Exception error) when (error is InvalidOperationException or System.Runtime.InteropServices.COMException or NotImplementedException)
        {
            log.Error("could not read the files sent with a window message", error);
            return [];
        }
    }

    internal const string ThumbPrefix = "/thumb/";

    // A thumbnail is made off the window's thread (ShellThumbnails); the request waits on a
    // deferral. A file with no picture answers 404, and the page keeps its own glyph.
    private async void ServeThumbnail(CoreWebView2 core, CoreWebView2WebResourceRequestedEventArgs args)
    {
        CoreWebView2Deferral? deferral = null;
        try
        {
            if (!IsAppUri(args.Request.Uri) || !Uri.TryCreate(args.Request.Uri, UriKind.Absolute, out var uri) ||
                !uri.AbsolutePath.StartsWith(ThumbPrefix, StringComparison.Ordinal)) return;
            var vaultPath = Uri.UnescapeDataString(uri.AbsolutePath[ThumbPrefix.Length..]);
            deferral = args.GetDeferral();
            var png = await host.ThumbnailAsync(vaultPath);
            args.Response = png is null
                ? core.Environment.CreateWebResourceResponse(null, 404, "Not Found", "Cache-Control: no-store")
                // The address carries the file's version (?v=), so a picture is kept as long as the page
                // asks for that address: a row drawn again shows it at once instead of its glyph (N7).
                : core.Environment.CreateWebResourceResponse(new MemoryStream(png), 200, "OK", "Content-Type: image/png\r\nCache-Control: private, max-age=31536000, immutable");
        }
        // An async void handler: nothing may escape it (it would end the app).
        catch (Exception error) when (error is not OutOfMemoryException)
        {
            log.Error("could not serve a thumbnail", error);
        }
        finally
        {
            try { deferral?.Complete(); }
            catch (Exception error) when (error is not OutOfMemoryException) { log.Error("could not finish a thumbnail answer", error); }
        }
    }

    private static bool IsShotRequest(string uri)
        => IsAppUri(uri) && Uri.TryCreate(uri, UriKind.Absolute, out var parsed) && parsed.AbsolutePath.StartsWith(WindowShots.Prefix, StringComparison.Ordinal);

    // Send feedback's picture, exactly the bytes that would be sent, from memory. no-store: the
    // WebView's disk cache never keeps a picture of the window. One the host no longer holds is 404.
    private void ServeShot(CoreWebView2 core, CoreWebView2WebResourceRequestedEventArgs args)
    {
        try
        {
            var path = new Uri(args.Request.Uri).AbsolutePath;
            var shot = host.Shots.Get(WindowShots.IdOfPath(path));
            args.Response = shot is null
                ? core.Environment.CreateWebResourceResponse(null, 404, "Not Found", "Cache-Control: no-store")
                : core.Environment.CreateWebResourceResponse(new MemoryStream(shot.Png, writable: false), 200, "OK", "Content-Type: image/png\r\nCache-Control: no-store");
        }
        catch (Exception error) when (error is ArgumentException or UriFormatException or InvalidOperationException or System.Runtime.InteropServices.COMException)
        {
            log.Error("could not serve the window's picture", error);
        }
    }

    // Send feedback's "Add a picture of this window" (the page has hidden its dialog and masked
    // what a picture must not show). CapturePreviewAsync is what this WebView draws: never the
    // screen, never another window. Over 2 MB, it is taken again smaller with the DevTools
    // protocol's Page.captureScreenshot (the same page, clip.scale), measured each time, at most
    // three times and never below a quarter. Held in memory only; nothing goes to disk.
    async Task<WindowCapture?> IBridgeWindow.CaptureWindowAsync(int cssWidth, int cssHeight)
    {
        if (web?.CoreWebView2 is not { } core) return null;
        try
        {
            byte[] png;
            using (var stream = new MemoryStream())
            {
                await core.CapturePreviewAsync(CoreWebView2CapturePreviewImageFormat.Png, stream);
                png = stream.ToArray();
            }
            var scale = 1.0;
            var scaled = false;
            for (var round = 0; round < 3 && png.LongLength > Armory.Client.FeedbackScreenshots.MaximumBytes; round++)
            {
                if (Armory.Client.ScreenshotFit.NextScale(png.LongLength, scale) is not { } next) break;
                var answer = await core.CallDevToolsProtocolMethodAsync("Page.captureScreenshot", WindowShots.CaptureParameters(cssWidth, cssHeight, next));
                using var document = System.Text.Json.JsonDocument.Parse(answer);
                png = Convert.FromBase64String(document.RootElement.GetProperty("data").GetString() ?? "");
                scale = next;
                scaled = true;
            }
            return new WindowCapture(png, scaled);
        }
        catch (Exception error) when (error is InvalidOperationException or System.Runtime.InteropServices.COMException or ArgumentException
            or System.Text.Json.JsonException or FormatException or KeyNotFoundException or IOException)
        {
            log.Error("could not take a picture of the window", error);
            return null;
        }
    }

    private void ServeStartPage(CoreWebView2 core, CoreWebView2WebResourceRequestedEventArgs args)
    {
        try
        {
            if (!IsAppUri(args.Request.Uri) || !Uri.TryCreate(args.Request.Uri, UriKind.Absolute, out var uri) ||
                !string.Equals(uri.AbsolutePath, "/index.html", StringComparison.OrdinalIgnoreCase)) return;
            var html = PageAssets.Versioned(File.ReadAllText(Path.Combine(AgentPaths.WebRoot, "index.html")), AgentPaths.Version);
            args.Response = core.Environment.CreateWebResourceResponse(new MemoryStream(System.Text.Encoding.UTF8.GetBytes(html)), 200, "OK",
                "Content-Type: text/html; charset=utf-8\r\nCache-Control: no-store");
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException or InvalidOperationException or System.Runtime.InteropServices.COMException)
        {
            log.Error("could not serve the start page; the folder mapping serves it instead", error);
        }
    }

    // The WebView2 profile outlives an upgrade. The first start of a new version empties the
    // HTTP cache once, so not even a file the page loads without ?v= can come from the old one.
    private async Task ForgetOldPageAsync(CoreWebView2 core)
    {
        var marker = Path.Combine(paths.WebView2Folder, "page-version.txt");
        try
        {
            var seen = File.Exists(marker) ? File.ReadAllText(marker).Trim() : null;
            if (string.Equals(seen, AgentPaths.Version, StringComparison.Ordinal)) return;
            await core.Profile.ClearBrowsingDataAsync(CoreWebView2BrowsingDataKinds.DiskCache | CoreWebView2BrowsingDataKinds.CacheStorage);
            File.WriteAllText(marker, AgentPaths.Version);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or InvalidOperationException or System.Runtime.InteropServices.COMException)
        {
            log.Error("could not clear the window's cache after an update", error);
        }
    }

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
        if (web?.CoreWebView2 is { } core && lastView.Take(json))
        {
            try { core.PostWebMessageAsJson(json); }
            catch (Exception error) when (error is InvalidOperationException or System.Runtime.InteropServices.COMException) { }
        }
        var theme = host.EffectiveTheme;
        BackColor = Background(theme);
        if (web is not null) web.DefaultBackgroundColor = Background(theme);
    }

    // What is moving right now (at most four a second, from the engine's timer thread): posted on
    // its own as an 'activity' message, newest only, and never with a whole view. The page patches
    // its activity panel and status line in place (app.js patchActivity).
    private void OnActivityChanged(ActivityView activity)
    {
        var json = BridgeMessages.ActivityMessage(activity);
        lock (viewGate)
        {
            pendingActivity = json;
            if (activityPostScheduled) return;
            activityPostScheduled = true;
        }
        if (!IsHandleCreated || IsDisposed)
        {
            lock (viewGate) activityPostScheduled = false;
            return;
        }
        try { BeginInvoke(FlushActivity); }
        catch (InvalidOperationException) { lock (viewGate) activityPostScheduled = false; }
    }

    private void FlushActivity()
    {
        string? json;
        lock (viewGate)
        {
            json = pendingActivity;
            pendingActivity = null;
            activityPostScheduled = false;
        }
        if (json is null || web?.CoreWebView2 is not { } core) return;
        try { core.PostWebMessageAsJson(json); }
        catch (Exception error) when (error is InvalidOperationException or System.Runtime.InteropServices.COMException) { }
    }

    // On the window thread: now when the page is ready, else right after its ready.
    internal void PostWhenReady(string json)
    {
        if (pageReady && web?.CoreWebView2 is not null)
        {
            ((IBridgeWindow)this).Post(json);
            return;
        }
        forReadyPage.Enqueue(json);
        while (forReadyPage.Count > 16) forReadyPage.Dequeue();
    }

    private void PageReady()
    {
        pageReady = true;
        while (forReadyPage.TryDequeue(out var json)) ((IBridgeWindow)this).Post(json);
    }

    void IBridgeWindow.Post(string json)
    {
        if (web?.CoreWebView2 is not { } core || !lastView.Take(json)) return;
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

    IReadOnlyList<string>? IBridgeWindow.ChooseFiles(string title)
    {
        using var dialog = new OpenFileDialog
        {
            Title = title,
            Multiselect = true,
            CheckFileExists = true,
            CheckPathExists = true,
            // A shortcut picks the file it points to; Armory never copies links themselves.
            DereferenceLinks = true,
            RestoreDirectory = true,
            Filter = "SolidWorks files (*.sldprt;*.sldasm;*.slddrw)|*.sldprt;*.sldasm;*.slddrw|All files (*.*)|*.*",
            FilterIndex = 2,
            InitialDirectory = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
        };
        return dialog.ShowDialog(this) == DialogResult.OK && dialog.FileNames.Length > 0 ? dialog.FileNames : null;
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
        runtimeTip?.Dispose();
        runtimeTip = new ToolTip();
        runtimeTip.SetToolTip(button, HostTips.GetWebView2);
        panel.Controls.Add(new Panel { Height = 1 }, 0, 0);
        panel.Controls.Add(message, 0, 1);
        panel.Controls.Add(button, 0, 2);
        Controls.Add(panel);
        AcceptButton = button;
    }
}
