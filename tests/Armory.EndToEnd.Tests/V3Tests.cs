using System.Text;
using Armory.Agent.Engine.View;
using Armory.Client;
using Armory.TestSupport;
using static Armory.EndToEnd.Tests.ScenarioTests;

namespace Armory.EndToEnd.Tests;

// The v0.3 app paths end to end (ARMORY.md "The v0.3 server contract (migration 0233)", "What
// the Windows app must do"), on the real server SQL plus the test stand-in for the 0233 parts
// the app calls (ArmoryV3StandIn: can_take_back, armory_project_purged, the batch RPCs, and
// what a purge leaves behind).
public sealed class V3Tests
{
    private const string Gear = "Robot 2027/Old/Gear.SLDPRT", Spacer = "Robot 2027/Old/Spacer.SLDPRT";

    private static void NoViolations(params Computer[] computers)
    {
        foreach (var c in computers)
        {
            Assert.Empty(c.Disk.OpenWriteViolations);
            Assert.Empty(c.Disk.UnpreservedOverwrites);
        }
    }

    // Deleted forever is the one deliberate exception to "nothing is ever lost" (ARMORY.md rule 5):
    // the server's history is gone, so the copies moved aside here are the only bytes that do not
    // match server history, and they went to the recovery folder (moved, never deleted), never
    // over anything, never while open.
    private static void OnlyDeletedForeverMovedAside(Computer c, string folder)
    {
        Assert.Empty(c.Disk.OpenWriteViolations);
        Assert.All(c.Disk.UnpreservedOverwrites, u => Assert.StartsWith("recovery " + folder + "/", u));
    }

    private static async Task<Team> V3TeamAsync()
    {
        var t = await TeamAsync();
        await ArmoryV3StandIn.ApplyCoreAsync(t.World.Database);
        return t;
    }

    // Item 2: the take-back control shows exactly when can_take_back is true (a site admin who is
    // a student here included), and a server older than 0233 falls back to the role. The refusal
    // stays P0001 "only a mentor or cad_lead may break a lock", read by its code and words.
    [PostgresFact]
    public async Task Force_check_in_shows_exactly_when_the_server_says_can_take_back()
    {
        await using var t = await TeamAsync();
        t.A.Write(Plate, "v1");
        await t.A.SyncAsync();
        await t.B.SyncAsync();
        // 0232: no can_take_back, so a student's role says no.
        Assert.False(t.B.Engine.View.Projects.Single().CanTakeBack);
        await ArmoryV3StandIn.ApplyCoreAsync(t.World.Database);
        t.World.Supabase.AdminEmails.Add(Maria);
        await t.B.SyncAsync();
        await t.A.SyncAsync();
        Assert.True(t.B.Engine.View.Projects.Single().CanTakeBack);
        Assert.False(t.A.Engine.View.Projects.Single().CanTakeBack);
        var file = await t.FileId("Plate.SLDPRT");
        Assert.True((await t.A.CheckOutAsync(Plate)).Ok);
        await t.B.SyncAsync();
        Assert.True((await t.B.Engine.GetFileDetailAsync(file))!.CanTakeBack);
        // This SQL (0232's break_lock) refuses a student: the app says so in a sentence.
        var refused = await t.B.TakeBackAsync(file);
        Assert.Equal((false, "Only a mentor or CAD lead can force a check in."), (refused.Ok, refused.Message));
        Assert.Equal(1, t.World.Supabase.RpcCount("armory_break_lock"));
        Assert.Equal(1, await t.LiveLocks(file));
        t.World.Supabase.AdminEmails.Remove(Maria);
        await t.B.SyncAsync();
        Assert.False(t.B.Engine.View.Projects.Single().CanTakeBack);
        Assert.Equal("Only a mentor or CAD lead can force a check in.", (await t.B.TakeBackAsync(file)).Message);
        Assert.Equal(1, t.World.Supabase.RpcCount("armory_break_lock")); // not offered, not asked
    }

    // Item 3, folder_purged: those files and their history are gone. Every computer drops them; one
    // still open goes to the recovery folder once closed; nothing of them is sent again, and a new
    // file at the same path is a new file.
    [PostgresFact]
    public async Task A_folder_deleted_forever_leaves_every_computer_and_is_never_sent_again()
    {
        await using var t = await V3TeamAsync();
        t.A.Write(Gear, "gear v1");
        t.A.Write(Spacer, "spacer v1");
        await t.A.SyncAsync();
        await t.B.SyncAsync();
        var gear = await t.FileId("Gear.SLDPRT");
        var spacer = await t.FileId("Spacer.SLDPRT");
        Assert.Equal(2, await t.Mentor.Api.DeleteFolderAsync(t.Project, "Old", t.Mentor.Device, Guid.NewGuid()));
        t.B.Open(Gear);
        await t.A.SyncAsync();
        await t.B.SyncAsync();
        Assert.Null(t.A.Read(Gear));
        Assert.Equal("gear v1", t.B.Text(Gear)); // open: it waits

        var purged = await ArmoryV3StandIn.PurgeFolderAsync(t.World.Database, t.Project, "Old", Mentor);
        Assert.Equal(2, purged["files"]!.GetValue<int>());
        // Still open on B, and saved again after it was deleted forever: never local work to send.
        t.B.ForceWrite(Gear, "gear, saved after it was deleted forever");
        var writes = Writes(t.World);
        await t.A.SyncTimesAsync(2);
        await t.B.SyncTimesAsync(2);
        // Not one write was even tried for them (the server would refuse it, but it is never asked).
        Assert.Equal(writes, Writes(t.World));
        Assert.DoesNotContain(t.B.Logged, l => l.StartsWith("membership:", StringComparison.Ordinal));
        // Open on B: still there, untouched, not shown, nothing sent for it.
        Assert.Equal("gear, saved after it was deleted forever", t.B.Text(Gear));
        Assert.DoesNotContain(t.B.Engine.View.Projects.SelectMany(p => p.Folders).SelectMany(f => f.Files), f => f.FileId == gear.ToString() || f.FileId == spacer.ToString());
        Assert.DoesNotContain(t.B.NoticeItems, n => n.Item.Path == Gear);
        Assert.Equal(0, await t.World.CountAsync("select count(*) from armory_files where name in ('Gear.SLDPRT', 'Spacer.SLDPRT')"));
        // Saved again now that this computer knows: still never kept as work to send.
        t.B.ForceWrite(Gear, "gear, saved once more");
        await t.B.SyncAsync();
        Assert.Equal(writes, Writes(t.World));
        t.B.Close(Gear);
        await t.B.SyncTimesAsync(2);
        Assert.Null(t.B.Read(Gear));
        Assert.Contains(Gear, t.B.Disk.Recovered);
        Assert.Equal(0, await t.World.CountAsync("select count(*) from armory_files where name in ('Gear.SLDPRT', 'Spacer.SLDPRT')"));
        Assert.Contains(t.B.Logged, l => l.StartsWith("folder deleted forever: Robot 2027 › Old, 2 files by " + Mentor, StringComparison.Ordinal));

        // The name is free again: a new Gear at the same path is a new file, with a new id.
        t.A.Write(Gear, "gear, a new one");
        await t.A.SyncAsync();
        var again = await t.FileId("Gear.SLDPRT");
        Assert.NotEqual(gear, again);
        Assert.Equal(Hash("gear, a new one"), await t.CurrentHash(again));
        await t.B.SyncAsync();
        Assert.Equal("gear, a new one", t.B.Text(Gear));
        OnlyDeletedForeverMovedAside(t.A, "Robot 2027/Old");
        OnlyDeletedForeverMovedAside(t.B, "Robot 2027/Old");
    }

    // Item 3, a project deleted forever: gone from armory_my_projects, armory_project_purged gives a
    // time. Its records and its folder leave this computer quietly (files to the recovery folder,
    // an open one once closed), with one line; it is asked about once.
    [PostgresFact]
    public async Task A_project_deleted_forever_leaves_this_computer_with_one_line()
    {
        await using var t = await V3TeamAsync();
        t.A.Write(Plate, "plate v1");
        t.A.Write(Gear, "gear v1");
        await t.A.SyncAsync();
        await t.B.SyncAsync();
        Assert.True((await t.B.CheckOutAsync(Plate)).Ok);
        t.B.Save(Plate, "Maria's unsent work");
        t.B.Open(Plate);
        Assert.True(await t.Mentor.Api.SetProjectArchivedAsync(t.Project, true, Guid.NewGuid()));
        await ArmoryV3StandIn.PurgeProjectAsync(t.World.Database, t.Project, Mentor);

        await t.A.SyncTimesAsync(3);
        Assert.Null(t.A.Read(Plate));
        Assert.Null(t.A.Read(Gear));
        Assert.Contains(Plate, t.A.Disk.Recovered);
        Assert.Contains(Gear, t.A.Disk.Recovered);
        Assert.False(Directory.Exists(t.A.Disk.Full("Robot 2027")));
        Assert.Empty(t.A.Engine.View.Projects);
        var card = t.A.Card(NoticeKinds.ProjectDeleted)!;
        Assert.Equal("Robot 2027 was deleted forever on ideabosco.com, so Armory took it off this computer.", card.Title);
        Assert.Equal(NoticeTones.Info, card.Tone);
        Assert.Equal(1, t.World.Supabase.RpcCount("armory_project_purged") - 0); // A asked once, however many passes
        Assert.DoesNotContain(t.A.Engine.View.Notices, n => n.Kind == NoticeKinds.CantSend);

        // B had it open, with work never checked in: it stays until closed, is never sent, and
        // nothing calls it "outside every project".
        await t.B.SyncTimesAsync(2);
        Assert.Equal("Maria's unsent work", t.B.Text(Plate));
        Assert.Null(t.B.Read(Gear));
        Assert.DoesNotContain(t.B.Engine.View.Notices, n => n.Kind == NoticeKinds.CantSend);
        Assert.Empty(t.B.Engine.View.MyFiles);
        Assert.NotNull(t.B.Card(NoticeKinds.ProjectDeleted));
        t.B.Close(Plate);
        await t.B.SyncTimesAsync(2);
        Assert.Null(t.B.Read(Plate));
        Assert.Contains(Plate, t.B.Disk.Recovered);
        Assert.False(Directory.Exists(t.B.Disk.Full("Robot 2027")));
        Assert.Equal(2, t.World.Supabase.RpcCount("armory_project_purged"));
        Assert.Equal(0, t.World.Supabase.RpcCount("armory_save_side_version") + t.World.Supabase.RpcCount("armory_save_side_version_with_release"));
        Assert.Equal(SyncStates.Synced, t.B.Engine.View.Sync.State);
        OnlyDeletedForeverMovedAside(t.A, "Robot 2027");
        OnlyDeletedForeverMovedAside(t.B, "Robot 2027");
    }

    // Item 3, removed: armory_project_purged answers null, and it is handled as before 0.3 (not
    // synced, its folder left as it is). Asked once per start.
    [PostgresFact]
    public async Task Removed_from_a_project_is_handled_as_before_and_asked_once_per_start()
    {
        await using var t = await V3TeamAsync();
        t.A.Write(Plate, "plate v1");
        await t.A.SyncAsync();
        await t.B.SyncAsync();
        Assert.True(await t.Mentor.Api.RemoveMemberAsync(t.Project, Maria, Guid.NewGuid()));
        await t.B.SyncTimesAsync(3);
        Assert.Equal(1, t.World.Supabase.RpcCount("armory_project_purged"));
        Assert.Equal("plate v1", t.B.Text(Plate)); // left as it is
        Assert.Empty(t.B.Engine.View.Projects);
        Assert.Null(t.B.Card(NoticeKinds.ProjectDeleted));
        Assert.Empty(t.B.Disk.Recovered);
        Assert.Contains(t.B.Logged, l => l.StartsWith("membership: no longer a member of Robot 2027", StringComparison.Ordinal));
        t.B.Restart();
        await t.B.SyncTimesAsync(2);
        Assert.Equal(2, t.World.Supabase.RpcCount("armory_project_purged"));
        // Added back: it syncs again.
        Assert.True(await t.Mentor.Api.AddMemberAsync(t.Project, Maria, MemberRole.Student, Guid.NewGuid()));
        await t.B.SyncAsync();
        Assert.Single(t.B.Engine.View.Projects);
        NoViolations(t.B);
    }

    // "Starts answering that refusal": a project still listed whose change feed answers P0001 "not a
    // project member" is asked about at once, and the pass carries on.
    [PostgresFact]
    public async Task A_not_a_member_answer_from_the_change_feed_asks_whether_it_was_deleted()
    {
        await using var t = await V3TeamAsync();
        t.A.Write(Plate, "plate v1");
        await t.A.SyncAsync();
        await using (var c = await t.World.Database.OpenAsync())
        {
            await new Npgsql.NpgsqlCommand("""
                alter function public.armory_list_changes(uuid, bigint) rename to armory_list_changes_real;
                create function public.armory_list_changes(p_project uuid, p_after bigint default 0) returns setof public.armory_change_feed
                language plpgsql stable security definer set search_path = '' as $$ begin raise exception 'not a project member'; end $$;
                grant execute on function public.armory_list_changes(uuid, bigint) to authenticated;
                """, c).ExecuteNonQueryAsync();
        }
        var report = await t.A.SyncAsync();
        Assert.True(report.SignedIn);
        Assert.Equal(1, t.World.Supabase.RpcCount("armory_project_purged"));
        Assert.Contains(t.A.Logged, l => l.StartsWith("membership: no longer a member of Robot 2027", StringComparison.Ordinal));
        Assert.Equal("plate v1", t.A.Text(Plate));
        Assert.Null(t.A.Card(NoticeKinds.ProjectDeleted));
    }

    // Item 6: several check outs take their locks in one armory_lock_files, and several check ins
    // let them go in one armory_release_locks. Each file answers on its own: one taken by someone
    // else is held, one the server refuses says why in its words, and the rest are checked out.
    [PostgresFact]
    public async Task Several_check_outs_go_in_one_batch_and_each_file_answers_on_its_own()
    {
        await using var t = await V3TeamAsync();
        string[] names = ["A.SLDPRT", "B.SLDPRT", "C.SLDPRT", "D.SLDPRT"];
        foreach (var name in names) t.A.Write("Robot 2027/Box/" + name, name + " v1");
        await t.A.SyncAsync();
        await t.B.SyncAsync();
        Assert.True((await t.B.CheckOutAsync("Robot 2027/Box/D.SLDPRT")).Ok);
        var c = await t.FileId("C.SLDPRT");
        var (batchLocks, batchReleases, releases) = (t.World.Supabase.RpcCount("armory_lock_files"), t.World.Supabase.RpcCount("armory_release_locks"),
            t.World.Supabase.RpcCount("armory_release_lock"));
        await using (var connection = await t.World.Database.OpenAsync())
        {
            await new Npgsql.NpgsqlCommand($"""
                create function public.test_refuse_c() returns trigger language plpgsql as $$ begin raise exception 'this file is being audited'; end $$;
                create trigger test_refuse_c before insert or update on public.armory_locks for each row when (new.file_id = '{c}') execute function public.test_refuse_c();
                """, connection).ExecuteNonQueryAsync();
        }
        var single = t.World.Supabase.RpcCount("armory_acquire_lock");
        var answer = await t.A.CheckOutAsync("Robot 2027/Box");
        Assert.True(answer.Ok);
        Assert.Equal("Checked out 2 of 4 files. Maria Lopez has 1 of them checked out. 1 file was refused: C.SLDPRT (The server said: this file is being audited).", answer.Message);
        Assert.Equal(batchLocks + 1, t.World.Supabase.RpcCount("armory_lock_files"));
        Assert.Equal(single, t.World.Supabase.RpcCount("armory_acquire_lock"));
        Assert.False(t.A.Disk.IsReadOnly("Robot 2027/Box/A.SLDPRT"));
        Assert.False(t.A.Disk.IsReadOnly("Robot 2027/Box/B.SLDPRT"));
        Assert.True(t.A.Disk.IsReadOnly("Robot 2027/Box/C.SLDPRT"));
        Assert.Equal(2, t.A.Engine.View.MyFiles.Count);
        Assert.Contains(t.A.NoticeItems, n => n.Card.Kind == NoticeKinds.CantSend && n.Item.Path == "Robot 2027/Box/C.SLDPRT");
        Assert.Contains(t.A.Logged, l => l.Contains("armory_lock_files refused Robot 2027/Box/C.SLDPRT: P0001 this file is being audited", StringComparison.Ordinal));

        t.A.Save("Robot 2027/Box/A.SLDPRT", "A v2");
        var checkIn = await t.A.CheckInAsync("Robot 2027/Box");
        Assert.Equal((true, "Checked in 2 files."), (checkIn.Ok, checkIn.Message));
        Assert.Equal(batchReleases + 1, t.World.Supabase.RpcCount("armory_release_locks"));
        Assert.Equal(releases, t.World.Supabase.RpcCount("armory_release_lock"));
        Assert.Equal(0, await t.LiveLocks(await t.FileId("A.SLDPRT")));
        Assert.Equal(0, await t.LiveLocks(await t.FileId("B.SLDPRT")));
        Assert.Equal(Hash("A v2"), await t.CurrentHash(await t.FileId("A.SLDPRT")));
        Assert.True(t.A.Disk.IsReadOnly("Robot 2027/Box/A.SLDPRT"));
        Assert.Empty(t.A.Engine.View.MyFiles);
        NoViolations(t.A, t.B);
    }

    // A site without the batch RPCs (404 PGRST202): the files go one by one, and a batch is not
    // asked for again within the hour.
    [PostgresFact]
    public async Task Without_the_batch_rpcs_the_files_go_one_by_one()
    {
        await using var t = await TeamAsync();
        foreach (var name in new[] { "A.SLDPRT", "B.SLDPRT", "C.SLDPRT" }) t.A.Write("Robot 2027/Box/" + name, name);
        // Three adds check themselves in: one armory_release_locks, refused 404, then one by one.
        await t.A.SyncAsync();
        Assert.Equal(1, t.World.Supabase.RpcCount("armory_release_locks"));
        Assert.Equal(3, t.World.Supabase.RpcCount("armory_release_lock"));
        Assert.Equal(0, await t.World.CountAsync("select count(*) from armory_locks where broken_at is null"));
        Assert.Contains(t.A.Logged, l => l.StartsWith("batches: the site has no armory_release_locks", StringComparison.Ordinal));
        // Within the hour, no batch is asked for.
        var acquires = t.World.Supabase.RpcCount("armory_acquire_lock");
        Assert.Equal("Checked out 3 files.", (await t.A.CheckOutAsync("Robot 2027/Box")).Message);
        Assert.Equal(0, t.World.Supabase.RpcCount("armory_lock_files"));
        Assert.Equal(acquires + 3, t.World.Supabase.RpcCount("armory_acquire_lock"));
        Assert.Equal("Checked in 3 files.", (await t.A.CheckInAsync("Robot 2027/Box")).Message);
        Assert.Equal(1, t.World.Supabase.RpcCount("armory_release_locks"));
        Assert.Equal(6, t.World.Supabase.RpcCount("armory_release_lock"));
        // An hour later it is asked for again, refused again, and the files still go one by one.
        t.A.Clock.Advance(TimeSpan.FromHours(1));
        Assert.Equal("Checked out 3 files.", (await t.A.CheckOutAsync("Robot 2027/Box")).Message);
        Assert.Equal(1, t.World.Supabase.RpcCount("armory_lock_files"));
        Assert.Equal(acquires + 6, t.World.Supabase.RpcCount("armory_acquire_lock"));
        Assert.Contains(t.A.Logged, l => l.StartsWith("batches: the site has no armory_lock_files", StringComparison.Ordinal));
        Assert.Equal(3, t.A.Engine.View.MyFiles.Count);
        NoViolations(t.A);
    }

    // Item 1: Realtime brings a change to another computer within moments although its poll is a
    // minute; an event only wakes it to read again. Every subscription is filtered by project_id,
    // so a site admin's computer never receives a project it does not sync.
    [PostgresFact]
    public async Task Live_updates_wake_another_computer_and_every_subscription_is_filtered()
    {
        await using var t = await TeamAsync();
        // A project this admin is no member of (they made it, gave it a mentor and left it), which
        // 0233 lets a site admin read: an unfiltered subscription would bring its changes too.
        var other = await t.Mentor.Api.CreateProjectAsync("Robot 2026", 2026, Guid.NewGuid());
        await t.Mentor.Api.AddMemberAsync(other, Maria, MemberRole.Mentor, Guid.NewGuid());
        Assert.True(await t.Mentor.Api.RemoveMemberAsync(other, Mentor, Guid.NewGuid()));
        var mentorPc = await t.World.ComputerAsync("mentor laptop", Mentor);
        t.A.Write(Plate, "plate v1");
        await t.A.SyncAsync();
        foreach (var c in new[] { t.B, mentorPc })
        {
            c.Live = true;
            c.Poll = TimeSpan.FromMinutes(1);
            c.Restart();
            await c.SyncAsync();
            c.Engine.Start();
        }
        await Until(() => t.World.Supabase.RealtimeChannels.Count == 3, TimeSpan.FromSeconds(15)); // Maria: 2 projects; the mentor: 1
        var file = await t.FileId("Plate.SLDPRT");
        Assert.Equal("Available", t.B.Row(Plate).Checkout.Label);

        var delivered = t.World.Supabase.RealtimeDelivered;
        Assert.True((await t.A.CheckOutAsync(Plate)).Ok);
        await Until(() => t.B.Engine.View.Projects.SelectMany(p => p.Folders).SelectMany(f => f.Files).Any(f => f.Path == Plate && f.Checkout.State == CheckoutStates.Other),
            TimeSpan.FromSeconds(10));
        Assert.Equal("Checked out by Alex Kim on student A laptop", t.B.Row(Plate).Checkout.Label);
        Assert.True(t.World.Supabase.RealtimeDelivered > delivered);

        // A change in Robot 2026 reaches Maria (who syncs it) and never the mentor's computer.
        var before = t.World.Supabase.RealtimeDelivered;
        var maria = await t.World.PersonAsync(Maria);
        await maria.Api.CreateFileAsync(other, "", "Elsewhere.SLDPRT", maria.Device, Guid.NewGuid());
        await Until(() => t.World.Supabase.RealtimeDelivered > before, TimeSpan.FromSeconds(5));
        await Task.Delay(300);
        Assert.Equal(1, t.World.Supabase.RealtimeDelivered - before);

        var joins = t.World.Supabase.RealtimeJoins;
        Assert.Equal(3, joins.Count);
        Assert.All(joins, j => Assert.Equal(("INSERT", "public", "armory_change_feed", true), (j.Event, j.Schema, j.Table, j.Accepted)));
        Assert.Equal(new[] { $"project_id=eq.{t.Project}", $"project_id=eq.{other}" }.Order(), joins.Where(j => j.Email == Maria).Select(j => j.Filter!).Order());
        Assert.Equal([$"project_id=eq.{t.Project}"], joins.Where(j => j.Email == Mentor).Select(j => j.Filter));
        _ = file;
    }

    // Since 0233 armory_can_view is true for a site admin on every project, so an admin's session
    // can read every project's files and changes. The app syncs armory_my_projects, which stays
    // membership only: an admin who belongs to one project and can read three syncs one, reads
    // only that one, listens to only that one, and never asks for the website's list of all.
    [PostgresFact]
    public async Task An_admin_who_belongs_to_one_project_and_can_read_three_syncs_one()
    {
        await using var t = await V3TeamAsync(); // Mr. Pina (Mentor) is a site admin and Robot 2027's mentor
        var maria = await t.World.PersonAsync(Maria);
        var alex = await t.World.PersonAsync(Alex);
        // Two more projects with files in them, each given a mentor and then left by the admin.
        var others = new List<Guid>();
        foreach (var (name, owner) in new[] { ("Robot 2026", maria), ("Class Projects", alex) })
        {
            var project = await t.Mentor.Api.CreateProjectAsync(name, 2026, Guid.NewGuid());
            await t.Mentor.Api.AddMemberAsync(project, owner.Email, MemberRole.Mentor, Guid.NewGuid());
            Assert.True(await t.Mentor.Api.RemoveMemberAsync(project, Mentor, Guid.NewGuid()));
            var file = await owner.Api.CreateFileAsync(project, "Secret", name.Replace(' ', '-') + ".SLDPRT", owner.Device, Guid.NewGuid());
            await owner.CommitAsync(project, file, Encoding.UTF8.GetBytes(name + " work"));
            others.Add(project);
        }
        t.A.Write(Plate, "plate v1");
        await t.A.SyncAsync();

        // The admin can read all three...
        var all = (await t.Mentor.Rest.CallAsync("armory_project_summaries", new Dictionary<string, object?>()))!.AsArray();
        Assert.Equal(3, all.Count);
        foreach (var project in others)
        {
            Assert.Single(await t.Mentor.Api.ProjectFilesAsync(project));
            Assert.NotEmpty(await t.Mentor.Api.ListChangesAsync(project, 0));
        }
        // ...and belongs to one.
        Assert.Equal([t.Project], (await t.Mentor.Api.MyProjectsAsync()).Select(p => p.Id));

        var office = await t.World.ComputerAsync("pina office PC", Mentor);
        office.Live = true;
        office.Restart();
        t.World.Supabase.RecordRpcArguments = true;
        await office.SyncTimesAsync(3);
        Assert.Equal(["Robot 2027"], office.Engine.View.Projects.Select(p => p.Name));
        Assert.Equal("plate v1", office.Text(Plate));
        Assert.True(Directory.Exists(office.Disk.Full("Robot 2027")));
        Assert.False(Directory.Exists(office.Disk.Full("Robot 2026")));
        Assert.False(Directory.Exists(office.Disk.Full("Class Projects")));
        var known = (await office.Engine.DescribeAsync())["engine"]!["projects"]!.AsArray();
        Assert.Equal([t.Project.ToString()], known.Select(p => p!["id"]!.GetValue<string>()));
        // Every project-scoped read it made named its own project, and it never asked for all.
        var reads = t.World.Supabase.RpcArguments.Where(r => r.Email == Mentor && r.Arguments.TryGetProperty("p_project", out _)).ToList();
        Assert.Contains(reads, r => r.Function == "armory_list_changes");
        Assert.Contains(reads, r => r.Function == "armory_project_files");
        Assert.All(reads, r => Assert.Equal(t.Project.ToString(), r.Arguments.GetProperty("p_project").GetString()));
        Assert.Equal(1, t.World.Supabase.RpcCount("armory_project_summaries")); // the test's own call above
        Assert.DoesNotContain(t.World.Supabase.RpcArguments, r => r.Function == "armory_file_history" && r.Email == Mentor);

        // It listens to its one project only, so another project's changes never reach it.
        office.Engine.Start();
        await Until(() => t.World.Supabase.RealtimeChannels.Count(c => c.Email == Mentor) == 1, TimeSpan.FromSeconds(15));
        Assert.Equal([$"project_id=eq.{t.Project}"], t.World.Supabase.RealtimeJoins.Where(j => j.Email == Mentor).Select(j => j.Filter));
        var delivered = t.World.Supabase.RealtimeDelivered;
        var more = await maria.Api.CreateFileAsync(others[0], "Secret", "More.SLDPRT", maria.Device, Guid.NewGuid());
        await maria.CommitAsync(others[0], more, Encoding.UTF8.GetBytes("more work"));
        await Task.Delay(500);
        Assert.Equal(delivered, t.World.Supabase.RealtimeDelivered);
        Assert.Equal(["Robot 2027"], office.Engine.View.Projects.Select(p => p.Name));
        NoViolations(office);
    }

    // Every write RPC a computer can send, counted.
    private static int Writes(World world) => new[]
    {
        "armory_create_file", "armory_acquire_lock", "armory_release_lock", "armory_lock_files", "armory_release_locks", "armory_commit_version",
        "armory_commit_version_with_release", "armory_save_side_version", "armory_save_side_version_with_release", "armory_tombstone", "armory_move_file",
    }.Sum(world.Supabase.RpcCount);

    private static async Task Until(Func<bool> condition, TimeSpan within)
    {
        var started = DateTime.UtcNow;
        while (!condition() && DateTime.UtcNow - started < within) await Task.Delay(50);
        Assert.True(condition(), $"not within {within.TotalSeconds:0} s");
    }
}
