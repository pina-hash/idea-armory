using System.Text.Json;
using Armory.Agent.Engine.View;

namespace Armory.Agent;

// Page-to-host messages (docs/agent/BRIDGE.md). Only these fields are ever read.
internal sealed record OpenFileMessage(string? FileId);
internal sealed record ShowInFolderMessage(string? Path);
internal sealed record SaveSettingsMessage(string? VaultRoot, bool? StartAtSignIn, string? Theme);

// What the bridge needs from the window it lives in. Every member runs on the UI thread.
internal interface IBridgeWindow
{
    void Post(string json);
    string? ChooseFolder(string current);
    // Add files: the Windows file picker, several files at once. Null when the student cancels.
    // Called once the bridge carries addFiles (the integration with the v2 engine).
    IReadOnlyList<string>? ChooseFiles(string title);
    void ShowProblem(string message);
}

// Dispatches exactly BridgeMessages.PageToHost. Unknown or malformed messages are ignored.
// Nothing from the page reaches a process without validation: showInFolder resolves only
// a valid VaultPath inside the vault root, openVault opens only the configured root.
internal sealed class Bridge(AgentHost host, IBridgeWindow window, AgentLog log)
{
    internal static bool TryRead(string webMessageJson, out string type, out JsonElement message)
    {
        type = string.Empty;
        message = default;
        try
        {
            using var document = JsonDocument.Parse(webMessageJson);
            var root = document.RootElement;
            // window.chrome.webview.postMessage(object) arrives as JSON; a page that posts
            // JSON.stringify(object) arrives as a JSON string holding that JSON.
            if (root.ValueKind == JsonValueKind.String)
            {
                using var inner = JsonDocument.Parse(root.GetString() ?? string.Empty);
                return TryRead(inner.RootElement, out type, out message);
            }
            return TryRead(root, out type, out message);
        }
        catch (JsonException) { return false; }
    }

    private static bool TryRead(JsonElement root, out string type, out JsonElement message)
    {
        type = string.Empty;
        message = default;
        if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("type", out var t) || t.ValueKind != JsonValueKind.String) return false;
        type = t.GetString() ?? string.Empty;
        message = root.Clone();
        return true;
    }

    // files: the full paths of File objects the page sent with the message
    // (chrome.webview.postMessageWithAdditionalObjects, read by MainWindow from
    // CoreWebView2WebMessageReceivedEventArgs.AdditionalObjects), such as files dropped on the
    // window. Empty for every other message. Only the message that adds dropped files (handled
    // once the engine can add files) may use them, and only through CopyIn's own checks.
    internal async Task HandleAsync(string webMessageJson, IReadOnlyList<string>? files = null)
    {
        if (!TryRead(webMessageJson, out var type, out var message)) return;
        try
        {
            switch (type)
            {
                case BridgeMessages.Ready:
                    PostView();
                    break;
                case BridgeMessages.Connect:
                    await host.ConnectAsync();
                    break;
                case BridgeMessages.CancelConnect:
                    host.CancelConnect();
                    break;
                case BridgeMessages.SignOut:
                    host.SignOut();
                    break;
                case BridgeMessages.Pause:
                    host.Pause();
                    break;
                case BridgeMessages.Resume:
                    host.Resume();
                    break;
                case BridgeMessages.OpenVault:
                    OpenVault();
                    break;
                case BridgeMessages.OpenFile:
                    await OpenFileAsync(Read<OpenFileMessage>(message));
                    break;
                case BridgeMessages.ShowInFolder:
                    ShowInFolder(Read<ShowInFolderMessage>(message));
                    break;
                case BridgeMessages.SaveSettings:
                    await SaveSettingsAsync(Read<SaveSettingsMessage>(message));
                    break;
                case BridgeMessages.ChooseVaultRoot:
                    await ChooseVaultRootAsync();
                    break;
                default:
                    return;
            }
        }
        catch (Exception error) when (error is not OutOfMemoryException)
        {
            log.Error("window message " + type + " failed", error);
        }
    }

    private static T? Read<T>(JsonElement message) where T : class
    {
        try { return message.Deserialize<T>(BridgeMessages.Json); }
        catch (JsonException) { return null; }
    }

    private void PostView() => window.Post(BridgeMessages.ViewMessage(host.View));

    private void OpenVault()
    {
        var root = host.Settings.VaultRoot;
        Directory.CreateDirectory(root);
        Shell.OpenFolder(root);
    }

    private async Task OpenFileAsync(OpenFileMessage? request)
    {
        if (request is null || !Guid.TryParse(request.FileId, out var fileId)) return;
        var detail = await host.GetFileDetailAsync(fileId);
        if (detail is not null) window.Post(BridgeMessages.DetailMessage(detail));
    }

    private void ShowInFolder(ShowInFolderMessage? request)
    {
        var root = host.Settings.VaultRoot;
        if (request is null || !VaultLocator.TryResolve(root, request.Path, out var file)) return;
        if (File.Exists(file)) { Shell.SelectInExplorer(file!); return; }
        // Not on this computer yet: open the nearest folder that exists inside the vault.
        for (var folder = Path.GetDirectoryName(file); folder is not null; folder = Path.GetDirectoryName(folder))
        {
            var plain = Shell.Plain(folder);
            if (!plain.StartsWith(root, StringComparison.OrdinalIgnoreCase)) break;
            if (Directory.Exists(folder)) { Shell.OpenFolder(folder); return; }
        }
    }

    private async Task SaveSettingsAsync(SaveSettingsMessage? request)
    {
        if (request is null) return;
        var current = host.Settings;
        var problem = await host.SaveSettingsAsync(request.VaultRoot ?? current.VaultRoot, request.StartAtSignIn ?? current.StartAtSignIn, request.Theme ?? current.Theme);
        if (problem is not null) window.ShowProblem(problem);
        PostView();
    }

    private async Task ChooseVaultRootAsync()
    {
        var current = host.Settings;
        var chosen = window.ChooseFolder(current.VaultRoot);
        if (chosen is not null && !string.Equals(chosen, current.VaultRoot, StringComparison.OrdinalIgnoreCase))
        {
            var problem = await host.SaveSettingsAsync(chosen, current.StartAtSignIn, current.Theme);
            if (problem is not null) window.ShowProblem(problem);
        }
        PostView();
    }
}
