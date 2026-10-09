using Armory.Core;

namespace Armory.Core.Tests;

// Saving down to the project's pinned release with SolidWorks' "Save to Version" option
// (research section 3): which option value, or why it cannot.
public sealed class SaveDownPlanTests
{
    [Theory]
    [InlineData(2026, true, 2025, SaveDownPlan.Penultimate)]
    [InlineData(2026, true, 2024, SaveDownPlan.Antepenultimate)]
    [InlineData(2027, true, 2025, SaveDownPlan.Antepenultimate)]
    [InlineData(2027, true, 2026, SaveDownPlan.Penultimate)]
    [InlineData(2026, false, 2025, SaveDownPlan.OldServicePack)]
    [InlineData(2028, true, 2025, SaveDownPlan.TooFarApart)]
    [InlineData(2026, false, 2023, SaveDownPlan.TooFarApart)]
    [InlineData(2025, true, 2025, SaveDownPlan.NotNeeded)]
    [InlineData(2025, false, 2026, SaveDownPlan.NotNeeded)]
    [InlineData(2026, true, 2026, SaveDownPlan.NotNeeded)]
    [InlineData(2025, false, 2024, SaveDownPlan.Unsupported)]
    [InlineData(2026, true, 0, SaveDownPlan.Unsupported)]
    [InlineData(0, true, 2025, SaveDownPlan.Unsupported)]
    public void Plan_follows_the_running_release_its_option_and_the_pin(int running, bool hasOption, int pinned, SaveDownPlan expected)
        => Assert.Equal(expected, SaveDown.Plan(running, hasOption, pinned));

    [Theory]
    [InlineData("34.2.0", 2025, SaveDownPlan.OldServicePack)]   // 2026 SP2: no Save to Version yet
    [InlineData("34.3.0", 2025, SaveDownPlan.Penultimate)]      // 2026 SP3 has it
    [InlineData("34.4.1", 2025, SaveDownPlan.Penultimate)]
    [InlineData("34.-1.0", 2025, SaveDownPlan.OldServicePack)]  // a 2026 beta
    [InlineData("35.0.0", 2025, SaveDownPlan.Antepenultimate)]  // 2027 from its first build
    [InlineData("33.5.0", 2025, SaveDownPlan.NotNeeded)]
    [InlineData("36.0.0", 2025, SaveDownPlan.TooFarApart)]
    public void Plan_from_the_revision_number(string revision, int pinned, SaveDownPlan expected)
        => Assert.Equal(expected, SaveDown.Plan(SolidWorksRevision.Parse(revision)!.Value, pinned));

    [Fact]
    public void Only_the_two_save_down_plans_save_and_they_carry_the_option_value()
    {
        foreach (var plan in Enum.GetValues<SaveDownPlan>())
        {
            Assert.Equal(plan is SaveDownPlan.Penultimate or SaveDownPlan.Antepenultimate, plan.CanSave());
            Assert.Equal(plan switch { SaveDownPlan.Penultimate => 1, SaveDownPlan.Antepenultimate => 2, _ => (int?)null }, plan.SaveToVersionValue());
        }
    }
}
