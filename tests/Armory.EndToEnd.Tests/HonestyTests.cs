using System.Text;
using Armory.Agent.Engine;
using Armory.Agent.Engine.View;
using Armory.Telemetry;
using Armory.TestSupport;
using static Armory.EndToEnd.Tests.ScenarioTests;

namespace Armory.EndToEnd.Tests;

// The engine says what is true, and only once (0.3.3, from IDEA-06's incidents and the audit
// docs/agent/feedback-audit.md): a copy whose name another file holds is refused at the plan and
// left alone (it was planned, expected as an upload and refused again on every pass: "moving
// 142", "142 refused", "Uploading 0 of 142 files"); an archived project's files are never
// news or "uploading"; a record that never got its first version is said so; a sign-out is a
// stop, never a refusal; and the flight and the snapshot name the files.
public sealed class HonestyTests
{
    private const string Copy = "Robot 2027/Intake/Plate.SLDPRT";
    private const string NameTakenWords = "Robot 2027 already has Plate.SLDPRT in Drivetrain.";

    private static List<FlightEvent> Since(FlightRecorder flight, long sequence) => flight.Snapshot().Where(e => e.Sequence > sequence).ToList();
    private static int Rpc(Team t, string function) => t.World.Supabase.RpcCount(function);
    private static List<string> LoggedSince(Computer c, int count) { lock (c.Logged) return c.Logged.Skip(count).ToList(); }

    // The team has Plate.SLDPRT in Drivetrain; both computers have it.
    private static async Task<Team> PlateTeamAsync()
    {
        var t = await TeamAsync();
        t.B.Write(Plate, "team plate");
        await t.B.SyncAsync();
        await t.A.SyncAsync();
        return t;
    }

    [PostgresFact]
    public async Task A_name_taken_copy_is_refused_once_and_left_alone()
    {
        await using var t = await PlateTeamAsync();
        t.A.Write(Copy, "alex copy");
        t.A.Write("Robot 2027/Intake/Roller.SLDPRT", "roller");
        var flightBefore = t.A.Flight.Recorded;
        var first = await t.A.SyncAsync();
        Assert.Equal((1, 1), (first.Uploaded, first.Refused));
        var card = t.A.Card(NoticeKinds.NameShared)!;
        Assert.Equal(NameTakenWords, Assert.Single(card.Items).Detail);
        // A SolidWorks copy is told which of the two ways out fits.
        Assert.Equal("A project keeps one file per name, because SolidWorks finds parts by name. If it's the same part as the team's, delete your copy " +
            "and use the team's. If it's a different part, give it a new name in SolidWorks (Save As, or Pack and Go with a prefix) so your assemblies follow it.", card.Detail);
        // The flight has the refusal once: the copy, why, and the file that holds its name.
        var refusal = Assert.Single(Since(t.A.Flight, flightBefore), e => e.Kind == FlightKind.Refusal);
        Assert.Equal((Copy, "nameTaken", Plate), (refusal.Target, refusal.Name, refusal.Detail));

        // Every later pass leaves it alone: nothing counted, no server call for it, nothing saved
        // again (after the one pass that reads this pass's own changes back), nothing in the flight.
        Assert.Equal(0, (await t.A.SyncAsync()).Refused);
        var (creates, saves) = (Rpc(t, "armory_create_file"), t.A.State.Saves);
        flightBefore = t.A.Flight.Recorded;
        for (var pass = 0; pass < 3; pass++) Assert.Equal(0, (await t.A.SyncAsync()).Refused);
        Assert.Equal(creates, Rpc(t, "armory_create_file"));
        Assert.Equal(saves, t.A.State.Saves);
        Assert.DoesNotContain(Since(t.A.Flight, flightBefore), e => e.Kind == FlightKind.Refusal);
        Assert.All(Since(t.A.Flight, flightBefore).Where(e => e.Kind == FlightKind.PassEnd), e => Assert.Equal(0, e.Count4));
        // It is still one card, its words unchanged, and the copy is untouched and not in Armory.
        Assert.Equal(NameTakenWords, Assert.Single(t.A.Card(NoticeKinds.NameShared)!.Items).Detail);
        Assert.Equal("alex copy", t.A.Text(Copy));
        Assert.Equal(FileStatuses.NotInArmory, t.A.Row(Copy).Status);
        Assert.Equal(0, await t.World.CountAsync("select count(*) from armory_files where project_id=@p and folder='Intake' and name='Plate.SLDPRT'", ("p", t.Project)));
    }

    [PostgresFact]
    public async Task A_name_taken_copy_goes_in_when_its_namesake_is_renamed()
    {
        await using var t = await PlateTeamAsync();
        t.A.Write(Copy, "alex copy");
        await t.A.SyncAsync();
        Assert.NotNull(t.A.Card(NoticeKinds.NameShared));
        // Maria renames the team's Plate: Alex's copy goes in by itself on his next pass.
        Assert.True((await t.B.Engine.RenameFileAsync(Plate, "Plate-Drivetrain.SLDPRT")).Ok);
        var flightBefore = t.A.Flight.Recorded;
        await t.A.SyncTimesAsync(2);
        Assert.Null(t.A.Card(NoticeKinds.NameShared));
        Assert.Equal(Hash("alex copy"), await t.CurrentHash(await t.FileId("Plate.SLDPRT")));
        Assert.Equal(FileStatuses.Synced, t.A.Row(Copy).Status);
        // The flight says the refusal is over, once.
        var ended = Assert.Single(Since(t.A.Flight, flightBefore), e => e.Kind == FlightKind.Refusal);
        Assert.Equal((Copy, null, true), (ended.Target, ended.Name, ended.Ok));

        // A copy renamed by the student goes in under its new name; one deleted leaves nothing waiting.
        t.B.Write("Robot 2027/Drivetrain/Gear.SLDPRT", "team gear");
        t.B.Write("Robot 2027/Drivetrain/Bolt.SLDPRT", "team bolt");
        await t.B.SyncAsync();
        await t.A.SyncAsync();
        t.A.Write("Robot 2027/Intake/Gear.SLDPRT", "alex gear");
        t.A.Write("Robot 2027/Intake/Bolt.SLDPRT", "alex bolt");
        await t.A.SyncAsync();
        Assert.Equal(2, t.A.Card(NoticeKinds.NameShared)!.Count);
        File.Move(t.A.Disk.Full("Robot 2027/Intake/Gear.SLDPRT"), t.A.Disk.Full("Robot 2027/Intake/Intake Gear.SLDPRT"));
        t.A.Delete("Robot 2027/Intake/Bolt.SLDPRT");
        await t.A.SyncTimesAsync(2);
        Assert.Null(t.A.Card(NoticeKinds.NameShared));
        Assert.Equal(Hash("alex gear"), await t.CurrentHash(await t.FileId("Intake Gear.SLDPRT")));
        Assert.Equal(0, t.A.Engine.View.Sync.PendingCount);
        Assert.Equal("Everything is saved to Armory.", t.A.Engine.View.Sync.Line);
    }

    [PostgresFact]
    public async Task A_name_taken_copy_is_never_counted_as_moving()
    {
        await using var t = await PlateTeamAsync();
        t.A.Write(Copy, "alex copy");
        t.A.Write("Robot 2027/Intake/Roller.SLDPRT", "roller");
        var lines = new List<string?>();
        t.A.Activities += a => { lock (lines) lines.Add(a.Line); };
        var logged = t.A.Logged.Count;
        var first = await t.A.SyncAsync();
        Assert.Equal((1, 1), (first.Uploaded, first.Refused));
        // Moving: Roller alone, of the two files planned (Plate and Roller; the copy is no plan).
        Assert.Contains("pass: moving 1 of 2 files (whole)", LoggedSince(t.A, logged));
        Assert.Contains(LoggedSince(t.A, logged), l => l.StartsWith("pass: ended", StringComparison.Ordinal) && l.EndsWith(", 1 uploaded, 0 kept copies, 1 refused", StringComparison.Ordinal));
        // Only Roller was ever expected as an upload.
        lock (lines) Assert.All(lines.OfType<string>().Where(l => l.StartsWith("Uploading", StringComparison.Ordinal)), l => Assert.Contains("of 1 file", l));
        // Later passes: nothing moving, nothing refused, no log line at all.
        logged = t.A.Logged.Count;
        for (var pass = 0; pass < 3; pass++)
        {
            var report = await t.A.SyncAsync();
            Assert.Equal((0, 0, 0, 0), (report.Uploaded, report.Downloaded, report.SideVersions, report.Refused));
        }
        Assert.DoesNotContain(LoggedSince(t.A, logged), l => l.StartsWith("pass:", StringComparison.Ordinal));
    }

    [PostgresFact]
    public async Task A_name_taken_copy_is_not_retried_by_the_archive_of_earlier_saves()
    {
        await using var t = await PlateTeamAsync();
        // Two saves of the copy while offline: the first is an earlier save, kept on this computer.
        t.A.Offline = true;
        t.A.Write(Copy, "alex copy v1");
        await t.A.SyncAsync();
        t.A.Write(Copy, "alex copy v2");
        await t.A.SyncAsync();
        t.A.Offline = false;
        var flightBefore = t.A.Flight.Recorded;
        Assert.Equal(1, (await t.A.SyncAsync()).Refused);
        var creates = Rpc(t, "armory_create_file");
        for (var pass = 0; pass < 3; pass++) Assert.Equal(0, (await t.A.SyncAsync()).Refused);
        Assert.Equal(creates, Rpc(t, "armory_create_file"));
        Assert.Single(Since(t.A.Flight, flightBefore), e => e.Kind == FlightKind.Refusal);
        Assert.False(t.World.HashOnServer(Hash("alex copy v1")));
        // Once the name is free, the copy goes in and its earlier save is kept in its history.
        Assert.True((await t.B.Engine.RenameFileAsync(Plate, "Plate-Drivetrain.SLDPRT")).Ok);
        await t.A.SyncTimesAsync(3);
        var copy = await t.FileId("Plate.SLDPRT");
        Assert.Equal(Hash("alex copy v2"), await t.CurrentHash(copy));
        // The copy went in by its plan, its earlier save after it, kept on top of its version (the
        // archive never added it on its own, ahead of the bytes on disk).
        Assert.Equal(["earlier save, kept|" + (await t.World.QueryAsync("select current_version_id::text from armory_files where id=@f", r => r.GetString(0), ("f", copy))).Single()],
            await t.World.QueryAsync("select reason||'|'||coalesce(parent_version_id::text, 'none') from armory_side_versions where file_id=@f", r => r.GetString(0), ("f", copy)));
        Assert.True(t.World.HashOnServer(Hash("alex copy v1")));
        Assert.Null(t.A.Card(NoticeKinds.NameShared));
    }

    // The window while the only new files are name-blocked: never "Uploading", and the status
    // line says how many and why (IDEA-06 flashed "Uploading 0 of 142 files, 260.9 MB left"
    // between "Checking for changes." and "A few files need you", every pass).
    [PostgresFact]
    public async Task Name_blocked_files_never_read_as_uploading()
    {
        await using var t = await TeamAsync();
        for (var i = 1; i <= 25; i++) t.B.Write($"Robot 2027/Parts/Part-{i:D3}.SLDPRT", "part " + i);
        await t.B.SyncAsync();
        await t.A.SyncAsync();
        // What the window could show at any moment: every activity message and view, and the
        // activity line at every step of every pass (a pass this quick raises few messages).
        var lines = new List<string>();
        t.A.Activities += a => { lock (lines) lines.Add(a.Line ?? ""); };
        t.A.Views += v => { lock (lines) { lines.Add(v.Sync.Line); lines.Add(v.Activity.Line ?? ""); } };
        t.A.CrashPoint = _ => { lock (lines) lines.Add(t.A.Engine.ActivityNow.Line ?? ""); };
        t.A.Restart();
        for (var i = 1; i <= 25; i++) t.A.Write($"Robot 2027/Copied Assembly/Part-{i:D3}.SLDPRT", "copy " + i);
        await t.A.SyncTimesAsync(3);
        lock (lines) Assert.DoesNotContain(lines, l => l.StartsWith("Uploading", StringComparison.Ordinal));
        var view = t.A.Engine.View;
        Assert.Equal((SyncStates.Attention, "Everything else is saved. 25 files can't be added until they have names of their own."), (view.Sync.State, view.Sync.Line));
        Assert.Equal(0, view.Sync.PendingCount);
        Assert.Null(view.Activity.Upload);
        // One running line says it, once.
        Assert.Single(view.Activity.Log, l => l.Line == "25 files are waiting for you: their names are taken in Robot 2027.");
        Assert.All(view.Projects.SelectMany(p => p.Folders).Where(f => f.Path == "Copied Assembly").SelectMany(f => f.Files),
            r => Assert.Equal(FileStatuses.NotInArmory, r.Status));
        // The incident snapshot names them (the first 20 of the card) and counts refusals by kind.
        var snapshot = await t.A.Engine.DescribeAsync();
        var names = snapshot["notices"]!.AsArray().Single(n => n!["kind"]!.GetValue<string>() == NoticeKinds.NameShared)!;
        Assert.Equal(25, names["count"]!.GetValue<int>());
        Assert.Equal(20, names["items"]!.AsArray().Count);
        Assert.Equal("Robot 2027/Copied Assembly/Part-001.SLDPRT", names["items"]![0]!["path"]!.GetValue<string>());
        Assert.Equal("Robot 2027 already has Part-001.SLDPRT in Parts.", names["items"]![0]!["detail"]!.GetValue<string>());
        Assert.Equal(25, snapshot["refusals"]!["nameTaken"]!.GetValue<int>());
        Assert.Equal(view.Sync.Line, snapshot["sync"]!["line"]!.GetValue<string>());
    }

    // An archived project is skipped (decision D8): its refusals are not news, and its new files
    // are "not in Armory", never "uploading" under a status line that says everything is saved.
    [PostgresFact]
    public async Task A_refusal_in_an_archived_project_is_not_a_notice_and_its_new_file_is_not_uploading()
    {
        await using var t = await PlateTeamAsync();
        t.A.Write(Copy, "alex copy");
        await t.A.SyncAsync();
        Assert.NotNull(t.A.Card(NoticeKinds.NameShared));
        t.A.Offline = true;
        const string Roller = "Robot 2027/Intake/Roller.SLDPRT";
        t.A.Write(Roller, "roller"); // never added
        await t.A.SyncAsync();
        t.A.Offline = false;
        Assert.True(await t.Mentor.Api.SetProjectArchivedAsync(t.Project, true, Guid.NewGuid()));
        await t.A.SyncTimesAsync(2);
        Assert.Null(t.A.Card(NoticeKinds.NameShared));
        Assert.Equal(FileStatuses.NotInArmory, t.A.Row(Roller).Status);
        Assert.Equal(FileStatuses.NotInArmory, t.A.Row(Copy).Status);
        var view = t.A.Engine.View;
        Assert.Equal((SyncStates.Synced, "Everything is saved to Armory."), (view.Sync.State, view.Sync.Line));
        // No row says Uploading while the status says everything is saved.
        Assert.DoesNotContain(view.Projects.SelectMany(p => p.Folders).SelectMany(f => f.Files), r => r.Status == FileStatuses.Uploading);
    }

    // A record whose first version never arrived is said so on every computer, never "uploading"
    // forever (38 of them in FRC 2026 Off-Season); the computer whose add it was removes it once
    // its file is gone, its save kept in its history.
    [PostgresFact]
    public async Task A_create_without_a_first_version_is_not_shown_uploading()
    {
        await using var t = await TeamAsync();
        // Made on the website's side, with no version: nobody's computer holds its add.
        await t.Mentor.Api.CreateFileAsync(t.Project, "Drivetrain", "Empty.SLDPRT", t.Mentor.Device, Guid.NewGuid());
        const string Empty = "Robot 2027/Drivetrain/Empty.SLDPRT";
        foreach (var c in new[] { t.A, t.B })
        {
            await c.SyncTimesAsync(3);
            Assert.Equal(FileStatuses.NoVersion, c.Row(Empty).Status);
            Assert.Equal((SyncStates.Synced, "Everything is saved to Armory."), (c.Engine.View.Sync.State, c.Engine.View.Sync.Line));
            Assert.Null(c.Read(Empty));
        }
        Assert.Equal(0, await t.World.CountAsync("select count(*) from armory_files where name='Empty.SLDPRT' and deleted_at is not null"));

        // Alex's own add stops right after its create, and he deletes the file before it goes on.
        const string Gear = "Robot 2027/Drivetrain/Gear.SLDPRT";
        t.A.CrashPoint = point => { if (point == "after-create") throw new SimulatedCrash(point); };
        t.A.Restart();
        t.A.Write(Gear, "gear");
        await Assert.ThrowsAsync<SimulatedCrash>(() => t.A.SyncAsync());
        t.A.CrashPoint = null;
        t.A.Delete(Gear);
        t.A.Restart();
        await t.A.SyncAsync();
        await t.B.SyncAsync();
        Assert.Equal(FileStatuses.NoVersion, t.B.Row(Gear).Status);
        // A second scan without it: the empty record goes for the team, the save kept in its history.
        await t.A.SyncTimesAsync(2);
        var gear = await t.FileId("Gear.SLDPRT");
        Assert.Equal(1, await t.World.CountAsync("select count(*) from armory_files where id=@f and deleted_at is not null", ("f", gear)));
        Assert.True(t.World.HashOnServer(Hash("gear")));
        Assert.Equal(0, await t.LiveLocks(gear));
        await t.B.SyncAsync();
        Assert.DoesNotContain(t.B.Engine.View.Projects.SelectMany(p => p.Folders).SelectMany(f => f.Files), r => r.Path == Gear);
        // The website's empty record stays: no computer holds its add.
        Assert.Equal(FileStatuses.NoVersion, t.A.Row(Empty).Status);
        Assert.DoesNotContain(t.A.Engine.View.Notices, n => n.Tone != NoticeTones.Info);
    }

    // A sign-out in the middle of a pass is a stop like going offline: the write stays in flight
    // and goes once the computer is connected again. Never a refusal ("This save couldn't be read
    // back from this computer's safe copy", as 0.3.1 said for good).
    [PostgresFact]
    public async Task A_sign_out_mid_upload_is_not_a_refusal()
    {
        await using var t = await TeamAsync();
        await t.A.SyncAsync();
        const string Roller = "Robot 2027/Intake/Roller.SLDPRT";
        t.A.Write(Roller, "roller");
        var signedOut = false;
        t.A.CrashPoint = point => { if (point == "before-commit" && !signedOut) { signedOut = true; t.A.Sessions.SignOut(); } };
        t.A.Restart();
        var report = await t.A.SyncAsync();
        Assert.True(signedOut);
        Assert.Equal(0, report.Refused);
        Assert.False(report.Online);
        Assert.DoesNotContain(t.A.Engine.View.Notices, n => n.Kind is NoticeKinds.CantSend or NoticeKinds.CantRead);
        var saved = EngineState.Load(t.A.State).Files[Roller];
        Assert.Null(saved.Refusal);
        Assert.Equal("commit", saved.Inflight?.Kind);
        Assert.Contains(t.A.Engine.View.Activity.Log, l => l.Line == "This computer was signed out of Armory. Your work is safe here until you connect it again.");
        // Connected again: the save goes in, nothing lost, nothing refused.
        t.A.CrashPoint = null;
        await t.A.ConnectAsync(Alex);
        Assert.Equal(0, (await t.A.SyncAsync()).Refused);
        Assert.Equal(Hash("roller"), await t.CurrentHash(await t.FileId("Roller.SLDPRT")));
        Assert.Empty(t.A.Engine.View.Notices);
    }

    // A stale ~$ marker is in the flight once, not on every pass (IDEA-06's 224 markers spent
    // every pass's 200 notices and hid everything else).
    [PostgresFact]
    public async Task A_stale_marker_is_recorded_once_not_every_pass()
    {
        await using var t = await PlateTeamAsync();
        t.A.Open(Plate);
        t.A.Disk.CrashApp(Plate);
        await t.A.SyncAsync();
        t.A.Clock.Advance(TimeSpan.FromMinutes(11));
        var before = t.A.Flight.Recorded;
        await t.A.SyncTimesAsync(4);
        Assert.Single(Since(t.A.Flight, before), e => e.Kind == FlightKind.Notice && e.Target == Plate);
        Assert.Contains(t.A.Engine.View.Notices, n => n.Title == "SolidWorks may have closed unexpectedly");
    }

    // Two computers both named IDEA-06 (imaged alike) read apart wherever both are known: the
    // name with the first four characters of its device id.
    [PostgresFact]
    public async Task Two_computers_with_one_name_read_apart()
    {
        await using var t = await TeamAsync();
        var abraham = await t.World.ComputerAsync("IDEA-06 one", Alex, deviceName: "IDEA-06");
        var seraj = await t.World.ComputerAsync("IDEA-06 two", Maria, deviceName: "IDEA-06");
        const string Bracket = "Robot 2027/Drivetrain/Bracket.SLDPRT";
        abraham.Write(Plate, "plate");
        abraham.Write(Bracket, "bracket");
        await abraham.SyncAsync();
        Assert.True((await abraham.CheckOutAsync(Plate)).Ok);
        var tag = abraham.DeviceId.ToString("N")[..4];
        await seraj.SyncAsync();
        await t.B.SyncAsync();
        // Seraj's computer is an IDEA-06 too: Abraham's reads apart from it.
        Assert.Equal($"Checked out by Alex Kim on IDEA-06 ({tag})", seraj.Row(Plate).Checkout.Label);
        Assert.Equal($"IDEA-06 ({tag})", seraj.Row(Plate).Checkout.Device);
        Assert.Equal($"Alex Kim on IDEA-06 ({tag}) has Plate.SLDPRT checked out, so it can't be renamed now. Ask them to check it in, or ask a mentor or CAD lead to force check it in.",
            (await seraj.Engine.RenameFileAsync(Plate, "Plate v2.SLDPRT")).Message);
        // Maria's lab PC knows one IDEA-06 only: the plain name.
        Assert.Equal("Checked out by Alex Kim on IDEA-06", t.B.Row(Plate).Checkout.Label);
        // Once it knows both, both read apart.
        Assert.True((await seraj.CheckOutAsync(Bracket)).Ok);
        await t.B.SyncAsync();
        Assert.Equal($"Checked out by Alex Kim on IDEA-06 ({tag})", t.B.Row(Plate).Checkout.Label);
        Assert.Equal($"Checked out by you on IDEA-06 ({seraj.DeviceId.ToString("N")[..4]})", t.B.Row(Bracket).Checkout.Label);
    }
}
