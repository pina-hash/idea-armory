using Npgsql;

namespace Armory.Server.Tests;

// The server simulation's five-minute budget measures the simulation, not a neighbor. The
// end-to-end suite (tests/Armory.EndToEnd.Tests) drives the same PostgreSQL cluster hard from
// another test process; on a four-core CI runner the overlap made the simulation 3.8 times
// slower. Each end-to-end world holds this cluster-wide advisory lock shared
// (Armory.TestSupport.HeavyRunLock, same key, same "postgres" database); the simulation takes
// it exclusively before its stopwatch starts. The connection is unpooled, so disposing it (or
// the process dying) ends the session and frees the lock.
internal sealed class HeavyRunLock : IDisposable
{
    // "ARMORY" in ASCII. Must equal Armory.TestSupport.HeavyRunLock.Key.
    internal const long Key = 0x41524D4F5259;

    private readonly NpgsqlConnection connection;

    private HeavyRunLock(NpgsqlConnection connection) => this.connection = connection;

    internal static HeavyRunLock Exclusive()
    {
        var root = Environment.GetEnvironmentVariable("ARMORY_TEST_POSTGRES")
            ?? throw new InvalidOperationException("Set ARMORY_TEST_POSTGRES to a throwaway PostgreSQL cluster connection string.");
        var connection = new NpgsqlConnection(new NpgsqlConnectionStringBuilder(root) { Database = "postgres", Pooling = false }.ConnectionString);
        try
        {
            connection.Open();
            using var command = new NpgsqlCommand("set lock_timeout = '20min'; select pg_advisory_lock(@key)", connection) { CommandTimeout = 0 };
            command.Parameters.AddWithValue("key", Key);
            command.ExecuteNonQuery();
            return new HeavyRunLock(connection);
        }
        catch
        {
            connection.Dispose();
            throw;
        }
    }

    public void Dispose() => connection.Dispose();
}
