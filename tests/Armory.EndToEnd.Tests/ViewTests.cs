using Armory.Agent.Engine.View;
using Armory.TestSupport;
using static Armory.EndToEnd.Tests.ScenarioTests;

namespace Armory.EndToEnd.Tests;

// What the window shows (docs/agent/BRIDGE.md, v2-design.md 4.5 and 4.6) when the server can't
// be read, across restarts and while a pass is still running: every row keeps its label, a
// dismissed card stays dismissed, and kept copies make one quiet card.
public sealed class ViewTests
{
    private const string Bracket = "Robot 2027/Drivetrain/Bracket.SLDPRT";

    // A laptop opened at home, offline from the start: the team's files are listed as this
    // computer last knew them, each with its file id, who has it and its status, never "waiting"
    // for files that have nothing to upload.
    [PostgresFact]
    public async Task Restarted_offline_every_row_keeps_its_label_and_status()
    {
        await using var t = await TeamAsync();
        t.A.Write(Plate, "plate");
        t.A.Write(Bracket, "bracket");
        await t.A.SyncAsync();
        await t.B.SyncAsync();
        var plate = await t.FileId("Plate.SLDPRT");
        var bracket = await t.FileId("Bracket.SLDPRT");
        Assert.True((await t.A.CheckOutAsync(Bracket)).Ok);
        Assert.True((await t.B.CheckOutAsync(Plate)).Ok);
        await t.B.SyncAsync();
        t.B.Offline = true;
        t.B.Restart();
        await t.B.SyncAsync();
        var view = t.B.Engine.View;
        Assert.Equal(SyncStates.Offline, view.Sync.State);
        Assert.Equal(0, view.Sync.PendingCount);
        Assert.Null(view.Activity.Waiting);
        var plateRow = t.B.Row(Plate);
        Assert.Equal((plate.ToString(), "Checked out by you", CheckoutStates.Mine, FileStatuses.Synced),
            (plateRow.FileId, plateRow.Checkout.Label, plateRow.Checkout.State, plateRow.Status));
        var bracketRow = t.B.Row(Bracket);
        Assert.Equal((bracket.ToString(), "Checked out by Alex Kim on student A laptop", CheckoutStates.Other, FileStatuses.Synced),
            (bracketRow.FileId, bracketRow.Checkout.Label, bracketRow.Checkout.State, bracketRow.Status));
        var mine = Assert.Single(view.MyFiles);
        Assert.Equal((Plate, "Checked out by you", FileStatuses.Synced), (mine.Path, mine.Checkout.Label, mine.Status));
        // The read-only rule still holds, and the quiet question still says who has a file.
        Assert.False(t.B.Disk.IsReadOnly(Plate));
        Assert.True(t.B.Disk.IsReadOnly(Bracket));
        t.B.Open(Bracket);
        await t.B.SyncAsync();
        var asked = Assert.IsType<PromptView>(t.B.Engine.View.Prompt);
        Assert.Equal(("Checked out by Alex Kim on student A laptop", false), (asked.Checkout.Label, asked.CanCheckOut));
        // A save while offline is a change of a checked-out file, then waits to upload.
        t.B.Save(Plate, "Maria offline");
        await t.B.SyncAsync();
        Assert.Equal(FileStatuses.Changed, t.B.Row(Plate).Status);
        Assert.Equal(1, t.B.Engine.View.Sync.PendingCount);
    }

    // A card the student dismissed stays dismissed: while a pass is still gathering its notices,
    // and after the app starts again. A new item brings the card back with only the new one.
    [PostgresFact]
    public async Task A_dismissed_card_stays_dismissed_after_a_restart()
    {
        await using var t = await TeamAsync();
        t.A.Write("Loose.SLDPRT", "outside every project");
        await t.A.SyncAsync();
        var card = t.A.Card(NoticeKinds.CantSend)!;
        Assert.Equal("Loose.SLDPRT can't be uploaded", card.Title);
        // Dismissed while a pass is running, before it has found this notice again.
        t.A.Engine.CrashPoint = p => { if (p == "after-capture") t.A.Engine.DismissNotice(card.Key); };
        await t.A.SyncAsync();
        t.A.Engine.CrashPoint = null;
        Assert.Null(t.A.Card(NoticeKinds.CantSend));
        await t.A.SyncAsync();
        Assert.Null(t.A.Card(NoticeKinds.CantSend));
        t.A.Restart();
        await t.A.SyncAsync();
        Assert.Null(t.A.Card(NoticeKinds.CantSend));
        await t.A.SyncAsync();
        Assert.Null(t.A.Card(NoticeKinds.CantSend));
        t.A.Write("Loose-2.SLDPRT", "another one");
        await t.A.SyncAsync();
        var again = t.A.Card(NoticeKinds.CantSend)!;
        Assert.Equal("Loose-2.SLDPRT", Assert.Single(again.Items).Name);
    }

    // Saves made without a check out while the file is open: one item for the file (not one per
    // save), a card that can be dismissed, and that needs the student only while the checked-in
    // version still waits for the file to close.
    [PostgresFact]
    public async Task Kept_copies_make_one_quiet_card_per_file()
    {
        await using var t = await TeamAsync();
        t.A.Write(Plate, "v1");
        await t.A.SyncAsync();
        await t.B.SyncAsync();
        var file = await t.FileId("Plate.SLDPRT");
        t.B.Open(Plate);
        foreach (var save in new[] { "Maria 1", "Maria 2", "Maria 3" })
        {
            t.B.ForceWrite(Plate, save);
            await t.B.SyncAsync();
        }
        Assert.Equal(3, await t.World.CountAsync("select count(*) from armory_side_versions where file_id=@f and reason='changed without a check out'", ("f", file)));
        var card = t.B.Card(NoticeKinds.KeptCopy)!;
        var item = Assert.Single(card.Items);
        Assert.Equal(Plate, item.Path);
        Assert.Equal("Your change to Plate.SLDPRT was kept as your own copy", card.Title);
        Assert.Equal("Saved without a check out. The checked-in version comes back when you close Plate.SLDPRT.", card.Detail);
        Assert.Equal(NoticeTones.Look, card.Tone); // the checked-in version still waits for the file to close
        Assert.Equal(BridgeMessages.DismissNotice, card.Action!.Command);
        t.B.Close(Plate);
        await t.B.SyncAsync();
        Assert.Equal("v1", t.B.Text(Plate));
        card = t.B.Card(NoticeKinds.KeptCopy)!;
        Assert.Equal(NoticeTones.Info, card.Tone); // news now: nothing waits on the student
        Assert.Equal(SyncStates.Synced, t.B.Engine.View.Sync.State);
        t.B.Engine.DismissNotice(card.Key);
        await t.B.SyncAsync();
        Assert.Null(t.B.Card(NoticeKinds.KeptCopy));
        // In the file's history, a save kept while checked out is routine; Maria's are not.
        Assert.True((await t.A.CheckOutAsync(Plate)).Ok);
        t.A.Save(Plate, "v2 by Alex");
        await t.A.SyncAsync();
        var history = (await t.A.Engine.GetFileDetailAsync(file))!.History;
        Assert.Contains(history, h => h.Kind == HistoryKinds.KeptCopy && h.Note == "Saved while checked out" && h.Routine);
        Assert.All(history.Where(h => h.Note.StartsWith("Changed without a check out", StringComparison.Ordinal)), h => Assert.False(h.Routine));
        Assert.Equal(3, history.Count(h => h.Note == "Changed without a check out, kept as Maria Lopez's own copy"));
    }
}
