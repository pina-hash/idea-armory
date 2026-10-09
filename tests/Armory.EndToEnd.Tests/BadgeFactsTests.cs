using Armory.Core;
using Armory.TestSupport;
using static Armory.EndToEnd.Tests.ScenarioTests;

namespace Armory.EndToEnd.Tests;

// File Explorer's badges from the engine's own facts (SyncEngine.BadgeFactsAsync, then
// BadgeRules.Entries, docs/agent/EXPLORER.md 2.1): what each computer of a team shows on its
// files as they are checked out, changed, refused and checked in.
public sealed class BadgeFactsTests
{
    private const string Gear = "Robot 2027/Drivetrain/Gear.SLDPRT";

    private static async Task<Dictionary<string, BadgeState>> BadgesAsync(Computer c)
        => BadgeRules.Entries(await c.Engine.BadgeFactsAsync()).ToDictionary(e => e.Path, e => e.State, StringComparer.OrdinalIgnoreCase);

    [PostgresFact]
    public async Task Each_computer_badges_its_files_from_what_its_engine_knows()
    {
        await using var t = await TeamAsync();
        t.A.Write(Plate, "v1");
        await t.A.SyncAsync();
        await t.B.SyncAsync();
        var a = await BadgesAsync(t.A);
        Assert.Equal(BadgeState.Synced, a[Plate]);
        // Synced does not climb to its folders, and the vault root never has a badge.
        Assert.False(a.ContainsKey("Robot 2027/Drivetrain"));
        Assert.Equal(BadgeState.Synced, (await BadgesAsync(t.B))[Plate]);

        Assert.True((await t.A.CheckOutAsync(Plate)).Ok);
        await t.B.SyncAsync();
        a = await BadgesAsync(t.A);
        Assert.Equal(BadgeState.Mine, a[Plate]);
        Assert.Equal(BadgeState.Mine, a["Robot 2027/Drivetrain"]);
        Assert.Equal(BadgeState.Mine, a["Robot 2027"]);
        var b = await BadgesAsync(t.B);
        Assert.Equal(BadgeState.Locked, b[Plate]);
        Assert.False(b.ContainsKey("Robot 2027"));

        // A new file not in Armory yet, waiting offline: Mine. A save on B without a check out,
        // still open there: Attention, on the file and every folder above it.
        t.A.Offline = true;
        t.A.Write(Gear, "a new gear");
        await t.A.SyncAsync();
        Assert.Equal(BadgeState.Mine, (await BadgesAsync(t.A))[Gear]);
        t.A.Offline = false;
        t.B.Open(Plate);
        t.B.ForceWrite(Plate, "B changed it without a check out");
        await t.B.SyncAsync();
        b = await BadgesAsync(t.B);
        Assert.Equal(BadgeState.Attention, b[Plate]);
        Assert.Equal(BadgeState.Attention, b["Robot 2027/Drivetrain"]);

        // Checked in: everyone's copy is up to date and nobody has it.
        Assert.True((await t.A.CheckInAsync(Plate)).Ok);
        await t.A.SyncAsync();
        a = await BadgesAsync(t.A);
        Assert.Equal(BadgeState.Synced, a[Plate]);
        Assert.Equal(BadgeState.Synced, a[Gear]);
    }

    [PostgresFact]
    public async Task A_file_outside_every_project_has_no_badge()
    {
        await using var t = await TeamAsync();
        t.A.Write(Plate, "v1");
        await t.A.SyncAsync();
        t.B.Write("Loose notes.txt", "not in a project");
        await t.B.SyncAsync();
        var facts = await t.B.Engine.BadgeFactsAsync();
        Assert.DoesNotContain(facts, f => f.Path == "Loose notes.txt");
        Assert.Contains(facts, f => f.Path == Plate && f.InArmory && f.Status == BadgeFileStatus.Synced && f.Checkout == BadgeCheckout.Available);
    }
}
