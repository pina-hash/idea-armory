using Npgsql;
namespace Armory.Server.Tests;

[Collection("db")]
public sealed class ServerContractTests(DatabaseFixture db)
{
    private const string Hash="aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
    private async Task<(Guid Project,Guid File)> Seed()
    {
        await using var c=await db.Open("owner@example.com");
        var p=(Guid)(await Cmd(c,"insert into armory_projects(name,season) values('Robot',2026) returning id").ExecuteScalarAsync())!;
        await Cmd(c,"insert into armory_members values(@p,'owner@example.com','mentor'),(@p,'student@example.com','student')",("p",p)).ExecuteNonQueryAsync();
        var f=(Guid)(await Cmd(c,"insert into armory_files(project_id,name) values(@p,'Part.SLDPRT') returning id",("p",p)).ExecuteScalarAsync())!;return(p,f);
    }
    private static async Task<Guid> Device(NpgsqlConnection c,string name="device") => (Guid)(await Cmd(c,"select armory_register_device(@n,@o)",("n",name),("o",Guid.NewGuid())).ExecuteScalarAsync())!;

    [DatabaseFact] public async Task SamePersonOnTwoDevicesCannotShareLockAndOtherDeviceCommitBecomesSideVersion()
    {
        var(_,f)=await Seed(); await using var c=await db.Open("student@example.com"); var a=await Device(c,"school PC"); var b=await Device(c,"laptop");
        Assert.True((bool)(await Cmd(c,"select armory_acquire_lock(@f,@d,@o)",("f",f),("d",a),("o",Guid.NewGuid())).ExecuteScalarAsync())!);
        Assert.False((bool)(await Cmd(c,"select armory_acquire_lock(@f,@d,@o)",("f",f),("d",b),("o",Guid.NewGuid())).ExecuteScalarAsync())!);
        await using var row=await Cmd(c,"select * from armory_commit_version(@f,null,'key',@h,3,@d,@o)",("f",f),("h",Hash),("d",b),("o",Guid.NewGuid())).ExecuteReaderAsync();
        Assert.True(await row.ReadAsync()); Assert.False(row.GetBoolean(1)); var side=row.GetGuid(0); await row.DisposeAsync();
        Assert.Equal(side,(Guid)(await Cmd(c,"select id from armory_side_versions where file_id=@f",("f",f)).ExecuteScalarAsync())!);
        Assert.Equal(DBNull.Value,await Cmd(c,"select current_version_id from armory_files where id=@f",("f",f)).ExecuteScalarAsync());
    }
    [DatabaseFact] public async Task BreakNoticeNamesTheExactFormerDevice()
    {
        var(p,f)=await Seed(); await using var student=await db.Open("student@example.com"); var device=await Device(student);
        await Cmd(student,"select armory_acquire_lock(@f,@d,@o)",("f",f),("d",device),("o",Guid.NewGuid())).ExecuteNonQueryAsync();
        await using var owner=await db.Open("owner@example.com"); var breaker=await Device(owner);
        Assert.True((bool)(await Cmd(owner,"select armory_break_lock(@f,@d,@o)",("f",f),("d",breaker),("o",Guid.NewGuid())).ExecuteScalarAsync())!);
        Assert.Equal(device,(Guid)(await Cmd(owner,"select broken_holder_device_id from armory_locks where file_id=@f",("f",f)).ExecuteScalarAsync())!);
        Assert.Equal(device,(Guid)(await Cmd(owner,"select (payload->>'former_device_id')::uuid from armory_list_changes(@p,0) where kind='lock_broken'",("p",p)).ExecuteScalarAsync())!);
    }
    [DatabaseFact] public async Task DeviceIdentityCannotBeStolen()
    {
        var(_,f)=await Seed(); await using var student=await db.Open("student@example.com"); var stolen=await Device(student); await using var owner=await db.Open("owner@example.com");
        await Assert.ThrowsAsync<PostgresException>(()=>Cmd(owner,"select armory_acquire_lock(@f,@d,@o)",("f",f),("d",stolen),("o",Guid.NewGuid())).ExecuteNonQueryAsync());
    }
    [DatabaseFact] public async Task EveryWriteRpcReturnsItsOriginalResultOnReplay()
    {
        var(p,f)=await Seed(); await using var c=await db.Open("student@example.com");
        var registerOp=Guid.NewGuid(); var d=(Guid)(await Cmd(c,"select armory_register_device('laptop',@o)",("o",registerOp)).ExecuteScalarAsync())!; Assert.Equal(d,(Guid)(await Cmd(c,"select armory_register_device('changed',@o)",("o",registerOp)).ExecuteScalarAsync())!);
        var acquire=Guid.NewGuid(); Assert.Equal(await Scalar(c,"select armory_acquire_lock(@f,@d,@o)",f,d,acquire),await Scalar(c,"select armory_acquire_lock(@f,@d,@o)",f,d,acquire));
        var commit=Guid.NewGuid(); var v1=(Guid)(await Cmd(c,"select version_id from armory_commit_version(@f,null,'key',@h,1,@d,@o)",("f",f),("h",Hash),("d",d),("o",commit)).ExecuteScalarAsync())!; var v2=(Guid)(await Cmd(c,"select version_id from armory_commit_version(@f,null,'different',@h,1,@d,@o)",("f",f),("h",Hash),("d",d),("o",commit)).ExecuteScalarAsync())!; Assert.Equal(v1,v2);
        var side=Guid.NewGuid(); var s1=(Guid)(await Cmd(c,"select armory_save_side_version(@f,@v,'key',@h,1,'x',@d,@o)",("f",f),("v",v1),("h",Hash),("d",d),("o",side)).ExecuteScalarAsync())!; var s2=(Guid)(await Cmd(c,"select armory_save_side_version(@f,@v,'key',@h,1,'x',@d,@o)",("f",f),("v",v1),("h",Hash),("d",d),("o",side)).ExecuteScalarAsync())!; Assert.Equal(s1,s2);
        var tomb=Guid.NewGuid(); Assert.Equal(await Scalar(c,"select armory_tombstone(@f,@v,@d,@o)",f,d,tomb,v1),await Scalar(c,"select armory_tombstone(@f,@v,@d,@o)",f,d,tomb,v1));
        var release=Guid.NewGuid(); Assert.Equal(await Scalar(c,"select armory_release_lock(@f,@d,@o)",f,d,release),await Scalar(c,"select armory_release_lock(@f,@d,@o)",f,d,release));
        var allocation=Guid.NewGuid(); var n1=await Cmd(c,"select part_number from armory_allocate_part_number(@p,1,2026,@o)",("p",p),("o",allocation)).ExecuteScalarAsync(); var n2=await Cmd(c,"select part_number from armory_allocate_part_number(@p,1,2026,@o)",("p",p),("o",allocation)).ExecuteScalarAsync(); Assert.Equal(n1,n2);
        await using var owner=await db.Open("owner@example.com"); var od=await Device(owner); var ao=Guid.NewGuid(); await Cmd(owner,"select armory_acquire_lock(@f,@d,@o)",("f",f),("d",od),("o",ao)).ExecuteNonQueryAsync(); var breakOp=Guid.NewGuid(); Assert.Equal(await Scalar(owner,"select armory_break_lock(@f,@d,@o)",f,od,breakOp),await Scalar(owner,"select armory_break_lock(@f,@d,@o)",f,od,breakOp));
    }
    [DatabaseFact] public async Task OperationIdReuseAcrossCallerOrRpcIsRefused()
    {
        var(_,f)=await Seed(); var op=Guid.NewGuid(); await using var student=await db.Open("student@example.com"); var sd=await Device(student); await Cmd(student,"select armory_acquire_lock(@f,@d,@o)",("f",f),("d",sd),("o",op)).ExecuteNonQueryAsync();
        await Assert.ThrowsAsync<PostgresException>(()=>Cmd(student,"select armory_release_lock(@f,@d,@o)",("f",f),("d",sd),("o",op)).ExecuteNonQueryAsync());
        await using var owner=await db.Open("owner@example.com"); var od=await Device(owner); await Assert.ThrowsAsync<PostgresException>(()=>Cmd(owner,"select armory_acquire_lock(@f,@d,@o)",("f",f),("d",od),("o",op)).ExecuteNonQueryAsync());
    }
    [DatabaseFact] public async Task SamePersonDeviceLockRaceHasExactlyOneWinnerForOneThousandIterations()
    {
        var(_,f)=await Seed(); await using var setup=await db.Open("student@example.com"); var a=await Device(setup,"a"); var b=await Device(setup,"b");
        for(var i=0;i<1000;i++){await Cmd(setup,"delete from armory_locks where file_id=@f",("f",f)).ExecuteNonQueryAsync();await using var x=await db.Open("student@example.com");await using var y=await db.Open("student@example.com");var one=Scalar(x,"select armory_acquire_lock(@f,@d,@o)",f,a,Guid.NewGuid());var two=Scalar(y,"select armory_acquire_lock(@f,@d,@o)",f,b,Guid.NewGuid());var results=await Task.WhenAll(one,two);Assert.NotEqual(results[0],results[1]);}
    }
    [DatabaseFact] public async Task SimultaneousReplayWritesExactlyOnceForOneThousandIterations()
    {
        var(_,f)=await Seed(); await using var setup=await db.Open("student@example.com"); var d=await Device(setup);
        for(var i=0;i<1000;i++){var op=Guid.NewGuid();await using var a=await db.Open("student@example.com");await using var b=await db.Open("student@example.com");var x=Cmd(a,"select armory_save_side_version(@f,null,'key',@h,1,'race',@d,@o)",("f",f),("h",Hash),("d",d),("o",op)).ExecuteScalarAsync();var y=Cmd(b,"select armory_save_side_version(@f,null,'key',@h,1,'race',@d,@o)",("f",f),("h",Hash),("d",d),("o",op)).ExecuteScalarAsync();var ids=await Task.WhenAll(x,y);Assert.Equal(ids[0],ids[1]);}
        Assert.Equal(1000L,(long)(await Cmd(setup,"select count(*) from armory_side_versions where file_id=@f",("f",f)).ExecuteScalarAsync())!);
    }
    [DatabaseFact] public async Task ReceiptAndDeviceTablesHaveRlsReadOnlyGrants()
    {
        await using var c=await db.Open("owner@example.com"); foreach(var table in new[]{"armory_devices","armory_operation_receipts"}){Assert.True((bool)(await Cmd(c,"select has_table_privilege('authenticated',@t,'select')",("t",table)).ExecuteScalarAsync())!);Assert.False((bool)(await Cmd(c,"select has_table_privilege('authenticated',@t,'insert')",("t",table)).ExecuteScalarAsync())!);Assert.True((bool)(await Cmd(c,"select relrowsecurity from pg_class where oid=@t::regclass",("t",table)).ExecuteScalarAsync())!);}
    }
    private static async Task<bool> Scalar(NpgsqlConnection c,string sql,Guid f,Guid d,Guid o,Guid? v=null)=>(bool)(await Cmd(c,sql,("f",f),("d",d),("o",o),("v",v??(object)DBNull.Value)).ExecuteScalarAsync())!;
    private static NpgsqlCommand Cmd(NpgsqlConnection c,string sql,params(string,object)[] ps){var x=new NpgsqlCommand(sql,c);foreach(var(n,v)in ps)x.Parameters.AddWithValue(n,v);return x;}
}
