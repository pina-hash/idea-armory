using System.Text.Json;
using System.Text.Json.Serialization;

namespace Armory.Agent.Engine.View;

// The window's whole picture (docs/agent/BRIDGE.md, v2-design.md 4.6). The page renders from
// this alone. AgentViewContractTests holds every record here to the fields wwwroot/bridge.js
// documents; data objects use kind and direction, never a type field.
public sealed record AgentView(
    string Connection,
    ConnectView Connect,
    AccountView? Account,
    SyncView Sync,
    ActivityView Activity,
    string VaultRoot,
    IReadOnlyList<NoticeGroupView> Notices,
    PromptView? Prompt,
    IReadOnlyList<MyFileView> MyFiles,
    IReadOnlyList<ProjectView> Projects,
    SettingsView Settings,
    string EffectiveTheme);

public sealed record ConnectView(string Phase, string? Message);
public sealed record AccountView(string Email, string DeviceName);
public sealed record SyncView(string State, string Line, string? Detail, int PendingCount);
// The files THIS computer has checked out, in any project (archived ones too, so they can
// always be checked in).
public sealed record MyFileView(string? FileId, string Path, string Name, string Project, string Status, string? Note, CheckoutView Checkout);
public sealed record ProjectView(string Id, string Name, bool Archived, string Role, bool CanTakeBack, IReadOnlyList<FolderView> Folders);
// Path is in the project ("" for its top folder); FileCount counts the files directly in it.
public sealed record FolderView(string Path, string Name, int FileCount, IReadOnlyList<FileRowView> Files);
public sealed record FileRowView(string? FileId, string Name, string Path, string Status, CheckoutView Checkout, bool Changed, bool ReleaseNotChecked,
    string? UpdatedAt, string? UpdatedBy);
public sealed record SettingsView(string VaultRoot, bool StartAtSignIn, string Theme);
public sealed record FileDetailView(string FileId, string Name, string Path, string Project, string Folder, string Status, CheckoutView Checkout,
    bool ReleaseNotChecked, bool CanTakeBack, IReadOnlyList<HistoryEntryView> History);
public sealed record HistoryEntryView(string Id, string Kind, string Author, string At, long Bytes, string Note, bool ReleaseNotChecked, bool IsCurrent);
// Who has a file checked out. Label is always set: "Checked out by you", "Checked out by Maria
// Lopez on LAB-PC-07", "Checked out by you on LAB-PC-07" (my other computer) or "Available".
public sealed record CheckoutView(string State, string Label, string? Name, string? Email, string? Device, string? Since);
// SolidWorks opened a file this computer has not checked out. Key is one per open
// ("prompt:<path>:<when the open was first seen>"); dismissNotice with it hides that one only.
public sealed record PromptView(string Key, string? FileId, string Path, string Name, CheckoutView Checkout, bool CanCheckOut);
public sealed record ActivityView(string? Line, DirectionView? Upload, DirectionView? Download, DirectionView? Move, WaitingView? Waiting,
    IReadOnlyList<ActiveTransferView> Active);
public sealed record DirectionView(int FilesDone, int FilesTotal, long BytesDone, long BytesTotal, long BytesPerSecond, int? SecondsLeft, string Line);
public sealed record WaitingView(int Count, string Line);
public sealed record ActiveTransferView(string Path, string Name, string Direction, long BytesDone, long BytesTotal);
public sealed record NoticeGroupView(string Key, string Kind, string Tone, string Title, string Detail, int Count, NoticeActionView? Action,
    IReadOnlyList<NoticeItemView> Items);
public sealed record NoticeActionView(string Label, string Command, IReadOnlyList<string> Paths);
public sealed record NoticeItemView(string? FileId, string Path, string Name, string? Detail);

// What an action from the window came to, in one plain sentence (v2-design.md 4.2):
// "Checked in Plate.SLDPRT.", "Close Plate.SLDPRT in SolidWorks first."
public sealed record ActionResult(bool Ok, string Message);

public static class Connections
{
    public const string SignedOut = "signedOut", Connecting = "connecting", SignedIn = "signedIn", VaultOwnedByOther = "vaultOwnedByOther";
}
public static class SyncStates
{
    public const string Synced = "synced", Syncing = "syncing", Offline = "offline", Paused = "paused", Attention = "attention";
}
public static class FileStatuses
{
    public const string Synced = "synced", Changed = "changed", Uploading = "uploading", Downloading = "downloading", Waiting = "waiting",
        NewerWaiting = "newerWaiting", KeptCopy = "keptCopy", NotInArmory = "notInArmory", NotOnThisComputer = "notOnThisComputer";
    public static readonly IReadOnlySet<string> All = new HashSet<string>(StringComparer.Ordinal)
        { Synced, Changed, Uploading, Downloading, Waiting, NewerWaiting, KeptCopy, NotInArmory, NotOnThisComputer };
}
public static class CheckoutStates
{
    public const string Available = "available", Mine = "mine", Other = "other", MyOtherComputer = "myOtherComputer";
}
public static class Directions
{
    public const string Upload = "upload", Download = "download", Move = "move";
}
public static class NoticeTones
{
    public const string Info = "info", Look = "look", Bad = "bad";
}
public static class NoticeKinds
{
    public const string Import = "import", NameShared = "nameShared", NewerWaiting = "newerWaiting", KeptCopy = "keptCopy", TakenBack = "takenBack",
        FolderPutBack = "folderPutBack", ProjectPutBack = "projectPutBack", ProjectRenaming = "projectRenaming", CantSend = "cantSend",
        CantRead = "cantRead", CheckInPartial = "checkInPartial";
}
public static class HistoryKinds
{
    public const string Version = "version", KeptCopy = "keptCopy", Removed = "removed";
}

// Message names on the bridge, both directions. AgentViewContractTests keeps
// wwwroot/bridge.js in step with these lists.
public static class BridgeMessages
{
    public const string View = "view", FileDetail = "fileDetail", Activity = "activity", ActionResult = "actionResult";
    public static readonly IReadOnlyList<string> HostToPage = [View, FileDetail, Activity, ActionResult];
    public const string Ready = "ready", Connect = "connect", CancelConnect = "cancelConnect", SignOut = "signOut", Pause = "pause", Resume = "resume",
        OpenVault = "openVault", OpenFile = "openFile", LaunchFile = "launchFile", ShowInFolder = "showInFolder", CheckOut = "checkOut", CheckIn = "checkIn",
        UndoCheckOut = "undoCheckOut", TakeBack = "takeBack", CreateFolder = "createFolder", RenameFolder = "renameFolder", DeleteFolder = "deleteFolder",
        AddFiles = "addFiles", DropFiles = "dropFiles", DismissNotice = "dismissNotice", SaveSettings = "saveSettings", ChooseVaultRoot = "chooseVaultRoot";
    public static readonly IReadOnlyList<string> PageToHost = [Ready, Connect, CancelConnect, SignOut, Pause, Resume, OpenVault, OpenFile, LaunchFile, ShowInFolder,
        CheckOut, CheckIn, UndoCheckOut, TakeBack, CreateFolder, RenameFolder, DeleteFolder, AddFiles, DropFiles, DismissNotice, SaveSettings, ChooseVaultRoot];

    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
    };
    public static string ViewMessage(AgentView view) => JsonSerializer.Serialize(new { type = View, view }, Json);
    public static string DetailMessage(FileDetailView detail) => JsonSerializer.Serialize(new { type = FileDetail, detail }, Json);
    public static string ActivityMessage(ActivityView activity) => JsonSerializer.Serialize(new { type = Activity, activity }, Json);
    // The one answer to an action: the action's requestId comes back with it.
    public static string ActionResultMessage(string? requestId, bool ok, string message)
        => JsonSerializer.Serialize(new { type = ActionResult, requestId, ok, message }, Json);
}
