using Npgsql;

namespace Armory.TestSupport;

/// <summary>
/// A cluster-wide PostgreSQL advisory lock (in the cluster's <c>postgres</c> database) that
/// keeps the end-to-end suite and the server simulation from running at once. Both drive the
/// same throwaway cluster hard from separate test processes, and the server simulation has a
/// five-minute budget that measures the simulation, not a neighbor: on a four-core CI runner
/// the overlap made it 3.8 times slower. End-to-end worlds hold the lock shared (they still
/// run in parallel with each other); <c>Armory.Server.Tests.HeavyRunLock</c> takes it
/// exclusively with the same key before the simulation's stopwatch starts.
/// The connection is unpooled, so disposing it (or the process dying) ends the session and
/// frees the lock.
/// </summary>
public sealed class HeavyRunLock : IAsyncDisposable
{
    /// <summary>"ARMORY" in ASCII. Must equal <c>Armory.Server.Tests.HeavyRunLock.Key</c>.</summary>
    public const long Key = 0x41524D4F5259;

    private readonly NpgsqlConnection _connection;

    private HeavyRunLock(NpgsqlConnection connection, bool shared)
    {
        _connection = connection;
        Shared = shared;
    }

    /// <summary>True for a shared hold, false for an exclusive one.</summary>
    public bool Shared { get; }

    /// <summary>Waits for and takes the lock shared. <paramref name="key"/> is for this class's own tests.</summary>
    public static Task<HeavyRunLock> SharedAsync(long key = Key, CancellationToken cancellationToken = default) => AcquireAsync(shared: true, key, cancellationToken);

    /// <summary>Waits for and takes the lock exclusively. <paramref name="key"/> is for this class's own tests.</summary>
    public static Task<HeavyRunLock> ExclusiveAsync(long key = Key, CancellationToken cancellationToken = default) => AcquireAsync(shared: false, key, cancellationToken);

    private static async Task<HeavyRunLock> AcquireAsync(bool shared, long key, CancellationToken cancellationToken)
    {
        var root = Environment.GetEnvironmentVariable(ArmoryTestDatabase.EnvironmentVariable);
        if (string.IsNullOrWhiteSpace(root))
            throw new InvalidOperationException($"Set {ArmoryTestDatabase.EnvironmentVariable} to a throwaway PostgreSQL cluster connection string.");
        var builder = new NpgsqlConnectionStringBuilder(root) { Database = "postgres", Pooling = false };
        var connection = new NpgsqlConnection(builder.ConnectionString);
        try
        {
            await connection.OpenAsync(cancellationToken);
            // Blocking waits queue fairly in PostgreSQL; lock_timeout bounds a wait on a holder
            // that never lets go.
            var function = shared ? "pg_advisory_lock_shared" : "pg_advisory_lock";
            await using var command = new NpgsqlCommand($"set lock_timeout = '20min'; select {function}(@key)", connection) { CommandTimeout = 0 };
            command.Parameters.AddWithValue("key", key);
            await command.ExecuteNonQueryAsync(cancellationToken);
            return new HeavyRunLock(connection, shared);
        }
        catch
        {
            await connection.DisposeAsync();
            throw;
        }
    }

    public ValueTask DisposeAsync() => _connection.DisposeAsync();
}
