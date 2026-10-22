using Armory.Agent.Engine.View;
using Armory.TestSupport;
using static Armory.EndToEnd.Tests.ScenarioTests;

namespace Armory.EndToEnd.Tests;

// 0.4.2 (the field round of 2026-10-22, items 1 and 2): IDEA-06 showed "1,498 files can't be
// uploaded" with "SHOW THE 200 FILES", each file saying it was outside every project, and the
// other 1,298 reachable nowhere. A mass condition is one card with one sentence and one action;
// its list reaches every file, grouped by reason, and its count is the card's.
public sealed class NoticeScaleTests
{
    [PostgresFact]
    public async Task One_thousand_five_hundred_files_outside_every_project_are_one_card_and_every_one_is_in_its_list()
    {
        await using var t = await TeamAsync();
        t.A.Write(Plate, "plate");
        for (var i = 1; i <= 1500; i++) t.A.Write($"Old Robot/Assembly Parts/F26-ROB-{i:0000} Standoff.SLDPRT", "part " + i);
        await t.A.SyncTimesAsync(2);

        var view = t.A.Engine.View;
        var card = Assert.Single(view.Notices, n => n.Kind == NoticeKinds.OutsideProject);
        Assert.Equal("1,500 files are outside every project", card.Title);
        Assert.Equal("Armory keeps only files inside a project folder. Move them into your project folders, and they upload by themselves.", card.Detail);
        Assert.Equal(NoticeTones.Bad, card.Tone);
        Assert.Equal(new NoticeActionView("Open the Armory folder", BridgeMessages.OpenVault, []), card.Action);
        Assert.Equal(1500, card.Count);
        Assert.Equal(200, card.Items.Count);
        // Nothing else says it again, and nothing of it went to the server.
        Assert.DoesNotContain(view.Notices, n => n.Kind == NoticeKinds.CantSend);
        Assert.Equal(1, await t.World.CountAsync("select count(*) from armory_files where project_id=@p", ("p", t.Project)));

        var all = (await t.A.Engine.NoticeItemsAsync(card.Key))!;
        Assert.Equal(card.Key, all.Key);
        Assert.Equal(1500, all.Count);
        var reason = Assert.Single(all.Groups);
        Assert.Equal(1500, reason.Count);
        Assert.Equal("Outside every project. Move it into one of your project folders so Armory can keep it.", reason.Reason);
        Assert.Equal(1500, reason.Items.Select(i => i.Path).Distinct(StringComparer.Ordinal).Count());
        Assert.Equal("Old Robot/Assembly Parts/F26-ROB-0001 Standoff.SLDPRT", reason.Items[0].Path);
        Assert.Equal("Old Robot/Assembly Parts/F26-ROB-1500 Standoff.SLDPRT", reason.Items[^1].Path);
        // A card that is gone answers nothing.
        Assert.Null(await t.A.Engine.NoticeItemsAsync("no such card"));
    }

    [PostgresFact]
    public async Task A_card_s_list_groups_its_files_by_reason_and_its_counts_add_up()
    {
        await using var t = await TeamAsync();
        t.A.Write(Plate, "plate");
        t.A.Write("Robot 2027/Drivetrain/Gear.SLDPRT", "gear");
        await t.A.SyncAsync();
        // Files sharing a name with the team's (one reason), and loose ones (another card).
        for (var i = 0; i < 3; i++) t.A.Write($"Robot 2027/Copy {i}/Plate.SLDPRT", "a copy " + i);
        t.A.Write("Robot 2027/Copy 9/Gear.SLDPRT", "a gear copy");
        for (var i = 0; i < 4; i++) t.A.Write($"Loose/Part-{i}.SLDPRT", "loose " + i);
        await t.A.SyncTimesAsync(2);

        var view = t.A.Engine.View;
        var names = Assert.Single(view.Notices, n => n.Kind == NoticeKinds.NameShared);
        var outside = Assert.Single(view.Notices, n => n.Kind == NoticeKinds.OutsideProject);
        Assert.Equal(4, outside.Count);
        var list = (await t.A.Engine.NoticeItemsAsync(names.Key))!;
        Assert.Equal(names.Count, list.Count);
        Assert.Equal(list.Count, list.Groups.Sum(g => g.Count));
        Assert.All(list.Groups, g => Assert.Equal(g.Count, g.Items.Count));
        Assert.Equal(list.Groups.OrderByDescending(g => g.Count).Select(g => g.Reason), list.Groups.Select(g => g.Reason));
        Assert.Equal(4, (await t.A.Engine.NoticeItemsAsync(outside.Key))!.Groups.Sum(g => g.Count));
    }
}
