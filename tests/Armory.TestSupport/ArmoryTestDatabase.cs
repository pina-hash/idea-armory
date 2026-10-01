using Npgsql;

namespace Armory.TestSupport;

/// <summary>
/// A fresh PostgreSQL database on the throwaway cluster named by ARMORY_TEST_POSTGRES, with
/// the roles <c>anon</c> and <c>authenticated</c>, the test-only identity and admin stubs
/// (<c>test-sql/*.sql</c>) and then the production SQL (<c>server-sql/*.sql</c>), the same way
/// <c>Armory.Server.Tests.DatabaseFixture</c> builds one. Disposing it drops the database.
/// </summary>
public sealed class ArmoryTestDatabase : IAsyncDisposable
{
    public const string EnvironmentVariable = "ARMORY_TEST_POSTGRES";

    private readonly string _root;
    private readonly NpgsqlDataSource _dataSource;
    private int _disposed;

    private ArmoryTestDatabase(string root, string databaseName, string connectionString)
    {
        _root = root;
        DatabaseName = databaseName;
        ConnectionString = connectionString;
        _dataSource = NpgsqlDataSource.Create(connectionString);
    }

    /// <summary>True when ARMORY_TEST_POSTGRES is set.</summary>
    public static bool IsAvailable => !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(EnvironmentVariable));

    /// <summary>The unique name of this database (<c>armory_ts_&lt;guid&gt;</c>).</summary>
    public string DatabaseName { get; }

    /// <summary>A superuser connection string for this database, with IncludeErrorDetail=true.</summary>
    public string ConnectionString { get; }

    /// <summary>Creates the database and applies the test SQL, then the production SQL.</summary>
    public static async Task<ArmoryTestDatabase> CreateAsync(CancellationToken cancellationToken = default)
    {
        var root = Environment.GetEnvironmentVariable(EnvironmentVariable);
        if (string.IsNullOrWhiteSpace(root))
            throw new InvalidOperationException($"Set {EnvironmentVariable} to a throwaway PostgreSQL cluster connection string.");

        var admin = new NpgsqlConnectionStringBuilder(root) { Database = "postgres" }.ConnectionString;
        var name = "armory_ts_" + Guid.NewGuid().ToString("N");
        await using (var connection = new NpgsqlConnection(admin))
        {
            await connection.OpenAsync(cancellationToken);
            // The name is generated above from a GUID, never from input.
            await new NpgsqlCommand($"create database {name}", connection).ExecuteNonQueryAsync(cancellationToken);
        }

        var connectionString = new NpgsqlConnectionStringBuilder(root) { Database = name, IncludeErrorDetail = true }.ConnectionString;
        var database = new ArmoryTestDatabase(root, name, connectionString);
        try
        {
            await using var setup = await database.OpenAsync(cancellationToken);
            await EnsureRoles(setup, cancellationToken);
            await ApplySqlDirectory(setup, "test-sql", cancellationToken);
            await ApplySqlDirectory(setup, "server-sql", cancellationToken);
            return database;
        }
        catch
        {
            await database.DisposeAsync();
            throw;
        }
    }

    /// <summary>Opens a superuser connection to this database.</summary>
    public async Task<NpgsqlConnection> OpenAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        return await _dataSource.OpenConnectionAsync(cancellationToken);
    }

    /// <summary>
    /// Opens a superuser connection whose session has <c>armory.test_email</c> set to
    /// <paramref name="email"/> and <c>armory.test_admins</c> set to that email when
    /// <paramref name="isAdmin"/> is true (empty otherwise). The role is not changed, as with
    /// DatabaseFixture.Open; run <c>set role authenticated</c> to act through RLS.
    /// </summary>
    public async Task<NpgsqlConnection> OpenAs(string email, bool isAdmin = false, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(email);
        var connection = await OpenAsync(cancellationToken);
        try
        {
            await using var command = new NpgsqlCommand(
                "select set_config('armory.test_email',$1,false), set_config('armory.test_admins',$2,false)", connection);
            command.Parameters.Add(new NpgsqlParameter { Value = email });
            command.Parameters.Add(new NpgsqlParameter { Value = isAdmin ? email.Trim().ToLowerInvariant() : "" });
            await command.ExecuteNonQueryAsync(cancellationToken);
            return connection;
        }
        catch
        {
            await connection.DisposeAsync();
            throw;
        }
    }

    /// <summary>
    /// Runs <paramref name="body"/> in one transaction the way PostgREST runs a request:
    /// <c>armory.test_email</c> and <c>armory.test_admins</c> are set locally, then
    /// <c>set local role authenticated</c>, or <c>set local role anon</c> with no identity when
    /// <paramref name="email"/> is null. The transaction commits when the body returns and
    /// rolls back when it throws.
    /// </summary>
    public async Task<T> RunAsCallerAsync<T>(string? email, IEnumerable<string>? adminEmails,
        Func<NpgsqlConnection, CancellationToken, Task<T>> body, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(body);
        await using var connection = await OpenAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        await using (var identity = new NpgsqlCommand(
            "select set_config('armory.test_email',$1,true), set_config('armory.test_admins',$2,true)", connection, transaction))
        {
            identity.Parameters.Add(new NpgsqlParameter { Value = email ?? "" });
            identity.Parameters.Add(new NpgsqlParameter { Value = string.Join(',', adminEmails ?? []) });
            await identity.ExecuteNonQueryAsync(cancellationToken);
        }
        await using (var role = new NpgsqlCommand(email is null ? "set local role anon" : "set local role authenticated", connection, transaction))
        {
            await role.ExecuteNonQueryAsync(cancellationToken);
        }
        var result = await body(connection, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return result;
    }

    /// <summary>Drops the database (and only this database).</summary>
    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        await _dataSource.DisposeAsync();
        var admin = new NpgsqlConnectionStringBuilder(_root) { Database = "postgres", Pooling = false }.ConnectionString;
        await using var connection = new NpgsqlConnection(admin);
        await connection.OpenAsync();
        await new NpgsqlCommand($"drop database if exists {DatabaseName} with (force)", connection).ExecuteNonQueryAsync();
    }

    // Roles are cluster wide, so parallel test processes can race to create them. A loser of
    // that race sees 42710 (duplicate_object) or 23505 (unique_violation) and simply retries.
    private static async Task EnsureRoles(NpgsqlConnection connection, CancellationToken cancellationToken)
    {
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                await new NpgsqlCommand(
                    "do $$ begin if not exists(select from pg_roles where rolname='anon') then create role anon nologin; end if; if not exists(select from pg_roles where rolname='authenticated') then create role authenticated nologin; end if; end $$;",
                    connection).ExecuteNonQueryAsync(cancellationToken);
                return;
            }
            catch (PostgresException e) when (attempt < 5 && e.SqlState is PostgresErrorCodes.DuplicateObject or PostgresErrorCodes.UniqueViolation)
            {
                await Task.Delay(50 * attempt, cancellationToken);
            }
        }
    }

    private static async Task ApplySqlDirectory(NpgsqlConnection connection, string directory, CancellationToken cancellationToken)
    {
        var path = Path.Combine(AppContext.BaseDirectory, directory);
        if (!Directory.Exists(path)) throw new DirectoryNotFoundException($"SQL folder {path} was not copied to the test output.");
        foreach (var file in Directory.GetFiles(path, "*.sql").Order(StringComparer.Ordinal))
        {
            await new NpgsqlCommand(await File.ReadAllTextAsync(file, cancellationToken), connection) { CommandTimeout = 120 }
                .ExecuteNonQueryAsync(cancellationToken);
        }
    }
}
