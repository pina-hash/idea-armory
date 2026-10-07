using System.Text.Json;
using System.Text.Json.Serialization;

namespace Armory.Agent.Engine.View;

// The window's whole picture (docs/agent/BRIDGE.md). The page renders from this alone.
public sealed record AgentView(
    string Connection,
    ConnectView Connect,
    AccountView? Account,
    SyncView Sync,
    string VaultRoot,
    IReadOnlyList<MyFileView> MyFiles,
    IReadOnlyList<AttentionView> NeedsMe,
    IReadOnlyList<ProjectView> Projects,
    SettingsView Settings,
    string EffectiveTheme);

public sealed record ConnectView(string Phase, string? Message);
public sealed record AccountView(string Email, string DeviceName);
public sealed record SyncView(string State, string Line, string? Detail, int PendingCount);
public sealed record MyFileView(string? FileId, string Path, string Name, string Project, string Status, string? Note);
public sealed record AttentionView(string Kind, string? FileId, string Path, string Name, string Title, string Detail, string? At);
public sealed record ProjectView(string Id, string Name, IReadOnlyList<FolderView> Folders);
public sealed record FolderView(string Path, string Name, IReadOnlyList<FileRowView> Files);
public sealed record FileRowView(string FileId, string Name, string Path, string Status, HolderView? Holder, bool ReleaseNotChecked, string? UpdatedAt, string? UpdatedBy);
public sealed record HolderView(string Name, string Email, string Device, string Since, bool IsMe, bool IsMyOtherComputer, bool SavedToArmory);
public sealed record SettingsView(string VaultRoot, bool StartAtSignIn, string Theme);
public sealed record FileDetailView(string FileId, string Name, string Path, string Project, string Folder, string Status, HolderView? Holder,
    bool ReleaseNotChecked, IReadOnlyList<HistoryEntryView> History);
public sealed record HistoryEntryView(string Id, string Kind, string Author, string At, long Bytes, string Note, bool ReleaseNotChecked, bool IsCurrent);

// v2 records the page already reads (docs/agent/BRIDGE.md, v2-design.md 4.6). The engine
// lane puts them into AgentView (activity, notices, prompt, every row's checkout) when it
// rebuilds the records above to v2; until then they serialize on their own messages.
// AgentViewContractTests holds every record here to the fields wwwroot/bridge.js names.
public sealed record CheckoutView(string State, string Label, string? Name, string? Email, string? Device, string? Since);
// Key: one per open of the file in SolidWorks ("prompt:<path>:<marker first seen, ISO-8601>"), so
// Not now (dismissNotice with this key) hides this one question and the next open asks again.
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
    public const string Synced = "synced", Syncing = "syncing", WaitingToSend = "waitingToSend", EditingByMe = "editingByMe",
        EditingByOther = "editingByOther", NewerWaiting = "newerWaiting", Conflict = "conflict", Refused = "refused", NotOnThisComputer = "notOnThisComputer";
    public static readonly IReadOnlySet<string> All = new HashSet<string>(StringComparer.Ordinal)
        { Synced, Syncing, WaitingToSend, EditingByMe, EditingByOther, NewerWaiting, Conflict, Refused, NotOnThisComputer };
}
public static class AttentionKinds
{
    public const string NewerWaiting = "newerWaiting", SideVersion = "sideVersion", Refused = "refused", LockBroken = "lockBroken",
        NameTaken = "nameTaken", ReleaseNotChecked = "releaseNotChecked";
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

// Message names on the bridge, both directions. AgentViewContractTests keeps
// wwwroot/bridge.js in step with these lists.
public static class BridgeMessages
{
    public const string View = "view", FileDetail = "fileDetail", Activity = "activity", ActionResult = "actionResult";
    public static readonly IReadOnlyList<string> HostToPage = [View, FileDetail, Activity, ActionResult];
    public const string Ready = "ready", Connect = "connect", CancelConnect = "cancelConnect", SignOut = "signOut", Pause = "pause", Resume = "resume",
        OpenVault = "openVault", OpenFile = "openFile", LaunchFile = "launchFile", ShowInFolder = "showInFolder", CheckOut = "checkOut", CheckIn = "checkIn",
        UndoCheckOut = "undoCheckOut", TakeBack = "takeBack", CreateFolder = "createFolder", RenameFolder = "renameFolder", DeleteFolder = "deleteFolder",
        RenameFile = "renameFile", AddFiles = "addFiles", DropFiles = "dropFiles", DismissNotice = "dismissNotice", SaveSettings = "saveSettings", ChooseVaultRoot = "chooseVaultRoot";
    public static readonly IReadOnlyList<string> PageToHost = [Ready, Connect, CancelConnect, SignOut, Pause, Resume, OpenVault, OpenFile, LaunchFile, ShowInFolder,
        CheckOut, CheckIn, UndoCheckOut, TakeBack, CreateFolder, RenameFolder, DeleteFolder, RenameFile, AddFiles, DropFiles, DismissNotice, SaveSettings,
        ChooseVaultRoot];

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
