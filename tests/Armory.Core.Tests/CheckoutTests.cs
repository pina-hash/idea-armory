using System.Security.Cryptography;
using System.Text;
using Armory.Core;

namespace Armory.Core.Tests;

// Explicit check out (v2, PDM style) next to Automatic (v1). One test per Explicit rule
// (E1, E2, E3 and offline in docs/core/reconciliation.md), the release gate on every route,
// the read-only rule, and proof that Automatic plans are unchanged.
public sealed class CheckoutTests
{
    // Every Automatic plan over the small state space below, hashed. Recorded from the
    // unchanged v1 reconciler at b18791d, so any change to Automatic behavior shows here.
    private const string AutomaticFingerprint = "22de5ad905fbe042bf760c896a1b9194b26126d5e2a3541946f05701d0294518";
    private static readonly Revision Removed = new("v3", null, "Maria");
    private static SyncInput Explicit => Fixtures.Input with { Checkout = CheckoutMode.Explicit };

    internal static IEnumerable<SyncInput> SmallStateSpace()
    {
        Revision?[] revisions = [null, Fixtures.Base, Fixtures.Newer, new("v3", null, "Maria")];
        string?[] locals = [null, "base", "remote", "edit"];
        foreach (var path in new[] { Fixtures.Path(), Fixtures.Path("robot/gear.SLDPRT") })
        foreach (var baseline in revisions)
        foreach (var remote in revisions)
        foreach (var local in locals)
        foreach (var ownership in Enum.GetValues<LockOwnership>())
        foreach (var open in new[] { false, true })
        foreach (var online in new[] { false, true })
        foreach (var broken in new[] { false, true })
        foreach (var preserved in new string?[] { null, "edit" })
        foreach (var saved in new SolidWorksRelease?[] { null, new(2024), new(2026) })
        foreach (var pinned in new SolidWorksRelease?[] { null, new(2025) })
        foreach (var gate in Enum.GetValues<ReleaseGateMode>())
            yield return new SyncInput(path, baseline, local, remote, ownership, open, online, broken, saved, pinned, preserved, gate);
    }

    private static string Describe(SyncPlan plan)
        => string.Join(";", plan.Actions.Select(a => $"{a.Kind}|{a.Reason}|{a.ReleaseNotChecked}")) + "#" +
           string.Join(";", plan.Intents.Select(i => $"{i.Kind}|{i.Path}|{i.Hash}"));

    private static string Fingerprint(Func<SyncInput, SyncInput> shape)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        foreach (var input in SmallStateSpace())
            hash.AppendData(Encoding.UTF8.GetBytes(Describe(Reconciler.Plan(shape(input))) + "\n"));
        return Convert.ToHexStringLower(hash.GetHashAndReset());
    }

    private static SyncActionKind[] Kinds(SyncInput input) => Reconciler.Plan(input).Actions.Select(a => a.Kind).ToArray();
    private static (SyncActionKind Kind, SideVersionReason? Why)[] Steps(SyncInput input)
        => Reconciler.Plan(input).Actions.Select(a => (a.Kind, a.Why)).ToArray();
    private static IntentKind[] Intents(SyncInput input) => Reconciler.Plan(input).Intents.Select(i => i.Kind).ToArray();
    private static SyncActionKind Refresh(bool open) => open ? SyncActionKind.NotifyNewerVersionWaiting : SyncActionKind.Download;
    private static SyncInput AsAutomatic(SyncInput input) => input with { Checkout = CheckoutMode.Automatic, Request = CheckoutRequest.None };

    // Every online input whose local bytes changed (E1), across locks, requests, bases and remotes.
    private static IEnumerable<SyncInput> ChangedRoutes(VaultPath path)
    {
        foreach (var baseline in new Revision?[] { null, Fixtures.Base, Removed })
        foreach (var remote in new Revision?[] { null, Fixtures.Base, Fixtures.Newer, Removed })
        foreach (var ownership in Enum.GetValues<LockOwnership>())
        foreach (var request in Enum.GetValues<CheckoutRequest>())
        foreach (var broken in new[] { false, true })
        foreach (var open in new[] { false, true })
            yield return Explicit with
            {
                Path = path, Base = baseline, Remote = remote, LocalHash = "edit", Lock = ownership,
                Request = request, LockWasBroken = broken, IsOpen = open,
            };
    }

    [Fact]
    public void Automatic_plans_match_the_v1_fingerprint()
        => Assert.Equal(AutomaticFingerprint, Fingerprint(input => input));

    [Fact]
    public void Automatic_is_the_default_ignores_requests_and_never_names_why()
    {
        Assert.Equal(CheckoutMode.Automatic, Fixtures.Input.Checkout);
        Assert.Equal(CheckoutRequest.None, Fixtures.Input.Request);
        Assert.Null(new SyncAction(SyncActionKind.SaveSideVersion).Why);
        foreach (var request in new[] { CheckoutRequest.CheckIn, CheckoutRequest.Undo })
            Assert.Equal(AutomaticFingerprint, Fingerprint(input => input with { Request = request }));
        foreach (var input in SmallStateSpace())
            Assert.All(Reconciler.Plan(input).Actions, a => Assert.Null(a.Why));
    }

    [Fact]
    public void Only_this_devices_check_out_makes_a_shared_file_writable()
    {
        Assert.True(CheckoutRules.IsReadOnlyOnDisk(LockOwnership.Free));
        Assert.True(CheckoutRules.IsReadOnlyOnDisk(LockOwnership.OtherPerson));
        Assert.True(CheckoutRules.IsReadOnlyOnDisk(LockOwnership.MyOtherDevice));
        Assert.False(CheckoutRules.IsReadOnlyOnDisk(LockOwnership.ThisDevice));
    }

    // The check out rule: the lock is taken only over the live shared version, unchanged.
    [Fact]
    public void Check_out_takes_the_lock_only_over_the_live_shared_version()
    {
        static CheckOutStep Step(Revision? baseline, string? local, Revision? remote, bool open = false)
            => CheckoutRules.NextCheckOutStep(baseline, local, remote, open);
        foreach (var open in new[] { false, true })
        {
            Assert.Equal(CheckOutStep.TakeLock, Step(Fixtures.Base, "base", Fixtures.Base, open));
            // Bytes saved without a check out are never checked out over, open or not.
            Assert.Equal(CheckOutStep.KeepChangesFirst, Step(Fixtures.Base, "forced", Fixtures.Base, open));
            Assert.Equal(CheckOutStep.KeepChangesFirst, Step(Fixtures.Base, "forced", Fixtures.Newer, open));
            // A same-named file this copy never had from the server is not the shared version.
            Assert.Equal(CheckOutStep.KeepChangesFirst, Step(null, "mine", Fixtures.Base, open));
            Assert.Equal(CheckOutStep.KeepChangesFirst, Step(Removed, "mine", Fixtures.Newer, open));
        }
        // A copy that is behind, or missing, is brought up to date first when it is closed (D18).
        Assert.Equal(CheckOutStep.DownloadFirst, Step(Fixtures.Base, "base", Fixtures.Newer));
        Assert.Equal(CheckOutStep.CloseFirst, Step(Fixtures.Base, "base", Fixtures.Newer, open: true));
        Assert.Equal(CheckOutStep.DownloadFirst, Step(null, null, Fixtures.Base));
        Assert.Equal(CheckOutStep.DownloadFirst, Step(Removed, null, Fixtures.Newer));
        Assert.Equal(CheckOutStep.CloseFirst, Step(null, null, Fixtures.Base, open: true));
        // A copy removed on this computer is a pending removal, not a check out.
        Assert.Equal(CheckOutStep.RemovedHere, Step(Fixtures.Base, null, Fixtures.Base));
        // Nothing to check out without a live shared version.
        foreach (var remote in new Revision?[] { null, Removed })
        foreach (var baseline in new Revision?[] { null, Fixtures.Base, Removed })
        foreach (var local in new string?[] { null, "base", "new" })
        foreach (var open in new[] { false, true })
            Assert.Equal(CheckOutStep.NotShared, Step(baseline, local, remote, open));
    }

    // The rule agrees with the Explicit reconciler over the whole small state space: a check out
    // it allows can never share anything but the shared version at check in, and every other
    // step is exactly what one pass with the lock free does (keep and put back, download,
    // notify while open, or the pending removal).
    [Fact]
    public void Check_out_rule_agrees_with_the_explicit_reconciler()
    {
        var steps = new HashSet<CheckOutStep>();
        foreach (var input in SmallStateSpace().Where(i => i.IsOnline && !i.LockWasBroken))
        {
            var step = CheckoutRules.NextCheckOutStep(input.Base, input.LocalHash, input.Remote, input.IsOpen);
            steps.Add(step);
            var free = Kinds(input with { Checkout = CheckoutMode.Explicit, Lock = LockOwnership.Free });
            var checkIn = Kinds(input with { Checkout = CheckoutMode.Explicit, Lock = LockOwnership.ThisDevice, Request = CheckoutRequest.CheckIn });
            switch (step)
            {
                case CheckOutStep.TakeLock:
                    Assert.Equal([SyncActionKind.None], checkIn);
                    Assert.Equal([SyncActionKind.None], free);
                    break;
                case CheckOutStep.KeepChangesFirst:
                    if (free is [SyncActionKind.Refuse]) break; // the release gate refuses the kept copy too
                    Assert.Equal([SyncActionKind.SaveSideVersion, Refresh(input.IsOpen)], free);
                    break;
                case CheckOutStep.DownloadFirst: Assert.Equal([SyncActionKind.Download], free); break;
                case CheckOutStep.CloseFirst: Assert.Equal([SyncActionKind.NotifyNewerVersionWaiting], free); break;
                case CheckOutStep.RemovedHere: Assert.Equal([input.IsOpen ? SyncActionKind.Refuse : SyncActionKind.ProposeTombstone], free); break;
                case CheckOutStep.NotShared: Assert.False(input.Remote is { IsTombstone: false }); break;
            }
        }
        Assert.Equal(Enum.GetValues<CheckOutStep>().Length, steps.Count);
    }

    // E1, first row: bytes already kept against a removal go to recovery, exactly as Automatic.
    [Theory]
    [InlineData(false, SyncActionKind.MoveLocalToRecovery)]
    [InlineData(true, SyncActionKind.NotifyNewerVersionWaiting)]
    public void Explicit_kept_bytes_over_a_removed_file_go_to_recovery_as_automatic(bool open, SyncActionKind expected)
    {
        foreach (var baseline in new Revision?[] { null, Fixtures.Base, Removed })
        foreach (var ownership in Enum.GetValues<LockOwnership>())
        foreach (var request in Enum.GetValues<CheckoutRequest>())
        {
            var input = Explicit with
            {
                Base = baseline, LocalHash = "edit", PreservedLocalHash = "edit", Remote = Removed, IsOpen = open, Lock = ownership, Request = request,
            };
            Assert.Equal([expected], Kinds(input));
            Assert.Equal(Kinds(AsAutomatic(input)), Kinds(input));
        }
    }

    // E1, gate row: a release known to be newer, a missing pin, or an unknown release under
    // Enforce refuses on every Explicit route (adds, revivals, kept copies, check in, undo).
    [Theory]
    [InlineData(".SLDPRT")][InlineData(".sldasm")][InlineData(".SldDrw")]
    public void Explicit_release_gate_refuses_on_every_route_as_automatic(string extension)
    {
        foreach (var route in ChangedRoutes(Fixtures.Path("robot/gear" + extension)))
        foreach (var mode in Enum.GetValues<ReleaseGateMode>())
        {
            var input = route with { SavedRelease = new(2026), PinnedRelease = new(2025), ReleaseGate = mode };
            var action = Assert.Single(Reconciler.Plan(input).Actions);
            Assert.Equal(SyncActionKind.Refuse, action.Kind);
            Assert.Contains("2026", action.Reason);
            Assert.Contains("2025", action.Reason);
            Assert.Equal(Reconciler.Plan(AsAutomatic(input)).Actions, Reconciler.Plan(input).Actions);
            Assert.Equal(SyncActionKind.Refuse, Assert.Single(Reconciler.Plan(input with { PinnedRelease = null }).Actions).Kind);
            var unknown = input with { SavedRelease = null, ReleaseGate = ReleaseGateMode.Enforce };
            Assert.Equal(SyncActionKind.Refuse, Assert.Single(Reconciler.Plan(unknown).Actions).Kind);
        }
    }

    // E1, gate row: Warn carries "release not checked" on the one upload or kept copy of every route.
    [Theory]
    [InlineData(".SLDPRT")][InlineData(".sldasm")][InlineData(".SldDrw")]
    public void Explicit_warn_marks_an_unknown_release_on_every_upload_and_kept_copy(string extension)
    {
        foreach (var route in ChangedRoutes(Fixtures.Path("robot/gear" + extension)))
        {
            var input = route with { SavedRelease = null, PinnedRelease = new(2025), ReleaseGate = ReleaseGateMode.Warn };
            var actions = Reconciler.Plan(input).Actions;
            Assert.DoesNotContain(actions, a => a.Kind == SyncActionKind.Refuse);
            var carrying = Assert.Single(actions, a => a.Kind is SyncActionKind.Upload or SyncActionKind.AcquireLockThenUpload or SyncActionKind.SaveSideVersion);
            Assert.True(carrying.ReleaseNotChecked);
            var known = Reconciler.Plan(input with { SavedRelease = new(2024) }).Actions;
            Assert.All(known, a => Assert.False(a.ReleaseNotChecked));
            Assert.Equal(actions.Select(a => (a.Kind, a.Why)), known.Select(a => (a.Kind, a.Why)));
        }
    }

    [Fact]
    public void Explicit_never_gates_unchanged_or_deleted_files()
    {
        foreach (var local in new string?[] { null, "base" })
        {
            var input = Explicit with { Path = Fixtures.Path("robot/gear.SLDPRT"), LocalHash = local, SavedRelease = new(2026), PinnedRelease = new(2025) };
            Assert.DoesNotContain(Reconciler.Plan(input).Actions, a => a.Kind == SyncActionKind.Refuse);
        }
    }

    // E1, add row: a file the server has never had is added under its own lock and shared at once.
    [Fact]
    public void Explicit_a_new_file_is_added_under_its_own_lock()
    {
        foreach (var baseline in new Revision?[] { null, Removed })
        foreach (var ownership in Enum.GetValues<LockOwnership>())
        foreach (var request in Enum.GetValues<CheckoutRequest>())
        foreach (var broken in new[] { false, true })
        foreach (var open in new[] { false, true })
        {
            var input = Explicit with
            {
                Base = baseline, Remote = null, LocalHash = "new", Lock = ownership, Request = request, LockWasBroken = broken, IsOpen = open,
            };
            var expected = ownership == LockOwnership.ThisDevice ? SyncActionKind.Upload : SyncActionKind.AcquireLockThenUpload;
            Assert.Equal([(expected, (SideVersionReason?)null)], Steps(input));
        }
    }

    // E1, add row: a tracked file (a live base) whose server record is missing is not an add.
    // Its bytes are kept, nothing is shared, exactly as Automatic.
    [Fact]
    public void Explicit_a_tracked_file_missing_from_the_server_is_kept_not_added()
    {
        foreach (var local in new[] { "edit", "base" })
        foreach (var ownership in Enum.GetValues<LockOwnership>())
        foreach (var request in Enum.GetValues<CheckoutRequest>())
        foreach (var broken in new[] { false, true })
        foreach (var open in new[] { false, true })
        {
            var input = Explicit with
            {
                Base = Fixtures.Base, Remote = null, LocalHash = local, Lock = ownership, Request = request, LockWasBroken = broken, IsOpen = open,
            };
            if (local == "base" && !broken) Assert.Equal([SyncActionKind.None], Kinds(input)); // E3: unchanged
            else Assert.Equal([(SyncActionKind.SaveSideVersion, broken ? SideVersionReason.LockBroken : SideVersionReason.Conflict)], Steps(input));
            Assert.Equal(Kinds(AsAutomatic(input)), Kinds(input));
        }
    }

    // E1, revival row: re-adding a removed name (C4) takes the lock and continues its history.
    [Fact]
    public void Explicit_re_adding_a_removed_name_revives_it()
    {
        foreach (var baseline in new Revision?[] { null, Removed })
        foreach (var ownership in Enum.GetValues<LockOwnership>())
        foreach (var request in Enum.GetValues<CheckoutRequest>())
        foreach (var broken in new[] { false, true })
        foreach (var open in new[] { false, true })
        {
            var input = Explicit with
            {
                Base = baseline, Remote = Removed, LocalHash = "new", Lock = ownership, Request = request, LockWasBroken = broken, IsOpen = open,
            };
            var expected = ownership == LockOwnership.ThisDevice ? SyncActionKind.Upload : SyncActionKind.AcquireLockThenUpload;
            Assert.Equal([expected], Kinds(input));
        }
        // A removal this copy never saw is not a re-add: the bytes are kept and nothing is shared.
        Assert.Equal([(SyncActionKind.SaveSideVersion, SideVersionReason.Conflict)],
            Steps(Explicit with { Base = new Revision("v2", null, "Alex"), Remote = Removed, LocalHash = "new" }));
    }

    // E1, conflict row: as Automatic (kept copy, then the shared version), with a reason.
    [Theory]
    [InlineData(false)][InlineData(true)]
    public void Explicit_conflicts_keep_a_side_version_then_refresh_as_automatic(bool open)
    {
        foreach (var request in Enum.GetValues<CheckoutRequest>())
        foreach (var ownership in Enum.GetValues<LockOwnership>())
        {
            var input = Explicit with { LocalHash = "edit", IsOpen = open, Request = request, Lock = ownership };
            SyncInput[] cases =
            [
                input with { Remote = Fixtures.Newer },
                input with { LockWasBroken = true },
                input with { LockWasBroken = true, LocalHash = "base" },
                input with { Remote = Removed },
                input with { Remote = Removed, LockWasBroken = true },
            ];
            Assert.Equal([(SyncActionKind.SaveSideVersion, SideVersionReason.Conflict), (Refresh(open), null)], Steps(cases[0]));
            Assert.Equal([(SyncActionKind.SaveSideVersion, SideVersionReason.LockBroken), (Refresh(open), null)], Steps(cases[1]));
            Assert.Equal([(SyncActionKind.SaveSideVersion, SideVersionReason.LockBroken), (Refresh(open), null)], Steps(cases[2]));
            Assert.Equal([(SyncActionKind.SaveSideVersion, SideVersionReason.Conflict)], Steps(cases[3]));
            Assert.Equal([(SyncActionKind.SaveSideVersion, SideVersionReason.LockBroken)], Steps(cases[4]));
            foreach (var c in cases) Assert.Equal(Kinds(AsAutomatic(c)), Kinds(c));
        }
    }

    // E1, lock row: someone else (or my other computer) has it checked out.
    [Theory]
    [InlineData(LockOwnership.OtherPerson, false)][InlineData(LockOwnership.OtherPerson, true)]
    [InlineData(LockOwnership.MyOtherDevice, false)][InlineData(LockOwnership.MyOtherDevice, true)]
    public void Explicit_a_change_to_a_file_checked_out_elsewhere_is_kept_then_refreshed(LockOwnership ownership, bool open)
    {
        foreach (var request in Enum.GetValues<CheckoutRequest>())
        {
            var input = Explicit with { LocalHash = "edit", Lock = ownership, IsOpen = open, Request = request };
            Assert.Equal([(SyncActionKind.SaveSideVersion, SideVersionReason.ChangedWithoutCheckOut), (Refresh(open), null)], Steps(input));
            Assert.Equal(Kinds(AsAutomatic(input)), Kinds(input));
        }
    }

    // E1, Free row: bytes changed with nobody holding the file (the attribute was cleared).
    [Theory]
    [InlineData(false)][InlineData(true)]
    public void Explicit_a_change_without_a_check_out_is_kept_and_the_shared_bytes_put_back(bool open)
    {
        foreach (var request in Enum.GetValues<CheckoutRequest>())
            Assert.Equal([(SyncActionKind.SaveSideVersion, SideVersionReason.ChangedWithoutCheckOut), (Refresh(open), null)],
                Steps(Explicit with { LocalHash = "edit", Lock = LockOwnership.Free, IsOpen = open, Request = request }));
        // Automatic took the lock for the same change.
        Assert.Equal([SyncActionKind.AcquireLockThenUpload], Kinds(Fixtures.Input with { LocalHash = "edit", IsOpen = open }));
    }

    // E1, ThisDevice rows.
    [Theory]
    [InlineData(false)][InlineData(true)]
    public void Explicit_saves_while_checked_out_are_kept_until_check_in(bool open)
        => Assert.Equal([(SyncActionKind.SaveSideVersion, SideVersionReason.SavedWhileCheckedOut)],
            Steps(Explicit with { LocalHash = "edit", Lock = LockOwnership.ThisDevice, IsOpen = open }));

    [Theory]
    [InlineData(false)][InlineData(true)]
    public void Explicit_check_in_shares_the_checked_out_bytes(bool open)
        => Assert.Equal([(SyncActionKind.Upload, (SideVersionReason?)null)],
            Steps(Explicit with { LocalHash = "edit", Lock = LockOwnership.ThisDevice, IsOpen = open, Request = CheckoutRequest.CheckIn }));

    [Theory]
    [InlineData(false)][InlineData(true)]
    public void Explicit_undo_keeps_the_changes_then_restores_the_shared_version(bool open)
        => Assert.Equal([(SyncActionKind.SaveSideVersion, SideVersionReason.UndoCheckOut), (Refresh(open), null)],
            Steps(Explicit with { LocalHash = "edit", Lock = LockOwnership.ThisDevice, IsOpen = open, Request = CheckoutRequest.Undo }));

    // E2: a deleted file someone else has checked out is put back, not refused.
    [Theory]
    [InlineData(LockOwnership.OtherPerson)][InlineData(LockOwnership.MyOtherDevice)]
    public void Explicit_a_deleted_file_checked_out_elsewhere_is_put_back(LockOwnership ownership)
    {
        foreach (var request in Enum.GetValues<CheckoutRequest>())
            Assert.Equal([SyncActionKind.Download], Kinds(Explicit with { LocalHash = null, Lock = ownership, Request = request }));
        Assert.Equal([SyncActionKind.Refuse], Kinds(Fixtures.Input with { LocalHash = null, Lock = ownership }));
    }

    // E2 (every other deletion) and E3 (unchanged files) match Automatic exactly.
    [Fact]
    public void Explicit_deleted_and_unchanged_files_otherwise_match_automatic()
    {
        var compared = 0;
        foreach (var input in SmallStateSpace().Where(i => i.IsOnline))
        foreach (var request in Enum.GetValues<CheckoutRequest>())
        {
            var deleted = input.LocalHash is null;
            var unchanged = input.LocalHash is not null && input.LocalHash == input.Base?.Hash && !input.LockWasBroken;
            if (!deleted && !unchanged) continue;
            var putBack = deleted && input.Remote is { IsTombstone: false } && input.Base is not null && Reconciler.SameRevision(input.Base, input.Remote) &&
                !input.IsOpen && input.Lock is LockOwnership.OtherPerson or LockOwnership.MyOtherDevice;
            if (putBack) continue;
            Assert.Equal(Reconciler.Plan(input).Actions, Reconciler.Plan(input with { Checkout = CheckoutMode.Explicit, Request = request }).Actions);
            compared++;
        }
        Assert.True(compared > 10_000);
    }

    // Offline: as Automatic, but never a lock intent for a file with a live shared version.
    [Fact]
    public void Explicit_offline_edits_never_queue_a_lock_on_a_shared_file()
    {
        var offline = Explicit with { IsOnline = false };
        Assert.Equal([IntentKind.Upload], Intents(offline with { LocalHash = "edit" }));
        Assert.Equal([IntentKind.Upload], Intents(offline with { LocalHash = "edit", Remote = Fixtures.Newer, Lock = LockOwnership.OtherPerson }));
        Assert.Equal([IntentKind.Upload], Intents(offline with { LocalHash = "edit", Lock = LockOwnership.ThisDevice }));
        Assert.Equal([IntentKind.AcquireLock, IntentKind.Upload], Intents(offline with { Base = null, Remote = null, LocalHash = "new" }));
        Assert.Equal([IntentKind.AcquireLock, IntentKind.Upload], Intents(offline with { Base = Removed, Remote = Removed, LocalHash = "new" }));
        Assert.Equal([IntentKind.Tombstone], Intents(offline with { LocalHash = null }));
        foreach (var input in SmallStateSpace().Where(i => !i.IsOnline))
        foreach (var request in Enum.GetValues<CheckoutRequest>())
        {
            var automatic = Reconciler.Plan(input);
            var plan = Reconciler.Plan(input with { Checkout = CheckoutMode.Explicit, Request = request });
            Assert.Empty(plan.Actions);
            var expected = input.Remote is { IsTombstone: false }
                ? automatic.Intents.Where(i => i.Kind != IntentKind.AcquireLock).ToArray()
                : automatic.Intents.ToArray();
            Assert.Equal(expected, plan.Intents);
        }
    }

    // Properties over the whole small state space, for every request.
    [Fact]
    public void Explicit_shares_only_at_check_in_or_add_and_names_every_kept_copy()
    {
        foreach (var input in SmallStateSpace())
        foreach (var request in Enum.GetValues<CheckoutRequest>())
        {
            var actions = Reconciler.Plan(input with { Checkout = CheckoutMode.Explicit, Request = request }).Actions;
            Assert.All(actions, a => Assert.Equal(a.Kind == SyncActionKind.SaveSideVersion, a.Why is not null));
            if (input.IsOpen) Assert.DoesNotContain(actions, a => a.Kind is SyncActionKind.Download or SyncActionKind.MoveLocalToRecovery);
            if (!actions.Any(a => a.Kind is SyncActionKind.Upload or SyncActionKind.AcquireLockThenUpload)) continue;
            // An add has no server record and no live base; a revival re-adds a removal this copy saw (or never had).
            var add = (input.Remote is null && input.Base is not { IsTombstone: false }) ||
                (input.Remote is { IsTombstone: true } && (input.Base is null || Reconciler.SameRevision(input.Base, input.Remote)));
            Assert.False(input.Remote is null && input.Base is { IsTombstone: false }, $"Shared a tracked file missing from the server: {input}");
            var checkIn = request == CheckoutRequest.CheckIn && input.Lock == LockOwnership.ThisDevice && !input.LockWasBroken &&
                Reconciler.SameRevision(input.Base, input.Remote);
            Assert.True(add || checkIn, $"Shared outside check in or add: {input}");
            if (!add) Assert.Equal(SyncActionKind.Upload, Assert.Single(actions).Kind);
        }
    }
}
