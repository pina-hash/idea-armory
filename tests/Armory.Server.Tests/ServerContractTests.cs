using Npgsql;
namespace Armory.Server.Tests;

[Collection("db")]
public sealed class ServerContractTests(DatabaseFixture db)
{
    private const string Hash="aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
    private async Task<(Guid Project,Guid File)> Seed()
    {
        await using var c=await db.Open("owner@example.com");
        var p=(Guid)(await new NpgsqlCommand("insert into armory_projects(name,season) values('Robot',2026) returning id",c).ExecuteScalarAsync())!;
        await new NpgsqlCommand("insert into armory_members values(@p,'owner@example.com','mentor'),(@p,'student@example.com','student')",c){Parameters={new("p",p)}}.ExecuteNonQueryAsync();
        var f=(Guid)(await new NpgsqlCommand("insert into armory_files(project_id,name) values(@p,'Part.SLDPRT') returning id",c){Parameters={new("p",p)}}.ExecuteScalarAsync())!;return(p,f);
    }
    [Fact] public async Task RpcHappyPathsAndRefusals()
    {
        var(p,f)=await Seed();await using var c=await db.Open("student@example.com");
        Assert.True((bool)(await Cmd(c,"select armory_acquire_lock(@f)",("f",f)).ExecuteScalarAsync())!);
        Assert.True((bool)(await Cmd(c,"select armory_acquire_lock(@f)",("f",f)).ExecuteScalarAsync())!); // idempotent for the holder
        var row=await Cmd(c,"select * from armory_commit_version(@f,null,'key',@h,3)",("f",f),("h",Hash)).ExecuteReaderAsync();Assert.True(await row.ReadAsync());Assert.True(row.GetBoolean(1));var version=row.GetGuid(0);await row.DisposeAsync();
        Assert.False((bool)(await Cmd(c,"select armory_tombstone(@f,gen_random_uuid())",("f",f)).ExecuteScalarAsync())!);
        Assert.NotNull(await Cmd(c,"select * from armory_allocate_part_number(@p,1,2026)",("p",p)).ExecuteScalarAsync());
        Assert.True((long)(await Cmd(c,"select count(*) from armory_list_changes(@p,0)",("p",p)).ExecuteScalarAsync())!>=2);
        Assert.True((bool)(await Cmd(c,"select armory_release_lock(@f)",("f",f)).ExecuteScalarAsync())!);
        await Assert.ThrowsAsync<PostgresException>(async()=>await Cmd(c,"select * from armory_commit_version(@f,@v,'x',@h,1)",("f",f),("v",version),("h",Hash)).ExecuteNonQueryAsync());
        await Assert.ThrowsAsync<PostgresException>(async()=>await Cmd(c,"select armory_break_lock(@f)",("f",f)).ExecuteNonQueryAsync());
    }
    [Fact] public async Task VersionRowsAreImmutable()
    {
        var(_,f)=await Seed();await using var c=await db.Open("student@example.com");await Cmd(c,"select armory_acquire_lock(@f)",("f",f)).ExecuteNonQueryAsync();await Cmd(c,"select * from armory_commit_version(@f,null,'key',@h,1)",("f",f),("h",Hash)).ExecuteNonQueryAsync();
        await Assert.ThrowsAsync<PostgresException>(async()=>await Cmd(c,"update armory_versions set byte_length=2").ExecuteNonQueryAsync());await Assert.ThrowsAsync<PostgresException>(async()=>await Cmd(c,"delete from armory_versions").ExecuteNonQueryAsync());
    }
    [Fact] public async Task RlsIsolatesProjectsAndAnonCannotUseRpcs()
    {
        var(p,_)=await Seed();await using var owner=await db.Open("owner@example.com");
        var other=(Guid)(await Cmd(owner,"insert into armory_projects(name,season) values('Other',2026) returning id").ExecuteScalarAsync())!;
        await Cmd(owner,"insert into armory_members values(@p,'other@example.com','student')",("p",other)).ExecuteNonQueryAsync();
        await using var member=await db.Open("student@example.com");await Cmd(member,"set role authenticated").ExecuteNonQueryAsync();
        Assert.Equal(1L,(long)(await Cmd(member,"select count(*) from armory_projects").ExecuteScalarAsync())!);
        await Cmd(member,"reset role").ExecuteNonQueryAsync();await Cmd(member,"set role anon").ExecuteNonQueryAsync();
        await Assert.ThrowsAsync<PostgresException>(async()=>await Cmd(member,"select armory_list_changes(@p,0)",("p",p)).ExecuteNonQueryAsync());
    }
    [Fact] public async Task ExactlyOneOfTwoLockRacersWinsForOneThousandIterations()
    {
        var(_,f)=await Seed();for(var i=0;i<1000;i++){await using(var reset=await db.Open("owner@example.com"))await Cmd(reset,"delete from armory_locks where file_id=@f",("f",f)).ExecuteNonQueryAsync();await using var a=await db.Open("student@example.com");await using var b=await db.Open("owner@example.com");var x=Cmd(a,"select armory_acquire_lock(@f)",("f",f)).ExecuteScalarAsync();var y=Cmd(b,"select armory_acquire_lock(@f)",("f",f)).ExecuteScalarAsync();var results=await Task.WhenAll(x,y);Assert.NotEqual((bool)results[0]!,(bool)results[1]!);}
    }
    [Fact] public async Task SameParentCommitRaceAdvancesOnceAndPreservesOther()
    {
        var(_,f)=await Seed();
        // Two agent connections for the same holder race from the same observed parent.
        await using(var setup=await db.Open("student@example.com"))await Cmd(setup,"insert into armory_locks(file_id,holder_email) values(@f,'student@example.com')",("f",f)).ExecuteNonQueryAsync();for(var i=0;i<1000;i++){await using var a=await db.Open("student@example.com");await using var b=await db.Open("student@example.com");var parent=await Cmd(a,"select current_version_id from armory_files where id=@f",("f",f)).ExecuteScalarAsync();var p=parent is DBNull?null:parent;var x=Cmd(a,"select advanced from armory_commit_version(@f,@p,'a',@h,1)",("f",f),("p",p??DBNull.Value),("h",Hash)).ExecuteScalarAsync();var y=Cmd(b,"select advanced from armory_commit_version(@f,@p,'b',@h,1)",("f",f),("p",p??DBNull.Value),("h",Hash)).ExecuteScalarAsync();var results=await Task.WhenAll(x,y);Assert.Equal(1,results.Count(v=>(bool)v!));}
    }
    private static NpgsqlCommand Cmd(NpgsqlConnection c,string sql,params(string,object)[] ps){var x=new NpgsqlCommand(sql,c);foreach(var(n,v)in ps)x.Parameters.AddWithValue(n,v);return x;}
}
