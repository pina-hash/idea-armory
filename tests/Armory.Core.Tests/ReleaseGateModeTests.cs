using Armory.Core;

namespace Armory.Core.Tests;

// The per-project release gate (lane A): Warn uploads an unreadable release marked "release
// not checked"; Enforce refuses it; a release known to be newer is refused in both modes.
public sealed class ReleaseGateModeTests
{
    private static SyncInput Part(string extension = ".SLDPRT") => Fixtures.Input with
    {
        Path = Fixtures.Path("robot/gear" + extension), LocalHash = "edit", PinnedRelease = new(2025),
    };

    [Theory]
    [InlineData(ReleaseGateMode.Enforce)][InlineData(ReleaseGateMode.Warn)]
    public void A_release_known_to_be_newer_is_refused_in_both_modes_naming_both_releases(ReleaseGateMode mode)
    {
        foreach (var ownership in Enum.GetValues<LockOwnership>())
        foreach (var remote in new[] { Fixtures.Base, Fixtures.Newer, new Revision("dead", null, "Maria") })
        {
            var action = Assert.Single(Reconciler.Plan(Part() with { Lock = ownership, Remote = remote, SavedRelease = new(2026), ReleaseGate = mode }).Actions);
            Assert.Equal(SyncActionKind.Refuse, action.Kind);
            Assert.Contains("2026", action.Reason);
            Assert.Contains("2025", action.Reason);
        }
    }

    [Theory]
    [InlineData(".SLDPRT")][InlineData(".sldasm")][InlineData(".SldDrw")]
    public void Warn_uploads_an_unknown_release_marked_not_checked_on_every_upload_route(string extension)
    {
        foreach (var ownership in Enum.GetValues<LockOwnership>())
        foreach (var remote in new[] { Fixtures.Base, Fixtures.Newer, new Revision("dead", null, "Maria") })
        {
            var input = Part(extension) with { Lock = ownership, Remote = remote, SavedRelease = null, ReleaseGate = ReleaseGateMode.Warn };
            var actions = Reconciler.Plan(input).Actions;
            var upload = Assert.Single(actions, a => a.Kind is SyncActionKind.Upload or SyncActionKind.AcquireLockThenUpload or SyncActionKind.SaveSideVersion);
            Assert.True(upload.ReleaseNotChecked);
            Assert.DoesNotContain(actions, a => a.Kind == SyncActionKind.Refuse);
            // The same input with a readable, allowed release is checked.
            var known = Reconciler.Plan(input with { SavedRelease = new(2024) }).Actions;
            Assert.All(known, a => Assert.False(a.ReleaseNotChecked));
            Assert.Equal(actions.Select(a => a.Kind), known.Select(a => a.Kind));
        }
    }

    [Fact]
    public void Enforce_refuses_an_unknown_release_and_is_the_library_default()
    {
        var input = Part() with { SavedRelease = null };
        Assert.Equal(ReleaseGateMode.Enforce, input.ReleaseGate);
        var action = Assert.Single(Reconciler.Plan(input).Actions);
        Assert.Equal(SyncActionKind.Refuse, action.Kind);
        Assert.Contains("unknown", action.Reason);
    }

    [Fact]
    public void Warn_never_overrides_a_missing_or_invalid_pin()
    {
        var missing = Assert.Single(Reconciler.Plan(Part() with { PinnedRelease = null, ReleaseGate = ReleaseGateMode.Warn }).Actions);
        Assert.Equal(SyncActionKind.Refuse, missing.Kind);
        Assert.Contains("no pinned SolidWorks release", missing.Reason);
        var invalid = Assert.Single(Reconciler.Plan(Part() with { PinnedRelease = new(0), ReleaseGate = ReleaseGateMode.Warn }).Actions);
        Assert.Equal(SyncActionKind.Refuse, invalid.Kind);
        Assert.False(SolidWorksVersionGate.Decide(null, new(1900), ReleaseGateMode.Warn).Allowed);
    }

    [Fact]
    public void Generic_files_are_never_marked_or_gated()
    {
        foreach (var mode in Enum.GetValues<ReleaseGateMode>())
        {
            var actions = Reconciler.Plan(Fixtures.Input with { LocalHash = "edit", ReleaseGate = mode, PinnedRelease = null }).Actions;
            Assert.Equal(SyncActionKind.AcquireLockThenUpload, Assert.Single(actions).Kind);
            Assert.False(actions[0].ReleaseNotChecked);
        }
    }

    [Theory]
    [InlineData(null, ReleaseGateMode.Warn, true, true)]
    [InlineData(null, ReleaseGateMode.Enforce, false, false)]
    [InlineData(1994, ReleaseGateMode.Warn, true, true)]
    [InlineData(2025, ReleaseGateMode.Enforce, true, false)]
    [InlineData(2023, ReleaseGateMode.Warn, true, false)]
    [InlineData(2026, ReleaseGateMode.Warn, false, false)]
    [InlineData(2026, ReleaseGateMode.Enforce, false, false)]
    public void Decide_matches_the_gate_table(int? saved, ReleaseGateMode mode, bool allowed, bool notChecked)
    {
        var decision = SolidWorksVersionGate.Decide(saved is null ? null : new SolidWorksRelease(saved.Value), new(2025), mode);
        Assert.Equal(allowed, decision.Allowed);
        Assert.Equal(notChecked, decision.ReleaseNotChecked);
        Assert.Equal(allowed, decision.Problem is null);
    }

    [Fact]
    public void Offline_plans_still_queue_unknown_release_saves_as_private_intents()
    {
        var plan = Reconciler.Plan(Part() with { IsOnline = false, SavedRelease = null, ReleaseGate = ReleaseGateMode.Enforce });
        Assert.Empty(plan.Actions);
        Assert.Contains(plan.Intents, i => i.Kind == IntentKind.Upload);
    }
}
