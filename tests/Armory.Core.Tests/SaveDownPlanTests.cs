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

    [Theory]
    [InlineData("34.4.1", true, true, false, SaveToVersionSupport.Available)]
    [InlineData("35.0.0", true, true, false, SaveToVersionSupport.Available)]
    [InlineData("34.2.0", true, true, false, SaveToVersionSupport.OldServicePack)]   // no option before SP3
    [InlineData("34.4.1", false, true, false, SaveToVersionSupport.NotConfigured)]   // numbers unknown: never guessed
    [InlineData("34.4.1", true, false, false, SaveToVersionSupport.NotLicensed)]     // didn't read back
    [InlineData("34.4.1", true, true, true, SaveToVersionSupport.NotLicensed)]       // a save with it on wrote 2026 (B4)
    [InlineData("33.5.0", true, true, false, SaveToVersionSupport.Unsupported)]
    [InlineData("34.2.0", false, false, true, SaveToVersionSupport.OldServicePack)]
    public void Support_says_whether_this_SolidWorks_can_save_down(string revision, bool enumsKnown, bool readBack, bool failed, SaveToVersionSupport expected)
        => Assert.Equal(expected, SaveDown.Support(SolidWorksRevision.Parse(revision)!.Value, enumsKnown, readBack, failed));

    [Theory]
    [InlineData(SaveDownPlan.Penultimate, true, true, false, false, SaveToVersionChoice.SaveDown)]
    [InlineData(SaveDownPlan.Antepenultimate, true, true, false, false, SaveToVersionChoice.SaveDown)]
    [InlineData(SaveDownPlan.Penultimate, true, true, true, false, SaveToVersionChoice.Off)]          // blocked: saved here as 2026
    [InlineData(SaveDownPlan.Penultimate, true, true, false, true, SaveToVersionChoice.Off)]          // kept on this computer
    [InlineData(SaveDownPlan.Penultimate, true, false, false, false, SaveToVersionChoice.StudentOwn)] // not in the vault
    [InlineData(SaveDownPlan.Penultimate, false, true, false, false, SaveToVersionChoice.StudentOwn)] // option not usable: never touched
    [InlineData(SaveDownPlan.Penultimate, false, true, true, true, SaveToVersionChoice.StudentOwn)]
    [InlineData(SaveDownPlan.NotNeeded, true, true, false, false, SaveToVersionChoice.StudentOwn)]
    [InlineData(SaveDownPlan.TooFarApart, true, true, false, false, SaveToVersionChoice.StudentOwn)]
    [InlineData(SaveDownPlan.OldServicePack, true, true, false, false, SaveToVersionChoice.StudentOwn)]
    [InlineData(SaveDownPlan.Unsupported, true, true, true, true, SaveToVersionChoice.StudentOwn)]
    public void Choose_sets_the_option_only_for_vault_documents_that_can_go_back(SaveDownPlan plan, bool usable, bool vault, bool blocked, bool keepLocal, SaveToVersionChoice expected)
        => Assert.Equal(expected, SaveDown.Choose(plan, usable, vault, blocked, keepLocal));

    // Over every input: the option is only ever turned on for a vault document SolidWorks can
    // save down, never for a blocked one or one the student keeps here.
    [Fact]
    public void The_option_is_on_only_where_a_save_down_can_happen()
    {
        var bools = new[] { false, true };
        foreach (var plan in Enum.GetValues<SaveDownPlan>())
        foreach (var usable in bools)
        foreach (var vault in bools)
        foreach (var blocked in bools)
        foreach (var keepLocal in bools)
        {
            var choice = SaveDown.Choose(plan, usable, vault, blocked, keepLocal);
            if (choice == SaveToVersionChoice.SaveDown) Assert.True(plan.CanSave() && usable && vault && !blocked && !keepLocal);
            if (choice == SaveToVersionChoice.Off) Assert.True(plan.CanSave() && usable && vault && (blocked || keepLocal));
            if (!vault || !usable) Assert.Equal(SaveToVersionChoice.StudentOwn, choice);
        }
    }
}
