using System.Security.Cryptography;
using Armory.Agent.Engine;
using Armory.Agent.Engine.View;
using Armory.Client;
using Armory.Core;
using Armory.Core.Tests;
using Armory.TestSupport;
using static Armory.EndToEnd.Tests.ScenarioTests;

namespace Armory.EndToEnd.Tests;

// The engine with a SolidWorks link (docs/agent/SOLIDWORKS.md; FakeSolidWorksLink plays
// SolidWorks): which opens ask (C5), "Check out and reopen" (in place, never closing a document
// or discarding a change, and without a link once the file is closed), and saving down (the
// link's stamp decides the upload; a file that can't go back, or a save down SolidWorks didn't
// do, stays a private draft and is never uploaded as 2026).
public sealed class SolidWorksLinkTests
{
    private const string Bracket = "Robot 2027/Drivetrain/Bracket.SLDPRT", Drawing = "Robot 2027/Drivetrain/Plate.SLDDRW", Arm = "Robot 2027/Arm/Arm.SLDASM";

    private static byte[] Part(int code, int seed = 0)
        => SwContainer.Typical(code, [17000, code]).With("Contents/Config-0", [.. BitConverter.GetBytes(seed), .. new byte[64]]).Build();
    // A saved-down part whose two release fields disagree (the reader can't place it).
    private static byte[] Mixed(int seed = 0)
        => SwContainer.Typical(18000, [17000, 19000]).With("Contents/Config-0", [.. BitConverter.GetBytes(seed), .. new byte[64]]).Build();
    private static string HashOf(byte[] bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));

    private static Task<long> AnywhereOnServer(Team t, string hash)
        => t.World.CountAsync("select (select count(*) from armory_versions where content_sha256=@h) + (select count(*) from armory_side_versions where content_sha256=@h)", ("h", hash));

    private static string Refusal(Computer c, string path) => Assert.Single(c.NoticeItems, n => n.Card.Kind == NoticeKinds.CantSend && n.Item.Path == path).Item.Detail!;

    private static NoticeGroupView SolidWorksCard(Computer c) => Assert.IsType<NoticeGroupView>(c.Card(NoticeKinds.SolidWorks));

    // Only the document the student opened asks, never the parts an assembly loaded; once per
    // document per SolidWorks session (another SolidWorks asks again); opens within 1.5 seconds
    // are one question; and nothing is checked out because it was opened.
    [PostgresFact]
    public async Task Only_documents_the_student_opened_ask_once_per_SolidWorks_session()
    {
        await using var t = await TeamAsync();
        t.A.Write(Arm, "arm");
        t.A.Write(Plate, "plate");
        t.A.Write(Bracket, "bracket");
        await t.A.SyncAsync();
        var sw = t.B.LinkSolidWorks();
        await t.B.SyncAsync();
        var raised = 0;
        t.B.Engine.OpenPromptsChanged += _ => Interlocked.Increment(ref raised);

        // The assembly, then the two parts it loads.
        sw.Open(Arm, docType: SolidWorksDocTypes.Assembly);
        sw.Open(Plate, topLevel: false, seconds: 0.2);
        sw.Open(Bracket, topLevel: false, seconds: 0.3);
        foreach (var path in new[] { Arm, Plate, Bracket }) t.B.Open(path); // SolidWorks' markers, one per document
        await t.B.LinkSettledAsync();
        await t.B.SyncAsync();
        var asked = Assert.Single(t.B.Engine.OpenPrompts);
        Assert.Equal((Arm, "Arm.SLDASM", OpenAskKind.CheckOut, true, (string?)null), (asked.Path, asked.Name, asked.Kind, asked.ViaSolidWorks, asked.CheckedOutBy));
        Assert.Equal(await t.FileId("Arm.SLDASM"), asked.FileId);
        Assert.Equal([Arm], t.B.Engine.OpenWithoutCheckOut);
        Assert.Equal(Arm, t.B.Engine.View.Prompt!.Path);
        Assert.True(raised > 0);

        // The student opens one of its parts in its own window ten seconds later: it asks too, alone.
        sw.Activate(Plate, seconds: 10);
        await t.B.LinkSettledAsync();
        Assert.Equal([Arm, Plate], t.B.Engine.OpenPrompts.Select(p => p.Path));
        Assert.NotEqual(t.B.Engine.OpenPrompts[0].Group, t.B.Engine.OpenPrompts[1].Group);
        Assert.Equal(Plate, t.B.Engine.View.Prompt!.Path); // the window asks about the newest

        // Closed and opened again in the same SolidWorks: no second notification (the window still asks).
        sw.Close(Plate);
        sw.Open(Plate, seconds: 20);
        await t.B.LinkSettledAsync();
        Assert.Equal([Arm], t.B.Engine.OpenPrompts.Select(p => p.Path));
        Assert.Equal(Plate, t.B.Engine.View.Prompt!.Path);

        // A second SolidWorks: its own session, so it asks again; three opens within 1.5 s are one group.
        sw.Pid = 7788;
        sw.Attach();
        sw.Open(Plate, seconds: 40);
        sw.Open(Bracket, seconds: 40.6);
        sw.Open(Arm, docType: SolidWorksDocTypes.Assembly, seconds: 41.4);
        await t.B.LinkSettledAsync();
        var second = t.B.Engine.OpenPrompts;
        Assert.Equal([Arm, Bracket, Plate], second.Select(p => p.Path)); // one prompt per file, from the newer open
        Assert.Single(second.Select(p => p.Group).Distinct());
        Assert.NotEqual(asked.Group, second[0].Group);

        // Opening never checked anything out.
        foreach (var name in new[] { "Arm.SLDASM", "Plate.SLDPRT", "Bracket.SLDPRT" }) Assert.Equal(0, await t.LiveLocks(await t.FileId(name)));
        // SolidWorks closed (its markers went with it): its documents and questions go.
        sw.Detach();
        sw.Pid = 4120;
        sw.Detach();
        foreach (var path in new[] { Arm, Plate, Bracket }) t.B.Close(path);
        await t.B.LinkSettledAsync();
        await t.B.SyncAsync();
        Assert.Empty(t.B.Engine.OpenPrompts);
        Assert.Null(t.B.Engine.View.Prompt);
    }

    // Check out and reopen with the link: checked out, then made writable in SolidWorks in place
    // (SetReadOnlyState), nothing closed, nothing opened again.
    [PostgresFact]
    public async Task Check_out_and_reopen_makes_the_open_document_writable_in_place()
    {
        await using var t = await TeamAsync();
        t.A.Write(Plate, "v1");
        await t.A.SyncAsync();
        var sw = t.B.LinkSolidWorks();
        await t.B.SyncAsync();
        var doc = sw.Open(Plate);
        t.B.Open(Plate);
        await t.B.LinkSettledAsync();
        await t.B.SyncAsync();
        Assert.Equal(OpenAskKind.CheckOut, Assert.Single(t.B.Engine.OpenPrompts).Kind);

        var answer = await t.B.Engine.CheckOutAndReopenAsync([Plate]);
        Assert.True(answer.Ok, answer.Message);
        Assert.Equal("Checked out Plate.SLDPRT. You can save it in SolidWorks now.", answer.Message);
        Assert.Equal(["MakeWritable " + sw.Full(Plate), "SetReadOnlyState(false) " + sw.Full(Plate)], sw.Calls);
        Assert.False(doc.ReadOnly);
        Assert.False(t.B.Disk.IsReadOnly(Plate));
        Assert.Empty(t.B.Disk.Launched);
        Assert.Equal(1, await t.LiveLocks(await t.FileId("Plate.SLDPRT")));
        Assert.Empty(t.B.Engine.OpenPrompts);
        Assert.Null(t.B.Engine.View.Prompt);
        // The student saves in SolidWorks: it is theirs now.
        t.B.Save(Plate, "v2 by Maria");
        Assert.True((await t.B.CheckInAsync(Plate)).Ok);
        Assert.Equal(Hash("v2 by Maria"), await t.CurrentHash(await t.FileId("Plate.SLDPRT")));
    }

    // In place didn't work and the student had changed it: no reload, no close, no discard. The
    // answer says how to save instead.
    [PostgresFact]
    public async Task A_document_with_unsaved_changes_is_never_reloaded_closed_or_discarded()
    {
        await using var t = await TeamAsync();
        t.A.Write(Plate, "v1");
        await t.A.SyncAsync();
        var sw = t.B.LinkSolidWorks();
        await t.B.SyncAsync();
        var doc = sw.Open(Plate);
        doc.InPlaceWorks = false;
        sw.Modify(Plate);
        t.B.Open(Plate);
        await t.B.LinkSettledAsync();
        await t.B.SyncAsync();

        var answer = await t.B.Engine.CheckOutAndReopenAsync([Plate]);
        Assert.True(answer.Ok);
        Assert.Equal("Checked out Plate.SLDPRT. SolidWorks still has it read-only. To save, close it in SolidWorks and open it again from Armory. " +
            "Changes made before the check out can't be saved to it.", answer.Message);
        Assert.Equal(["MakeWritable " + sw.Full(Plate), "SetReadOnlyState(false) " + sw.Full(Plate)], sw.Calls);
        Assert.DoesNotContain(sw.Calls, c => c.Contains("Reload", StringComparison.Ordinal) || c.Contains("Close", StringComparison.Ordinal) || c.Contains("Discard", StringComparison.Ordinal));
        Assert.True(doc.Changed);
        Assert.True(doc.ReadOnly);
        Assert.Empty(t.B.Disk.Launched);
        // Checked out all the same; a notification can offer to open it again.
        Assert.Equal(1, await t.LiveLocks(await t.FileId("Plate.SLDPRT")));
        Assert.Equal(OpenAskKind.Reopen, Assert.Single(t.B.Engine.OpenPrompts).Kind);
    }

    // In place didn't work and nothing changed: a part reloads (never discarding), a drawing is
    // closed and reopened (never discarding); when that is refused too, Armory opens it again once
    // the student closes it.
    [PostgresFact]
    public async Task An_unchanged_document_falls_back_to_a_reload_or_a_reopen_without_discarding()
    {
        await using var t = await TeamAsync();
        t.A.Write(Plate, "plate");
        t.A.Write(Drawing, "drawing");
        t.A.Write(Bracket, "bracket");
        await t.A.SyncAsync();
        var sw = t.B.LinkSolidWorks();
        await t.B.SyncAsync();
        sw.Open(Plate).InPlaceWorks = false;
        sw.Open(Drawing, docType: SolidWorksDocTypes.Drawing).InPlaceWorks = false;
        var stuck = sw.Open(Bracket);
        stuck.InPlaceWorks = false;
        stuck.ReopenWorks = false;
        foreach (var path in new[] { Plate, Drawing, Bracket }) t.B.Open(path);
        await t.B.LinkSettledAsync();
        await t.B.SyncAsync();

        Assert.Equal("Checked out Plate.SLDPRT. You can save it in SolidWorks now.", (await t.B.Engine.CheckOutAndReopenAsync([Plate])).Message);
        Assert.Contains("ReloadOrReplace(False, null, DiscardChanges: False) " + sw.Full(Plate), sw.Calls);
        Assert.Equal("Checked out Plate.SLDDRW. You can save it in SolidWorks now.", (await t.B.Engine.CheckOutAndReopenAsync([Drawing])).Message);
        Assert.Contains("CloseAndReopen(options: 4) " + sw.Full(Drawing), sw.Calls);
        Assert.Equal("Checked out Bracket.SLDPRT. Close it in SolidWorks and Armory opens it again, ready to save.", (await t.B.Engine.CheckOutAndReopenAsync([Bracket])).Message);
        Assert.Empty(t.B.Disk.Launched);
        // Closed in SolidWorks: Armory opens it again, once.
        sw.Close(Bracket);
        t.B.Close(Bracket);
        await t.B.LinkSettledAsync();
        await t.B.SyncAsync();
        Assert.True(SpinWait.SpinUntil(() => t.B.Disk.Launched.Contains(Bracket), TimeSpan.FromSeconds(10)), "Bracket.SLDPRT never opened again");
        await t.B.SyncAsync();
        Assert.Single(t.B.Disk.Launched, Bracket);
    }

    // Someone else has it: nothing is made writable, and the question says who.
    [PostgresFact]
    public async Task A_file_someone_else_has_is_not_made_writable_and_the_question_says_who()
    {
        await using var t = await TeamAsync();
        t.A.Write(Plate, "v1");
        await t.A.SyncAsync();
        Assert.True((await t.A.CheckOutAsync(Plate)).Ok);
        var sw = t.B.LinkSolidWorks();
        await t.B.SyncAsync();
        sw.Open(Plate);
        t.B.Open(Plate);
        await t.B.LinkSettledAsync();
        await t.B.SyncAsync();
        var asked = Assert.Single(t.B.Engine.OpenPrompts);
        Assert.Equal((OpenAskKind.HeldByOther, "Alex Kim on student A laptop"), (asked.Kind, asked.CheckedOutBy));

        var answer = await t.B.Engine.CheckOutAndReopenAsync([Plate]);
        Assert.False(answer.Ok);
        Assert.Equal("Plate.SLDPRT is checked out by Alex Kim on student A laptop.", answer.Message);
        Assert.Empty(sw.Calls);
    }

    // Without the link: checked out while open, then opened again by Armory once the student
    // closes it, within five minutes; after that, nothing opens by surprise.
    [PostgresFact]
    public async Task Without_the_link_a_file_closed_within_five_minutes_opens_again_once()
    {
        await using var t = await TeamAsync();
        t.A.Write(Plate, "plate");
        t.A.Write(Bracket, "bracket");
        await t.A.SyncAsync();
        await t.B.SyncAsync();
        t.B.Open(Plate);
        t.B.Open(Bracket);
        await t.B.SyncAsync();
        Assert.Equal("Checked out Plate.SLDPRT. Close it in SolidWorks and Armory opens it again, ready to save.", (await t.B.Engine.CheckOutAndReopenAsync([Plate])).Message);
        Assert.False(t.B.Disk.IsReadOnly(Plate));
        t.B.Close(Plate);
        await t.B.SyncAsync();
        Assert.True(SpinWait.SpinUntil(() => t.B.Disk.Launched.Contains(Plate), TimeSpan.FromSeconds(10)), "Plate.SLDPRT never opened again");
        await t.B.SyncTimesAsync(2);
        Assert.Single(t.B.Disk.Launched, Plate);

        Assert.True((await t.B.Engine.CheckOutAndReopenAsync([Bracket])).Ok);
        t.B.Clock.Advance(TimeSpan.FromMinutes(6));
        t.B.Close(Bracket);
        await t.B.SyncTimesAsync(2);
        await Task.Delay(300);
        Assert.DoesNotContain(Bracket, t.B.Disk.Launched);
    }

    // Without the link, SolidWorks' markers that appear together (an assembly and its parts) are
    // one question; a marker seconds later is another. Nothing is checked out.
    [PostgresFact]
    public async Task Without_the_link_a_burst_of_markers_is_one_question()
    {
        await using var t = await TeamAsync();
        t.A.Write(Arm, "arm");
        t.A.Write(Plate, "plate");
        t.A.Write(Bracket, "bracket");
        await t.A.SyncAsync();
        await t.B.SyncAsync();
        t.B.Open(Arm);
        t.B.Open(Plate);
        await t.B.SyncAsync();
        t.B.Clock.Advance(TimeSpan.FromSeconds(1));
        t.B.Open(Bracket);
        await t.B.SyncAsync();
        var prompts = t.B.Engine.OpenPrompts;
        Assert.Equal(3, prompts.Count);
        Assert.Single(prompts.Select(p => p.Group).Distinct());
        Assert.All(prompts, p => Assert.False(p.ViaSolidWorks));
        t.B.Close(Bracket);
        await t.B.SyncAsync();
        t.B.Clock.Advance(TimeSpan.FromSeconds(10));
        t.B.Open(Bracket);
        await t.B.SyncAsync();
        Assert.Equal(2, t.B.Engine.OpenPrompts.Select(p => p.Group).Distinct().Count());
        foreach (var name in new[] { "Arm.SLDASM", "Plate.SLDPRT", "Bracket.SLDPRT" }) Assert.Equal(0, await t.LiveLocks(await t.FileId(name)));
    }

    // A save down the reader can't place (its two release fields disagree) uploads by the link's
    // stamp for those exact bytes, even in Enforce; while SolidWorks saves, nothing of the path is
    // decided.
    [PostgresFact]
    public async Task A_saved_down_file_uploads_by_its_stamp()
    {
        await using var t = await TeamAsync();
        Assert.True(await t.Mentor.Api.SetReleaseGateAsync(t.Project, ProjectReleaseGate.Enforce, Guid.NewGuid()));
        t.A.ReleaseReader = new SolidWorksSavedReleaseReader();
        t.A.Restart();
        t.A.Write(Plate, Part(18000));
        await t.A.SyncAsync();
        var file = await t.FileId("Plate.SLDPRT");
        t.B.ReleaseReader = new SolidWorksSavedReleaseReader();
        var sw = t.B.LinkSolidWorks();
        await t.B.SyncAsync();
        Assert.Equal(2025, Assert.Single(sw.Pins).PinnedRelease);
        Assert.Equal(sw.Full("Robot 2027"), sw.Pins[0].FullFolder);
        Assert.True((await t.B.CheckOutAsync(Plate)).Ok);
        sw.Open(Plate, readOnly: false);

        // SolidWorks writes the file: held until it says it saved.
        var saving = sw.Saving(Plate);
        await t.B.LinkSettledAsync();
        var savedDown = Mixed(1);
        t.B.Save(Plate, savedDown);
        await t.B.SyncAsync();
        Assert.Equal(0, await AnywhereOnServer(t, HashOf(savedDown)));
        Assert.DoesNotContain(t.B.NoticeItems, n => n.Item.Path == Plate);

        sw.Saved(Plate, saving, new ReleaseStamp(HashOf(savedDown), SavedReleaseRule.StampYear(2025, 2025), "34.4.1", 2026, 2025, sw.Now));
        await t.B.LinkSettledAsync();
        Assert.True((await t.B.CheckInAsync(Plate)).Ok);
        Assert.Equal(HashOf(savedDown), await t.CurrentHash(file));
        Assert.Equal(1, await t.World.CountAsync("select count(*) from armory_version_releases r join armory_versions v on v.id=r.version_id where v.content_sha256=@h and r.saved_release=2025 and r.release_checked",
            ("h", HashOf(savedDown))));
    }

    // A document SolidWorks says can't go back to 2025 is saved as 2026 here and stays a private
    // draft in Warn and in Enforce, with SolidWorks' own words for why and what to change.
    [PostgresFact]
    public async Task A_blocked_document_stays_a_private_draft_in_both_modes()
    {
        await using var t = await TeamAsync();
        t.A.Write(Plate, Part(18000));
        await t.A.SyncAsync();
        t.B.ReleaseReader = new SolidWorksSavedReleaseReader();
        var sw = t.B.LinkSolidWorks();
        await t.B.SyncAsync();
        Assert.True((await t.B.CheckOutAsync(Plate)).Ok);
        sw.Open(Plate, readOnly: false);
        sw.Compatibility(Plate, 2025, blocked: [new CompatibilityItem("Hole Wizard instances on sketch geometry", "Clear \"Create instances on sketch geometry\".", "Hole1")]);
        await t.B.LinkSettledAsync();
        const string Words = "It uses Hole Wizard instances on sketch geometry, which SolidWorks 2025 doesn't have. Change it to something 2025 has " +
            "(SolidWorks suggests: Clear \"Create instances on sketch geometry\"), then save again. Until then nobody else gets these changes.";
        var card = SolidWorksCard(t.B);
        Assert.Equal(("Plate.SLDPRT is saved on this computer only", Words), (card.Title, card.Detail));

        // Saved with Save to Version off: 2026, on this computer only.
        var newer = Part(19000, 3);
        var saving = sw.Saving(Plate, LinkSaveMode.PrivateDraft);
        await t.B.LinkSettledAsync();
        t.B.Save(Plate, newer);
        sw.Saved(Plate, saving, new ReleaseStamp(HashOf(newer), SavedReleaseRule.StampYear(2026, 2026), "34.4.1", 2026, null, sw.Now));
        await t.B.LinkSettledAsync();
        await t.B.SyncAsync();
        Assert.Equal(Words, Refusal(t.B, Plate));
        Assert.Equal(0, await AnywhereOnServer(t, HashOf(newer)));
        Assert.True(await t.Mentor.Api.SetReleaseGateAsync(t.Project, ProjectReleaseGate.Enforce, Guid.NewGuid()));
        await t.B.SyncAsync();
        Assert.Equal(Words, Refusal(t.B, Plate));
        Assert.Equal(0, await AnywhereOnServer(t, HashOf(newer)));
        Assert.False((await t.B.CheckInAsync(Plate)).Ok);
        Assert.Equal(0, await AnywhereOnServer(t, HashOf(newer)));
        Assert.Equal(newer, t.B.Read(Plate));
    }

    // Save to Version was on and SolidWorks still wrote 2026 (no license for it, B4): in Warn
    // too, those bytes are never uploaded, not even "release not checked".
    [PostgresFact]
    public async Task A_save_down_SolidWorks_did_not_do_is_never_uploaded_as_2026()
    {
        await using var t = await TeamAsync();
        t.A.Write(Plate, Part(18000));
        await t.A.SyncAsync();
        t.B.ReleaseReader = new SolidWorksSavedReleaseReader();
        var sw = t.B.LinkSolidWorks();
        await t.B.SyncAsync();
        Assert.True((await t.B.CheckOutAsync(Plate)).Ok);
        sw.Open(Plate, readOnly: false);
        var unknown = Mixed(5); // the reader can't place it either
        var saving = sw.Saving(Plate);
        await t.B.LinkSettledAsync();
        t.B.Save(Plate, unknown);
        sw.Saved(Plate, saving, new ReleaseStamp(HashOf(unknown), SavedReleaseRule.StampYear(2026, 2025), "34.4.1", 2026, 2025, sw.Now));
        await t.B.LinkSettledAsync();
        await t.B.SyncTimesAsync(2);
        Assert.Equal("SolidWorks on this computer couldn't save Plate.SLDPRT in 2025, so it stays on this computer only. Nobody else gets these changes yet. " +
            "Ask a CAD lead or a mentor what to do.", Refusal(t.B, Plate));
        Assert.False((await t.B.CheckInAsync(Plate)).Ok);
        Assert.Equal(0, await AnywhereOnServer(t, HashOf(unknown)));
        Assert.Equal(unknown, t.B.Read(Plate));
    }

    // Before a save down that drops something, the student is told what, and can keep the file on
    // this computer instead; "Save it in 2025 now" asks the link to save it down.
    [PostgresFact]
    public async Task What_a_save_down_drops_is_said_before_the_save_and_the_student_can_keep_it_here()
    {
        await using var t = await TeamAsync();
        t.A.Write(Arm, "arm");
        await t.A.SyncAsync();
        var sw = t.B.LinkSolidWorks();
        await t.B.SyncAsync();
        Assert.True((await t.B.CheckOutAsync(Arm)).Ok);
        sw.Open(Arm, readOnly: false, docType: SolidWorksDocTypes.Assembly);
        sw.Compatibility(Arm, 2025, drops: [new DropItem(DropItem.Appearances, 3), new DropItem(DropItem.ExplodeSteps, 2), new DropItem(DropItem.SimulationStudies, 1),
            new DropItem(DropItem.Decals, 0)]);
        await t.B.LinkSettledAsync();
        var card = SolidWorksCard(t.B);
        Assert.Equal("When you save Arm.SLDASM, Armory saves it in SolidWorks 2025", card.Title);
        Assert.Equal("Your team uses 2025, and 2025 can't keep: 3 appearances (colors), 2 explode steps, 1 simulation study. Part numbers and descriptions are kept by Armory.", card.Detail);
        Assert.Equal(("Keep this file on this computer only", BridgeMessages.KeepLocal), (card.Action!.Label, card.Action.Command));
        var ask = Assert.Single(t.B.Engine.SaveDownPrompts);
        Assert.Equal((Arm, card.Title, card.Detail, 2025), (ask.Path, ask.Title, ask.Text, ask.PinnedRelease));

        var kept = await t.B.Engine.KeepLocalAsync([Arm]);
        Assert.Equal("Arm.SLDASM stays on this computer only when you save it. Nobody else gets those changes until it is saved in SolidWorks 2025.", kept.Message);
        Assert.Contains("KeepLocal(True) " + sw.Full(Arm), sw.Calls);
        card = SolidWorksCard(t.B);
        Assert.Equal(("Arm.SLDASM is saved on this computer only", "You chose to keep it here. Until it is saved in SolidWorks 2025, nobody else gets these changes."), (card.Title, card.Detail));
        Assert.Equal(("Save it in 2025 now", BridgeMessages.SaveDown), (card.Action!.Label, card.Action.Command));
        Assert.Empty(t.B.Engine.SaveDownPrompts);

        var now = await t.B.Engine.SaveDownNowAsync([Arm]);
        Assert.Equal("Saved Arm.SLDASM in SolidWorks 2025.", now.Message);
        Assert.Contains("KeepLocal(False) " + sw.Full(Arm), sw.Calls);
        Assert.Contains("SaveInPinnedRelease " + sw.Full(Arm), sw.Calls);

        // "Save in 2025" answered: the same list doesn't ask again; a save down confirms what it dropped.
        Assert.True((await t.B.Engine.AnswerSaveDownAsync(Arm, keepLocal: false)).Ok);
        Assert.Null(t.B.Card(NoticeKinds.SolidWorks));
        var saving = sw.Saving(Arm);
        sw.Saved(Arm, saving, new ReleaseStamp(Hash("arm"), 2025, "34.4.1", 2026, 2025, sw.Now));
        await t.B.LinkSettledAsync();
        Assert.Contains(t.B.Engine.View.Activity.Log, l => l.Line == "Saved Arm.SLDASM in SolidWorks 2025. Not kept: 3 appearances (colors), 2 explode steps, 1 simulation study.");
        // A save down SolidWorks canceled (its Previous Release Check): not saved yet, said plainly.
        var again = sw.Saving(Arm);
        sw.SaveCanceled(Arm, again);
        await t.B.LinkSettledAsync();
        card = SolidWorksCard(t.B);
        Assert.Equal(("SolidWorks couldn't save Arm.SLDASM in 2025, so it isn't saved yet", NoticeTones.Bad), (card.Title, card.Tone));
    }

    // Settings: what the link found, in one line, and why it can't save down.
    [PostgresFact]
    public async Task The_settings_line_says_what_the_link_found()
    {
        await using var t = await TeamAsync();
        Assert.Null(t.B.Engine.View.SolidWorks); // no link on this computer
        var sw = new FakeSolidWorksLink(World.Root);
        t.B.SolidWorks = sw;
        t.B.Restart();
        await t.B.SyncAsync();
        Assert.Equal(new SolidWorksView(SolidWorksStates.None, "SolidWorks isn't running.", null), t.B.Engine.View.SolidWorks);
        sw.Attach("34.4.1");
        await t.B.LinkSettledAsync();
        Assert.Equal(new SolidWorksView(SolidWorksStates.Attached, "Linked to SolidWorks 2026 SP4.1. It saves team files in 2025.", null), t.B.Engine.View.SolidWorks);
        sw.Attach("34.2.0", SaveToVersionSupport.OldServicePack);
        await t.B.LinkSettledAsync();
        Assert.Equal(new SolidWorksView(SolidWorksStates.CantSaveDown, "Linked to SolidWorks 2026 SP2. It can't save team files in 2025.",
            "Update SolidWorks 2026 to Service Pack 3 or newer so Armory can save team files in 2025. Until then, files you save stay on this computer only."), t.B.Engine.View.SolidWorks);
        sw.Attach("34.4.1", SaveToVersionSupport.NotConfigured);
        await t.B.LinkSettledAsync();
        Assert.Equal(SolidWorksStates.CantSaveDown, t.B.Engine.View.SolidWorks!.State);
        Assert.StartsWith("Update SolidWorks 2026 to Service Pack 3 or newer", t.B.Engine.View.SolidWorks!.Detail);
        sw.Attach("33.5.0", SaveToVersionSupport.Unsupported);
        await t.B.LinkSettledAsync();
        Assert.Equal(new SolidWorksView(SolidWorksStates.Attached, "Linked to SolidWorks 2025 SP5.", null), t.B.Engine.View.SolidWorks);
        sw.Detach();
        sw.Refuse(9001);
        await t.B.LinkSettledAsync();
        Assert.Equal(SolidWorksStates.Administrator, t.B.Engine.View.SolidWorks!.State);
        Assert.Equal("SolidWorks was started as administrator, so Armory can't link to it", SolidWorksCard(t.B).Title);
    }
}
