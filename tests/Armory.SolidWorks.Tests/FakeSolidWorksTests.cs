using Armory.Agent.Engine;
using Armory.Core;

namespace Armory.SolidWorks.Tests;

// The SolidWorks link against tests/Armory.FakeSolidWorks, a real out-of-process COM server
// (Running Object Table, IDispatch, connection points with the real event numbers), started as a
// child process (docs/agent/SOLIDWORKS.md, "Tests"). Windows only.
public sealed class FakeSolidWorksTests : IDisposable
{
    private readonly TempVault vault = new();

    public void Dispose() => vault.Dispose();

    private static bool Advised(string line) => line.StartsWith("advise ", StringComparison.Ordinal);

    // Attach only after StartupProcessCompleted; FileOpenPostNotify for a vault document gives
    // Opened, ModifyNotify Modified, FileSaveNotify's answer reaches SolidWorks, FileSavePostNotify
    // gives Saved with its type and a stamp; a document outside the vault gets no document events;
    // DestroyNotify leaves no sink advised and every reference let go.
    [WindowsFact]
    public async Task The_link_attaches_after_start_up_and_follows_the_events()
    {
        using var sw = await FakeSolidWorks.StartAsync("33.5.0", startAfterMs: 1500);
        var baseline = await sw.RefsAsync();
        await using var link = new LinkUnderTest(vault.Root, () => [sw.Pid]);
        var attached = await link.WaitForAsync<LinkAttached>();
        Assert.Equal((sw.Pid, "33.5.0", 2025), (attached.ProcessId, attached.Revision, attached.RunningYear));
        var startedAt = sw.IndexOf(l => l == "started");
        Assert.True(startedAt >= 0);
        Assert.True(sw.IndexOf(Advised) > startedAt, "an event was advised before SolidWorks finished starting");
        Assert.DoesNotContain(sw.Lines.Take(startedAt), l => l is "call GetDocuments" or "call ActiveDoc");

        var plate = vault.File("Robot 2027/Plate.SLDPRT", "plate");
        sw.Send($"open \"{plate}\"");
        var opened = await link.WaitForAsync<LinkOpened>(o => o.Path == plate);
        Assert.Equal((SolidWorksDocTypes.Part, true, true, false), (opened.DocType, opened.ReadOnly, opened.TopLevel, opened.FutureVersion));
        var partAdvises = sw.Lines.Count(l => l.StartsWith("advise " + SwConstants.DPartDocEvents, StringComparison.OrdinalIgnoreCase));
        Assert.True(partAdvises > 0);

        // Outside the vault: no Opened, no document events.
        var outside = Path.Combine(Path.GetTempPath(), "Armory-SW-outside-" + Guid.NewGuid().ToString("N")[..6] + ".SLDPRT");
        sw.Send($"open \"{outside}\"");
        await sw.WaitForAsync(l => l.StartsWith("event FileOpenPostNotify", StringComparison.Ordinal), TimeSpan.FromSeconds(10), sw.Lines.Count - 1);
        await Task.Delay(500);
        Assert.DoesNotContain(link.Records.OfType<LinkOpened>(), o => o.Path == outside);
        Assert.Equal(partAdvises, sw.Lines.Count(l => l.StartsWith("advise " + SwConstants.DPartDocEvents, StringComparison.OrdinalIgnoreCase)));

        sw.Send($"activate \"{plate}\"");
        sw.Send($"modify \"{plate}\"");
        await link.WaitForAsync<LinkModified>(m => m.Path == plate);
        var from = sw.Lines.Count;
        sw.Send($"save \"{plate}\"");
        Assert.Equal("event FileSaveNotify 0", await sw.WaitForAsync(l => l.StartsWith("event FileSaveNotify", StringComparison.Ordinal), TimeSpan.FromSeconds(10), from));
        var saving = await link.WaitForAsync<LinkSaving>(s => s.Path == plate);
        Assert.Equal(LinkSaveMode.Current, saving.Mode); // no project pins: nothing to save down
        var saved = await link.WaitForAsync<LinkSaved>(s => s.Path == plate);
        Assert.Equal((saving.SaveId, SwConstants.SaveTypeSave), (saved.SaveId, saved.SaveType));
        Assert.Equal(LinkSession.HashOf(plate), saved.Stamp!.Sha256);
        Assert.Equal(2025, saved.Stamp.Year); // the fake's history: 18000, as meant by SolidWorks 2025

        sw.Send($"close \"{plate}\"");
        await link.WaitForAsync<LinkClosed>(c => c.Path == plate);
        sw.Send("exit");
        await link.WaitForAsync<LinkDetached>();
        var refs = await sw.WaitForAsync(l => l.StartsWith("refs ", StringComparison.Ordinal), TimeSpan.FromSeconds(30), from);
        Assert.Contains("sinks=0", refs, StringComparison.Ordinal);
        Assert.Contains($"app={baseline.App} ", refs, StringComparison.Ordinal);
        await sw.WaitForAsync(l => l == "exited", TimeSpan.FromSeconds(30));
        Assert.DoesNotContain(sw.Lines, l => l.StartsWith("call CloseDoc", StringComparison.Ordinal) || l.StartsWith("error", StringComparison.Ordinal));
    }

    // While SolidWorks answers "busy" for three seconds, the link's calls are retried (its
    // message filter) and go through once it answers.
    [WindowsFact]
    public async Task A_busy_SolidWorks_is_retried_until_it_answers()
    {
        using var sw = await FakeSolidWorks.StartAsync(startAfterMs: 200);
        await using var link = new LinkUnderTest(vault.Root, () => [sw.Pid]);
        await link.WaitForAsync<LinkAttached>();
        var plate = vault.File("Robot 2027/Plate.SLDPRT", "plate");
        sw.Send("reject-calls-for 3000");
        await sw.WaitForAsync(l => l == "rejecting", TimeSpan.FromSeconds(10));
        var watch = System.Diagnostics.Stopwatch.StartNew();
        sw.Send($"open \"{plate}\"");
        await link.WaitForAsync<LinkOpened>(o => o.Path == plate, TimeSpan.FromSeconds(40));
        Assert.True(watch.Elapsed > TimeSpan.FromSeconds(2), $"opened after {watch.Elapsed.TotalSeconds:F1} s, while SolidWorks was still busy. It wrote:\n" + string.Join("\n", sw.Lines));
        Assert.True((await sw.RefsAsync()).Rejected > 0);
    }

    // Armory quits while SolidWorks keeps running: every sink unadvised, every reference let go,
    // and SolidWorks still answers.
    [WindowsFact]
    public async Task Armory_quitting_releases_every_reference_and_SolidWorks_keeps_running()
    {
        using var sw = await FakeSolidWorks.StartAsync(startAfterMs: 200);
        var baseline = await sw.RefsAsync();
        var link = new LinkUnderTest(vault.Root, () => [sw.Pid]);
        await link.WaitForAsync<LinkAttached>();
        var plate = vault.File("Robot 2027/Plate.SLDPRT", "plate");
        sw.Send($"open \"{plate}\"");
        await link.WaitForAsync<LinkOpened>(o => o.Path == plate);
        Assert.True((await sw.RefsAsync()).Sinks > 0);
        await link.DisposeAsync();
        var after = await sw.RefsAsync();
        Assert.Equal((baseline.App, 0, 0), (after.App, after.Docs, after.Sinks));
        Assert.False(sw.Exited);
    }

    // SolidWorks killed mid-session: the session ends with Detached and no exception, and
    // discovery goes on (a SolidWorks started afterwards is linked).
    [WindowsFact]
    public async Task A_killed_SolidWorks_ends_its_session_quietly_and_the_next_one_is_linked()
    {
        var pids = new List<int>();
        await using var link = new LinkUnderTest(vault.Root, () => { lock (pids) return [.. pids]; });
        using (var first = await FakeSolidWorks.StartAsync(startAfterMs: 200))
        {
            lock (pids) pids.Add(first.Pid);
            await link.WaitForAsync<LinkAttached>(a => a.ProcessId == first.Pid);
            first.Kill();
            await link.WaitForAsync<LinkDetached>(d => d.ProcessId == first.Pid);
        }
        using var second = await FakeSolidWorks.StartAsync(startAfterMs: 200);
        lock (pids) pids.Add(second.Pid);
        await link.WaitForAsync<LinkAttached>(a => a.ProcessId == second.Pid);
        Assert.DoesNotContain(link.Log, l => l.Contains("Exception", StringComparison.Ordinal) && !l.Contains("COMException", StringComparison.Ordinal));
    }

    // SolidWorks already running with documents open: the link catches up, and the one the
    // student opened (active, with its own window) is theirs; a part an assembly loaded is not.
    [WindowsFact]
    public async Task Documents_open_before_the_link_came_are_caught_up()
    {
        using var sw = await FakeSolidWorks.StartAsync(startAfterMs: 200);
        await sw.WaitForAsync(l => l == "started", TimeSpan.FromSeconds(30));
        var arm = vault.File("Robot 2027/Arm.SLDASM", "arm");
        var plate = vault.File("Robot 2027/Plate.SLDPRT", "plate");
        sw.Send($"open \"{arm}\"");
        sw.Send($"open \"{plate}\" readonly reference");
        await sw.RefsAsync(); // answered after both opens
        await using var link = new LinkUnderTest(vault.Root, () => [sw.Pid]);
        var openedArm = await link.WaitForAsync<LinkOpened>(o => o.Path == arm);
        var openedPlate = await link.WaitForAsync<LinkOpened>(o => o.Path == plate);
        Assert.Equal((SolidWorksDocTypes.Assembly, true), (openedArm.DocType, openedArm.TopLevel));
        Assert.Equal((SolidWorksDocTypes.Part, false), (openedPlate.DocType, openedPlate.TopLevel));
    }

    [WindowsFact]
    public async Task Two_SolidWorks_get_two_sessions()
    {
        using var one = await FakeSolidWorks.StartAsync("33.5.0", startAfterMs: 200);
        using var two = await FakeSolidWorks.StartAsync("34.4.1", startAfterMs: 200);
        await using var link = new LinkUnderTest(vault.Root, () => [one.Pid, two.Pid]);
        var first = await link.WaitForAsync<LinkAttached>(a => a.ProcessId == one.Pid);
        var second = await link.WaitForAsync<LinkAttached>(a => a.ProcessId == two.Pid);
        Assert.Equal((2025, 2026), (first.RunningYear, second.RunningYear));
        Assert.Equal(2, await link.Link.AttachedCountAsync());
    }

    // After a check out: SetReadOnlyState(false) in place; with in place refused and the
    // document changed, nothing is reloaded, closed or discarded.
    [WindowsFact]
    public async Task Make_writable_switches_in_place_and_never_reloads_a_changed_document()
    {
        using var sw = await FakeSolidWorks.StartAsync(startAfterMs: 200);
        await using var link = new LinkUnderTest(vault.Root, () => [sw.Pid]);
        await link.WaitForAsync<LinkAttached>();
        var plate = vault.File("Robot 2027/Plate.SLDPRT", "plate");
        var bracket = vault.File("Robot 2027/Bracket.SLDPRT", "bracket");
        sw.Send($"open \"{plate}\"");
        sw.Send($"open \"{bracket}\"");
        await link.WaitForAsync<LinkOpened>(o => o.Path == bracket);
        Assert.Equal(WritableOutcome.MadeWritableInPlace, await link.Link.MakeWritableAsync(plate, CancellationToken.None));
        Assert.Equal(WritableOutcome.AlreadyWritable, await link.Link.MakeWritableAsync(plate, CancellationToken.None));
        sw.Send($"inplace \"{bracket}\" off");
        sw.Send($"modify \"{bracket}\"");
        await link.WaitForAsync<LinkModified>(m => m.Path == bracket);
        Assert.Equal(WritableOutcome.HasUnsavedChanges, await link.Link.MakeWritableAsync(bracket, CancellationToken.None));
        Assert.DoesNotContain(sw.Lines, l => l.StartsWith("reload", StringComparison.Ordinal) || l.StartsWith("closeAndReopen", StringComparison.Ordinal) || l.StartsWith("call CloseDoc", StringComparison.Ordinal));
        Assert.Equal(WritableOutcome.NotOpen, await link.Link.MakeWritableAsync(vault.File("Robot 2027/Gone.SLDPRT"), CancellationToken.None));
    }

    // SolidWorks 2026 SP4 with the preference numbers known: Save to Version is set to 2025 while
    // a vault document of a project pinned to 2025 is the active one, and the student's own
    // setting comes back when the link lets go.
    [WindowsFact]
    public async Task Save_to_version_follows_the_active_vault_document_and_the_students_own_comes_back()
    {
        var settings = Path.Combine(vault.Root, "solidworks.json");
        File.WriteAllText(settings, "{\"saveToVersionIds\":{\"enableToggle\":570,\"versionValue\":290}}");
        using var sw = await FakeSolidWorks.StartAsync("34.4.1", startAfterMs: 200);
        sw.Send("set-pref 290 0");
        var link = new LinkUnderTest(vault.Root, () => [sw.Pid], settings);
        var attached = await link.WaitForAsync<LinkAttached>();
        Assert.Equal(SaveToVersionSupport.Available, attached.SaveDown);
        link.Link.SetPins([new ProjectPin(Path.Combine(vault.Root, "Robot 2027"), 2025, false)]);
        var plate = vault.File("Robot 2027/Plate.SLDPRT", "plate");
        sw.Send($"open \"{plate}\" writable");
        await link.WaitForAsync<LinkOpened>(o => o.Path == plate);
        await WaitForPrefsAsync(sw, "t570=True", "i290=1");
        sw.Send($"save \"{plate}\"");
        var saving = await link.WaitForAsync<LinkSaving>(s => s.Path == plate);
        Assert.Equal(LinkSaveMode.SaveDown, saving.Mode);
        await link.DisposeAsync();
        await WaitForPrefsAsync(sw, "t570=False", "i290=0");
    }

    private static async Task WaitForPrefsAsync(FakeSolidWorks sw, params string[] wanted)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(20);
        while (true)
        {
            var from = sw.Lines.Count;
            sw.Send("prefs");
            var line = await sw.WaitForAsync(l => l.StartsWith("prefs", StringComparison.Ordinal), TimeSpan.FromSeconds(10), from);
            if (wanted.All(w => line.Contains(w, StringComparison.Ordinal))) return;
            if (DateTime.UtcNow > deadline) throw new TimeoutException($"Save to Version is \"{line}\", not {string.Join(", ", wanted)}.");
            await Task.Delay(100);
        }
    }
}
