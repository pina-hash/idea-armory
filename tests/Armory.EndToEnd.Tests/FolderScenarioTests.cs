using System.Text;
using Armory.Agent.Engine.View;
using Armory.Client;
using Armory.TestSupport;
using static Armory.EndToEnd.Tests.ScenarioTests;

namespace Armory.EndToEnd.Tests;

// Folders, projects and imports (brief section 5, v2-design.md 4.3): a directory rename is ONE
// folder move, a folder deleted on disk is ONE removal for the team, a refused one is put back
// with one notice naming who, a project's folder follows its name on the site and is never made
// again beside one a student renamed, an archived project stops syncing, and a bulk add is one
// import summary with the files that share a name in one card.
public sealed class FolderScenarioTests
{
    private const string Pack = "Robot 2027/Pack";
    private const string Inner = "Robot 2027/Pack/CopyDesignTemp";
    private const string Gearbox = "Robot 2027/Drivetrain/Gearbox";
    private const string Bracket = "Robot 2027/Drivetrain/Bracket.SLDPRT";

    private static void NoViolations(Computer c)
    {
        Assert.Empty(c.Disk.OpenWriteViolations);
        Assert.Empty(c.Disk.UnpreservedOverwrites);
    }

    private static Task<long> Changes(Team t, string kind) => t.World.CountAsync("select count(*) from armory_change_feed where project_id=@p and kind=@k", ("p", t.Project), ("k", kind));
    private static Task<List<(Guid Id, string Folder, string Name)>> LiveFiles(Team t) =>
        t.World.QueryAsync("select id, folder, name from armory_files where project_id=@p and deleted_at is null order by folder, name", r => (r.GetGuid(0), r.GetString(1), r.GetString(2)), ("p", t.Project));
    private static bool FolderExists(Computer c, string folder) => Directory.Exists(c.Disk.Full(folder));

    // a. Students unzip a Pack and Go of about 60 files, 14 of them named like parts the project
    // already has; the connection drops in the middle, so some files are in Armory and the rest
    // wait; then the student renames the inner folder. The rename is one armory_rename_folder,
    // never a move or a removal per file; the files keep their ids; the ones that waited are
    // added at the new place; the other computer moves its folder in place without downloading
    // anything again; and the uploading student sees one import summary and one card for the 14.
    [PostgresFact]
    public async Task Pack_and_Go_with_duplicate_names_then_the_inner_folder_is_renamed()
    {
        await using var t = await TeamAsync();
        // Maria added the project's parts earlier (her own import summary, which she closed).
        for (var i = 1; i <= 14; i++) t.B.Write($"Robot 2027/Parts/Shared-{i:D2}.SLDPRT", $"the project's own part {i}");
        await t.B.SyncAsync();
        t.B.Engine.DismissNotice(t.B.Card(NoticeKinds.Import)!.Key);
        await t.A.SyncAsync();
        Assert.Empty(t.A.Engine.View.Notices);

        List<string> unzipped = [];
        for (var i = 1; i <= 46; i++) unzipped.Add($"{Inner}/{(i % 3 == 0 ? "Sub/" : "")}Copy-{i:D2}.SLDPRT");
        for (var i = 1; i <= 14; i++) unzipped.Add($"{Inner}/Shared-{i:D2}.SLDPRT");
        foreach (var path in unzipped) t.A.Write(path, "pack and go " + Path.GetFileName(path));
        // The connection drops after 20 files are in.
        var committed = 0;
        t.A.CrashPoint = p => { if (p == "after-commit" && ++committed == 20) t.A.Offline = true; };
        t.A.Restart();
        await t.A.SyncAsync();
        t.A.CrashPoint = null;
        t.A.Restart();
        var before = (await LiveFiles(t)).Where(f => f.Folder.StartsWith("Pack/", StringComparison.Ordinal)).ToList();
        Assert.InRange(before.Count, 20, 21); // the 21st may be created, without its bytes
        await t.B.SyncAsync();
        var bHad = unzipped.Count(p => t.B.Read(p) is not null);
        Assert.Equal(20, bHad);
        var (bGets, bReplaces) = (t.B.Network.StorageGets, t.B.Disk.Replaces);

        // The student renames the inner folder (still offline), then the connection comes back.
        t.A.RenameFolder(Inner, $"{Pack}/Gearbox");
        await t.A.SyncAsync();
        Assert.Equal(0, t.World.Supabase.RpcCount("armory_rename_folder"));
        t.A.Offline = false;
        await t.A.SyncTimesAsync(2);
        await t.B.SyncTimesAsync(2);

        // One folder rename, nothing per file.
        Assert.Equal(1, t.World.Supabase.RpcCount("armory_rename_folder"));
        Assert.Equal(0, t.World.Supabase.RpcCount("armory_move_file"));
        Assert.Equal(0, t.World.Supabase.RpcCount("armory_tombstone"));
        Assert.Equal(1, await Changes(t, "folder_renamed"));
        Assert.Equal(0, await Changes(t, "file_moved"));
        Assert.Equal(["Pack/CopyDesignTemp|Pack/Gearbox"], await t.World.QueryAsync("select payload->>'from' || '|' || (payload->>'to') from armory_change_feed where project_id=@p and kind='folder_renamed'",
            r => r.GetString(0), ("p", t.Project)));
        // The same files, by id, at the new place; the ones that waited were added there.
        var after = await LiveFiles(t);
        foreach (var (id, folder, name) in before) Assert.Contains(after, f => f.Id == id && f.Name == name && f.Folder == folder.Replace("Pack/CopyDesignTemp", "Pack/Gearbox", StringComparison.Ordinal));
        var packed = after.Where(f => f.Folder.StartsWith("Pack/", StringComparison.Ordinal)).ToList();
        Assert.Equal(46, packed.Count);
        Assert.All(packed, f => Assert.True(f.Folder is "Pack/Gearbox" or "Pack/Gearbox/Sub", f.Folder));
        foreach (var f in packed) Assert.Equal(1, await t.Versions(f.Id));
        Assert.Equal(14, after.Count(f => f.Folder == "Parts")); // the 14 shared names were never added again
        Assert.Equal(0, await t.World.CountAsync("select count(*) from armory_locks l join armory_files f on f.id=l.file_id where f.project_id=@p and l.broken_at is null", ("p", t.Project)));
        Assert.False(FolderExists(t.A, Inner));
        Assert.All(packed, f => Assert.Equal("pack and go " + f.Name, t.A.Text($"Robot 2027/{f.Folder}/{f.Name}")));

        // B moved its folder in place: nothing it had came down again, nothing went to recovery.
        Assert.Contains(new Armory.Agent.Engine.FolderMove(Inner, $"{Pack}/Gearbox"), t.B.Disk.MovedFolders);
        Assert.False(FolderExists(t.B, Inner));
        Assert.Empty(t.B.Disk.Recovered);
        Assert.Equal(46 - bHad, t.B.Network.StorageGets - bGets);
        Assert.Equal(46 - bHad, t.B.Disk.Replaces - bReplaces);
        Assert.All(packed, f => Assert.Equal("pack and go " + f.Name, t.B.Text($"Robot 2027/{f.Folder}/{f.Name}")));
        Assert.Empty(t.B.Engine.View.Notices);

        // The uploading student: one import summary and one card for the 14 shared names,
        // nothing per file anywhere else.
        Assert.Equal(2, t.A.Engine.View.Notices.Count);
        var import = t.A.Card(NoticeKinds.Import)!;
        Assert.Equal("Added 46 of 60 files to Robot 2027 › Pack", import.Title);
        Assert.Single(import.Items);
        var shared = t.A.Card(NoticeKinds.NameShared)!;
        Assert.Equal(14, shared.Count);
        Assert.Equal(14, shared.Items.Count);
        Assert.Equal("14 files share a name with other files in this project", shared.Title);
        Assert.All(shared.Items, i => Assert.StartsWith($"{Pack}/Gearbox/Shared-", i.Path, StringComparison.Ordinal));
        Assert.Empty(t.A.Engine.View.MyFiles);
        Assert.Null(t.A.Engine.View.Activity.Waiting);
        NoViolations(t.A); NoViolations(t.B);
    }

    // b. A folder deleted on disk: one scan alone does nothing; then one armory_delete_folder
    // removes its files for the team (never one removal per file), the other computer keeps its
    // copies in recovery, and the empty folder goes on every computer.
    [PostgresFact]
    public async Task A_folder_deleted_on_disk_tombstones_its_files_in_one_call()
    {
        await using var t = await TeamAsync();
        string[] inside = [$"{Gearbox}/Housing.SLDPRT", $"{Gearbox}/Gear-14T.SLDPRT", $"{Gearbox}/Gear-60T.SLDPRT", $"{Gearbox}/Shafts/Input.SLDPRT", $"{Gearbox}/Shafts/Output.SLDPRT"];
        foreach (var path in inside) t.A.Write(path, "bytes of " + path);
        t.A.Write(Plate, "plate");
        await t.A.SyncAsync();
        await t.B.SyncAsync();
        Assert.All(inside, p => Assert.NotNull(t.B.Read(p)));

        Directory.Delete(t.A.Disk.Full(Gearbox), recursive: true);
        await t.A.SyncAsync();
        // One scan is not a removal.
        Assert.Equal(0, t.World.Supabase.RpcCount("armory_delete_folder"));
        Assert.Equal(0, await t.World.CountAsync("select count(*) from armory_tombstones"));
        await t.A.SyncAsync();
        Assert.Equal(1, t.World.Supabase.RpcCount("armory_delete_folder"));
        Assert.Equal(0, t.World.Supabase.RpcCount("armory_tombstone"));
        Assert.Equal(1, await Changes(t, "folder_deleted"));
        Assert.Equal(5, await t.World.CountAsync("select count(*) from armory_files where project_id=@p and deleted_at is not null and folder like 'Drivetrain/Gearbox%'", ("p", t.Project)));
        Assert.Equal(["Plate.SLDPRT"], (await LiveFiles(t)).Select(f => f.Name));
        Assert.Empty(t.A.Engine.View.Notices);
        Assert.False(FolderExists(t.A, Gearbox));
        Assert.Equal("plate", t.A.Text(Plate));

        await t.B.SyncTimesAsync(2);
        Assert.Equal(inside.Order(StringComparer.Ordinal), t.B.Disk.Recovered.Order(StringComparer.Ordinal));
        Assert.False(FolderExists(t.B, Gearbox));
        Assert.True(FolderExists(t.B, "Robot 2027/Drivetrain"));
        Assert.Equal("plate", t.B.Text(Plate));
        Assert.Empty(t.B.Engine.View.Notices);
        await t.A.SyncTimesAsync(2);
        Assert.Equal(1, t.World.Supabase.RpcCount("armory_delete_folder"));
        NoViolations(t.A); NoViolations(t.B);
    }

    // b, refused. Maria has a file in the folder checked out: the removal is refused, and the
    // folder comes back on Alex's computer (downloaded again) with exactly one notice naming her.
    [PostgresFact]
    public async Task A_folder_deleted_on_disk_while_Maria_has_a_file_checked_out_is_put_back()
    {
        await using var t = await TeamAsync();
        string[] inside = [$"{Gearbox}/Housing.SLDPRT", $"{Gearbox}/Gear-14T.SLDPRT", $"{Gearbox}/Shafts/Input.SLDPRT"];
        foreach (var path in inside) t.A.Write(path, "bytes of " + path);
        await t.A.SyncAsync();
        await t.B.SyncAsync();
        Assert.True((await t.B.CheckOutAsync($"{Gearbox}/Gear-14T.SLDPRT")).Ok);

        Directory.Delete(t.A.Disk.Full(Gearbox), recursive: true);
        await t.A.SyncTimesAsync(2);
        Assert.Equal(1, t.World.Supabase.RpcCount("armory_delete_folder"));
        Assert.Equal(0, await t.World.CountAsync("select count(*) from armory_tombstones"));
        Assert.Equal(0, await Changes(t, "folder_deleted"));
        Assert.Equal(3, (await LiveFiles(t)).Count);
        Assert.All(inside, p => Assert.Equal("bytes of " + p, t.A.Text(p)));
        Assert.All(inside, p => Assert.True(t.A.Disk.IsReadOnly(p)));
        var notice = Assert.Single(t.A.Engine.View.Notices);
        Assert.Equal(NoticeKinds.FolderPutBack, notice.Kind);
        Assert.Equal("Gearbox was put back: Maria Lopez has 1 of its files checked out.", notice.Title);
        Assert.Single(notice.Items);
        await t.A.SyncTimesAsync(2);
        Assert.Equal(1, t.World.Supabase.RpcCount("armory_delete_folder"));
        Assert.Single(t.A.Engine.View.Notices);
        NoViolations(t.A); NoViolations(t.B);
    }

    // A folder renamed on disk while someone else has a file in it checked out: the one rename is
    // refused and the folder is put back where it was, with one notice naming who.
    [PostgresFact]
    public async Task A_folder_renamed_on_disk_while_Maria_has_a_file_checked_out_is_put_back()
    {
        await using var t = await TeamAsync();
        string[] inside = [$"{Gearbox}/Housing.SLDPRT", $"{Gearbox}/Gear-14T.SLDPRT"];
        foreach (var path in inside) t.A.Write(path, "bytes of " + path);
        await t.A.SyncAsync();
        await t.B.SyncAsync();
        Assert.True((await t.B.CheckOutAsync($"{Gearbox}/Housing.SLDPRT")).Ok);

        t.A.RenameFolder(Gearbox, "Robot 2027/Drivetrain/Gears");
        await t.A.SyncAsync();
        Assert.Equal(1, t.World.Supabase.RpcCount("armory_rename_folder"));
        Assert.Equal(0, t.World.Supabase.RpcCount("armory_move_file"));
        Assert.True(FolderExists(t.A, Gearbox));
        Assert.False(FolderExists(t.A, "Robot 2027/Drivetrain/Gears"));
        Assert.All(inside, p => Assert.Equal("bytes of " + p, t.A.Text(p)));
        Assert.All(await LiveFiles(t), f => Assert.Equal("Drivetrain/Gearbox", f.Folder));
        var notice = Assert.Single(t.A.Engine.View.Notices);
        Assert.Equal(NoticeKinds.FolderPutBack, notice.Kind);
        Assert.Equal("Gearbox was put back: Maria Lopez has 1 of its files checked out.", notice.Title);
        await t.A.SyncTimesAsync(2);
        Assert.Equal(1, t.World.Supabase.RpcCount("armory_rename_folder"));
        Assert.Equal(0, await t.World.CountAsync("select count(*) from armory_tombstones"));
        Assert.Equal(2, await t.World.CountAsync("select count(*) from armory_files where project_id=@p", ("p", t.Project)));
        NoViolations(t.A); NoViolations(t.B);
    }

    // c. A removed name added again revives the removed file: the same id, its history keeping
    // v1, one file_revived, and the new bytes current, committed on top of v1.
    [PostgresFact]
    public async Task A_removed_name_is_revived_with_its_history()
    {
        await using var t = await TeamAsync();
        t.A.Write(Plate, "v1 by Alex");
        await t.A.SyncAsync();
        await t.B.SyncAsync();
        var file = await t.FileId("Plate.SLDPRT");
        var v1 = (await t.World.QueryAsync("select current_version_id from armory_files where id=@f", r => r.GetGuid(0), ("f", file))).Single();
        t.A.Delete(Plate);
        await t.A.SyncTimesAsync(2);
        await t.B.SyncAsync();
        Assert.Equal(1, await t.World.CountAsync("select count(*) from armory_files where id=@f and deleted_at is not null", ("f", file)));
        Assert.Null(t.B.Read(Plate));

        t.B.Write(Plate, "v2 by Maria, the name used again");
        await t.B.SyncTimesAsync(2);
        Assert.Equal(file, await t.FileId("Plate.SLDPRT"));
        Assert.Equal(1, await t.World.CountAsync("select count(*) from armory_files where project_id=@p and name='Plate.SLDPRT'", ("p", t.Project)));
        Assert.Equal(1, await t.World.CountAsync("select count(*) from armory_change_feed where entity_id=@f and kind='file_revived'", ("f", file)));
        Assert.Equal(2, await t.Versions(file));
        Assert.Equal(1, await t.World.CountAsync("select count(*) from armory_versions where id=@v and content_sha256=@h", ("v", v1), ("h", Hash("v1 by Alex"))));
        Assert.Equal(Hash("v2 by Maria, the name used again"), await t.CurrentHash(file));
        Assert.Equal(v1, (await t.World.QueryAsync("select v.parent_version_id from armory_files f join armory_versions v on v.id=f.current_version_id where f.id=@f", r => r.GetGuid(0), ("f", file))).Single());
        Assert.Equal(0, await t.Sides(file));
        var history = (await t.B.Engine.GetFileDetailAsync(file))!.History;
        Assert.Equal(["Added again, with its history", "Added to Armory"], history.Select(h => h.Note));
        Assert.Contains(history, h => h.Id == v1.ToString());
        Assert.Empty(t.B.Engine.View.Notices);
        await t.A.SyncTimesAsync(2);
        Assert.Equal("v2 by Maria, the name used again", t.A.Text(Plate));
        NoViolations(t.A); NoViolations(t.B);
    }

    // d. The project is renamed on the site while Maria has a file open: her folder waits, with
    // one notice and her bytes untouched; once the file closes the folder is renamed in place:
    // nothing downloaded, nothing removed, no second folder, and her check out goes on.
    [PostgresFact]
    public async Task A_project_renamed_on_the_site_while_a_file_is_open()
    {
        await using var t = await TeamAsync();
        t.A.Write(Plate, "plate");
        t.A.Write(Bracket, "bracket");
        await t.A.SyncAsync();
        await t.B.SyncAsync();
        Assert.True((await t.B.CheckOutAsync(Bracket)).Ok);
        t.B.Save(Bracket, "bracket, Maria's work in progress");
        t.B.Open(Plate);
        var (creates, aReplaces, bReplaces) = (t.World.Supabase.RpcCount("armory_create_file"), t.A.Disk.Replaces, t.B.Disk.Replaces);

        Assert.True(await t.Mentor.Api.RenameProjectAsync(t.Project, "Robot 2028", Guid.NewGuid()));
        await t.B.SyncAsync();
        Assert.True(FolderExists(t.B, "Robot 2027"));
        Assert.False(FolderExists(t.B, "Robot 2028"));
        Assert.Equal("plate", t.B.Text(Plate));
        Assert.Equal("bracket, Maria's work in progress", t.B.Text(Bracket));
        var waiting = Assert.Single(t.B.Engine.View.Notices);
        Assert.Equal(NoticeKinds.ProjectRenaming, waiting.Kind);
        Assert.Equal("Close Plate.SLDPRT to finish renaming Robot 2027 to Robot 2028", waiting.Title);
        Assert.Equal("Robot 2028", Assert.Single(t.B.Engine.View.Projects).Name);

        // Alex has nothing open: his folder is renamed in place at once.
        await t.A.SyncAsync();
        Assert.False(FolderExists(t.A, "Robot 2027"));
        Assert.Equal("plate", t.A.Text("Robot 2028/Drivetrain/Plate.SLDPRT"));
        Assert.Contains(new Armory.Agent.Engine.FolderMove("Robot 2027", "Robot 2028"), t.A.Disk.MovedFolders);
        Assert.Empty(t.A.Engine.View.Notices);

        t.B.Close(Plate);
        await t.B.SyncTimesAsync(2);
        Assert.False(FolderExists(t.B, "Robot 2027"));
        Assert.Equal("plate", t.B.Text("Robot 2028/Drivetrain/Plate.SLDPRT"));
        Assert.Equal("bracket, Maria's work in progress", t.B.Text("Robot 2028/Drivetrain/Bracket.SLDPRT"));
        Assert.Empty(t.B.Engine.View.Notices);
        Assert.Equal((aReplaces, bReplaces), (t.A.Disk.Replaces, t.B.Disk.Replaces));
        Assert.Empty(t.A.Disk.Recovered);
        Assert.Empty(t.B.Disk.Recovered);
        Assert.Equal(0, await t.World.CountAsync("select count(*) from armory_tombstones"));
        Assert.Equal(creates, t.World.Supabase.RpcCount("armory_create_file"));
        Assert.Equal(2, await t.World.CountAsync("select count(*) from armory_files where project_id=@p", ("p", t.Project)));
        // Her check out goes on, at the new place.
        var bracket = await t.FileId("Bracket.SLDPRT");
        Assert.Equal(Maria, await t.Holder(bracket));
        var mine = Assert.Single(t.B.Engine.View.MyFiles);
        Assert.Equal(("Robot 2028/Drivetrain/Bracket.SLDPRT", "Robot 2028"), (mine.Path, mine.Project));
        Assert.False(t.B.Disk.IsReadOnly("Robot 2028/Drivetrain/Bracket.SLDPRT"));
        Assert.True(t.B.Disk.IsReadOnly("Robot 2028/Drivetrain/Plate.SLDPRT"));
        Assert.True((await t.B.CheckInAsync("Robot 2028/Drivetrain/Bracket.SLDPRT")).Ok);
        Assert.Equal(Hash("bracket, Maria's work in progress"), await t.CurrentHash(bracket));
        await t.A.SyncAsync();
        Assert.Equal("bracket, Maria's work in progress", t.A.Text("Robot 2028/Drivetrain/Bracket.SLDPRT"));
        NoViolations(t.A); NoViolations(t.B);
    }

    // D16. A project's folder renamed in File Explorer is put back to the project's name, never
    // followed and never made again beside it; while a file inside is open it waits for it.
    [PostgresFact]
    public async Task An_Explorer_rename_of_a_project_folder_is_put_back()
    {
        await using var t = await TeamAsync();
        t.A.Write(Plate, "plate");
        t.A.Write(Bracket, "bracket");
        await t.A.SyncAsync();
        var (creates, replaces) = (t.World.Supabase.RpcCount("armory_create_file"), t.A.Disk.Replaces);

        t.A.RenameFolder("Robot 2027", "Robot 2027 old");
        await t.A.SyncAsync();
        Assert.True(FolderExists(t.A, "Robot 2027"));
        Assert.False(FolderExists(t.A, "Robot 2027 old"));
        Assert.Equal("plate", t.A.Text(Plate));
        var notice = Assert.Single(t.A.Engine.View.Notices);
        Assert.Equal(NoticeKinds.ProjectPutBack, notice.Kind);
        Assert.Equal("The Robot 2027 folder was renamed back", notice.Title);
        Assert.Equal("Project names are changed on ideabosco.com.", notice.Detail);
        t.A.Engine.DismissNotice(notice.Key);
        Assert.Empty(t.A.Engine.View.Notices);

        // Renamed again, and a file inside opened before Armory looks: nothing is made beside it
        // and nothing is removed; it goes back once the file closes.
        t.A.Clock.Advance(TimeSpan.FromMinutes(1));
        t.A.RenameFolder("Robot 2027", "Robot X");
        t.A.Open("Robot X/Drivetrain/Plate.SLDPRT");
        await t.A.SyncTimesAsync(2);
        Assert.False(FolderExists(t.A, "Robot 2027"));
        Assert.True(FolderExists(t.A, "Robot X"));
        var waiting = Assert.Single(t.A.Engine.View.Notices);
        Assert.Equal(NoticeKinds.ProjectPutBack, waiting.Kind);
        Assert.Contains("Close Plate.SLDPRT", waiting.Detail, StringComparison.Ordinal);
        Assert.Contains("Project names are changed on ideabosco.com.", waiting.Detail, StringComparison.Ordinal);
        t.A.Close("Robot X/Drivetrain/Plate.SLDPRT");
        await t.A.SyncAsync();
        Assert.True(FolderExists(t.A, "Robot 2027"));
        Assert.False(FolderExists(t.A, "Robot X"));
        Assert.Equal("bracket", t.A.Text(Bracket));
        Assert.Equal(0, await t.World.CountAsync("select count(*) from armory_tombstones"));
        Assert.Equal(creates, t.World.Supabase.RpcCount("armory_create_file"));
        Assert.Equal(replaces, t.A.Disk.Replaces);
        Assert.Equal(2, await t.World.CountAsync("select count(*) from armory_files where project_id=@p and deleted_at is null", ("p", t.Project)));
        Assert.True(t.A.Disk.IsReadOnly(Plate));
        Assert.Equal("The Robot 2027 folder was renamed back", Assert.Single(t.A.Engine.View.Notices).Title);
        NoViolations(t.A);
    }

    // D16. A project's folder removed in File Explorer is made again and downloaded: never a
    // removal of its files for the team.
    [PostgresFact]
    public async Task A_project_folder_removed_in_Explorer_is_put_back()
    {
        await using var t = await TeamAsync();
        t.A.Write(Plate, "plate");
        t.A.Write(Bracket, "bracket");
        await t.A.SyncAsync();
        Directory.Delete(t.A.Disk.Full("Robot 2027"), recursive: true);
        await t.A.SyncAsync();
        Assert.Equal("plate", t.A.Text(Plate));
        Assert.Equal("bracket", t.A.Text(Bracket));
        Assert.True(t.A.Disk.IsReadOnly(Plate));
        Assert.Equal(0, await t.World.CountAsync("select count(*) from armory_tombstones"));
        Assert.Equal(0, t.World.Supabase.RpcCount("armory_delete_folder"));
        Assert.Equal("The Robot 2027 folder was put back", Assert.Single(t.A.Engine.View.Notices).Title);
        await t.A.SyncTimesAsync(2);
        Assert.Equal(0, await t.World.CountAsync("select count(*) from armory_tombstones"));
        NoViolations(t.A);
    }

    // D8. An archived project stops syncing: nothing new is added or downloaded, nothing is
    // removed, its folder and its files (read-only bits included) stay as they are, and no
    // notice is shown. A check out this computer has there is still listed and can be checked in.
    [PostgresFact]
    public async Task An_archived_project_stops_syncing_and_keeps_its_folder()
    {
        await using var t = await TeamAsync();
        t.A.Write(Plate, "plate");
        t.A.Write(Bracket, "bracket v1");
        await t.A.SyncAsync();
        await t.B.SyncAsync();
        Assert.True((await t.A.CheckOutAsync(Bracket)).Ok);
        t.A.Save(Bracket, "bracket v2 by Alex");
        Assert.True(await t.Mentor.Api.SetProjectArchivedAsync(t.Project, true, Guid.NewGuid()));
        const string New = "Robot 2027/Drivetrain/New-Part.SLDPRT";
        t.A.Write(New, "a part added after the archive");
        await t.A.SyncTimesAsync(2);
        await t.B.SyncTimesAsync(2);

        Assert.Equal(0, await t.World.CountAsync("select count(*) from armory_files where project_id=@p and name='New-Part.SLDPRT'", ("p", t.Project)));
        Assert.Equal("a part added after the archive", t.A.Text(New));
        Assert.True(t.A.Disk.IsReadOnly(Plate));
        Assert.False(t.A.Disk.IsReadOnly(Bracket));
        foreach (var c in new[] { t.A, t.B })
        {
            var project = Assert.Single(c.Engine.View.Projects);
            Assert.True(project.Archived);
            Assert.Empty(c.Engine.View.Notices);
            Assert.Equal("plate", c.Text(Plate));
            Assert.Null(c.Engine.View.Activity.Waiting);
        }
        Assert.Equal(SyncStates.Synced, t.A.Engine.View.Sync.State);
        // The window's folder actions say so in the page's own words.
        Assert.Equal("Robot 2027 is archived. It no longer updates.", (await t.A.Engine.CreateFolderAsync(t.Project, "Drivetrain", "Notes")).Message);
        Assert.Equal("bracket v1", t.B.Text(Bracket));
        // Alex's check out there is still his, listed, and he can check it in.
        Assert.Equal(Bracket, Assert.Single(t.A.Engine.View.MyFiles).Path);
        var bracket = await t.FileId("Bracket.SLDPRT");
        Assert.True((await t.A.CheckInAsync(Bracket)).Ok);
        Assert.Equal(Hash("bracket v2 by Alex"), await t.CurrentHash(bracket));
        Assert.Equal(0, await t.LiveLocks(bracket));
        Assert.True(t.A.Disk.IsReadOnly(Bracket));
        Assert.Empty(t.A.Engine.View.MyFiles);
        await t.B.SyncAsync();
        Assert.Equal("bracket v1", t.B.Text(Bracket)); // B does not read an archived project
        Assert.Equal(0, t.World.Supabase.RpcCount("armory_tombstone"));

        // Restored, it syncs again from where it was.
        Assert.True(await t.Mentor.Api.SetProjectArchivedAsync(t.Project, false, Guid.NewGuid()));
        await t.A.SyncTimesAsync(2);
        await t.B.SyncTimesAsync(2);
        Assert.Equal(1, await t.World.CountAsync("select count(*) from armory_files where project_id=@p and name='New-Part.SLDPRT'", ("p", t.Project)));
        Assert.Equal("a part added after the archive", t.B.Text(New));
        Assert.Equal("bracket v2 by Alex", t.B.Text(Bracket));
        NoViolations(t.A); NoViolations(t.B);
    }

    // Season is shown nowhere (brief section 5): no field of the view or of File detail carries
    // it, for a project with a season and one without.
    [PostgresFact]
    public async Task No_view_field_carries_a_season()
    {
        await using var t = await TeamAsync();
        var seasonless = await t.Mentor.Api.CreateProjectAsync("Outreach", null, Guid.NewGuid());
        await t.Mentor.Api.AddMemberAsync(seasonless, Alex, MemberRole.Student, Guid.NewGuid());
        t.A.Write(Plate, "plate");
        t.A.Write("Outreach/Booth/Sign.SLDPRT", "sign");
        await t.A.SyncTimesAsync(2);
        var view = t.A.Engine.View;
        Assert.Equal(["Outreach", "Robot 2027"], view.Projects.Select(p => p.Name).Order(StringComparer.Ordinal));
        var json = BridgeMessages.ViewMessage(view);
        Assert.DoesNotContain("season", json, StringComparison.OrdinalIgnoreCase);
        foreach (var name in new[] { "Plate.SLDPRT", "Sign.SLDPRT" })
        {
            var id = (await t.World.QueryAsync("select id from armory_files where name=@n", r => r.GetGuid(0), ("n", name))).Single();
            Assert.DoesNotContain("season", BridgeMessages.DetailMessage((await t.A.Engine.GetFileDetailAsync(id))!), StringComparison.OrdinalIgnoreCase);
        }
        foreach (var type in typeof(AgentView).Assembly.GetTypes().Where(type => type.Namespace == typeof(AgentView).Namespace))
            Assert.DoesNotContain(type.GetProperties(), p => p.Name.Contains("Season", StringComparison.OrdinalIgnoreCase));
    }

    // The window's folder actions: New folder, Add files (files and a whole folder, never over
    // anything there), Rename folder and Delete folder, each one call for the team.
    [PostgresFact]
    public async Task Folders_made_renamed_deleted_and_filled_in_the_app()
    {
        await using var t = await TeamAsync();
        t.A.Write("Robot 2027/Intake/Roller.SLDPRT", "roller");
        await t.A.SyncAsync();
        await t.B.SyncAsync();
        var outside = Path.Combine(t.World.Temp, "outside");
        Directory.CreateDirectory(Path.Combine(outside, "Wheels", "Hubs"));
        File.WriteAllText(Path.Combine(outside, "Wheels", "Wheel.SLDPRT"), "wheel");
        File.WriteAllText(Path.Combine(outside, "Wheels", "Hubs", "Hub.SLDPRT"), "hub");
        File.WriteAllText(Path.Combine(outside, "Axle.SLDPRT"), "axle");

        var made = await t.A.Engine.CreateFolderAsync(t.Project, "Intake", "Rollers");
        Assert.True(made.Ok, made.Message);
        Assert.True(FolderExists(t.A, "Robot 2027/Intake/Rollers"));
        Assert.Contains(t.A.Engine.View.Projects.Single().Folders, f => f.Path == "Intake/Rollers");
        Assert.False((await t.A.Engine.CreateFolderAsync(t.Project, "Intake", "Rollers")).Ok);

        var added = await t.A.Engine.AddFilesAsync(t.Project, "Intake/Rollers", [Path.Combine(outside, "Wheels"), Path.Combine(outside, "Axle.SLDPRT")]);
        Assert.True(added.Ok, added.Message);
        Assert.Equal("Copied 3 files into Robot 2027 › Intake › Rollers.", added.Message);
        Assert.Equal("hub", t.A.Text("Robot 2027/Intake/Rollers/Wheels/Hubs/Hub.SLDPRT"));
        Assert.Equal(4, (await LiveFiles(t)).Count);
        var import = t.A.Card(NoticeKinds.Import)!;
        Assert.Equal("Added 3 of 3 files to Robot 2027 › Intake › Rollers", import.Title);
        // Never over a file that is there.
        File.WriteAllText(Path.Combine(outside, "Axle.SLDPRT"), "another axle");
        var again = await t.A.Engine.AddFilesAsync(t.Project, "Intake/Rollers", [Path.Combine(outside, "Axle.SLDPRT")]);
        Assert.False(again.Ok);
        Assert.Contains("already there", again.Message, StringComparison.Ordinal);
        Assert.Equal("axle", t.A.Text("Robot 2027/Intake/Rollers/Axle.SLDPRT"));

        var renamed = await t.A.Engine.RenameFolderAsync(t.Project, "Intake", "Intake v2");
        Assert.True(renamed.Ok, renamed.Message);
        Assert.Equal(1, t.World.Supabase.RpcCount("armory_rename_folder"));
        Assert.Equal(0, t.World.Supabase.RpcCount("armory_move_file"));
        Assert.Equal("wheel", t.A.Text("Robot 2027/Intake v2/Rollers/Wheels/Wheel.SLDPRT"));
        Assert.False(FolderExists(t.A, "Robot 2027/Intake"));
        await t.B.SyncTimesAsync(2);
        Assert.Equal("roller", t.B.Text("Robot 2027/Intake v2/Roller.SLDPRT"));
        Assert.False(FolderExists(t.B, "Robot 2027/Intake"));
        Assert.Contains(new Armory.Agent.Engine.FolderMove("Robot 2027/Intake", "Robot 2027/Intake v2"), t.B.Disk.MovedFolders);

        // Delete is refused while Maria has a file in it checked out, naming her.
        Assert.True((await t.B.CheckOutAsync("Robot 2027/Intake v2/Roller.SLDPRT")).Ok);
        await t.A.SyncAsync();
        var refused = await t.A.Engine.DeleteFolderAsync(t.Project, "Intake v2");
        Assert.False(refused.Ok);
        Assert.Equal("Intake v2 can't be deleted now: Maria Lopez has 1 of its files checked out.", refused.Message);
        Assert.True((await t.B.UndoCheckOutAsync("Robot 2027/Intake v2/Roller.SLDPRT")).Ok);
        await t.A.SyncAsync();
        var deleted = await t.A.Engine.DeleteFolderAsync(t.Project, "Intake v2");
        Assert.True(deleted.Ok, deleted.Message);
        Assert.Equal("Deleted Intake v2 and its 4 files. Their history is kept.", deleted.Message);
        Assert.Equal(1, t.World.Supabase.RpcCount("armory_delete_folder"));
        Assert.Equal(0, t.World.Supabase.RpcCount("armory_tombstone"));
        Assert.Empty(await LiveFiles(t));
        Assert.False(FolderExists(t.A, "Robot 2027/Intake v2"));
        await t.B.SyncTimesAsync(2);
        Assert.False(FolderExists(t.B, "Robot 2027/Intake v2"));
        Assert.Equal(4, t.B.Disk.Recovered.Count);
        NoViolations(t.A); NoViolations(t.B);
    }

    // Without directory identity (VaultScan.FolderMoves null), a folder renamed on disk is found
    // by its files' bytes and is still one folder rename.
    [PostgresFact]
    public async Task A_folder_renamed_without_directory_identity_is_still_one_folder_move()
    {
        await using var t = await TeamAsync();
        foreach (var name in new[] { "Housing", "Gear-14T", "Gear-60T" }) t.A.Write($"{Gearbox}/{name}.SLDPRT", name);
        await t.A.SyncAsync();
        await t.B.SyncAsync();
        t.A.Disk.ReportsFolderMoves = false;
        t.A.RenameFolder(Gearbox, "Robot 2027/Drivetrain/Gears");
        await t.A.SyncTimesAsync(2);
        Assert.Equal(1, t.World.Supabase.RpcCount("armory_rename_folder"));
        Assert.Equal(0, t.World.Supabase.RpcCount("armory_move_file"));
        Assert.Equal(0, t.World.Supabase.RpcCount("armory_tombstone"));
        Assert.All(await LiveFiles(t), f => Assert.Equal("Drivetrain/Gears", f.Folder));
        await t.B.SyncAsync();
        Assert.Equal("Housing", t.B.Text("Robot 2027/Drivetrain/Gears/Housing.SLDPRT"));
        Assert.False(FolderExists(t.B, Gearbox));
        Assert.Empty(t.B.Disk.Recovered);
        NoViolations(t.A); NoViolations(t.B);
    }

    // The team renames a folder while this computer has a file in it open: the closed files move
    // now, the open one once it closes, nothing is downloaded or removed, and the old folder goes.
    [PostgresFact]
    public async Task A_folder_renamed_by_the_team_waits_for_an_open_file()
    {
        await using var t = await TeamAsync();
        foreach (var name in new[] { "Housing", "Gear-14T", "Gear-60T" }) t.A.Write($"{Gearbox}/{name}.SLDPRT", name);
        await t.A.SyncAsync();
        await t.B.SyncAsync();
        t.B.Open($"{Gearbox}/Housing.SLDPRT");
        t.A.RenameFolder(Gearbox, "Robot 2027/Drivetrain/Gears");
        await t.A.SyncAsync();
        var replaces = t.B.Disk.Replaces;
        await t.B.SyncAsync();
        Assert.Equal("Gear-14T", t.B.Text("Robot 2027/Drivetrain/Gears/Gear-14T.SLDPRT"));
        Assert.Equal("Housing", t.B.Text($"{Gearbox}/Housing.SLDPRT"));
        Assert.Contains(t.B.NoticeItems, n => n.Card.Kind == NoticeKinds.NewerWaiting && n.Item.Path == $"{Gearbox}/Housing.SLDPRT");
        t.B.Close($"{Gearbox}/Housing.SLDPRT");
        await t.B.SyncTimesAsync(2);
        Assert.Equal("Housing", t.B.Text("Robot 2027/Drivetrain/Gears/Housing.SLDPRT"));
        Assert.False(FolderExists(t.B, Gearbox));
        Assert.Equal(replaces, t.B.Disk.Replaces);
        Assert.Empty(t.B.Disk.Recovered);
        Assert.Empty(t.B.Engine.View.Notices);
        NoViolations(t.A); NoViolations(t.B);
    }
}
