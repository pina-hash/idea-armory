using Armory.Agent.Engine;
using Armory.Agent.Engine.View;
using Armory.Core;
using Armory.TestSupport;
using static Armory.EndToEnd.Tests.ScenarioTests;

namespace Armory.EndToEnd.Tests;

public sealed class SaveBlockedCheckoutTests
{
    private const string Part = "Robot 2027/Drivetrain/Bracket.SLDPRT";

    [PostgresFact]
    public async Task A_blocked_save_never_checks_out_until_the_student_clicks_then_the_existing_checkout_path_is_used()
    {
        await using var team = await TeamAsync();
        team.A.Write(Part, "original part fixture");
        await team.A.SyncAsync();
        var sw = team.B.LinkSolidWorks();
        await team.B.SyncAsync();
        sw.Open(Part, topLevel: false); // a component loaded by an assembly also needs help
        await team.B.LinkSettledAsync();
        sw.Saving(Part, LinkSaveMode.Current);
        await team.B.LinkSettledAsync();
        var card = Assert.Single(team.B.Engine.View.Notices, n => n.Kind == "saveBlocked");
        Assert.Contains("have not checked it out", card.Detail);
        Assert.Equal("Check out", card.Action!.Label);
        Assert.True(team.B.Disk.IsReadOnly(Part));
        Assert.Equal(0, await team.LiveLocks(await team.FileId("Bracket.SLDPRT")));
        Assert.Empty(sw.Calls);
        // The same key from Home or a notification is resolved again before the click acts.
        var action = await team.B.Engine.SaveBlockedActionAsync(card.Key);
        Assert.Equal(BridgeMessages.CheckOut, action!.Command);
        Assert.Equal([Part], action.Paths);
        Assert.Equal(0, await team.LiveLocks(await team.FileId("Bracket.SLDPRT")));
        var answer = await team.B.Engine.CheckOutAndReopenAsync(action.Paths);
        Assert.True(answer.Ok, answer.Message);
        Assert.False(team.B.Disk.IsReadOnly(Part));
        Assert.Equal(1, await team.LiveLocks(await team.FileId("Bracket.SLDPRT")));
        Assert.Null(await team.B.Engine.SaveBlockedActionAsync(card.Key));
        Assert.DoesNotContain(team.B.Engine.View.Notices, n => n.Kind == "saveBlocked");
    }

    [PostgresFact]
    public async Task Someone_else_checking_out_before_the_click_removes_the_checkout_offer()
    {
        await using var team = await TeamAsync();
        team.A.Write(Part, "original part fixture");
        await team.A.SyncAsync();
        var sw = team.B.LinkSolidWorks();
        await team.B.SyncAsync();
        sw.Open(Part); sw.Saving(Part, LinkSaveMode.Current);
        await team.B.LinkSettledAsync();
        var key = Assert.Single(team.B.Engine.View.Notices, n => n.Kind == "saveBlocked").Key;
        Assert.True((await team.A.CheckOutAsync(Part)).Ok);
        await team.B.SyncAsync();
        var action = await team.B.Engine.SaveBlockedActionAsync(key);
        Assert.Equal("copy", action!.Command);
        Assert.True(team.B.Disk.IsReadOnly(Part));
        Assert.Empty(sw.Calls);
        Assert.Equal(1, await team.LiveLocks(await team.FileId("Bracket.SLDPRT")));
    }
}
