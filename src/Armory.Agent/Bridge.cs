using System.Text.Json;
using Armory.Agent.Engine.View;
using Armory.Core;

namespace Armory.Agent;

// Page-to-host messages (docs/agent/BRIDGE.md). Only these fields are ever read, and each
// record names exactly the fields wwwroot/bridge.js sends for its type (REQUIRED, plus the
// requestId of an action); AgentViewContractTests holds the two to each other.
internal sealed record OpenFileMessage(string? FileId);
internal sealed record LaunchFileMessage(string? Path, string? RequestId);
internal sealed record ShowInFolderMessage(string? Path);
internal sealed record CheckOutMessage(IReadOnlyList<string>? Paths, bool? Open, string? RequestId);
internal sealed record CheckInMessage(IReadOnlyList<string>? Paths, string? RequestId);
internal sealed record UndoCheckOutMessage(IReadOnlyList<string>? Paths, string? RequestId);
internal sealed record TakeBackMessage(string? FileId, string? RequestId);
// Force check in of many files in one action (0.3.1): Force check in all, the selection bar.
internal sealed record TakeBackAllMessage(IReadOnlyList<string>? FileIds, string? RequestId);
internal sealed record CreateFolderMessage(string? ProjectId, string? Parent, string? Name, string? RequestId);
internal sealed record RenameFolderMessage(string? ProjectId, string? Folder, string? NewName, string? RequestId);
internal sealed record DeleteFolderMessage(string? ProjectId, string? Folder, string? RequestId);
internal sealed record RenameFileMessage(string? Path, string? NewName, string? RequestId);
internal sealed record AddFilesMessage(string? ProjectId, string? Folder, string? RequestId);
internal sealed record DropFilesMessage(string? ProjectId, string? Folder, string? RequestId);
internal sealed record DismissNoticeMessage(string? Key);
internal sealed record SaveSettingsMessage(string? VaultRoot, bool? StartAtSignIn, string? Theme);
// Report a problem: kind is bug, idea or other; body is what the person wrote.
internal sealed record ReportProblemMessage(string? Kind, string? Body, string? RequestId);
// Send feedback (v0.3): kind is bug, idea or other; body is what the person wrote. A note on its own.
internal sealed record SendFeedbackMessage(string? Kind, string? Body, string? RequestId);

// What the bridge needs from the window it lives in. Every member runs on the UI thread.
internal interface IBridgeWindow
{
    void Post(string json);
    string? ChooseFolder(string current);
    // Add files: the Windows file picker, several files at once. Null when the student cancels.
    IReadOnlyList<string>? ChooseFiles(string title);
    void ShowProblem(string message);
}

// Dispatches exactly BridgeMessages.PageToHost, one case each (AgentViewContractTests checks
// that no type is left to the default). Unknown or malformed messages are ignored; an
// action (it carries a requestId) is always answered with one actionResult, a refusal
// included. Nothing from the page reaches a process without validation: showInFolder
// resolves only a valid VaultPath inside the vault root, openVault opens only the
// configured root, and every path, folder, name and id of an action is checked here
// before the host sees it.
internal sealed class Bridge(AgentHost host, IBridgeWindow window, AgentLog log)
{
    // Every page-to-host type and the record its fields are read into (null: no fields).
    internal static readonly IReadOnlyDictionary<string, Type?> MessageRecords = new Dictionary<string, Type?>(StringComparer.Ordinal)
    {
        [BridgeMessages.Ready] = null,
        [BridgeMessages.Connect] = null,
        [BridgeMessages.CancelConnect] = null,
        [BridgeMessages.SignOut] = null,
        [BridgeMessages.Pause] = null,
        [BridgeMessages.Resume] = null,
        [BridgeMessages.OpenVault] = null,
        [BridgeMessages.OpenFile] = typeof(OpenFileMessage),
        [BridgeMessages.LaunchFile] = typeof(LaunchFileMessage),
        [BridgeMessages.ShowInFolder] = typeof(ShowInFolderMessage),
        [BridgeMessages.CheckOut] = typeof(CheckOutMessage),
        [BridgeMessages.CheckIn] = typeof(CheckInMessage),
        [BridgeMessages.UndoCheckOut] = typeof(UndoCheckOutMessage),
        [BridgeMessages.TakeBack] = typeof(TakeBackMessage),
        [BridgeMessages.CreateFolder] = typeof(CreateFolderMessage),
        [BridgeMessages.RenameFolder] = typeof(RenameFolderMessage),
        [BridgeMessages.DeleteFolder] = typeof(DeleteFolderMessage),
        [BridgeMessages.RenameFile] = typeof(RenameFileMessage),
        [BridgeMessages.AddFiles] = typeof(AddFilesMessage),
        [BridgeMessages.DropFiles] = typeof(DropFilesMessage),
        [BridgeMessages.DismissNotice] = typeof(DismissNoticeMessage),
        [BridgeMessages.SaveSettings] = typeof(SaveSettingsMessage),
        [BridgeMessages.ChooseVaultRoot] = null,
        [BridgeMessages.ReportProblem] = typeof(ReportProblemMessage),
        [BridgeMessages.OpenIncidents] = null,
        [BridgeMessages.SendFeedback] = typeof(SendFeedbackMessage),
        [BridgeMessages.TakeBackAll] = typeof(TakeBackAllMessage),
    };

    // The answer to an action the window sent with something unusable in it.
    private static readonly ActionResult NotAFile = new(false, "That isn't a file or folder in your Armory folder.");
    // The most files one Force check in all names (a whole project's worth).
    private const int MaximumTakeBack = 20_000;
    private static readonly ActionResult NotAProject = new(false, "That project isn't on this computer.");
    private static readonly ActionResult NotAName = new(false, "That name can't be used for a folder.");
    private static readonly ActionResult NotAFileName = new(false, "That name can't be used for a file.");
    // The picker was closed without choosing: no sentence (the page shows none for an empty one).
    private static readonly ActionResult Nothing = new(false, "");

    private static string AddTitle(string folder) => folder.Length == 0 ? "Add files to Armory" : "Add files to " + folder[(folder.LastIndexOf('/') + 1)..];

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
    // CoreWebView2WebMessageReceivedEventArgs.AdditionalObjects): the files and folders dropped
    // on the window. Empty for every other message. Only dropFiles uses them, and only through
    // the engine's AddFilesAsync and the file system's CopyIn checks (a folder is copied whole).
    internal async Task HandleAsync(string webMessageJson, IReadOnlyList<string>? files = null)
    {
        if (!TryRead(webMessageJson, out var type, out var message)) return;
        // When the window asked: an action's answer is timed from here (the flight recorder's
        // window actions, and the slowAction rule over 10 seconds).
        var asked = host.Telemetry.Recorder.Now();
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
                case BridgeMessages.LaunchFile:
                    var launch = Read<LaunchFileMessage>(message);
                    await AnswerAsync(type, 1, asked, launch?.RequestId, TryPath(launch?.Path, out var file) ? host.LaunchFileAsync(file) : Refuse(NotAFile));
                    break;
                case BridgeMessages.ShowInFolder:
                    ShowInFolder(Read<ShowInFolderMessage>(message));
                    break;
                case BridgeMessages.CheckOut:
                    var checkOut = Read<CheckOutMessage>(message);
                    await AnswerAsync(type, checkOut?.Paths?.Count ?? 0, asked, checkOut?.RequestId, TryPaths(checkOut?.Paths, out var outPaths) ? host.CheckOutAsync(outPaths, checkOut!.Open == true) : Refuse(NotAFile));
                    break;
                case BridgeMessages.CheckIn:
                    var checkIn = Read<CheckInMessage>(message);
                    await AnswerAsync(type, checkIn?.Paths?.Count ?? 0, asked, checkIn?.RequestId, TryPaths(checkIn?.Paths, out var inPaths) ? host.CheckInAsync(inPaths) : Refuse(NotAFile));
                    break;
                case BridgeMessages.UndoCheckOut:
                    var undo = Read<UndoCheckOutMessage>(message);
                    await AnswerAsync(type, undo?.Paths?.Count ?? 0, asked, undo?.RequestId, TryPaths(undo?.Paths, out var undoPaths) ? host.UndoCheckOutAsync(undoPaths) : Refuse(NotAFile));
                    break;
                case BridgeMessages.TakeBack:
                    var takeBack = Read<TakeBackMessage>(message);
                    await AnswerAsync(type, 1, asked, takeBack?.RequestId, Guid.TryParse(takeBack?.FileId, out var taken) ? host.TakeBackAsync(taken) : Refuse(NotAFile));
                    break;
                case BridgeMessages.TakeBackAll:
                    var takeAll = Read<TakeBackAllMessage>(message);
                    var takeIds = new List<Guid>();
                    var allIds = takeAll?.FileIds is { Count: > 0 and <= MaximumTakeBack };
                    foreach (var s in allIds ? takeAll!.FileIds! : [])
                    {
                        if (!Guid.TryParse(s, out var g)) { allIds = false; break; }
                        takeIds.Add(g);
                    }
                    await AnswerAsync(type, takeIds.Count, asked, takeAll?.RequestId, allIds ? host.TakeBackAsync(takeIds) : Refuse(NotAFile));
                    break;
                case BridgeMessages.CreateFolder:
                    var create = Read<CreateFolderMessage>(message);
                    await AnswerAsync(type, 1, asked, create?.RequestId,
                        !Guid.TryParse(create?.ProjectId, out var createIn) ? Refuse(NotAProject)
                        : !TryFolder(create!.Parent, allowTop: true, out var parent) ? Refuse(NotAFile)
                        : !TryName(create.Name, out var newFolder) ? Refuse(NotAName)
                        : host.CreateFolderAsync(createIn, parent, newFolder));
                    break;
                case BridgeMessages.RenameFolder:
                    var rename = Read<RenameFolderMessage>(message);
                    await AnswerAsync(type, 1, asked, rename?.RequestId,
                        !Guid.TryParse(rename?.ProjectId, out var renameIn) ? Refuse(NotAProject)
                        : !TryFolder(rename!.Folder, allowTop: false, out var renamed) ? Refuse(NotAFile)
                        : !TryName(rename.NewName, out var newName) ? Refuse(NotAName)
                        : host.RenameFolderAsync(renameIn, renamed, newName));
                    break;
                case BridgeMessages.DeleteFolder:
                    var delete = Read<DeleteFolderMessage>(message);
                    await AnswerAsync(type, 1, asked, delete?.RequestId,
                        !Guid.TryParse(delete?.ProjectId, out var deleteIn) ? Refuse(NotAProject)
                        : !TryFolder(delete!.Folder, allowTop: false, out var deleted) ? Refuse(NotAFile)
                        : host.DeleteFolderAsync(deleteIn, deleted));
                    break;
                case BridgeMessages.RenameFile:
                    var renameFile = Read<RenameFileMessage>(message);
                    await AnswerAsync(type, 1, asked, renameFile?.RequestId,
                        !TryPath(renameFile?.Path, out var renamedFile) ? Refuse(NotAFile)
                        : !TryName(renameFile!.NewName, out var newFileName) ? Refuse(NotAFileName)
                        : host.RenameFileAsync(renamedFile, newFileName));
                    break;
                case BridgeMessages.AddFiles:
                    // The Windows file picker chooses the files (on this, the window's thread);
                    // closing it adds nothing and says nothing.
                    // The time the student spends in the picker is not the app's: the answer is
                    // timed from when the picker closes.
                    var add = Read<AddFilesMessage>(message);
                    if (!Guid.TryParse(add?.ProjectId, out var addIn)) await AnswerAsync(type, 0, asked, add?.RequestId, Refuse(NotAProject));
                    else if (!TryFolder(add!.Folder, allowTop: true, out var addTo)) await AnswerAsync(type, 0, asked, add.RequestId, Refuse(NotAFile));
                    else
                    {
                        var chosen = window.ChooseFiles(AddTitle(addTo));
                        await AnswerAsync(type, chosen?.Count ?? 0, host.Telemetry.Recorder.Now(), add.RequestId,
                            chosen is { Count: > 0 } ? host.AddFilesAsync(addIn, addTo, chosen) : Refuse(Nothing));
                    }
                    break;
                case BridgeMessages.DropFiles:
                    var drop = Read<DropFilesMessage>(message);
                    await AnswerAsync(type, files?.Count ?? 0, asked, drop?.RequestId,
                        !Guid.TryParse(drop?.ProjectId, out var dropIn) ? Refuse(NotAProject)
                        : !TryFolder(drop!.Folder, allowTop: true, out var dropTo) ? Refuse(NotAFile)
                        : host.AddFilesAsync(dropIn, dropTo, files ?? []));
                    break;
                case BridgeMessages.DismissNotice:
                    var dismiss = Read<DismissNoticeMessage>(message);
                    if (!string.IsNullOrWhiteSpace(dismiss?.Key)) host.DismissNotice(dismiss.Key);
                    break;
                case BridgeMessages.SaveSettings:
                    await SaveSettingsAsync(Read<SaveSettingsMessage>(message));
                    break;
                case BridgeMessages.ChooseVaultRoot:
                    await ChooseVaultRootAsync();
                    break;
                case BridgeMessages.ReportProblem:
                    var report = Read<ReportProblemMessage>(message);
                    await AnswerAsync(type, 0, asked, report?.RequestId, host.ReportProblemAsync(report?.Kind, report?.Body));
                    break;
                case BridgeMessages.SendFeedback:
                    var note = Read<SendFeedbackMessage>(message);
                    await AnswerAsync(type, 0, asked, note?.RequestId, host.SendFeedbackAsync(note?.Kind, note?.Body));
                    break;
                case BridgeMessages.OpenIncidents:
                    // The incidents folder, so a person can hand the files over by hand today.
                    Directory.CreateDirectory(host.Telemetry.IncidentsFolder);
                    Shell.OpenFolder(host.Telemetry.IncidentsFolder);
                    break;
                default:
                    log.Info("ignored a window message of unknown type " + type);
                    return;
            }
        }
        catch (Exception error) when (error is not OutOfMemoryException)
        {
            log.Error("window message " + type + " failed", error);
            host.Telemetry.Recorder.Exception("window message " + type, error);
        }
    }

    private static T? Read<T>(JsonElement message) where T : class
    {
        try { return message.Deserialize<T>(BridgeMessages.Json); }
        catch (JsonException) { return null; }
    }

    private void PostView() => window.Post(BridgeMessages.ViewMessage(host.View));

    // Every action gets exactly one actionResult, with its requestId, even when it fails. How
    // long the window waited for it goes into the flight recorder.
    private async Task AnswerAsync(string type, int targets, long asked, string? requestId, Task<ActionResult> work)
    {
        ActionResult result;
        try { result = await work; }
        catch (Exception error) when (error is not OutOfMemoryException)
        {
            log.Error("a window action failed", error);
            host.Telemetry.Recorder.Exception("window action " + type, error);
            result = new ActionResult(false, "Armory couldn't do that. Try again in a moment.");
        }
        window.Post(BridgeMessages.ActionResultMessage(requestId, result.Ok, result.Message));
        var recorder = host.Telemetry.Recorder;
        recorder.WindowAction(type, targets, recorder.MillisecondsSince(asked), result.Ok);
    }

    private static Task<ActionResult> Refuse(ActionResult why) => Task.FromResult(why);

    // A vault-relative path from the page ("Robot 2027/Drivetrain/Plate.SLDPRT"): a file, or
    // a folder meaning every file under it.
    private static bool TryPath(string? text, out string path)
    {
        path = string.Empty;
        if (!VaultPath.TryCreate(text, out var valid, out _)) return false;
        path = valid.Value;
        return true;
    }

    private static bool TryPaths(IReadOnlyList<string>? texts, out IReadOnlyList<string> paths)
    {
        paths = [];
        if (texts is null || texts.Count == 0) return false;
        List<string> clean = [];
        foreach (var text in texts)
        {
            if (!TryPath(text, out var path)) return false;
            clean.Add(path);
        }
        paths = clean;
        return true;
    }

    // A folder inside a project ("Drivetrain/Gearbox"); "" is the project's top folder.
    private static bool TryFolder(string? text, bool allowTop, out string folder)
    {
        folder = string.Empty;
        if (text is null) return false;
        if (text.Length == 0) return allowTop;
        return TryPath(text, out folder);
    }

    private static bool TryName(string? text, out string name)
    {
        name = text?.Trim() ?? string.Empty;
        return VaultPath.TryValidateName(name, out _);
    }

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
