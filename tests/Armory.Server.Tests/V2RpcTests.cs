using System.Text.Json;
using Npgsql;
using Xunit.Abstractions;
namespace Armory.Server.Tests;

// Contract v2 (C1 to C7): server/sql/005_v2.sql, which carries idea-app's 0232 verbatim. These
// tests pin what 0232 does, including the points where it differs from lane A's design notes
// (docs/server/contract.md, v2 section).
[Collection("db")]
public sealed class V2RpcTests(DatabaseFixture db, ITestOutputHelper output)
{
    private const string Hash = "cccccccccccccccccccccccccccccccccccccccccccccccccccccccccccccccc";
    private static string Unique(string prefix) => prefix + " " + Guid.NewGuid().ToString("N")[..8];

    // Each test has its own student, CAD lead and instructor (xUnit makes one instance per test),
    // so no test here changes which projects a fixed email can see in the shared database. The
    // guard ServerContractTests.RlsIsolatesProjectsAndAnonCannotUseRpcs counts the projects of
    // student@example.com and must not depend on the order the tests run in.
    private readonly string studentEmail = Person("student"), leadEmail = Person("lead"), instructorEmail = Person("instructor");
    private static string Person(string role) => $"{role}.{Guid.NewGuid().ToString("N")[..8]}@example.com";

    private async Task<NpgsqlConnection> Admin(string email = "admin@example.com")
    {
        var c = await db.Open(email);
        await Cmd(c, "select set_config('armory.test_admins',@e,false)", ("e", email)).ExecuteNonQueryAsync();
        return c;
    }
    private static async Task<Guid> CreateProject(NpgsqlConnection c, string name, short? season = 2027, Guid? op = null)
        => (Guid)(await Cmd(c, "select armory_create_project(@n,@s::smallint,@o)", ("n", name), ("s", (object?)season ?? DBNull.Value), ("o", op ?? Guid.NewGuid())).ExecuteScalarAsync())!;
    private static async Task AddMember(NpgsqlConnection c, Guid p, string email, string role)
        => await Cmd(c, "select armory_add_member(@p,@e,@r::armory_member_role,@o)", ("p", p), ("e", email), ("r", role), ("o", Guid.NewGuid())).ExecuteNonQueryAsync();
    private static async Task<Guid> Device(NpgsqlConnection c, string name = "device")
        => (Guid)(await Cmd(c, "select armory_register_device(@n,@o)", ("n", name), ("o", Guid.NewGuid())).ExecuteScalarAsync())!;
    private static async Task<Guid> CreateFile(NpgsqlConnection c, Guid p, string folder, string name, Guid device, Guid? op = null)
        => (Guid)(await Cmd(c, "select armory_create_file(@p,@f,@n,@d,@o)", ("p", p), ("f", folder), ("n", name), ("d", device), ("o", op ?? Guid.NewGuid())).ExecuteScalarAsync())!;
    private static async Task<bool> Acquire(NpgsqlConnection c, Guid file, Guid device)
        => (bool)(await Cmd(c, "select armory_acquire_lock(@f,@d,@o)", ("f", file), ("d", device), ("o", Guid.NewGuid())).ExecuteScalarAsync())!;
    private static async Task<bool> Release(NpgsqlConnection c, Guid file, Guid device)
        => (bool)(await Cmd(c, "select armory_release_lock(@f,@d,@o)", ("f", file), ("d", device), ("o", Guid.NewGuid())).ExecuteScalarAsync())!;
    private static async Task<bool> Break(NpgsqlConnection c, Guid file, Guid device)
        => (bool)(await Cmd(c, "select armory_break_lock(@f,@d,@o)", ("f", file), ("d", device), ("o", Guid.NewGuid())).ExecuteScalarAsync())!;
    private static async Task<bool> Tombstone(NpgsqlConnection c, Guid file, Guid? parent, Guid device)
        => (bool)(await Cmd(c, "select armory_tombstone(@f,@v,@d,@o)", ("f", file), ("v", (object?)parent ?? DBNull.Value), ("d", device), ("o", Guid.NewGuid())).ExecuteScalarAsync())!;
    private static async Task<(Guid Version, bool Advanced)> Commit(NpgsqlConnection c, Guid file, Guid? parent, Guid device)
    {
        await using var row = await Cmd(c, "select * from armory_commit_version(@f,@v,'key',@h,3,@d,@o)", ("f", file), ("v", (object?)parent ?? DBNull.Value), ("h", Hash), ("d", device), ("o", Guid.NewGuid())).ExecuteReaderAsync();
        Assert.True(await row.ReadAsync());
        return (row.GetGuid(0), row.GetBoolean(1));
    }
    private static async Task<bool> RenameProject(NpgsqlConnection c, Guid p, string name, Guid? op = null)
        => (bool)(await Cmd(c, "select armory_rename_project(@p,@n,@o)", ("p", p), ("n", name), ("o", op ?? Guid.NewGuid())).ExecuteScalarAsync())!;
    private static async Task<bool> SetArchived(NpgsqlConnection c, Guid p, bool? archived, Guid? op = null)
        => (bool)(await Cmd(c, "select armory_set_project_archived(@p,@a::boolean,@o)", ("p", p), ("a", (object?)archived ?? DBNull.Value), ("o", op ?? Guid.NewGuid())).ExecuteScalarAsync())!;
    private static async Task<int> RenameFolder(NpgsqlConnection c, Guid p, string from, string to, Guid device, Guid? op = null)
        => (int)(await Cmd(c, "select armory_rename_folder(@p,@f,@t,@d,@o)", ("p", p), ("f", from), ("t", to), ("d", device), ("o", op ?? Guid.NewGuid())).ExecuteScalarAsync())!;
    private static async Task<int> DeleteFolder(NpgsqlConnection c, Guid p, string folder, Guid device, Guid? op = null)
        => (int)(await Cmd(c, "select armory_delete_folder(@p,@f,@d,@o)", ("p", p), ("f", folder), ("d", device), ("o", op ?? Guid.NewGuid())).ExecuteScalarAsync())!;
    private static async Task<long> Count(NpgsqlConnection c, string sql, params (string, object)[] ps) => (long)(await Cmd(c, sql, ps).ExecuteScalarAsync())!;
    private static async Task<string?> Text(NpgsqlConnection c, string sql, params (string, object)[] ps) => await Cmd(c, sql, ps).ExecuteScalarAsync() as string;
    private static async Task<JsonElement> Json(NpgsqlConnection c, string sql, params (string, object)[] ps)
    {
        using var document = JsonDocument.Parse((string)(await Cmd(c, sql, ps).ExecuteScalarAsync())!);
        return document.RootElement.Clone();
    }
    private static async Task<JsonElement> MyProject(NpgsqlConnection c, Guid p)
        => (await Json(c, "select armory_my_projects()::text")).EnumerateArray().Single(x => x.GetProperty("id").GetGuid() == p);
    private static async Task<PostgresException> Refused(Func<Task> call, string state)
    {
        var error = await Assert.ThrowsAsync<PostgresException>(call);
        Assert.Equal(state, error.SqlState);
        return error;
    }
    private static JsonElement Detail(PostgresException error)
    {
        using var document = JsonDocument.Parse(error.Detail!);
        return document.RootElement.Clone();
    }
    private static string[] Names(JsonElement detail) => detail.GetProperty("names").EnumerateArray().Select(n => n.GetString()!).ToArray();

    // A project with a mentor (admin@example.com), this test's CAD lead and this test's student.
    private async Task<(Guid Project, NpgsqlConnection Mentor)> Team()
    {
        var mentor = await Admin();
        var p = await CreateProject(mentor, Unique("Robot"));
        await AddMember(mentor, p, leadEmail, "cad_lead");
        await AddMember(mentor, p, studentEmail, "student");
        return (p, mentor);
    }

    [DatabaseFact]
    public async Task ANullSeasonIsAcceptedAndEveryReaderToleratesIt()
    {
        await using var admin = await Admin();
        var op = Guid.NewGuid();
        var p = await CreateProject(admin, Unique("No season"), null, op);
        Assert.Equal(p, await CreateProject(admin, Unique("No season"), null, op)); // receipt hit
        Assert.Equal(DBNull.Value, await Cmd(admin, "select season from armory_projects where id=@p", ("p", p)).ExecuteScalarAsync());
        foreach (var bad in new short[] { 1999, 2101 })
            await Refused(() => CreateProject(admin, Unique("Bad season"), bad), "22023");
        var mine = await MyProject(admin, p);
        Assert.Equal(JsonValueKind.Null, mine.GetProperty("season").ValueKind);
        Assert.False(mine.GetProperty("archived").GetBoolean());
        Assert.Equal(JsonValueKind.Null, mine.GetProperty("archived_at").ValueKind);
        Assert.Equal("null", await Text(admin, "select jsonb_typeof(payload->'season') from armory_list_changes(@p,0) where kind='project_created'", ("p", p)));
        // Part numbers with no season on the call and none on the project use the current year in Los Angeles.
        var losAngeles = TimeZoneInfo.FindSystemTimeZoneById("America/Los_Angeles");
        var before = TimeZoneInfo.ConvertTime(DateTimeOffset.UtcNow, losAngeles).Year;
        var allocated = await Text(admin, "select part_number from armory_allocate_part_number(@p,1,null,@o)", ("p", p), ("o", Guid.NewGuid()));
        var after = TimeZoneInfo.ConvertTime(DateTimeOffset.UtcNow, losAngeles).Year;
        Assert.Contains(allocated, new[] { $"5669-{before % 100:D2}-0100", $"5669-{after % 100:D2}-0100" });
        Assert.Equal("5669-30-0100", await Text(admin, "select part_number from armory_allocate_part_number(@p,1,2030,@o)", ("p", p), ("o", Guid.NewGuid()))); // an explicit season still wins
        var seasoned = await CreateProject(admin, Unique("Season"), 2027);
        Assert.Equal("5669-27-0100", await Text(admin, "select part_number from armory_allocate_part_number(@p,1,null,@o)", ("p", seasoned), ("o", Guid.NewGuid())));
        Assert.Equal(2027, (await MyProject(admin, seasoned)).GetProperty("season").GetInt32());
    }

    [DatabaseFact]
    public async Task RenameProjectIsMentorOnlyFollowsCreateNameRulesAndReplays()
    {
        var (p, mentor) = await Team(); await using var _ = mentor;
        var original = (await Text(mentor, "select name from armory_projects where id=@p", ("p", p)))!;
        await AddMember(mentor, p, instructorEmail, "instructor");
        foreach (var email in new[] { leadEmail, studentEmail, instructorEmail, "stranger@example.com" })
        {
            await using var c = await db.Open(email);
            await Refused(() => RenameProject(c, p, Unique("Nope")), "42501");
        }
        foreach (var bad in new[] { "", "CON", "a/b", "trailing.", "trailing ", "x:y", ".armory", "~$lock", "Thumbs.db", "COM1.txt" })
            await Refused(() => RenameProject(mentor, p, bad), "22023");
        var other = Unique("Other");
        await CreateProject(mentor, other);
        var taken = await Refused(() => RenameProject(mentor, p, other.ToUpperInvariant()), "23505");
        Assert.Equal(other, Detail(taken).GetProperty("existing_name").GetString());
        Assert.False(await RenameProject(mentor, p, original)); // already that name
        var renamed = Unique("Renamed");
        var op = Guid.NewGuid();
        Assert.True(await RenameProject(mentor, p, renamed, op));
        Assert.True(await RenameProject(mentor, p, renamed, op)); // replay returns the first answer
        Assert.False(await RenameProject(mentor, p, renamed));
        Assert.True(await RenameProject(mentor, p, renamed.ToUpperInvariant())); // a case-only rename is a rename
        Assert.Equal(renamed.ToUpperInvariant(), (await MyProject(mentor, p)).GetProperty("name").GetString());
        Assert.True(await RenameProject(mentor, p, "Résumé " + Guid.NewGuid().ToString("N")[..6]));
        Assert.StartsWith("Résumé ", (await Text(mentor, "select name from armory_projects where id=@p", ("p", p)))!); // stored as NFC
        Assert.Equal(3L, await Count(mentor, "select count(*) from armory_list_changes(@p,0) where kind='project_renamed'", ("p", p)));
        var first = await Json(mentor, "select payload::text from armory_list_changes(@p,0) where kind='project_renamed' order by cursor limit 1", ("p", p));
        Assert.Equal((original, renamed, "admin@example.com"), (first.GetProperty("from").GetString(), first.GetProperty("to").GetString(), first.GetProperty("by").GetString()));
    }

    [DatabaseFact]
    public async Task ARenameAndACreateOfOneProjectNameHaveOneWinner()
    {
        var (p, mentor) = await Team(); await using var _ = mentor;
        for (var i = 0; i < 100; i++)
        {
            var name = Unique("Contest");
            await using var a = await Admin(); await using var b = await Admin();
            var create = Try(async () => { await CreateProject(a, name); return name; });
            var rename = Try(async () => { await RenameProject(b, p, name.ToUpperInvariant()); return name.ToUpperInvariant(); });
            var results = await Task.WhenAll(create, rename);
            Assert.Equal(1, results.Count(r => r.Won is not null));
            var winner = results.Single(r => r.Won is not null).Won!;
            Assert.Equal(winner, JsonDocument.Parse(results.Single(r => r.Won is null).Detail!).RootElement.GetProperty("existing_name").GetString());
            Assert.Equal(1L, await Count(mentor, "select count(*) from armory_projects where lower(name)=lower(@n)", ("n", name)));
        }
        static async Task<(string? Won, string? Detail)> Try(Func<Task<string>> call)
        {
            try { return (await call(), null); }
            catch (PostgresException e) when (e.SqlState == "23505") { return (null, e.Detail); }
        }
    }

    [DatabaseFact]
    public async Task ArchivingIsMentorOnlyShowsInMyProjectsAndDeletesNothing()
    {
        var (p, mentor) = await Team(); await using var _ = mentor;
        await using var student = await db.Open(studentEmail); var device = await Device(student);
        var f = await CreateFile(student, p, "Drivetrain", "Plate.SLDPRT", device);
        Assert.True(await Acquire(student, f, device));
        var v1 = await Commit(student, f, null, device);
        foreach (var email in new[] { leadEmail, studentEmail, "stranger@example.com" })
        {
            await using var c = await db.Open(email);
            await Refused(() => SetArchived(c, p, true), "42501");
        }
        await Refused(() => SetArchived(mentor, p, null), "22023");
        Assert.False(await SetArchived(mentor, p, false)); // not archived yet
        var op = Guid.NewGuid();
        Assert.True(await SetArchived(mentor, p, true, op));
        Assert.True(await SetArchived(mentor, p, true, op)); // replay
        Assert.False(await SetArchived(mentor, p, true)); // already archived
        var mine = await MyProject(student, p);
        Assert.True(mine.GetProperty("archived").GetBoolean());
        Assert.Equal(JsonValueKind.String, mine.GetProperty("archived_at").ValueKind);
        // Nothing is deleted: the file, its version and its checkout are all still there.
        var file = (await Json(student, "select armory_project_files(@p)::text", ("p", p))).EnumerateArray().Single();
        Assert.False(file.GetProperty("deleted").GetBoolean());
        Assert.Equal(v1.Version, file.GetProperty("current").GetProperty("id").GetGuid());
        Assert.Equal(studentEmail, file.GetProperty("lock").GetProperty("holder_email").GetString());
        Assert.Equal(0L, await Count(mentor, "select count(*) from armory_tombstones t join armory_files f on f.id=t.file_id where f.project_id=@p", ("p", p)));
        var change = await Json(mentor, "select payload::text from armory_list_changes(@p,0) where kind='project_archived'", ("p", p));
        Assert.True(change.GetProperty("archived").GetBoolean());
        Assert.Equal("admin@example.com", change.GetProperty("by").GetString());
        Assert.True(await SetArchived(mentor, p, false));
        mine = await MyProject(student, p);
        Assert.False(mine.GetProperty("archived").GetBoolean());
        Assert.Equal(JsonValueKind.Null, mine.GetProperty("archived_at").ValueKind);
        Assert.Equal(1L, await Count(mentor, "select count(*) from armory_list_changes(@p,0) where kind='project_archived'", ("p", p)));
        Assert.Equal(1L, await Count(mentor, "select count(*) from armory_list_changes(@p,0) where kind='project_restored'", ("p", p)));
    }

    [DatabaseFact]
    public async Task CreatingARemovedNameRevivesTheSameFileWithItsHistory()
    {
        var (p, mentor) = await Team(); await using var _ = mentor;
        await using var student = await db.Open(studentEmail); var laptop = await Device(student, "laptop");
        var f = await CreateFile(student, p, "Drivetrain", "Plate.SLDPRT", laptop);
        Assert.True(await Acquire(student, f, laptop));
        var v1 = await Commit(student, f, null, laptop);
        Assert.True(v1.Advanced);
        Assert.True(await Tombstone(student, f, v1.Version, laptop)); // 002 leaves the remover's checkout in place
        await using var lead = await db.Open(leadEmail); var leadPc = await Device(lead, "LAB-PC-07");
        var op = Guid.NewGuid();
        Assert.Equal(f, await CreateFile(lead, p, "Intake", "PLATE.sldprt", leadPc, op)); // the same file, not a new one
        Assert.Equal(f, await CreateFile(lead, p, "Intake", "PLATE.sldprt", leadPc, op)); // replay
        await using (var row = await Cmd(lead, "select deleted_at is null, folder, name, current_version_id from armory_files where id=@f", ("f", f)).ExecuteReaderAsync())
        {
            Assert.True(await row.ReadAsync());
            Assert.Equal((true, "Intake", "PLATE.sldprt", v1.Version), (row.GetBoolean(0), row.GetString(1), row.GetString(2), row.GetGuid(3)));
        }
        Assert.Equal(0L, await Count(lead, "select count(*) from armory_tombstones where file_id=@f", ("f", f)));
        Assert.Equal(0L, await Count(lead, "select count(*) from armory_locks where file_id=@f", ("f", f))); // the remover's checkout is released
        Assert.Equal(1L, await Count(lead, "select count(*) from armory_files where project_id=@p", ("p", p)));
        Assert.Equal(1L, await Count(lead, "select count(*) from armory_versions where file_id=@f", ("f", f)));
        var history = (await Json(lead, "select armory_file_history(@f)::text", ("f", f))).EnumerateArray().ToArray();
        Assert.Equal(v1.Version, Assert.Single(history).GetProperty("id").GetGuid()); // history continues; the removal entry is gone with its row
        Assert.Equal(1L, await Count(lead, "select count(*) from armory_list_changes(@p,0) where kind='file_revived'", ("p", p)));
        Assert.Equal(1L, await Count(lead, "select count(*) from armory_list_changes(@p,0) where kind='file_created'", ("p", p)));
        var revived = await Json(lead, "select payload::text from armory_list_changes(@p,0) where kind='file_revived' and entity_id=@f", ("p", p), ("f", f));
        Assert.Equal(("Intake", "PLATE.sldprt", "Drivetrain", "Plate.SLDPRT"), (revived.GetProperty("folder").GetString(), revived.GetProperty("name").GetString(), revived.GetProperty("old_folder").GetString(), revived.GetProperty("old_name").GetString()));
        Assert.Equal((studentEmail, leadEmail, leadPc), (revived.GetProperty("released_checkout_of").GetString(), revived.GetProperty("by").GetString(), revived.GetProperty("device_id").GetGuid()));
        var file = (await Json(lead, "select armory_project_files(@p)::text", ("p", p))).EnumerateArray().Single();
        Assert.Equal((false, "Intake", v1.Version), (file.GetProperty("deleted").GetBoolean(), file.GetProperty("folder").GetString(), file.GetProperty("current").GetProperty("id").GetGuid()));
        // The reviver checks it out and continues from the version that was current at removal.
        Assert.True(await Acquire(lead, f, leadPc));
        // Without that parent, the way the 0.1.0 agent commits a file it has just created, the commit
        // is a stale parent: kept aside, and the removed bytes stay the shared version, so 0.1.0 then
        // downloads them over the new file (docs/server/contract.md, open point 1).
        var orphan = await Commit(lead, f, null, leadPc);
        Assert.False(orphan.Advanced);
        Assert.Equal("stale parent", await Text(lead, "select reason from armory_side_versions where id=@v and file_id=@f", ("v", orphan.Version), ("f", f)));
        Assert.Equal(v1.Version, (Guid)(await Cmd(lead, "select current_version_id from armory_files where id=@f", ("f", f)).ExecuteScalarAsync())!);
        var v2 = await Commit(lead, f, v1.Version, leadPc);
        Assert.True(v2.Advanced);
        Assert.False((await Commit(student, f, v1.Version, laptop)).Advanced); // the former holder no longer holds it
        // A live clash still refuses with the existing folder.
        var taken = await Refused(() => CreateFile(student, p, "Elsewhere", "plate.SLDPRT", laptop), "23505");
        Assert.Equal(("Intake", f), (Detail(taken).GetProperty("existing_folder").GetString(), Detail(taken).GetProperty("file_id").GetGuid()));
        // Removing it again records the new remover and version.
        Assert.True(await Tombstone(lead, f, v2.Version, leadPc));
        await using (var row = await Cmd(lead, "select author_email, version_id from armory_tombstones where file_id=@f", ("f", f)).ExecuteReaderAsync())
        {
            Assert.True(await row.ReadAsync());
            Assert.Equal((leadEmail, v2.Version), (row.GetString(0), row.GetGuid(1)));
        }
    }

    [DatabaseFact]
    public async Task RacingRevivalsReviveOnceAndTellTheLoserWhereItLives()
    {
        var (p, mentor) = await Team(); await using var _ = mentor;
        await using var student = await db.Open(studentEmail); var studentPc = await Device(student);
        await using var lead = await db.Open(leadEmail); var leadPc = await Device(lead);
        for (var i = 0; i < 100; i++)
        {
            var name = $"Revive{i}.SLDPRT";
            var f = await CreateFile(student, p, "Old", name, studentPc);
            Assert.True(await Acquire(student, f, studentPc));
            Assert.True(await Tombstone(student, f, null, studentPc));
            await using var a = await db.Open(studentEmail); await using var b = await db.Open(leadEmail);
            var x = Try(() => CreateFile(a, p, "A", name, studentPc)); var y = Try(() => CreateFile(b, p, "B", name.ToLowerInvariant(), leadPc));
            var results = await Task.WhenAll(x, y);
            var winner = Assert.Single(results, r => r.Id is not null);
            Assert.Equal(f, winner.Id);
            var detail = JsonDocument.Parse(results.Single(r => r.Id is null).Detail!).RootElement;
            Assert.Equal(f, detail.GetProperty("file_id").GetGuid());
            Assert.Equal(await Text(mentor, "select folder from armory_files where id=@f", ("f", f)), detail.GetProperty("existing_folder").GetString());
            Assert.Equal(1L, await Count(mentor, "select count(*) from armory_list_changes(@p,0) where kind='file_revived' and entity_id=@f", ("p", p), ("f", f)));
        }
        static async Task<(Guid? Id, string? Detail)> Try(Func<Task<Guid>> call)
        {
            try { return (await call(), null); }
            catch (PostgresException e) when (e.SqlState == "23505") { return (null, e.Detail); }
        }
    }

    [DatabaseFact]
    public async Task FolderRenameMovesTheWholeSubtreeInOneChange()
    {
        var (p, mentor) = await Team(); await using var _ = mentor;
        await using var student = await db.Open(studentEmail); var laptop = await Device(student, "laptop");
        var top = await CreateFile(student, p, "Drive", "Base.SLDPRT", laptop);
        var gear = await CreateFile(student, p, "Drive/Gear", "Gear.SLDPRT", laptop);
        var tooth = await CreateFile(student, p, "Drive/Gear/Teeth", "Tooth.SLDPRT", laptop);
        var caseVariant = await CreateFile(student, p, "drive/Shaft", "Shaft.SLDPRT", laptop);
        var prefix = await CreateFile(student, p, "Drivetrain", "Belt.SLDPRT", laptop);
        var wildcard = await CreateFile(student, p, "Dr_ve", "Wildcard.SLDPRT", laptop);
        var removed = await CreateFile(student, p, "Drive/Old", "Removed.SLDPRT", laptop);
        Assert.True(await Acquire(student, removed, laptop)); Assert.True(await Tombstone(student, removed, null, laptop));
        Assert.True(await Acquire(student, gear, laptop)); // the caller's own checkout on this computer moves with the folder
        // '_' is a literal, never a wildcard: renaming Dr_ve leaves Drive alone.
        Assert.Equal(1, await RenameFolder(student, p, "Dr_ve", "Dr_ve 2", laptop));
        var op = Guid.NewGuid();
        Assert.Equal(3, await RenameFolder(student, p, "Drive", "Powertrain", laptop, op));
        Assert.Equal(3, await RenameFolder(student, p, "Drive", "Powertrain", laptop, op)); // replay
        async Task<string?> FolderOf(Guid file) => await Text(mentor, "select folder from armory_files where id=@f", ("f", file));
        Assert.Equal("Powertrain", await FolderOf(top));
        Assert.Equal("Powertrain/Gear", await FolderOf(gear));
        Assert.Equal("Powertrain/Gear/Teeth", await FolderOf(tooth));
        Assert.Equal("drive/Shaft", await FolderOf(caseVariant)); // 0232 matches the source folder exactly, case included
        Assert.Equal("Drivetrain", await FolderOf(prefix));
        Assert.Equal("Dr_ve 2", await FolderOf(wildcard));
        Assert.Equal("Drive/Old", await FolderOf(removed)); // a removed file keeps its folder
        Assert.Equal((studentEmail, laptop), ((await Text(mentor, "select holder_email from armory_locks where file_id=@f and broken_at is null", ("f", gear)))!, (Guid)(await Cmd(mentor, "select holder_device_id from armory_locks where file_id=@f", ("f", gear)).ExecuteScalarAsync())!));
        Assert.Equal(2L, await Count(mentor, "select count(*) from armory_list_changes(@p,0) where kind='folder_renamed'", ("p", p)));
        Assert.Equal(0L, await Count(mentor, "select count(*) from armory_list_changes(@p,0) where kind='file_moved'", ("p", p)));
        var change = await Json(mentor, "select payload::text from armory_list_changes(@p,0) where kind='folder_renamed' order by cursor desc limit 1", ("p", p));
        Assert.Equal(("Drive", "Powertrain", 3, laptop, studentEmail), (change.GetProperty("from").GetString(), change.GetProperty("to").GetString(), change.GetProperty("files").GetInt32(), change.GetProperty("device_id").GetGuid(), change.GetProperty("by").GetString()));
        // A folder with no live files moves nothing and writes no change.
        Assert.Equal(0, await RenameFolder(student, p, "Nothing here", "Still nothing", laptop));
        Assert.Equal(2L, await Count(mentor, "select count(*) from armory_list_changes(@p,0) where kind='folder_renamed'", ("p", p)));
    }

    [DatabaseFact]
    public async Task FolderRenameIsRefusedWhileSomeoneElseOrMyOtherComputerHasAFileCheckedOut()
    {
        var (p, mentor) = await Team(); await using var _ = mentor;
        await using var student = await db.Open(studentEmail); var laptop = await Device(student, "laptop"); var labPc = await Device(student, "lab PC");
        await using var lead = await db.Open(leadEmail); var leadPc = await Device(lead, "LAB-PC-07");
        var files = new List<Guid>();
        for (var i = 0; i < 14; i++) files.Add(await CreateFile(student, p, i % 2 == 0 ? "Gearbox" : "Gearbox/Stage", $"Part{i:D2}.SLDPRT", laptop));
        var outside = await CreateFile(student, p, "Gearbox2", "Outside.SLDPRT", laptop);
        Assert.True(await Acquire(lead, outside, leadPc)); // a checkout outside the folder never blocks it
        for (var i = 0; i < 12; i++) Assert.True(await Acquire(lead, files[i], leadPc));
        var op = Guid.NewGuid();
        var refused = await Refused(() => RenameFolder(student, p, "Gearbox", "Transmission", labPc, op), "55006");
        var detail = Detail(refused);
        Assert.Equal("checked_out", detail.GetProperty("reason").GetString());
        Assert.Equal(12, detail.GetProperty("total").GetInt32());
        Assert.Equal(Enumerable.Range(0, 10).Select(i => $"Part{i:D2}.SLDPRT"), Names(detail)); // at most 10, sorted
        Assert.Contains("12", refused.MessageText);
        var deleteRefused = await Refused(() => DeleteFolder(student, p, "Gearbox", labPc), "55006");
        Assert.Equal(12, Detail(deleteRefused).GetProperty("total").GetInt32());
        Assert.Equal(14L, await Count(mentor, "select count(*) from armory_files where project_id=@p and deleted_at is null and (folder='Gearbox' or folder='Gearbox/Stage')", ("p", p)));
        Assert.Equal(0L, await Count(mentor, "select count(*) from armory_list_changes(@p,0) where kind in ('folder_renamed','folder_deleted')", ("p", p)));
        for (var i = 0; i < 12; i++) Assert.True(await Release(lead, files[i], leadPc));
        // My own checkout on my other computer blocks this computer's rename too.
        Assert.True(await Acquire(student, files[13], laptop));
        var mine = await Refused(() => RenameFolder(student, p, "Gearbox", "Transmission", labPc, op), "55006");
        Assert.Equal(1, Detail(mine).GetProperty("total").GetInt32());
        Assert.Equal(new[] { "Part13.SLDPRT" }, Names(Detail(mine)));
        // A taken-back (broken) checkout does not block, and the refused operation id now succeeds.
        Assert.True(await Break(mentor, files[13], await Device(mentor)));
        Assert.Equal(14, await RenameFolder(student, p, "Gearbox", "Transmission", labPc, op));
        Assert.Equal("Gearbox2", await Text(mentor, "select folder from armory_files where id=@f", ("f", outside)));
        Assert.Equal(1L, await Count(mentor, "select count(*) from armory_list_changes(@p,0) where kind='folder_renamed'", ("p", p)));
    }

    [DatabaseFact]
    public async Task FolderRenameRefusesBadPathsAMoveIntoItselfAndAnExistingFolder()
    {
        var (p, mentor) = await Team(); await using var _ = mentor;
        await using var student = await db.Open(studentEmail); var laptop = await Device(student);
        await CreateFile(student, p, "A", "One.SLDPRT", laptop);
        await CreateFile(student, p, "A/B", "Two.SLDPRT", laptop);
        await CreateFile(student, p, "Target", "Three.SLDPRT", laptop);
        await CreateFile(student, p, "Other/Sub", "Four.SLDPRT", laptop);
        foreach (var (from, to) in new[] { ("", "X"), ("A", ""), ("/A", "X"), ("A", "/X"), ("A//B", "X"), ("A", "X//Y"), ("A", "X/"), ("A", "CON"), ("A", ".armory"), ("A", "~$x"), ("A", "x?"), ("A", "A"), ("A", "A/B"), ("A", "a/C") })
            await Refused(() => RenameFolder(student, p, from, to, laptop), "22023");
        foreach (var (target, name) in new[] { ("Target", "Three.SLDPRT"), ("target", "Three.SLDPRT"), ("Other", "Four.SLDPRT") })
        {
            var exists = Detail(await Refused(() => RenameFolder(student, p, "A", target, laptop), "55006"));
            Assert.Equal(("target_exists", 1), (exists.GetProperty("reason").GetString(), exists.GetProperty("total").GetInt32()));
            Assert.Equal(new[] { name }, Names(exists));
        }
        Assert.Equal(2, await RenameFolder(student, p, "A", "a", laptop)); // a case-only rename moves the whole folder
        Assert.Equal(2L, await Count(mentor, "select count(*) from armory_files where project_id=@p and folder in ('a','a/B')", ("p", p)));
        await using var stranger = await db.Open("stranger@example.com"); var strangerPc = await Device(stranger);
        await Refused(() => RenameFolder(stranger, p, "a", "Z", strangerPc), "42501");
        await using var lead = await db.Open(leadEmail); var leadPc = await Device(lead);
        await Assert.ThrowsAsync<PostgresException>(() => RenameFolder(student, p, "a", "Z", leadPc)); // someone else's device
        await Assert.ThrowsAsync<PostgresException>(() => DeleteFolder(student, p, "a", leadPc));
        Assert.Equal(1L, await Count(mentor, "select count(*) from armory_list_changes(@p,0) where kind='folder_renamed'", ("p", p)));
    }

    [DatabaseFact]
    public async Task DeleteFolderTombstonesTheSubtreeInOneChange()
    {
        var (p, mentor) = await Team(); await using var _ = mentor;
        await using var student = await db.Open(studentEmail); var laptop = await Device(student, "laptop");
        await using var lead = await db.Open(leadEmail); var leadPc = await Device(lead, "LAB-PC-07");
        var roller = await CreateFile(student, p, "Intake", "Roller.SLDPRT", laptop);
        Assert.True(await Acquire(student, roller, laptop)); // the caller's own checkout on this computer does not block
        var v1 = await Commit(student, roller, null, laptop);
        var arm = await CreateFile(student, p, "Intake/Arm", "Arm.SLDPRT", laptop);
        var keep = await CreateFile(student, p, "Intaker", "Keep.SLDPRT", laptop);
        Assert.True(await Acquire(lead, arm, leadPc));
        var refused = Detail(await Refused(() => DeleteFolder(student, p, "Intake", laptop), "55006"));
        Assert.Equal(("checked_out", 1), (refused.GetProperty("reason").GetString(), refused.GetProperty("total").GetInt32()));
        Assert.True(await Release(lead, arm, leadPc));
        foreach (var bad in new[] { "", "/Intake", "Intake/", "Intake//Arm", "CON" })
            await Refused(() => DeleteFolder(student, p, bad, laptop), "22023");
        var op = Guid.NewGuid();
        Assert.Equal(2, await DeleteFolder(student, p, "Intake", laptop, op));
        Assert.Equal(2, await DeleteFolder(student, p, "Intake", laptop, op)); // replay
        Assert.Equal(0, await DeleteFolder(student, p, "Intake", laptop)); // nothing live is left
        await using (var row = await Cmd(mentor, "select author_email, version_id from armory_tombstones where file_id=@f", ("f", roller)).ExecuteReaderAsync())
        {
            Assert.True(await row.ReadAsync());
            Assert.Equal((studentEmail, v1.Version), (row.GetString(0), row.GetGuid(1)));
        }
        Assert.Equal(DBNull.Value, await Cmd(mentor, "select version_id from armory_tombstones where file_id=@f", ("f", arm)).ExecuteScalarAsync());
        var files = (await Json(student, "select armory_project_files(@p)::text", ("p", p))).EnumerateArray().ToDictionary(f => f.GetProperty("id").GetGuid());
        Assert.True(files[roller].GetProperty("deleted").GetBoolean());
        Assert.True(files[arm].GetProperty("deleted").GetBoolean());
        Assert.False(files[keep].GetProperty("deleted").GetBoolean());
        Assert.Equal("Intake/Arm", files[arm].GetProperty("folder").GetString()); // removed rows keep their folder
        Assert.Equal(1L, await Count(mentor, "select count(*) from armory_list_changes(@p,0) where kind='folder_deleted'", ("p", p)));
        Assert.Equal(0L, await Count(mentor, "select count(*) from armory_list_changes(@p,0) where kind='tombstone'", ("p", p)));
        var change = await Json(mentor, "select payload::text from armory_list_changes(@p,0) where kind='folder_deleted'", ("p", p));
        Assert.Equal(("Intake", 2, studentEmail), (change.GetProperty("folder").GetString(), change.GetProperty("files").GetInt32(), change.GetProperty("by").GetString()));
        // A name from the deleted folder can be revived, and the remover's checkout goes with the revival.
        Assert.Equal(roller, await CreateFile(lead, p, "Intake 2", "roller.sldprt", leadPc));
        Assert.Equal(0L, await Count(mentor, "select count(*) from armory_locks where file_id=@f", ("f", roller)));
        Assert.True(await Acquire(lead, roller, leadPc));
        Assert.True((await Commit(lead, roller, v1.Version, leadPc)).Advanced);
    }

    [DatabaseFact]
    public async Task ACheckOutNeverLandsInsideAFolderRenameOrDelete()
    {
        var (p, mentor) = await Team(); await using var _ = mentor;
        await using var student = await db.Open(studentEmail); var laptop = await Device(student, "laptop");
        await using var lead = await db.Open(leadEmail); var leadPc = await Device(lead, "LAB-PC-07");
        async Task<int> FolderOp(NpgsqlConnection c, bool delete, string folder, NpgsqlTransaction? tx = null)
        {
            var sql = delete ? "select armory_delete_folder(@p,@f,@d,@o)" : "select armory_rename_folder(@p,@f,@t,@d,@o)";
            await using var command = Cmd(c, sql, ("p", p), ("f", folder), ("t", folder + " moved"), ("d", laptop), ("o", Guid.NewGuid()));
            command.Transaction = tx;
            return (int)(await command.ExecuteScalarAsync())!;
        }
        Task<long> FolderCursor(string folder) => Count(mentor, "select max(cursor) from armory_list_changes(@p,0) where kind in ('folder_renamed','folder_deleted') and coalesce(payload->>'from',payload->>'folder')=@f", ("p", p), ("f", folder));
        Task<long> LockCursor(Guid file) => Count(mentor, "select max(cursor) from armory_list_changes(@p,0) where kind='lock_acquired' and entity_id=@f", ("p", p), ("f", file));

        var mentorPc = await Device(mentor, "mentor laptop");
        async Task OpenCheckOutHoldsTheFolder(bool delete, string folder, Guid f)
        {
            await using (var holder = await db.Open(leadEmail))
            {
                await using var tx = await holder.BeginTransactionAsync();
                var acquire = Cmd(holder, "select armory_acquire_lock(@f,@d,@o)", ("f", f), ("d", leadPc), ("o", Guid.NewGuid()));
                acquire.Transaction = tx;
                Assert.True((bool)(await acquire.ExecuteScalarAsync())!);
                await using var mover = await db.Open(studentEmail);
                var operation = FolderOp(mover, delete, folder);
                Assert.False(await Task.WhenAny(operation, Task.Delay(750)) == operation, "The folder operation finished while another person's check-out was still open.");
                await tx.CommitAsync();
                var refused = await Refused(() => operation, "55006");
                Assert.Equal(1, Detail(refused).GetProperty("total").GetInt32());
            }
            Assert.Equal(folder, await Text(mentor, "select folder from armory_files where id=@f and deleted_at is null", ("f", f)));
        }

        foreach (var delete in new[] { false, true })
        {
            // A check-out that is still open makes the folder operation wait, then refuse.
            var folder = Unique("Held");
            var f = await CreateFile(student, p, folder, Unique("Part") + ".SLDPRT", laptop);
            await OpenCheckOutHoldsTheFolder(delete, folder, f);

            // The same for a check-out taken again after a take back, which updates the old lock row
            // instead of inserting one, so no foreign key check touches the file row. When the
            // check-out goes first, the change feed's foreign key holds 0232's project row until it
            // commits, so the folder operation then sees it. The other order is NOT held: a folder
            // operation that has the project row but has not yet read the locks misses a retaken
            // check-out waiting on that row (docs/server/contract.md, open point 2). Add that
            // interleaving here when lane W's follow-up migration closes it.
            folder = Unique("Retaken");
            f = await CreateFile(student, p, folder, Unique("Part") + ".SLDPRT", laptop);
            Assert.True(await Acquire(lead, f, leadPc));
            Assert.True(await Break(mentor, f, mentorPc));
            await OpenCheckOutHoldsTheFolder(delete, folder, f);

            // A folder operation that is still open makes the check-out wait until it lands.
            folder = Unique("Moving");
            f = await CreateFile(student, p, folder, Unique("Part") + ".SLDPRT", laptop);
            await using (var mover = await db.Open(studentEmail))
            {
                await using var tx = await mover.BeginTransactionAsync();
                Assert.Equal(1, await FolderOp(mover, delete, folder, tx));
                await using var holder = await db.Open(leadEmail);
                var acquire = Acquire(holder, f, leadPc);
                await Task.WhenAny(acquire, Task.Delay(250));
                await tx.CommitAsync();
                Assert.True(await acquire);
            }
            Assert.True(await LockCursor(f) > await FolderCursor(folder), "A check-out was recorded before the folder change it raced.");
        }

        // Free-running races: the folder operation either refuses, or every check-out it raced lands after it.
        var deadlocks = 0;
        for (var i = 0; i < 60; i++)
        {
            var delete = i % 2 == 1;
            var folder = Unique("Race");
            var f = await CreateFile(student, p, folder, Unique("Part") + ".SLDPRT", laptop);
            await using var mover = await db.Open(studentEmail); await using var holder = await db.Open(leadEmail);
            // 0232 takes the project row before the files, a check-out takes the file before the project, so a true
            // race can deadlock; detect it in 100 ms instead of the default second (both sides here are superuser).
            foreach (var c in new[] { mover, holder }) await Cmd(c, "set deadlock_timeout = '100ms'").ExecuteNonQueryAsync();
            var operation = Outcome(() => FolderOp(mover, delete, folder));
            var checkout = Outcome(async () => await Acquire(holder, f, leadPc) ? 1 : 0);
            var results = await Task.WhenAll(operation, checkout);
            deadlocks += results.Count(r => r.State == "40P01");
            var (moved, state) = results[0];
            var held = await Count(mentor, "select count(*) from armory_locks where file_id=@f and broken_at is null", ("f", f)) == 1;
            if (state == "55006") { Assert.True(held); Assert.Equal(folder, await Text(mentor, "select folder from armory_files where id=@f and deleted_at is null", ("f", f))); }
            else if (state is null) { Assert.Equal(1, moved); if (held) Assert.True(await LockCursor(f) > await FolderCursor(folder), $"Race {i}: a check-out landed inside a folder {(delete ? "delete" : "rename")}."); }
            else Assert.Equal("40P01", state);
        }
        output.WriteLine($"Free-running races: {deadlocks} deadlock abort(s) in 60.");
        static async Task<(int Value, string? State)> Outcome(Func<Task<int>> call)
        {
            try { return (await call(), null); }
            catch (PostgresException e) when (e.SqlState is "55006" or "40P01") { return (0, e.SqlState); }
        }
    }

    [DatabaseFact]
    public async Task ProjectCheckoutsListsLiveCheckoutsToMembersOnly()
    {
        var (p, mentor) = await Team(); await using var _ = mentor;
        var suffix = Guid.NewGuid().ToString("N")[..8];
        var maria = $"maria.{suffix}@example.com"; var alex = $"alex.{suffix}@example.com"; var plain = $"plain.{suffix}@example.com";
        await AddMember(mentor, p, maria, "student"); await AddMember(mentor, p, alex, "student"); await AddMember(mentor, p, plain, "student");
        await Cmd(mentor, "insert into public.profiles(email,full_name,display_name) values(@m,'Maria Lopez Garcia','Maria Lopez'),(@a,'Alex Kim','  ')", ("m", maria), ("a", alex)).ExecuteNonQueryAsync();
        Assert.Equal("[]", await Text(mentor, "select armory_project_checkouts(@p)::text", ("p", p)));
        await using var m = await db.Open(maria); var mariaPc = await Device(m, "LAB-PC-07");
        await using var a = await db.Open(alex); var alexPc = await Device(a, "Alex laptop");
        await using var n = await db.Open(plain); var plainPc = await Device(n, "Plain PC");
        var gear = await CreateFile(m, p, "Drivetrain", "Gear.SLDPRT", mariaPc);
        var shaft = await CreateFile(a, p, "", "Shaft.SLDPRT", alexPc);
        var plate = await CreateFile(n, p, "Intake", "Plate.SLDPRT", plainPc);
        var takenBack = await CreateFile(a, p, "", "Taken.SLDPRT", alexPc);
        var removed = await CreateFile(a, p, "", "Removed.SLDPRT", alexPc);
        await CreateFile(a, p, "", "Free.SLDPRT", alexPc);
        Assert.True(await Acquire(m, gear, mariaPc)); Assert.True(await Acquire(a, shaft, alexPc)); Assert.True(await Acquire(n, plate, plainPc));
        Assert.True(await Acquire(a, takenBack, alexPc)); Assert.True(await Break(mentor, takenBack, await Device(mentor)));
        Assert.True(await Acquire(a, removed, alexPc)); Assert.True(await Tombstone(a, removed, null, alexPc)); // the lock row stays, the file is removed
        var list = (await Json(n, "select armory_project_checkouts(@p)::text", ("p", p))).EnumerateArray().ToArray();
        Assert.Equal(new[] { gear, shaft, plate }, list.Select(x => x.GetProperty("file_id").GetGuid())); // in check-out order
        var first = list[0];
        Assert.Equal(("Drivetrain", "Gear.SLDPRT", maria, "Maria Lopez", "LAB-PC-07"), (first.GetProperty("folder").GetString(), first.GetProperty("name").GetString(), first.GetProperty("holder_email").GetString(), first.GetProperty("holder_name").GetString(), first.GetProperty("device_name").GetString()));
        var since = (DateTime)(await Cmd(mentor, "select acquired_at from armory_locks where file_id=@f", ("f", gear)).ExecuteScalarAsync())!;
        Assert.Equal(new DateTimeOffset(since, TimeSpan.Zero), first.GetProperty("since").GetDateTimeOffset());
        Assert.Equal("Alex Kim", list[1].GetProperty("holder_name").GetString()); // a blank display name falls back to the full name
        Assert.Equal(JsonValueKind.Null, list[2].GetProperty("holder_name").ValueKind); // no profile: null, never a guess
        await using var stranger = await db.Open("stranger@example.com");
        await Refused(() => Cmd(stranger, "select armory_project_checkouts(@p)", ("p", p)).ExecuteNonQueryAsync(), "42501");
    }

    [DatabaseFact]
    public async Task TakeBackStaysWithMentorsAndCadLeads()
    {
        var (p, mentor) = await Team(); await using var _ = mentor;
        await AddMember(mentor, p, instructorEmail, "instructor");
        await using var student = await db.Open(studentEmail); var studentPc = await Device(student);
        var f = await CreateFile(student, p, "", "Gear.SLDPRT", studentPc);
        Assert.True(await Acquire(student, f, studentPc));
        foreach (var email in new[] { instructorEmail, studentEmail, "stranger@example.com" })
        {
            await using var c = await db.Open(email);
            await Assert.ThrowsAsync<PostgresException>(async () => await Break(c, f, await Device(c)));
        }
        Assert.Equal(1L, await Count(mentor, "select count(*) from armory_locks where file_id=@f and broken_at is null", ("f", f)));
        await using var lead = await db.Open(leadEmail);
        Assert.True(await Break(lead, f, await Device(lead)));
        Assert.Equal(leadEmail, await Text(mentor, "select broken_by from armory_locks where file_id=@f", ("f", f)));
        Assert.Equal(0L, await Count(mentor, "select count(*) from jsonb_array_elements(armory_project_checkouts(@p)) as e(v) where (v->>'file_id')::uuid=@f", ("p", p), ("f", f)));
        foreach (var name in new[] { "armory_acquire_lock", "armory_release_lock", "armory_break_lock" })
            Assert.Equal(1L, await Count(mentor, "select count(*) from pg_proc p join pg_namespace n on n.oid=p.pronamespace where n.nspname='public' and p.proname=@n", ("n", name)));
    }

    [DatabaseFact]
    public async Task EveryV2WriteReturnsItsOriginalResultOnReplay()
    {
        var (p, mentor) = await Team(); await using var _ = mentor;
        await using var student = await db.Open(studentEmail); var laptop = await Device(student);
        var createOp = Guid.NewGuid();
        var unseasoned = await CreateProject(mentor, Unique("Replay"), null, createOp);
        Assert.Equal(unseasoned, await CreateProject(mentor, Unique("Replay"), null, createOp));
        var allocateOp = Guid.NewGuid();
        var number = await Text(mentor, "select part_number from armory_allocate_part_number(@p,2,null,@o)", ("p", unseasoned), ("o", allocateOp));
        Assert.Equal(number, await Text(mentor, "select part_number from armory_allocate_part_number(@p,2,null,@o)", ("p", unseasoned), ("o", allocateOp)));
        var renameOp = Guid.NewGuid(); var newName = Unique("Replayed");
        Assert.True(await RenameProject(mentor, p, newName, renameOp));
        Assert.True(await RenameProject(mentor, p, Unique("Ignored"), renameOp)); // the receipt answers; the new name is not applied
        Assert.Equal(newName, await Text(mentor, "select name from armory_projects where id=@p", ("p", p)));
        var archiveOp = Guid.NewGuid();
        Assert.True(await SetArchived(mentor, p, true, archiveOp));
        Assert.True(await SetArchived(mentor, p, false)); // restored
        Assert.True(await SetArchived(mentor, p, true, archiveOp)); // still the first answer, and nothing is written
        Assert.False((await MyProject(mentor, p)).GetProperty("archived").GetBoolean());
        var f = await CreateFile(student, p, "Bin", "Gear.SLDPRT", laptop);
        await CreateFile(student, p, "Bin/Deep", "Shaft.SLDPRT", laptop);
        var moveOp = Guid.NewGuid();
        Assert.Equal(2, await RenameFolder(student, p, "Bin", "Box", laptop, moveOp));
        Assert.Equal(2, await RenameFolder(student, p, "Bin", "Box", laptop, moveOp));
        var deleteOp = Guid.NewGuid();
        Assert.Equal(2, await DeleteFolder(student, p, "Box", laptop, deleteOp));
        Assert.Equal(2, await DeleteFolder(student, p, "Box", laptop, deleteOp));
        Assert.Equal(2, await RenameFolder(student, p, "Bin", "Box", laptop, moveOp)); // the files are gone, the receipt is not
        var reviveOp = Guid.NewGuid();
        Assert.Equal(f, await CreateFile(student, p, "Bin", "Gear.SLDPRT", laptop, reviveOp));
        Assert.Equal(f, await CreateFile(student, p, "Elsewhere", "Gear.SLDPRT", laptop, reviveOp));
        Assert.Equal("Bin", await Text(mentor, "select folder from armory_files where id=@f", ("f", f)));
        foreach (var (kind, count) in new[] { ("project_renamed", 1L), ("project_archived", 1L), ("project_restored", 1L), ("folder_renamed", 1L), ("folder_deleted", 1L), ("file_revived", 1L) })
            Assert.Equal(count, await Count(mentor, "select count(*) from armory_list_changes(@p,0) where kind=@k", ("p", p), ("k", kind)));
        // An operation id belongs to one RPC and one caller.
        await Assert.ThrowsAsync<PostgresException>(() => DeleteFolder(student, p, "Bin", laptop, moveOp));
        await using var lead = await db.Open(leadEmail);
        await Assert.ThrowsAsync<PostgresException>(async () => await RenameFolder(lead, p, "Bin", "Box", await Device(lead), moveOp));
    }

    [DatabaseFact]
    public async Task V2FunctionsAreForAuthenticatedOnlyAndHelpersForNoClient()
    {
        await using var c = await db.Open(studentEmail);
        Assert.Equal(0L, await Count(c, "select count(*) from pg_proc p join pg_namespace n on n.oid=p.pronamespace where n.nspname='public' and p.proname like 'armory\\_%' and has_function_privilege('anon',p.oid,'execute')"));
        var helpers = new[] { "armory_refuse_version_mutation", "armory_add_change", "armory_replay", "armory_remember", "armory_require_device",
            "armory_valid_segment", "armory_valid_folder", "armory_derived_operation", "armory_require_role", "armory_name_taken",
            "armory_check_release", "armory_record_release", "armory_live_name_taken", "armory_folder_files", "armory_refuse_checked_out" };
        var rpcs = new[] { "armory_create_project", "armory_allocate_part_number", "armory_my_projects", "armory_create_file", "armory_rename_project",
            "armory_set_project_archived", "armory_rename_folder", "armory_delete_folder", "armory_project_checkouts" };
        foreach (var name in helpers.Concat(rpcs))
            Assert.Equal(1L, await Count(c, "select count(*) from pg_proc p join pg_namespace n on n.oid=p.pronamespace where n.nspname='public' and p.proname=@n", ("n", name)));
        foreach (var name in helpers)
            Assert.Equal(0L, await Count(c, "select count(*) from pg_proc p join pg_namespace n on n.oid=p.pronamespace where n.nspname='public' and p.proname=@n and has_function_privilege('authenticated',p.oid,'execute')", ("n", name)));
        foreach (var name in rpcs.Append("armory_current_email").Append("armory_is_member"))
            Assert.Equal(1L, await Count(c, "select count(*) from pg_proc p join pg_namespace n on n.oid=p.pronamespace where n.nspname='public' and p.proname=@n and has_function_privilege('authenticated',p.oid,'execute')", ("n", name)));
        Assert.False((bool)(await Cmd(c, "select has_sequence_privilege('authenticated','public.armory_change_feed_cursor_seq','usage') or has_sequence_privilege('anon','public.armory_change_feed_cursor_seq','usage')").ExecuteScalarAsync())!);
        // RLS policies name armory_current_email, so a signed-in client reads its own devices and receipts directly.
        var device = await Device(c, "my laptop");
        await Cmd(c, "set role authenticated").ExecuteNonQueryAsync();
        try
        {
            Assert.Equal(0L, await Count(c, "select count(*) from armory_devices where owner_email<>@e", ("e", studentEmail)));
            Assert.Equal(1L, await Count(c, "select count(*) from armory_devices where id=@d", ("d", device)));
            Assert.True(await Count(c, "select count(*) from armory_operation_receipts") >= 1);
            Assert.Equal(0L, await Count(c, "select count(*) from armory_operation_receipts where caller_email<>@e", ("e", studentEmail)));
            await Refused(() => Cmd(c, "select armory_add_change(gen_random_uuid(),'x',gen_random_uuid(),'{}')").ExecuteNonQueryAsync(), "42501");
        }
        finally { await Cmd(c, "reset role").ExecuteNonQueryAsync(); }
    }

    private static NpgsqlCommand Cmd(NpgsqlConnection c, string sql, params (string, object)[] ps) { var x = new NpgsqlCommand(sql, c); foreach (var (n, v) in ps) x.Parameters.AddWithValue(n, v); return x; }
}
