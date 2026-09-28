using Xunit.Sdk;
using Npgsql;

namespace Armory.Server.Tests;

public sealed class DatabaseFixture : IAsyncLifetime
{
    public string? ConnectionString { get; private set; }
    public bool Available => ConnectionString is not null;
    public async Task InitializeAsync()
    {
        var root=Environment.GetEnvironmentVariable("ARMORY_TEST_POSTGRES");
        if(string.IsNullOrWhiteSpace(root)) return;
        var admin=new NpgsqlConnectionStringBuilder(root){Database="postgres"}.ConnectionString;
        var db="armory_"+Guid.NewGuid().ToString("N");
        await using(var c=new NpgsqlConnection(admin)){await c.OpenAsync();await using var cmd=new NpgsqlCommand($"create database {db}",c);await cmd.ExecuteNonQueryAsync();}
        ConnectionString=new NpgsqlConnectionStringBuilder(root){Database=db}.ConnectionString;
        await using var setup=new NpgsqlConnection(ConnectionString);await setup.OpenAsync();
        await new NpgsqlCommand("do $$ begin if not exists(select from pg_roles where rolname='anon') then create role anon nologin; end if; if not exists(select from pg_roles where rolname='authenticated') then create role authenticated nologin; end if; end $$;",setup).ExecuteNonQueryAsync();
        foreach(var file in Directory.GetFiles(Path.Combine(AppContext.BaseDirectory,"sql"),"*.sql").Order()) await new NpgsqlCommand(await File.ReadAllTextAsync(file),setup){CommandTimeout=120}.ExecuteNonQueryAsync();
    }
    public async Task DisposeAsync()
    {
        if(ConnectionString is null)return;var b=new NpgsqlConnectionStringBuilder(ConnectionString);var db=b.Database;b.Database="postgres";
        await using var c=new NpgsqlConnection(b.ConnectionString);await c.OpenAsync();await new NpgsqlCommand($"drop database {db} with (force)",c).ExecuteNonQueryAsync();
    }
    public async Task<NpgsqlConnection> Open(string email)
    {
        if(ConnectionString is null) throw SkipException.ForSkip("Set ARMORY_TEST_POSTGRES to a throwaway PostgreSQL cluster connection string.");
        var c=new NpgsqlConnection(ConnectionString);await c.OpenAsync();await new NpgsqlCommand($"select set_config('armory.test_email',@e,false)",c){Parameters={new("e",email)}}.ExecuteNonQueryAsync();return c;
    }
}
[CollectionDefinition("db")] public sealed class DatabaseCollection:ICollectionFixture<DatabaseFixture>;
