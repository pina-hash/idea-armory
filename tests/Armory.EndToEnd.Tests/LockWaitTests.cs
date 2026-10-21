using Armory.Agent.Engine.View;
using Armory.TestSupport;
using static Armory.EndToEnd.Tests.ScenarioTests;

namespace Armory.EndToEnd.Tests;

// 0.4.0 (repeatedFailure incidents 0063246e, 54c9e3c4, 3cdc2ff5 on 0.3.3): a file SolidWorks
// still holds ("being used by another process") waits, in an information notice, and goes up the
// moment SolidWorks lets go. Never a red error, never a repeated-failure incident.
public sealed class LockWaitTests
{
    private const string Template = "Robot 2027/Drivetrain/BLADE_TEMPLATE.SLDPRT";
    private const string Mine = "Robot 2027/Drivetrain/matthew body design 1.SLDPRT";

    // The official workflow: open a file you have not checked out (read-only), change it, and
    // Save As a new file. The new file is added to Armory; while SolidWorks still has it, it waits.
    [PostgresFact]
    public async Task Save_as_a_new_file_from_a_template_you_did_not_check_out_waits_then_uploads()
    {
        await using var t = await TeamAsync();
        t.A.Write(Template, "template");
        await t.A.SyncAsync();
        Assert.True(t.A.Disk.IsReadOnly(Template));
        t.A.Open(Template); // opened read-only, never checked out
        // Save As: SolidWorks writes the new file and keeps it.
        t.A.Write(Mine, "my blade");
        t.A.Disk.HoldUnreadable(Mine);
        await t.A.SyncTimesAsync(4);
        var view = t.A.Engine.View;
        Assert.DoesNotContain(view.Notices, n => n.Tone == NoticeTones.Bad);
        Assert.DoesNotContain(view.Notices, n => n.Kind == NoticeKinds.CantRead);
        var waiting = Assert.Single(view.Notices, n => n.Kind == NoticeKinds.LockWait);
        Assert.Equal(NoticeTones.Info, waiting.Tone);
        Assert.Equal("Waiting for SolidWorks to let go of matthew body design 1.SLDPRT", waiting.Title);
        Assert.NotEqual(SyncStates.Attention, view.Sync.State);
        Assert.Equal("Waiting for SolidWorks to let go of 1 file, then uploading it.", view.Sync.Now);
        // No failure in the flight, so no repeated-failure incident can follow.
        Assert.DoesNotContain(t.A.Flight.Snapshot(), e => e.Kind == Armory.Telemetry.FlightKind.FileFailed);
        // SolidWorks lets go: it goes up, and the template was never changed.
        t.A.Disk.Unhold(Mine);
        t.A.Close(Template);
        await t.A.SyncTimesAsync(2);
        var file = await t.FileId("matthew body design 1.SLDPRT");
        Assert.Equal(Hash("my blade"), await t.CurrentHash(file));
        Assert.DoesNotContain(t.A.Engine.View.Notices, n => n.Kind == NoticeKinds.LockWait);
        Assert.Equal(Hash("template"), await t.CurrentHash(await t.FileId("BLADE_TEMPLATE.SLDPRT")));
        Assert.Equal("Watching your folder for saves.", t.A.Engine.View.Sync.Now);
    }

    // A checked-out part SolidWorks holds while a pass wants to read it: the same waiting.
    [PostgresFact]
    public async Task A_checked_out_part_held_by_another_program_waits_without_an_error()
    {
        await using var t = await TeamAsync();
        t.A.Write(Plate, "v1");
        await t.A.SyncAsync();
        Assert.True((await t.A.CheckOutAsync(Plate)).Ok);
        t.A.Save(Plate, "v2");
        t.A.Disk.HoldUnreadable(Plate);
        await t.A.SyncTimesAsync(4);
        Assert.DoesNotContain(t.A.Engine.View.Notices, n => n.Tone == NoticeTones.Bad);
        Assert.Contains(t.A.Engine.View.Notices, n => n.Kind == NoticeKinds.LockWait);
        Assert.DoesNotContain(t.A.Flight.Snapshot(), e => e.Kind == Armory.Telemetry.FlightKind.FileFailed);
        t.A.Disk.Unhold(Plate);
        Assert.True((await t.A.CheckInAsync(Plate)).Ok);
        await t.A.SyncAsync();
        Assert.Equal(Hash("v2"), await t.CurrentHash(await t.FileId("Plate.SLDPRT")));
        Assert.DoesNotContain(t.A.Engine.View.Notices, n => n.Kind == NoticeKinds.LockWait);
    }
}
