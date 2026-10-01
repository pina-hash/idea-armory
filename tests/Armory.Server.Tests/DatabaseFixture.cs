using Npgsql;

namespace Armory.Server.Tests;

public sealed class DatabaseFactAttribute : FactAttribute
{
    public DatabaseFactAttribute()
    {
        if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("ARMORY_TEST_POSTGRES")))
        {
            Skip = "Set ARMORY_TEST_POSTGRES to a throwaway PostgreSQL cluster connection string.";
        }
    }
}

public sealed class DatabaseFixture : IAsyncLifetime
{
    private string? _database;
    public string? ConnectionString { get; private set; }

    public async Task InitializeAsync()
    {
        var root = Environment.GetEnvironmentVariable("ARMORY_TEST_POSTGRES");
        if (string.IsNullOrWhiteSpace(root)) return;

        (_database, ConnectionString) = await CreateDatabase(root);
        await using var setup = new NpgsqlConnection(ConnectionString);
        await setup.OpenAsync();
        await EnsureRoles(setup);

        await new NpgsqlCommand(
            "create table public.coin_ledger(id bigint); revoke all on public.coin_ledger from authenticated;",
            setup).ExecuteNonQueryAsync();
        await ApplySqlDirectory(setup, "test-sql");
        await ApplySqlDirectory(setup, "server-sql");
    }

    public async Task DisposeAsync()
    {
        if (_database is not null) await DropDatabase(_database);
    }

    public async Task<NpgsqlConnection> Open(string email)
    {
        var connection = new NpgsqlConnection(ConnectionString ?? throw new InvalidOperationException("Database test was not enabled."));
        await connection.OpenAsync();
        await new NpgsqlCommand("select set_config('armory.test_email',@e,false)", connection)
        {
            Parameters = { new("e", email) }
        }.ExecuteNonQueryAsync();
        return connection;
    }

    public async Task<(string Database, string ConnectionString)> CreateProductionDatabase()
    {
        var root = Environment.GetEnvironmentVariable("ARMORY_TEST_POSTGRES")
            ?? throw new InvalidOperationException("Database test was not enabled.");
        var result = await CreateDatabase(root);
        await using var setup = new NpgsqlConnection(result.ConnectionString);
        await setup.OpenAsync();
        await EnsureRoles(setup);
        await ApplySqlDirectory(setup, "server-sql");
        return result;
    }

    public async Task DropDatabase(string database)
    {
        var root = Environment.GetEnvironmentVariable("ARMORY_TEST_POSTGRES")
            ?? throw new InvalidOperationException("Database test was not enabled.");
        var admin = new NpgsqlConnectionStringBuilder(root) { Database = "postgres" }.ConnectionString;
        await using var connection = new NpgsqlConnection(admin);
        await connection.OpenAsync();
        await new NpgsqlCommand($"drop database {database} with (force)", connection).ExecuteNonQueryAsync();
    }

    private static async Task<(string Database, string ConnectionString)> CreateDatabase(string root)
    {
        var admin = new NpgsqlConnectionStringBuilder(root) { Database = "postgres" }.ConnectionString;
        var database = "armory_" + Guid.NewGuid().ToString("N");
        await using var connection = new NpgsqlConnection(admin);
        await connection.OpenAsync();
        await new NpgsqlCommand($"create database {database}", connection).ExecuteNonQueryAsync();
        return (database, new NpgsqlConnectionStringBuilder(root) { Database = database, IncludeErrorDetail = true }.ConnectionString);
    }

    private static Task EnsureRoles(NpgsqlConnection connection) => new NpgsqlCommand(
        "do $$ begin if not exists(select from pg_roles where rolname='anon') then create role anon nologin; end if; if not exists(select from pg_roles where rolname='authenticated') then create role authenticated nologin; end if; end $$;",
        connection).ExecuteNonQueryAsync();

    private static async Task ApplySqlDirectory(NpgsqlConnection connection, string directory)
    {
        foreach (var file in Directory.GetFiles(Path.Combine(AppContext.BaseDirectory, directory), "*.sql").Order())
        {
            await new NpgsqlCommand(await File.ReadAllTextAsync(file), connection) { CommandTimeout = 120 }.ExecuteNonQueryAsync();
        }
    }
}

[CollectionDefinition("db")]
public sealed class DatabaseCollection : ICollectionFixture<DatabaseFixture>;
