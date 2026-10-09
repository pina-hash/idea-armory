using Armory.Agent.Engine.View;
using Armory.Core;

namespace Armory.Agent.Engine.Tests;

// The engine hands File Explorer's badges its own status and check out names
// (SyncEngine.BadgeFactsAsync, BadgeFacts.FromNames): every one it can say has a badge name, so
// a status added to the view without one fails here, not on a student's computer.
public sealed class BadgeNamesTests
{
    [Fact]
    public void Every_file_status_and_check_out_state_the_engine_says_has_a_badge_name()
    {
        Assert.Equal(FileStatuses.All.Count, Enum.GetValues<BadgeFileStatus>().Length);
        Assert.Equal(FileStatuses.All.Count, FileStatuses.All.Select(BadgeFacts.StatusOf).Distinct().Count());
        string[] checkouts = [CheckoutStates.Available, CheckoutStates.Mine, CheckoutStates.Other, CheckoutStates.MyOtherComputer];
        Assert.Equal(Enum.GetValues<BadgeCheckout>(), checkouts.Select(BadgeFacts.CheckoutOf));
    }
}
