using Armory.Client;
using Armory.TestSupport;
using Npgsql;
using static Armory.EndToEnd.Tests.ScenarioTests;

namespace Armory.EndToEnd.Tests;

public sealed class FileActionsTests
{
    [PostgresFact]
    public async Task A_drop_moves_the_same_file_and_history_to_an_empty_folder_on_both_computers()
    {
        await using var t = await TeamAsync();
        await ArmoryV3StandIn.ApplySharedFoldersAsync(t.World.Database);
        t.A.Write(Plate, "original bytes"); await t.A.SyncAsync(); await t.B.SyncAsync();
        var id = await t.FileId("Plate.SLDPRT");
        Assert.True((await t.A.Engine.CreateFolderAsync(t.Project, "", "Empty")).Ok);
        var moved = await t.A.Engine.MoveFileAsync(Plate, id, t.Project, "Empty");
        Assert.True(moved.Ok, moved.Message);
        await t.A.SyncTimesAsync(2); await t.B.SyncTimesAsync(2);
        Assert.Equal("original bytes", t.A.Text("Robot 2027/Empty/Plate.SLDPRT"));
        Assert.Equal("original bytes", t.B.Text("Robot 2027/Empty/Plate.SLDPRT"));
        Assert.Null(t.A.Read(Plate)); Assert.Null(t.B.Read(Plate));
        Assert.Equal(id, await t.FileId("Plate.SLDPRT")); Assert.Equal(1, await t.Versions(id));
        Assert.Empty(t.A.Disk.OpenWriteViolations); Assert.Empty(t.B.Disk.UnpreservedOverwrites);
        t.A.Restart(); await t.A.SyncAsync();
        var back = await t.A.Engine.MoveFileAsync("Robot 2027/Empty/Plate.SLDPRT", id, t.Project, "");
        Assert.True(back.Ok, back.Message);
        Assert.Equal("original bytes", t.A.Text("Robot 2027/Plate.SLDPRT"));
    }

    [PostgresFact]
    public async Task Drops_refuse_stale_identity_other_projects_bad_folders_open_files_and_other_holders()
    {
        await using var t = await TeamAsync();
        t.A.Write(Plate, "safe bytes"); await t.A.SyncAsync(); await t.B.SyncAsync();
        var id = await t.FileId("Plate.SLDPRT");
        Assert.False((await t.A.Engine.MoveFileAsync(Plate, Guid.NewGuid(), t.Project, "")).Ok);
        Assert.False((await t.A.Engine.MoveFileAsync(Plate, id, Guid.NewGuid(), "")).Ok);
        foreach (var folder in new[] { "../Outside", "/Absolute", "Missing", "Drivetrain" })
            Assert.False((await t.A.Engine.MoveFileAsync(Plate, id, t.Project, folder)).Ok);
        t.A.Open(Plate);
        Assert.False((await t.A.Engine.MoveFileAsync(Plate, id, t.Project, "")).Ok); t.A.Close(Plate);
        Assert.True((await t.B.CheckOutAsync(Plate)).Ok);
        Assert.False((await t.A.Engine.MoveFileAsync(Plate, id, t.Project, "")).Ok);
        Assert.Equal(Maria, await t.Holder(id));
        Assert.Equal(0, t.World.Supabase.RpcCount("armory_break_lock"));
        Assert.Equal("safe bytes", t.A.Text(Plate));
        Assert.Equal(0, await t.World.CountAsync("select count(*) from armory_change_feed where kind='file_moved'"));
        Assert.True((await t.B.CheckInAsync(Plate)).Ok);
        await t.Mentor.Api.SetProjectArchivedAsync(t.Project, true, Guid.NewGuid());
        Assert.False((await t.A.Engine.MoveFileAsync(Plate, id, t.Project, "")).Ok);
    }

    [PostgresFact]
    public async Task A_lost_move_answer_survives_restart_without_another_move_or_lost_bytes()
    {
        await using var t = await TeamAsync();
        t.A.Write(Plate, "original bytes"); await t.A.SyncAsync(); await t.B.SyncAsync();
        var id = await t.FileId("Plate.SLDPRT");
        t.World.Supabase.DropRpcAcknowledgement("armory_move_file", 1);
        await t.A.Engine.MoveFileAsync(Plate, id, t.Project, "");
        t.A.Restart(); await t.A.SyncTimesAsync(3); await t.B.SyncTimesAsync(3);
        Assert.Equal("original bytes", t.A.Text("Robot 2027/Plate.SLDPRT"));
        Assert.Equal("original bytes", t.B.Text("Robot 2027/Plate.SLDPRT"));
        Assert.Equal(1, await t.Versions(id));
        Assert.Equal(1, await t.World.CountAsync("select count(*) from armory_change_feed where kind='file_moved'"));
        Assert.Empty(t.A.Disk.UnpreservedOverwrites); Assert.Empty(t.B.Disk.UnpreservedOverwrites);
    }

    [PostgresFact]
    public async Task A_drop_never_overwrites_an_unshared_destination_file()
    {
        await using var t = await TeamAsync();
        t.A.Write(Plate, "original bytes"); await t.A.SyncAsync();
        var id = await t.FileId("Plate.SLDPRT");
        t.A.Write("Robot 2027/Plate.SLDPRT", "unshared destination");
        var refused = await t.A.Engine.MoveFileAsync(Plate, id, t.Project, "");
        Assert.False(refused.Ok);
        Assert.Equal("unshared destination", t.A.Text("Robot 2027/Plate.SLDPRT"));
        Assert.Equal("original bytes", t.A.Text(Plate));
        Assert.Equal(1, await t.Versions(id));
        Assert.Equal(0, await t.World.CountAsync("select count(*) from armory_change_feed where kind='file_moved'"));
    }

    [PostgresFact]
    public async Task A_drop_refused_by_class_folder_permissions_keeps_the_source_and_its_history()
    {
        await using var t = await TeamAsync();
        await ArmoryV3StandIn.ApplyFileActionsAsync(t.World.Database);
        await ArmoryV3StandIn.ApplySharedFoldersAsync(t.World.Database);
        t.A.Write(Plate, "safe bytes"); await t.A.SyncAsync();
        var id = await t.FileId("Plate.SLDPRT");
        await using (var c = await t.World.Database.OpenAsync())
        {
            await using var command = new NpgsqlCommand("insert into armory_folders(project_id,folder,owner_email) values ($1,'Private',$2)", c);
            command.Parameters.AddWithValue(t.Project); command.Parameters.AddWithValue(Maria);
            await command.ExecuteNonQueryAsync();
        }
        t.A.Disk.EnsureFolder("Robot 2027/Private");
        await t.A.SyncAsync();
        var refused = await t.A.Engine.MoveFileAsync(Plate, id, t.Project, "Private");
        Assert.False(refused.Ok, refused.Message);
        Assert.Equal("safe bytes", t.A.Text(Plate)); Assert.Equal(1, await t.Versions(id));
        Assert.Equal(0, await t.World.CountAsync("select count(*) from armory_change_feed where kind='file_moved'"));
    }

    [PostgresFact]
    public async Task A_stale_file_page_never_trashes_a_different_file_that_reuses_its_path()
    {
        await using var t = await TeamAsync();
        await ArmoryV3StandIn.ApplyFileActionsAsync(t.World.Database);
        t.A.Write(Plate, "original"); await t.A.SyncTimesAsync(2);
        var original = await t.FileId("Plate.SLDPRT");
        Assert.True(await t.Mentor.Api.AcquireLockAsync(original, t.Mentor.Device, Guid.NewGuid()));
        Assert.True(await t.Mentor.Api.MoveFileAsync(original, "Drivetrain", "Original.SLDPRT", t.Mentor.Device, Guid.NewGuid()));
        Assert.True(await t.Mentor.Api.ReleaseLockAsync(original, t.Mentor.Device, Guid.NewGuid()));
        await t.B.SyncTimesAsync(2);
        t.B.Write(Plate, "replacement"); await t.B.SyncTimesAsync(2);
        var replacement = await t.FileId("Plate.SLDPRT");
        Assert.NotEqual(original, replacement);
        var refused = await t.A.Engine.TrashFileAsync(Plate, expectedFileId: original);
        Assert.False(refused.Ok, refused.Message);
        Assert.Contains("moved or changed", refused.Message);
        Assert.Equal(0, t.World.Supabase.RpcCount("armory_web_trash"));
        Assert.Equal(2, await t.World.CountAsync("select count(*) from armory_files where deleted_at is null"));
        Assert.Equal("replacement", t.B.Text(Plate));
        Assert.Equal("original", t.A.Text(Plate));
    }

    [PostgresFact]
    public async Task File_changes_do_not_read_folder_metadata_again_but_folder_changes_do()
    {
        await using var t = await TeamAsync();
        await ArmoryV3StandIn.ApplySharedFoldersAsync(t.World.Database);
        await t.A.SyncAsync(); await t.B.SyncAsync();
        var reads = t.World.Supabase.RpcCount("armory_list_folder_paths");
        t.A.Write(Plate, "one"); await t.A.SyncAsync(); await t.B.SyncAsync();
        t.A.Write("Robot 2027/Second.txt", "two"); await t.A.SyncAsync(); await t.B.SyncAsync();
        Assert.Equal(reads, t.World.Supabase.RpcCount("armory_list_folder_paths"));
        Assert.True((await t.A.Engine.CreateFolderAsync(t.Project, "", "Empty")).Ok);
        await t.B.SyncAsync();
        Assert.True(t.World.Supabase.RpcCount("armory_list_folder_paths") > reads);
        Assert.True(t.B.Disk.FolderExists("Robot 2027/Empty"));
    }

    [PostgresFact]
    public async Task Older_folder_rpcs_also_move_and_remove_shared_empty_folder_metadata()
    {
        await using var t = await TeamAsync();
        await ArmoryV3StandIn.ApplySharedFoldersAsync(t.World.Database);
        t.A.Write(Plate, "plate"); await t.A.SyncAsync();
        await t.A.Api.CreateFolderAsync(t.Project, "Drivetrain/Empty", t.A.DeviceId, Guid.NewGuid());
        // A client that has never asked for folder metadata uses the original RPCs.
        Assert.Equal(1, await t.Mentor.Api.RenameFolderAsync(t.Project, "Drivetrain", "Intake", t.Mentor.Device, Guid.NewGuid()));
        Assert.Equal(new[] { "Intake/Empty" }, (await t.A.Api.FolderPathsAsync(t.Project))!);
        Assert.Equal(1, await t.Mentor.Api.DeleteFolderAsync(t.Project, "Intake", t.Mentor.Device, Guid.NewGuid()));
        Assert.Empty((await t.A.Api.FolderPathsAsync(t.Project))!);
        Assert.Equal(1, await t.World.CountAsync("select count(*) from armory_versions"));
    }

    [PostgresFact]
    public async Task Empty_folders_reach_a_teammate_rename_and_delete_without_a_placeholder_file()
    {
        await using var t = await TeamAsync();
        await ArmoryV3StandIn.ApplySharedFoldersAsync(t.World.Database);
        await t.A.SyncAsync(); await t.B.SyncAsync();
        var made = await t.A.Engine.CreateFolderAsync(t.Project, "", "Empty");
        Assert.True(made.Ok, made.Message);
        await t.B.SyncAsync();
        Assert.Contains(t.B.Engine.View.Projects.Single().Folders, f => f.Path == "Empty" && f.Files.Count == 0);
        Assert.True(t.B.Disk.FolderExists("Robot 2027/Empty"));
        Assert.Equal(0, await t.World.CountAsync("select count(*) from armory_files"));
        var renamed = await t.A.Engine.RenameFolderAsync(t.Project, "Empty", "Tools");
        Assert.True(renamed.Ok, renamed.Message);
        await t.B.SyncTimesAsync(2);
        Assert.True(t.B.Disk.FolderExists("Robot 2027/Tools"));
        Assert.False(t.B.Disk.FolderExists("Robot 2027/Empty"));
        var deleted = await t.A.Engine.DeleteFolderAsync(t.Project, "Tools");
        Assert.True(deleted.Ok, deleted.Message);
        t.B.Restart();
        await t.B.SyncTimesAsync(2);
        Assert.False(t.B.Disk.FolderExists("Robot 2027/Tools"));
        Assert.Empty((await t.Mentor.Api.FolderPathsAsync(t.Project))!);
    }

    [PostgresFact]
    public async Task Folder_metadata_replays_and_refuses_other_devices_members_and_checked_out_files()
    {
        await using var t = await TeamAsync();
        await ArmoryV3StandIn.ApplySharedFoldersAsync(t.World.Database);
        await t.A.SyncAsync();
        var operation = Guid.NewGuid();
        var api = t.A.Api;
        Assert.True(await api.CreateFolderAsync(t.Project, "Empty/Nested", t.A.DeviceId, operation));
        Assert.True(await api.CreateFolderAsync(t.Project, "Empty/Nested", t.A.DeviceId, operation));
        Assert.Equal(1, await t.World.CountAsync("select count(*) from armory_shared_folders"));
        Assert.Equal(1, await t.World.CountAsync("select count(*) from armory_change_feed where kind='folder_created'"));
        Assert.Contains("device is not registered", (await Assert.ThrowsAsync<ArmoryRpcException>(() => api.CreateFolderAsync(t.Project, "No", t.B.DeviceId, Guid.NewGuid()))).Message);
        Assert.True((await Assert.ThrowsAsync<ArmoryRpcException>(() => api.FolderPathsAsync(Guid.NewGuid()))).IsForbidden);
        t.A.Write(Plate, "kept"); await t.A.SyncAsync();
        await api.CreateFolderAsync(t.Project, "Drivetrain/Empty", t.A.DeviceId, Guid.NewGuid());
        Assert.True((await t.A.CheckOutAsync(Plate)).Ok);
        var refusal = await Assert.ThrowsAsync<ArmoryRpcException>(() => t.B.Api.DeleteFolderAsync(t.Project, "Drivetrain", t.B.DeviceId, Guid.NewGuid()));
        Assert.True(refusal.IsInUse);
        Assert.Contains("Drivetrain/Empty", (await api.FolderPathsAsync(t.Project))!);
        Assert.Equal(0, await t.World.CountAsync("select count(*) from armory_files where deleted_at is not null"));
    }

    [PostgresFact]
    public async Task Trash_keeps_versions_moves_copies_to_recovery_and_restores_the_same_file()
    {
        await using var t = await TeamAsync();
        await ArmoryV3StandIn.ApplySharedFoldersAsync(t.World.Database);
        await ArmoryV3StandIn.ApplyFileActionsAsync(t.World.Database);
        t.A.Write(Plate, "plate bytes"); await t.A.SyncAsync(); await t.B.SyncAsync();
        var id = await t.FileId("Plate.SLDPRT");
        var trashed = await t.A.Engine.TrashFileAsync(Plate);
        Assert.True(trashed.Ok, trashed.Message);
        await t.B.SyncTimesAsync(2);
        Assert.Null(t.A.Read(Plate)); Assert.Null(t.B.Read(Plate));
        Assert.Contains(Plate, t.A.Disk.Recovered); Assert.Contains(Plate, t.B.Disk.Recovered);
        Assert.Equal(1, await t.Versions(id));
        Assert.True(await t.A.Api.TrashFileAsync(id, true, Guid.NewGuid()));
        await t.A.SyncTimesAsync(2); await t.B.SyncTimesAsync(2);
        Assert.Equal("plate bytes", t.A.Text(Plate)); Assert.Equal("plate bytes", t.B.Text(Plate));
        Assert.Equal(id, await t.FileId("Plate.SLDPRT"));
        Assert.Empty(t.A.Disk.OpenWriteViolations); Assert.Empty(t.B.Disk.UnpreservedOverwrites);
    }

    [PostgresFact]
    public async Task Trash_refuses_open_checked_out_archived_and_other_students_folder_files()
    {
        await using var t = await TeamAsync();
        await ArmoryV3StandIn.ApplySharedFoldersAsync(t.World.Database);
        await ArmoryV3StandIn.ApplyFileActionsAsync(t.World.Database);
        t.A.Write(Plate, "plate bytes"); await t.A.SyncAsync(); await t.B.SyncAsync();
        t.A.Open(Plate);
        Assert.False((await t.A.Engine.TrashFileAsync(Plate)).Ok);
        t.A.Close(Plate);
        Assert.True((await t.B.CheckOutAsync(Plate)).Ok);
        Assert.False((await t.A.Engine.TrashFileAsync(Plate)).Ok);
        Assert.True((await t.B.CheckInAsync(Plate)).Ok);
        await using (var c = await t.World.Database.OpenAsync())
        {
            await using var command = new NpgsqlCommand("insert into armory_folders(project_id,folder,owner_email) values ($1,'Drivetrain',$2)", c);
            command.Parameters.AddWithValue(t.Project); command.Parameters.AddWithValue(Maria);
            await command.ExecuteNonQueryAsync();
        }
        var denied = await t.A.Engine.TrashFileAsync(Plate);
        Assert.False(denied.Ok); Assert.Contains("own folder", denied.Message);
        Assert.True((await Assert.ThrowsAsync<ArmoryRpcException>(() => t.A.Api.CreateFolderAsync(t.Project, "Drivetrain/Private", t.A.DeviceId, Guid.NewGuid()))).IsNotYourFolder);
        await t.Mentor.Api.SetProjectArchivedAsync(t.Project, true, Guid.NewGuid());
        Assert.False((await t.A.Engine.TrashFileAsync(Plate)).Ok);
        Assert.Equal("plate bytes", t.A.Text(Plate));
        Assert.Equal(0, await t.World.CountAsync("select count(*) from armory_tombstones"));
    }

    [PostgresFact]
    public async Task A_lost_trash_answer_is_reconciled_without_losing_bytes_or_adding_another_tombstone()
    {
        await using var t = await TeamAsync();
        await ArmoryV3StandIn.ApplySharedFoldersAsync(t.World.Database);
        await ArmoryV3StandIn.ApplyFileActionsAsync(t.World.Database);
        t.A.Write(Plate, "plate bytes"); await t.A.SyncAsync();
        t.World.Supabase.DropRpcAcknowledgement("armory_web_trash", 1);
        var answer = await t.A.Engine.TrashFileAsync(Plate);
        Assert.Contains("couldn't confirm", answer.Message);
        await t.A.SyncTimesAsync(2);
        Assert.Null(t.A.Read(Plate));
        Assert.Contains(Plate, t.A.Disk.Recovered);
        Assert.Equal(1, await t.World.CountAsync("select count(*) from armory_tombstones"));
        Assert.Equal(1, await t.Versions(await t.FileId("Plate.SLDPRT")));
        Assert.Empty(t.A.Disk.UnpreservedOverwrites);
    }
}
