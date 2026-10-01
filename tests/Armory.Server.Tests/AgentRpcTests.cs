using System.Text.Json;
using Npgsql;
namespace Armory.Server.Tests;

// Lane A additions (docs/agent/CONTRACT.md section 6) and the per-project release gate.
[Collection("db")]
public sealed class AgentRpcTests(DatabaseFixture db)
{
    private const string Hash = "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb";
    private static string Unique(string prefix) => prefix + " " + Guid.NewGuid().ToString("N")[..8];

    private async Task<NpgsqlConnection> Admin(string email = "admin@example.com")
    {
        var c = await db.Open(email);
        await Cmd(c, "select set_config('armory.test_admins',@e,false)", ("e", email)).ExecuteNonQueryAsync();
        return c;
    }
    private static async Task<Guid> CreateProject(NpgsqlConnection c, string name, Guid? op = null)
        => (Guid)(await Cmd(c, "select armory_create_project(@n,2027::smallint,@o)", ("n", name), ("o", op ?? Guid.NewGuid())).ExecuteScalarAsync())!;
    private static async Task<bool> AddMember(NpgsqlConnection c, Guid p, string email, string role, Guid? op = null)
        => (bool)(await Cmd(c, "select armory_add_member(@p,@e,@r::armory_member_role,@o)", ("p", p), ("e", email), ("r", role), ("o", op ?? Guid.NewGuid())).ExecuteScalarAsync())!;
    private static async Task<bool> RemoveMember(NpgsqlConnection c, Guid p, string email, Guid? op = null)
        => (bool)(await Cmd(c, "select armory_remove_member(@p,@e,@o)", ("p", p), ("e", email), ("o", op ?? Guid.NewGuid())).ExecuteScalarAsync())!;
    private static async Task<Guid> Device(NpgsqlConnection c, string name = "device")
        => (Guid)(await Cmd(c, "select armory_register_device(@n,@o)", ("n", name), ("o", Guid.NewGuid())).ExecuteScalarAsync())!;
    private static async Task<Guid> CreateFile(NpgsqlConnection c, Guid p, string folder, string name, Guid device, Guid? op = null)
        => (Guid)(await Cmd(c, "select armory_create_file(@p,@f,@n,@d,@o)", ("p", p), ("f", folder), ("n", name), ("d", device), ("o", op ?? Guid.NewGuid())).ExecuteScalarAsync())!;
    private static async Task<bool> Move(NpgsqlConnection c, Guid file, string folder, string name, Guid device, Guid? op = null)
        => (bool)(await Cmd(c, "select armory_move_file(@f,@fo,@n,@d,@o)", ("f", file), ("fo", folder), ("n", name), ("d", device), ("o", op ?? Guid.NewGuid())).ExecuteScalarAsync())!;
    private static async Task<bool> Acquire(NpgsqlConnection c, Guid file, Guid device)
        => (bool)(await Cmd(c, "select armory_acquire_lock(@f,@d,@o)", ("f", file), ("d", device), ("o", Guid.NewGuid())).ExecuteScalarAsync())!;
    private static async Task<long> Count(NpgsqlConnection c, string sql, params (string, object)[] ps) => (long)(await Cmd(c, sql, ps).ExecuteScalarAsync())!;
    private static async Task<PostgresException> Refused(Func<Task> call, string state)
    {
        var error = await Assert.ThrowsAsync<PostgresException>(call);
        Assert.Equal(state, error.SqlState);
        return error;
    }

    // A project with a mentor (admin@example.com), a CAD lead and a student.
    private async Task<(Guid Project, NpgsqlConnection Mentor)> Team()
    {
        var mentor = await Admin();
        var p = await CreateProject(mentor, Unique("Robot"));
        await AddMember(mentor, p, "lead@example.com", "cad_lead");
        await AddMember(mentor, p, "student@example.com", "student");
        return (p, mentor);
    }

    [DatabaseFact]
    public async Task CreateProjectRequiresAdminMakesCallerMentorAndReplays()
    {
        await using var student = await db.Open("student@example.com");
        await Refused(() => CreateProject(student, Unique("Nope")), "42501");
        await using var admin = await Admin();
        var name = Unique("Robot");
        var op = Guid.NewGuid();
        var p = await CreateProject(admin, name, op);
        Assert.Equal(p, await CreateProject(admin, name, op)); // receipt hit, not a duplicate-name refusal
        Assert.Equal(1L, await Count(admin, "select count(*) from armory_projects where name=@n", ("n", name)));
        Assert.Equal("mentor", (string)(await Cmd(admin, "select role::text from armory_members where project_id=@p and email='admin@example.com'", ("p", p)).ExecuteScalarAsync())!);
        Assert.Equal(1L, await Count(admin, "select count(*) from armory_list_changes(@p,0) where kind='project_created'", ("p", p)));
        Assert.Equal("warn", (string)(await Cmd(admin, "select release_gate from armory_projects where id=@p", ("p", p)).ExecuteScalarAsync())!);
        Assert.Equal((short)2025, (short)(await Cmd(admin, "select pinned_release from armory_projects where id=@p", ("p", p)).ExecuteScalarAsync())!);
        var taken = await Refused(() => CreateProject(admin, name.ToUpperInvariant()), "23505");
        Assert.Contains(name, taken.Detail);
        foreach (var bad in new[] { "", "CON", "a/b", "trailing.", "trailing ", "x:y", ".armory", "~$lock", "Thumbs.db", "COM1.txt" })
            await Refused(() => CreateProject(admin, bad), "22023");
        await Refused(() => Cmd(admin, "select armory_create_project('Old',1999::smallint,@o)", ("o", Guid.NewGuid())).ExecuteNonQueryAsync(), "22023");
        await using var other = await db.Open("other@example.com"); // another caller cannot reuse the operation id
        await Assert.ThrowsAsync<PostgresException>(() => Cmd(other, "select armory_create_project('Other',2027::smallint,@o)", ("o", op)).ExecuteNonQueryAsync());
    }

    [DatabaseFact]
    public async Task AddMemberRulesAndReplay()
    {
        var (p, mentor) = await Team(); await using var _ = mentor;
        var op = Guid.NewGuid();
        Assert.True(await AddMember(mentor, p, "  New@Example.com ", "student", op));
        Assert.True(await AddMember(mentor, p, "new@example.com", "student", op)); // replay returns the original answer
        Assert.False(await AddMember(mentor, p, "new@example.com", "student")); // already that role
        Assert.Equal(1L, await Count(mentor, "select count(*) from armory_members where project_id=@p and email='new@example.com'", ("p", p)));
        Assert.Equal(1L, await Count(mentor, "select count(*) from armory_list_changes(@p,0) where kind='member_added' and payload->>'email'='new@example.com'", ("p", p)));
        await using var lead = await db.Open("lead@example.com");
        Assert.True(await AddMember(lead, p, "another@example.com", "student"));
        Assert.True(await AddMember(lead, p, "another@example.com", "instructor"));
        Assert.Equal(1L, await Count(mentor, "select count(*) from armory_list_changes(@p,0) where kind='member_role_changed'", ("p", p)));
        await using var student = await db.Open("student@example.com");
        await Refused(() => AddMember(student, p, "x@example.com", "student"), "42501");
        await using var stranger = await db.Open("stranger@example.com");
        await Refused(() => AddMember(stranger, p, "x@example.com", "student"), "42501");
        foreach (var bad in new[] { "", "no-at-sign", "two@@example.com", "a b@example.com" })
            await Refused(() => AddMember(mentor, p, bad, "student"), "22023");
    }

    [DatabaseFact]
    public async Task OnlyAMentorGrantsMentorOrCadLead()
    {
        var (p, mentor) = await Team(); await using var _ = mentor;
        await using var lead = await db.Open("lead@example.com");
        await Refused(() => AddMember(lead, p, "x@example.com", "cad_lead"), "42501");
        await Refused(() => AddMember(lead, p, "x@example.com", "mentor"), "42501");
        await Refused(() => AddMember(lead, p, "student@example.com", "cad_lead"), "42501"); // promotion
        await Refused(() => AddMember(lead, p, "admin@example.com", "student"), "42501"); // demoting a mentor
        Assert.True(await AddMember(lead, p, "x@example.com", "student"));
        Assert.True(await AddMember(mentor, p, "x@example.com", "cad_lead"));
        await Refused(() => AddMember(lead, p, "x@example.com", "student"), "42501"); // a lead cannot change another lead
        Assert.True(await AddMember(mentor, p, "y@example.com", "mentor"));
        Assert.Equal(0L, await Count(mentor, "select count(*) from armory_members where project_id=@p and role in ('mentor','cad_lead') and email in ('student@example.com')", ("p", p)));
    }

    [DatabaseFact]
    public async Task RemoveMemberRulesAndReplay()
    {
        var (p, mentor) = await Team(); await using var _ = mentor;
        await using var lead = await db.Open("lead@example.com");
        await Refused(() => RemoveMember(lead, p, "student@example.com"), "42501");
        await using var student = await db.Open("student@example.com");
        await Refused(() => RemoveMember(student, p, "student@example.com"), "42501");
        var op = Guid.NewGuid();
        Assert.True(await RemoveMember(mentor, p, "student@example.com", op));
        Assert.True(await RemoveMember(mentor, p, "student@example.com", op)); // replay
        Assert.False(await RemoveMember(mentor, p, "student@example.com")); // already gone
        Assert.False(await RemoveMember(mentor, p, "never@example.com"));
        Assert.Equal(1L, await Count(mentor, "select count(*) from armory_list_changes(@p,0) where kind='member_removed'", ("p", p)));
        await Refused(() => Cmd(student, "select armory_project_files(@p)", ("p", p)).ExecuteNonQueryAsync(), "42501"); // removal takes effect
    }

    [DatabaseFact]
    public async Task TheLastMentorCanNeverBeRemoved()
    {
        var (p, mentor) = await Team(); await using var _ = mentor;
        await Refused(() => RemoveMember(mentor, p, "admin@example.com"), "P0001");
        await Refused(() => AddMember(mentor, p, "admin@example.com", "cad_lead"), "P0001");
        Assert.Equal(1L, await Count(mentor, "select count(*) from armory_members where project_id=@p and role='mentor'", ("p", p)));
        // Two mentors removing each other at the same moment must leave one of them.
        for (var i = 0; i < 100; i++)
        {
            var a = $"m{i}a@example.com"; var b = $"m{i}b@example.com";
            var team = await CreateProject(mentor, Unique("Race"));
            await AddMember(mentor, team, a, "mentor"); await AddMember(mentor, team, b, "mentor");
            await RemoveMember(mentor, team, "admin@example.com");
            await using var ca = await db.Open(a); await using var cb = await db.Open(b);
            var x = Attempt(() => RemoveMember(ca, team, b)); var y = Attempt(() => RemoveMember(cb, team, a));
            await Task.WhenAll(x, y);
            Assert.Equal(1L, await Count(mentor, "select count(*) from armory_members where project_id=@p and role='mentor'", ("p", team)));
        }
        static async Task Attempt(Func<Task> call) { try { await call(); } catch (PostgresException e) when (e.SqlState is "P0001" or "42501") { } }
    }

    [DatabaseFact]
    public async Task CreateFileHappyPathRefusalsAndReplay()
    {
        var (p, mentor) = await Team(); await using var _ = mentor;
        await using var student = await db.Open("student@example.com"); var device = await Device(student);
        var op = Guid.NewGuid();
        var f = await CreateFile(student, p, "Drivetrain/Gearbox", "Plate.SLDPRT", device, op);
        Assert.Equal(f, await CreateFile(student, p, "Drivetrain/Gearbox", "Plate.SLDPRT", device, op));
        Assert.Equal(1L, await Count(student, "select count(*) from armory_files where project_id=@p", ("p", p)));
        Assert.Equal("Drivetrain/Gearbox", (string)(await Cmd(student, "select folder from armory_files where id=@f", ("f", f)).ExecuteScalarAsync())!);
        Assert.Equal(1L, await Count(student, "select count(*) from armory_list_changes(@p,0) where kind='file_created' and entity_id=@f", ("p", p), ("f", f)));
        var composed = await CreateFile(student, p, "", "Café.txt", device); // stored as NFC
        Assert.Equal("Café.txt", (string)(await Cmd(student, "select name from armory_files where id=@f", ("f", composed)).ExecuteScalarAsync())!);
        foreach (var (folder, name) in new[] { ("", "CON.txt"), ("a//b", "x.txt"), ("a/", "x.txt"), ("/a", "x.txt"), ("a\\b", "x.txt"), ("a", "x?.txt"), ("a", "x."), ("a", "~$x.SLDPRT"), ("a", "desktop.ini"), (".armory", "x.txt"), ("a", "") })
            await Refused(() => CreateFile(student, p, folder, name, device), "22023");
        await using var stranger = await db.Open("stranger@example.com"); var strangerDevice = await Device(stranger);
        await Refused(() => CreateFile(stranger, p, "", "x.txt", strangerDevice), "42501");
        await Assert.ThrowsAsync<PostgresException>(() => CreateFile(student, p, "", "y.txt", strangerDevice)); // someone else's device
    }

    [DatabaseFact]
    public async Task TakenNameCarriesTheExistingFolder()
    {
        var (p, mentor) = await Team(); await using var _ = mentor;
        await using var student = await db.Open("student@example.com"); var device = await Device(student);
        var f = await CreateFile(student, p, "Drivetrain", "Plate.SLDPRT", device);
        foreach (var name in new[] { "plate.sldprt", "PLATE.SLDPRT" })
        {
            var taken = await Refused(() => CreateFile(student, p, "Intake", name, device), "23505");
            using var detail = JsonDocument.Parse(taken.Detail!);
            Assert.Equal("Drivetrain", detail.RootElement.GetProperty("existing_folder").GetString());
            Assert.Equal("Plate.SLDPRT", detail.RootElement.GetProperty("existing_name").GetString());
            Assert.Equal(f, detail.RootElement.GetProperty("file_id").GetGuid());
        }
        var decomposed = await CreateFile(student, p, "Root Folder", "Résumé.txt", device);
        var nfc = await Refused(() => CreateFile(student, p, "", "Résumé.TXT", device), "23505");
        Assert.Equal("Root Folder", JsonDocument.Parse(nfc.Detail!).RootElement.GetProperty("existing_folder").GetString());
        Assert.NotEqual(Guid.Empty, decomposed);
        // Two simultaneous creators: exactly one wins and the other is told where the winner lives.
        for (var i = 0; i < 100; i++)
        {
            await using var a = await db.Open("student@example.com"); await using var b = await db.Open("student@example.com");
            var name = $"Race{i}.SLDPRT";
            var x = Try(() => CreateFile(a, p, "A", name, device)); var y = Try(() => CreateFile(b, p, "B", name, device));
            var results = await Task.WhenAll(x, y);
            Assert.Equal(1, results.Count(r => r.Created));
            var loser = results.Single(r => !r.Created);
            Assert.Equal(results.Single(r => r.Created).Folder, JsonDocument.Parse(loser.Detail!).RootElement.GetProperty("existing_folder").GetString());
        }
        async Task<(bool Created, string Folder, string? Detail)> Try(Func<Task<Guid>> call)
        {
            try { var id = await call(); return (true, (string)(await Cmd(mentor, "select folder from armory_files where id=@f", ("f", id)).ExecuteScalarAsync())!, null); }
            catch (PostgresException e) when (e.SqlState == "23505") { return (false, "", e.Detail); }
        }
    }

    [DatabaseFact]
    public async Task MoveFileRequiresTheLockHolderAndWritesFileMoved()
    {
        var (p, mentor) = await Team(); await using var _ = mentor;
        await using var student = await db.Open("student@example.com"); var laptop = await Device(student, "laptop"); var labPc = await Device(student, "lab PC");
        var f = await CreateFile(student, p, "Drivetrain", "Plate.SLDPRT", laptop);
        await CreateFile(student, p, "Intake", "Roller.SLDPRT", laptop);
        Assert.False(await Move(student, f, "Intake", "Plate.SLDPRT", laptop)); // nobody holds the lock
        Assert.True(await Acquire(student, f, laptop));
        Assert.False(await Move(student, f, "Intake", "Plate.SLDPRT", labPc)); // same person, other device
        await using var lead = await db.Open("lead@example.com"); var leadDevice = await Device(lead);
        Assert.False(await Move(lead, f, "Intake", "Plate.SLDPRT", leadDevice)); // another person
        var op = Guid.NewGuid();
        Assert.True(await Move(student, f, "Intake/Rollers", "Plate-Left.SLDPRT", laptop, op));
        Assert.True(await Move(student, f, "Intake/Rollers", "Plate-Left.SLDPRT", laptop, op)); // replay
        Assert.Equal(1L, await Count(student, "select count(*) from armory_list_changes(@p,0) where kind='file_moved' and entity_id=@f", ("p", p), ("f", f)));
        await using (var change = await Cmd(student, "select payload from armory_list_changes(@p,0) where kind='file_moved' and entity_id=@f", ("p", p), ("f", f)).ExecuteReaderAsync())
        {
            Assert.True(await change.ReadAsync());
            using var payload = JsonDocument.Parse(change.GetString(0));
            Assert.Equal("Drivetrain", payload.RootElement.GetProperty("old_folder").GetString());
            Assert.Equal("Plate.SLDPRT", payload.RootElement.GetProperty("old_name").GetString());
            Assert.Equal("Intake/Rollers", payload.RootElement.GetProperty("folder").GetString());
            Assert.Equal("Plate-Left.SLDPRT", payload.RootElement.GetProperty("name").GetString());
        }
        var taken = await Refused(() => Move(student, f, "Drivetrain", "roller.sldprt", laptop), "23505");
        Assert.Equal("Intake", JsonDocument.Parse(taken.Detail!).RootElement.GetProperty("existing_folder").GetString());
        await Refused(() => Move(student, f, "Intake", "NUL", laptop), "22023");
        await using var stranger = await db.Open("stranger@example.com"); var strangerDevice = await Device(stranger);
        await Refused(() => Move(stranger, f, "", "x.SLDPRT", strangerDevice), "42501");
        Assert.True((bool)(await Cmd(mentor, "select armory_break_lock(@f,@d,@o)", ("f", f), ("d", await Device(mentor)), ("o", Guid.NewGuid())).ExecuteScalarAsync())!);
        Assert.False(await Move(student, f, "", "Plate.SLDPRT", laptop)); // a broken lock no longer authorizes a move
        Assert.Equal("Plate-Left.SLDPRT", (string)(await Cmd(student, "select name from armory_files where id=@f", ("f", f)).ExecuteScalarAsync())!);
    }

    [DatabaseFact]
    public async Task ReleaseGateDefaultsToWarnAndKnownNewerIsRefused()
    {
        var (p, mentor) = await Team(); await using var _ = mentor;
        await using var student = await db.Open("student@example.com"); var device = await Device(student);
        var part = await CreateFile(student, p, "", "Gear.SLDPRT", device);
        var text = await CreateFile(student, p, "", "notes.txt", device);
        Assert.True(await Acquire(student, part, device)); Assert.True(await Acquire(student, text, device));
        async Task<(Guid Version, bool Advanced)> Commit(Guid file, Guid? parent, short? release, Guid? op = null)
        {
            await using var row = await Cmd(student, "select * from armory_commit_version_with_release(@f,@p,'key',@h,3,@d,@o,@r)", ("f", file), ("p", (object?)parent ?? DBNull.Value), ("h", Hash), ("d", device), ("o", op ?? Guid.NewGuid()), ("r", (object?)release ?? DBNull.Value)).ExecuteReaderAsync();
            Assert.True(await row.ReadAsync()); return (row.GetGuid(0), row.GetBoolean(1));
        }
        var newer = await Refused(() => Commit(part, null, 2026), "22023");
        Assert.Contains("2026", newer.MessageText); Assert.Contains("2025", newer.MessageText);
        var op = Guid.NewGuid();
        var unchecked1 = await Commit(part, null, null, op); // warn mode: uploads, marked not checked
        Assert.True(unchecked1.Advanced);
        Assert.Equal(unchecked1, await Commit(part, null, null, op)); // receipt hit
        Assert.False((bool)(await Cmd(student, "select release_checked from armory_version_releases where version_id=@v", ("v", unchecked1.Version)).ExecuteScalarAsync())!);
        var notCad = await Commit(text, null, 2030); // the gate applies only to SolidWorks files
        Assert.Equal(0L, await Count(student, "select count(*) from armory_version_releases where version_id=@v", ("v", notCad.Version)));
        await using var lead = await db.Open("lead@example.com");
        await Refused(() => Cmd(lead, "select armory_set_release_gate(@p,'enforce',@o)", ("p", p), ("o", Guid.NewGuid())).ExecuteNonQueryAsync(), "42501");
        Assert.True((bool)(await Cmd(mentor, "select armory_set_release_gate(@p,'enforce',@o)", ("p", p), ("o", Guid.NewGuid())).ExecuteScalarAsync())!);
        await Refused(() => Cmd(mentor, "select armory_set_release_gate(@p,'off',@o)", ("p", p), ("o", Guid.NewGuid())).ExecuteNonQueryAsync(), "22023");
        await Refused(() => Commit(part, unchecked1.Version, null), "22023"); // enforce: unknown release refused
        var checkedVersion = await Commit(part, unchecked1.Version, 2025);
        Assert.True((bool)(await Cmd(student, "select release_checked from armory_version_releases where version_id=@v", ("v", checkedVersion.Version)).ExecuteScalarAsync())!);
        await Refused(() => Cmd(student, "select armory_save_side_version_with_release(@f,null,'key',@h,3,'conflict',@d,@o,2026::smallint)", ("f", part), ("h", Hash), ("d", device), ("o", Guid.NewGuid())).ExecuteNonQueryAsync(), "22023");
        var side = (Guid)(await Cmd(student, "select armory_save_side_version_with_release(@f,null,'key',@h,3,'conflict',@d,@o,2024::smallint)", ("f", part), ("h", Hash), ("d", device), ("o", Guid.NewGuid())).ExecuteScalarAsync())!;
        Assert.True((bool)(await Cmd(student, "select side from armory_version_releases where version_id=@v", ("v", side)).ExecuteScalarAsync())!);
        await Refused(() => Cmd(mentor, "select armory_raise_pinned_release(@p,2025::smallint,@o)", ("p", p), ("o", Guid.NewGuid())).ExecuteNonQueryAsync(), "22023");
        Assert.True((bool)(await Cmd(mentor, "select armory_raise_pinned_release(@p,2026::smallint,@o)", ("p", p), ("o", Guid.NewGuid())).ExecuteScalarAsync())!);
        Assert.True((await Commit(part, checkedVersion.Version, 2026)).Advanced);
        await Assert.ThrowsAsync<PostgresException>(() => Cmd(mentor, "update armory_version_releases set saved_release=2020").ExecuteNonQueryAsync());
        Assert.Equal(2L, await Count(mentor, "select count(*) from armory_list_changes(@p,0) where kind in ('release_gate_changed','pinned_release_raised')", ("p", p)));
    }

    [DatabaseFact]
    public async Task ACommitToARemovedFileIsKeptAsASideVersion()
    {
        var (p, mentor) = await Team(); await using var _ = mentor;
        await using var student = await db.Open("student@example.com"); var device = await Device(student);
        var f = await CreateFile(student, p, "", "Gear.SLDPRT", device);
        Assert.True(await Acquire(student, f, device));
        var v1 = (Guid)(await Cmd(student, "select version_id from armory_commit_version_with_release(@f,null,'key',@h,3,@d,@o,null)", ("f", f), ("h", Hash), ("d", device), ("o", Guid.NewGuid())).ExecuteScalarAsync())!;
        Assert.True((bool)(await Cmd(student, "select armory_tombstone(@f,@v,@d,@o)", ("f", f), ("v", v1), ("d", device), ("o", Guid.NewGuid())).ExecuteScalarAsync())!);
        // The holder still holds the lock and the parent is still current, yet nothing advances.
        var op = Guid.NewGuid();
        await using (var row = await Cmd(student, "select * from armory_commit_version_with_release(@f,@v,'key',@h,3,@d,@o,null)", ("f", f), ("v", v1), ("h", Hash), ("d", device), ("o", op)).ExecuteReaderAsync())
        {
            Assert.True(await row.ReadAsync()); Assert.False(row.GetBoolean(1));
        }
        Assert.Equal(1L, await Count(student, "select count(*) from armory_versions where file_id=@f", ("f", f)));
        Assert.Equal(1L, await Count(student, "select count(*) from armory_side_versions where file_id=@f and reason='file deleted'", ("f", f)));
        await Cmd(student, "select * from armory_commit_version_with_release(@f,@v,'key',@h,3,@d,@o,null)", ("f", f), ("v", v1), ("h", Hash), ("d", device), ("o", op)).ExecuteNonQueryAsync();
        Assert.Equal(1L, await Count(student, "select count(*) from armory_side_versions where file_id=@f", ("f", f))); // replay
    }

    [DatabaseFact]
    public async Task ReadSnapshotsAreMemberScoped()
    {
        var (p, mentor) = await Team(); await using var _ = mentor;
        await using var student = await db.Open("student@example.com"); var device = await Device(student, "Lab PC 12");
        var f = await CreateFile(student, p, "Drivetrain", "Plate.SLDPRT", device);
        Assert.True(await Acquire(student, f, device));
        await Cmd(student, "select * from armory_commit_version_with_release(@f,null,'key',@h,3,@d,@o,null)", ("f", f), ("h", Hash), ("d", device), ("o", Guid.NewGuid())).ExecuteNonQueryAsync();
        using var projects = JsonDocument.Parse((string)(await Cmd(student, "select armory_my_projects()::text").ExecuteScalarAsync())!);
        var mine = projects.RootElement.EnumerateArray().Single(x => x.GetProperty("id").GetGuid() == p);
        Assert.Equal("student", mine.GetProperty("role").GetString()); Assert.Equal("warn", mine.GetProperty("release_gate").GetString());
        using var files = JsonDocument.Parse((string)(await Cmd(student, "select armory_project_files(@p)::text", ("p", p)).ExecuteScalarAsync())!);
        var file = files.RootElement.EnumerateArray().Single();
        Assert.Equal("Drivetrain", file.GetProperty("folder").GetString());
        Assert.Equal(Hash, file.GetProperty("current").GetProperty("hash").GetString());
        Assert.False(file.GetProperty("current").GetProperty("release_checked").GetBoolean());
        Assert.Equal("Lab PC 12", file.GetProperty("lock").GetProperty("holder_device_name").GetString());
        using var history = JsonDocument.Parse((string)(await Cmd(student, "select armory_file_history(@f)::text", ("f", f)).ExecuteScalarAsync())!);
        Assert.Equal("version", history.RootElement.EnumerateArray().Single().GetProperty("kind").GetString());
        await using var stranger = await db.Open("stranger@example.com");
        await Refused(() => Cmd(stranger, "select armory_project_files(@p)", ("p", p)).ExecuteNonQueryAsync(), "42501");
        await Refused(() => Cmd(stranger, "select armory_file_history(@f)", ("f", f)).ExecuteNonQueryAsync(), "42501");
        Assert.DoesNotContain(p.ToString(), (string)(await Cmd(stranger, "select armory_my_projects()::text").ExecuteScalarAsync())!);
    }

    [DatabaseFact]
    public async Task EmptyAppIdentityIsRefusedLikeAMissingOne()
    {
        // idea-app's production current_user_email() returns '' when nobody is signed in.
        var production = await db.CreateProductionDatabase();
        try
        {
            await using var c = new NpgsqlConnection(production.ConnectionString); await c.OpenAsync();
            await Cmd(c, "create or replace function public.current_user_email() returns text language sql stable as $$ select ''::text $$").ExecuteNonQueryAsync();
            await Refused(() => Cmd(c, "select armory_register_device('x',@o)", ("o", Guid.NewGuid())).ExecuteNonQueryAsync(), "42501");
            await Refused(() => Cmd(c, "select armory_my_projects()").ExecuteNonQueryAsync(), "42501");
        }
        finally { await db.DropDatabase(production.Database); }
    }

    [Fact]
    public void ServerSqlNeverDefinesTheTestIdentityOrAdminStubs()
    {
        var root = AppContext.BaseDirectory;
        var production = string.Join('\n', Directory.GetFiles(Path.Combine(root, "server-sql"), "*.sql").Select(File.ReadAllText)).ToLowerInvariant();
        Assert.DoesNotContain("function public.is_admin", production);
        Assert.DoesNotContain("function public.current_user_email", production);
        Assert.DoesNotContain("armory.test_", production);
        var stubs = string.Join('\n', Directory.GetFiles(Path.Combine(root, "test-sql"), "*.sql").Select(File.ReadAllText)).ToLowerInvariant();
        Assert.Contains("function public.is_admin", stubs);
        Assert.Contains("function public.current_user_email", stubs);
    }

    private static NpgsqlCommand Cmd(NpgsqlConnection c, string sql, params (string, object)[] ps) { var x = new NpgsqlCommand(sql, c); foreach (var (n, v) in ps) x.Parameters.AddWithValue(n, v); return x; }
}
