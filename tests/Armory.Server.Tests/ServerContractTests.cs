using Npgsql;
namespace Armory.Server.Tests;

[Collection("db")]
public sealed class ServerContractTests(DatabaseFixture db)
{
    private const string Hash = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
    private async Task<(Guid Project, Guid File)> Seed()
    {
        await using var c = await db.Open("owner@example.com");
        var p = (Guid)(await Cmd(c, "insert into armory_projects(name,season) values('Robot',2026) returning id").ExecuteScalarAsync())!;
        await Cmd(c, "insert into armory_members values(@p,'owner@example.com','mentor'),(@p,'student@example.com','student')", ("p", p)).ExecuteNonQueryAsync();
        var f = (Guid)(await Cmd(c, "insert into armory_files(project_id,name) values(@p,'Part.SLDPRT') returning id", ("p", p)).ExecuteScalarAsync())!; return (p, f);
    }
    private static async Task<Guid> Device(NpgsqlConnection c, string name = "device") => (Guid)(await Cmd(c, "select armory_register_device(@n,@o)", ("n", name), ("o", Guid.NewGuid())).ExecuteScalarAsync())!;

    [DatabaseFact]
    public async Task RpcHappyPathsAndRefusals()
    {
        var (p, f) = await Seed(); await using var c = await db.Open("student@example.com"); var device = await Device(c);
        Assert.True((bool)(await Cmd(c, "select armory_acquire_lock(@f,@d,@o)", ("f", f), ("d", device), ("o", Guid.NewGuid())).ExecuteScalarAsync())!);
        Assert.True((bool)(await Cmd(c, "select armory_acquire_lock(@f,@d,@o)", ("f", f), ("d", device), ("o", Guid.NewGuid())).ExecuteScalarAsync())!); // idempotent for the holder
        var row = await Cmd(c, "select * from armory_commit_version(@f,null,'key',@h,3,@d,@o)", ("f", f), ("h", Hash), ("d", device), ("o", Guid.NewGuid())).ExecuteReaderAsync(); Assert.True(await row.ReadAsync()); Assert.True(row.GetBoolean(1)); var version = row.GetGuid(0); await row.DisposeAsync();
        Assert.False((bool)(await Cmd(c, "select armory_tombstone(@f,gen_random_uuid(),@d,@o)", ("f", f), ("d", device), ("o", Guid.NewGuid())).ExecuteScalarAsync())!);
        Assert.NotNull(await Cmd(c, "select * from armory_allocate_part_number(@p,1,2026,@o)", ("p", p), ("o", Guid.NewGuid())).ExecuteScalarAsync());
        Assert.True((long)(await Cmd(c, "select count(*) from armory_list_changes(@p,0)", ("p", p)).ExecuteScalarAsync())! >= 2);
        Assert.True((bool)(await Cmd(c, "select armory_release_lock(@f,@d,@o)", ("f", f), ("d", device), ("o", Guid.NewGuid())).ExecuteScalarAsync())!);
        await using var refused = await Cmd(c, "select * from armory_commit_version(@f,@v,'x',@h,1,@d,@o)", ("f", f), ("v", version), ("h", Hash), ("d", device), ("o", Guid.NewGuid())).ExecuteReaderAsync();
        Assert.True(await refused.ReadAsync());
        Assert.False(refused.GetBoolean(1));
        await refused.DisposeAsync();
        await Assert.ThrowsAsync<PostgresException>(async () => await Cmd(c, "select armory_break_lock(@f,@d,@o)", ("f", f), ("d", device), ("o", Guid.NewGuid())).ExecuteNonQueryAsync());
        await using var owner = await db.Open("owner@example.com");
        await Assert.ThrowsAsync<PostgresException>(() => Cmd(owner, "select armory_acquire_lock(@f,@d,@o)", ("f", f), ("d", device), ("o", Guid.NewGuid())).ExecuteNonQueryAsync());
    }
    [DatabaseFact]
    public async Task VersionRowsAreImmutable()
    {
        var (_, f) = await Seed(); await using var c = await db.Open("student@example.com"); var device = await Device(c); await Cmd(c, "select armory_acquire_lock(@f,@d,@o)", ("f", f), ("d", device), ("o", Guid.NewGuid())).ExecuteNonQueryAsync(); await Cmd(c, "select * from armory_commit_version(@f,null,'key',@h,1,@d,@o)", ("f", f), ("h", Hash), ("d", device), ("o", Guid.NewGuid())).ExecuteNonQueryAsync();
        await Assert.ThrowsAsync<PostgresException>(async () => await Cmd(c, "update armory_versions set byte_length=2").ExecuteNonQueryAsync()); await Assert.ThrowsAsync<PostgresException>(async () => await Cmd(c, "delete from armory_versions").ExecuteNonQueryAsync());
    }
    [DatabaseFact]
    public async Task ANewHolderCanAcquireAfterALockIsBroken()
    {
        var (_, f) = await Seed();
        await using (var student = await db.Open("student@example.com")) { var device = await Device(student); Assert.True((bool)(await Cmd(student, "select armory_acquire_lock(@f,@d,@o)", ("f", f), ("d", device), ("o", Guid.NewGuid())).ExecuteScalarAsync())!); }
        await using (var mentor = await db.Open("owner@example.com")) { var device = await Device(mentor); Assert.True((bool)(await Cmd(mentor, "select armory_break_lock(@f,@d,@o)", ("f", f), ("d", device), ("o", Guid.NewGuid())).ExecuteScalarAsync())!); }
        await using var replacement = await db.Open("owner@example.com"); var replacementDevice = await Device(replacement);
        Assert.True((bool)(await Cmd(replacement, "select armory_acquire_lock(@f,@d,@o)", ("f", f), ("d", replacementDevice), ("o", Guid.NewGuid())).ExecuteScalarAsync())!);
        Assert.Equal("owner@example.com", (string)(await Cmd(replacement, "select holder_email from armory_locks where file_id=@f and broken_at is null", ("f", f)).ExecuteScalarAsync())!);
    }
    [DatabaseFact]
    public async Task RlsIsolatesProjectsAndAnonCannotUseRpcs()
    {
        var (p, _) = await Seed(); await using var owner = await db.Open("owner@example.com");
        var other = (Guid)(await Cmd(owner, "insert into armory_projects(name,season) values('Other',2026) returning id").ExecuteScalarAsync())!;
        await Cmd(owner, "insert into armory_members values(@p,'other@example.com','student')", ("p", other)).ExecuteNonQueryAsync();
        await using var member = await db.Open("student@example.com"); await Cmd(member, "set role authenticated").ExecuteNonQueryAsync();
        Assert.Equal(1L, (long)(await Cmd(member, "select count(*) from armory_projects").ExecuteScalarAsync())!);
        await Cmd(member, "reset role").ExecuteNonQueryAsync(); await Cmd(member, "set role anon").ExecuteNonQueryAsync();
        await Assert.ThrowsAsync<PostgresException>(async () => await Cmd(member, "select armory_list_changes(@p,0)", ("p", p)).ExecuteNonQueryAsync());
    }
    [DatabaseFact]
    public async Task ExactlyOneOfTwoLockRacersWinsForOneThousandIterations()
    {
        var (_, f) = await Seed(); await using var setupStudent = await db.Open("student@example.com"); var studentDevice = await Device(setupStudent); await using var setupOwner = await db.Open("owner@example.com"); var ownerDevice = await Device(setupOwner);
        for (var i = 0; i < 1000; i++) { await Cmd(setupOwner, "delete from armory_locks where file_id=@f", ("f", f)).ExecuteNonQueryAsync(); await using var a = await db.Open("student@example.com"); await using var b = await db.Open("owner@example.com"); var x = Cmd(a, "select armory_acquire_lock(@f,@d,@o)", ("f", f), ("d", studentDevice), ("o", Guid.NewGuid())).ExecuteScalarAsync(); var y = Cmd(b, "select armory_acquire_lock(@f,@d,@o)", ("f", f), ("d", ownerDevice), ("o", Guid.NewGuid())).ExecuteScalarAsync(); var results = await Task.WhenAll(x, y); Assert.NotEqual((bool)results[0]!, (bool)results[1]!); }
    }
    [DatabaseFact]
    public async Task SameParentCommitRaceAdvancesOnceAndPreservesOther()
    {
        var (_, f) = await Seed(); await using var setup = await db.Open("student@example.com"); var device = await Device(setup); await Cmd(setup, "select armory_acquire_lock(@f,@d,@o)", ("f", f), ("d", device), ("o", Guid.NewGuid())).ExecuteNonQueryAsync();
        for (var i = 0; i < 1000; i++) { await using var a = await db.Open("student@example.com"); await using var b = await db.Open("student@example.com"); var parent = await Cmd(a, "select current_version_id from armory_files where id=@f", ("f", f)).ExecuteScalarAsync(); var observedParent = parent is DBNull ? null : parent; var x = Cmd(a, "select advanced from armory_commit_version(@f,@p,'a',@h,1,@d,@o)", ("f", f), ("p", observedParent ?? DBNull.Value), ("h", Hash), ("d", device), ("o", Guid.NewGuid())).ExecuteScalarAsync(); var y = Cmd(b, "select advanced from armory_commit_version(@f,@p,'b',@h,1,@d,@o)", ("f", f), ("p", observedParent ?? DBNull.Value), ("h", Hash), ("d", device), ("o", Guid.NewGuid())).ExecuteScalarAsync(); var results = await Task.WhenAll(x, y); Assert.Equal(1, results.Count(v => (bool)v!)); }
    }
    [DatabaseFact]
    public async Task GrantsAreLimitedToRlsProtectedArmoryTables()
    {
        await using var connection = await db.Open("owner@example.com");
        Assert.False((bool)(await Cmd(connection, "select has_table_privilege('authenticated','public.coin_ledger','select')").ExecuteScalarAsync())!);
        var tables = new[] { "projects", "members", "devices", "files", "versions", "side_versions", "locks", "tombstones", "part_number_allocations", "change_feed", "operation_receipts" };
        foreach (var table in tables) { var name = "public.armory_" + table; Assert.True((bool)(await Cmd(connection, "select has_table_privilege('authenticated',@table,'select')", ("table", name)).ExecuteScalarAsync())!); Assert.False((bool)(await Cmd(connection, "select has_table_privilege('anon',@table,'select')", ("table", name)).ExecuteScalarAsync())!); Assert.True((bool)(await Cmd(connection, "select relrowsecurity from pg_class where oid=@table::regclass", ("table", name)).ExecuteScalarAsync())!); }
    }
    [DatabaseFact]
    public async Task ProductionSqlIgnoresTestIdentityAndEveryRpcRefusesWithoutAppIdentity()
    {
        var production = await db.CreateProductionDatabase();
        try
        {
            await using var connection = new NpgsqlConnection(production.ConnectionString); await connection.OpenAsync();
            await Cmd(connection, "select set_config('armory.test_email','attacker@example.com',false)").ExecuteNonQueryAsync();
            var id = Guid.NewGuid(); var device = Guid.NewGuid(); var operation = Guid.NewGuid();
            var calls = new[]{
                "select armory_register_device('attacker',@operation)",
                "select armory_acquire_lock(@id,@device,@operation)",
                "select armory_release_lock(@id,@device,@operation)",
                "select armory_break_lock(@id,@device,@operation)",
                "select armory_save_side_version(@id,null,'key',@hash,1,'conflict',@device,@operation)",
                "select * from armory_commit_version(@id,null,'key',@hash,1,@device,@operation)",
                "select armory_tombstone(@id,null,@device,@operation)",
                "select * from armory_allocate_part_number(@id,1,2026,@operation)",
                "select * from armory_list_changes(@id,0)",
                "select armory_create_project('Robot',2027::smallint,@operation)",
                "select armory_add_member(@id,'student@example.com','student',@operation)",
                "select armory_remove_member(@id,'student@example.com',@operation)",
                "select armory_create_file(@id,'','Part.SLDPRT',@device,@operation)",
                "select armory_move_file(@id,'','Part.SLDPRT',@device,@operation)",
                "select * from armory_commit_version_with_release(@id,null,'key',@hash,1,@device,@operation,null)",
                "select armory_save_side_version_with_release(@id,null,'key',@hash,1,'conflict',@device,@operation,null)",
                "select armory_set_release_gate(@id,'enforce',@operation)",
                "select armory_raise_pinned_release(@id,2026::smallint,@operation)",
                "select armory_my_projects()",
                "select armory_project_files(@id)",
                "select armory_file_history(@id)"};
            foreach (var call in calls) await Assert.ThrowsAsync<PostgresException>(async () => await Cmd(connection, call, ("id", id), ("device", device), ("operation", operation), ("hash", Hash)).ExecuteNonQueryAsync());
        }
        finally { await db.DropDatabase(production.Database); }
    }
    [DatabaseFact]
    public async Task SamePersonOnTwoDevicesCannotShareLockAndOtherDeviceCommitBecomesSideVersion()
    {
        var (_, f) = await Seed(); await using var c = await db.Open("student@example.com"); var a = await Device(c, "school PC"); var b = await Device(c, "laptop");
        Assert.True((bool)(await Cmd(c, "select armory_acquire_lock(@f,@d,@o)", ("f", f), ("d", a), ("o", Guid.NewGuid())).ExecuteScalarAsync())!);
        Assert.False((bool)(await Cmd(c, "select armory_acquire_lock(@f,@d,@o)", ("f", f), ("d", b), ("o", Guid.NewGuid())).ExecuteScalarAsync())!);
        await using var row = await Cmd(c, "select * from armory_commit_version(@f,null,'key',@h,3,@d,@o)", ("f", f), ("h", Hash), ("d", b), ("o", Guid.NewGuid())).ExecuteReaderAsync();
        Assert.True(await row.ReadAsync()); Assert.False(row.GetBoolean(1)); var side = row.GetGuid(0); await row.DisposeAsync();
        Assert.Equal(side, (Guid)(await Cmd(c, "select id from armory_side_versions where file_id=@f", ("f", f)).ExecuteScalarAsync())!);
        Assert.Equal(DBNull.Value, await Cmd(c, "select current_version_id from armory_files where id=@f", ("f", f)).ExecuteScalarAsync());
    }
    [DatabaseFact]
    public async Task BreakNoticeNamesTheExactFormerDevice()
    {
        var (p, f) = await Seed(); await using var student = await db.Open("student@example.com"); var device = await Device(student);
        await Cmd(student, "select armory_acquire_lock(@f,@d,@o)", ("f", f), ("d", device), ("o", Guid.NewGuid())).ExecuteNonQueryAsync();
        await using var owner = await db.Open("owner@example.com"); var breaker = await Device(owner);
        Assert.True((bool)(await Cmd(owner, "select armory_break_lock(@f,@d,@o)", ("f", f), ("d", breaker), ("o", Guid.NewGuid())).ExecuteScalarAsync())!);
        Assert.Equal(device, (Guid)(await Cmd(owner, "select broken_holder_device_id from armory_locks where file_id=@f", ("f", f)).ExecuteScalarAsync())!);
        Assert.Equal(device, (Guid)(await Cmd(owner, "select (payload->>'former_device_id')::uuid from armory_list_changes(@p,0) where kind='lock_broken'", ("p", p)).ExecuteScalarAsync())!);
    }
    [DatabaseFact]
    public async Task DeviceIdentityCannotBeStolen()
    {
        var (_, f) = await Seed(); await using var student = await db.Open("student@example.com"); var stolen = await Device(student); await using var owner = await db.Open("owner@example.com");
        await Assert.ThrowsAsync<PostgresException>(() => Cmd(owner, "select armory_acquire_lock(@f,@d,@o)", ("f", f), ("d", stolen), ("o", Guid.NewGuid())).ExecuteNonQueryAsync());
    }
    [DatabaseFact]
    public async Task EveryWriteRpcReturnsItsOriginalResultOnReplay()
    {
        var (p, f) = await Seed(); await using var c = await db.Open("student@example.com");
        var registerOp = Guid.NewGuid(); var d = (Guid)(await Cmd(c, "select armory_register_device('laptop',@o)", ("o", registerOp)).ExecuteScalarAsync())!; Assert.Equal(d, (Guid)(await Cmd(c, "select armory_register_device('changed',@o)", ("o", registerOp)).ExecuteScalarAsync())!);
        var acquire = Guid.NewGuid(); Assert.Equal(await Scalar(c, "select armory_acquire_lock(@f,@d,@o)", f, d, acquire), await Scalar(c, "select armory_acquire_lock(@f,@d,@o)", f, d, acquire));
        var commit = Guid.NewGuid(); var v1 = (Guid)(await Cmd(c, "select version_id from armory_commit_version(@f,null,'key',@h,1,@d,@o)", ("f", f), ("h", Hash), ("d", d), ("o", commit)).ExecuteScalarAsync())!; var v2 = (Guid)(await Cmd(c, "select version_id from armory_commit_version(@f,null,'different',@h,1,@d,@o)", ("f", f), ("h", Hash), ("d", d), ("o", commit)).ExecuteScalarAsync())!; Assert.Equal(v1, v2);
        var side = Guid.NewGuid(); var s1 = (Guid)(await Cmd(c, "select armory_save_side_version(@f,@v,'key',@h,1,'x',@d,@o)", ("f", f), ("v", v1), ("h", Hash), ("d", d), ("o", side)).ExecuteScalarAsync())!; var s2 = (Guid)(await Cmd(c, "select armory_save_side_version(@f,@v,'key',@h,1,'x',@d,@o)", ("f", f), ("v", v1), ("h", Hash), ("d", d), ("o", side)).ExecuteScalarAsync())!; Assert.Equal(s1, s2);
        var tomb = Guid.NewGuid(); Assert.Equal(await Scalar(c, "select armory_tombstone(@f,@v,@d,@o)", f, d, tomb, v1), await Scalar(c, "select armory_tombstone(@f,@v,@d,@o)", f, d, tomb, v1));
        var release = Guid.NewGuid(); Assert.Equal(await Scalar(c, "select armory_release_lock(@f,@d,@o)", f, d, release), await Scalar(c, "select armory_release_lock(@f,@d,@o)", f, d, release));
        var allocation = Guid.NewGuid(); var n1 = await Cmd(c, "select part_number from armory_allocate_part_number(@p,1,2026,@o)", ("p", p), ("o", allocation)).ExecuteScalarAsync(); var n2 = await Cmd(c, "select part_number from armory_allocate_part_number(@p,1,2026,@o)", ("p", p), ("o", allocation)).ExecuteScalarAsync(); Assert.Equal(n1, n2);
        await using var owner = await db.Open("owner@example.com"); var od = await Device(owner); var ao = Guid.NewGuid(); await Cmd(owner, "select armory_acquire_lock(@f,@d,@o)", ("f", f), ("d", od), ("o", ao)).ExecuteNonQueryAsync(); var breakOp = Guid.NewGuid(); Assert.Equal(await Scalar(owner, "select armory_break_lock(@f,@d,@o)", f, od, breakOp), await Scalar(owner, "select armory_break_lock(@f,@d,@o)", f, od, breakOp));
    }
    [DatabaseFact]
    public async Task OperationIdReuseAcrossCallerOrRpcIsRefused()
    {
        var (_, f) = await Seed(); var op = Guid.NewGuid(); await using var student = await db.Open("student@example.com"); var sd = await Device(student); await Cmd(student, "select armory_acquire_lock(@f,@d,@o)", ("f", f), ("d", sd), ("o", op)).ExecuteNonQueryAsync();
        await Assert.ThrowsAsync<PostgresException>(() => Cmd(student, "select armory_release_lock(@f,@d,@o)", ("f", f), ("d", sd), ("o", op)).ExecuteNonQueryAsync());
        await using var owner = await db.Open("owner@example.com"); var od = await Device(owner); await Assert.ThrowsAsync<PostgresException>(() => Cmd(owner, "select armory_acquire_lock(@f,@d,@o)", ("f", f), ("d", od), ("o", op)).ExecuteNonQueryAsync());
    }
    [DatabaseFact]
    public async Task SamePersonDeviceLockRaceHasExactlyOneWinnerForOneThousandIterations()
    {
        var (_, f) = await Seed(); await using var setup = await db.Open("student@example.com"); var a = await Device(setup, "a"); var b = await Device(setup, "b");
        for (var i = 0; i < 1000; i++) { await Cmd(setup, "delete from armory_locks where file_id=@f", ("f", f)).ExecuteNonQueryAsync(); await using var x = await db.Open("student@example.com"); await using var y = await db.Open("student@example.com"); var one = Scalar(x, "select armory_acquire_lock(@f,@d,@o)", f, a, Guid.NewGuid()); var two = Scalar(y, "select armory_acquire_lock(@f,@d,@o)", f, b, Guid.NewGuid()); var results = await Task.WhenAll(one, two); Assert.NotEqual(results[0], results[1]); }
    }
    [DatabaseFact]
    public async Task SimultaneousReplayWritesExactlyOnceForOneThousandIterations()
    {
        var (_, f) = await Seed(); await using var setup = await db.Open("student@example.com"); var d = await Device(setup);
        for (var i = 0; i < 1000; i++) { var op = Guid.NewGuid(); await using var a = await db.Open("student@example.com"); await using var b = await db.Open("student@example.com"); var x = Cmd(a, "select armory_save_side_version(@f,null,'key',@h,1,'race',@d,@o)", ("f", f), ("h", Hash), ("d", d), ("o", op)).ExecuteScalarAsync(); var y = Cmd(b, "select armory_save_side_version(@f,null,'key',@h,1,'race',@d,@o)", ("f", f), ("h", Hash), ("d", d), ("o", op)).ExecuteScalarAsync(); var ids = await Task.WhenAll(x, y); Assert.Equal(ids[0], ids[1]); }
        Assert.Equal(1000L, (long)(await Cmd(setup, "select count(*) from armory_side_versions where file_id=@f", ("f", f)).ExecuteScalarAsync())!);
    }
    [DatabaseFact]
    public async Task ReceiptAndDeviceTablesHaveRlsReadOnlyGrants()
    {
        await using var c = await db.Open("owner@example.com"); foreach (var table in new[] { "armory_devices", "armory_operation_receipts" }) { Assert.True((bool)(await Cmd(c, "select has_table_privilege('authenticated',@t,'select')", ("t", table)).ExecuteScalarAsync())!); Assert.False((bool)(await Cmd(c, "select has_table_privilege('authenticated',@t,'insert')", ("t", table)).ExecuteScalarAsync())!); Assert.True((bool)(await Cmd(c, "select relrowsecurity from pg_class where oid=@t::regclass", ("t", table)).ExecuteScalarAsync())!); }
    }
    private static async Task<bool> Scalar(NpgsqlConnection c, string sql, Guid f, Guid d, Guid o, Guid? v = null) => (bool)(await Cmd(c, sql, ("f", f), ("d", d), ("o", o), ("v", v ?? (object)DBNull.Value)).ExecuteScalarAsync())!;
    private static NpgsqlCommand Cmd(NpgsqlConnection c, string sql, params (string, object)[] ps) { var x = new NpgsqlCommand(sql, c); foreach (var (n, v) in ps) x.Parameters.AddWithValue(n, v); return x; }
}
