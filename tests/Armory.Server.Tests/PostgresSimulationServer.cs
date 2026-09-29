using Npgsql;

namespace Armory.Server.Tests;

internal sealed class PostgresSimulationServer(DatabaseFixture fixture) : IDisposable
{
    private readonly Dictionary<int, NpgsqlConnection> connections = [];
    private NpgsqlConnection? mentor;
    internal Dictionary<string, byte[]> Blobs { get; } = new(StringComparer.Ordinal);
    internal Guid Project { get; private set; }
    internal Dictionary<string, Guid> Files { get; } = new(StringComparer.Ordinal);

    internal void Reset(int clientCount)
    {
        DisposeConnections();
        mentor = OpenNew("mentor@example.com");
        var setup = mentor;
        Command(setup, "truncate armory_change_feed,armory_part_number_allocations,armory_tombstones,armory_locks,armory_side_versions,armory_versions,armory_files,armory_members,armory_projects cascade").ExecuteNonQuery();
        Project = (Guid)Command(setup, "insert into armory_projects(name,season) values('Simulation',2026) returning id").ExecuteScalar()!;
        Command(setup, "insert into armory_members values(@p,'mentor@example.com','mentor')", ("p", Project)).ExecuteNonQuery();
        for (var i = 0; i < clientCount; i++)
        {
            Command(setup, "insert into armory_members values(@p,@e,'student')", ("p", Project), ("e", Email(i))).ExecuteNonQuery();
            connections.Add(i, OpenNew(Email(i)));
        }
        Files.Clear();
        Blobs.Clear();
    }

    internal void AddFile(string path, string hash, byte[] bytes)
    {
        var file = (Guid)Command(Mentor, "insert into armory_files(project_id,name) values(@p,@n) returning id", ("p", Project), ("n", path)).ExecuteScalar()!;
        Command(Mentor, "select armory_acquire_lock(@f)", ("f", file)).ExecuteNonQuery();
        var version = (Guid)Command(Mentor, "select version_id from armory_commit_version(@f,null,@k,@h,@b)", ("f", file), ("k", Key(hash)), ("h", hash), ("b", (long)bytes.Length)).ExecuteScalar()!;
        Command(Mentor, "select armory_release_lock(@f)", ("f", file)).ExecuteNonQuery();
        Files.Add(path, file);
        Blobs.Add(hash, bytes.ToArray());
        _ = version;
    }

    internal void Crash(int client) { connections[client].Dispose(); connections[client] = OpenNew(Email(client)); }
    internal bool Acquire(int client, string path) => (bool)Command(Client(client), "select armory_acquire_lock(@f)", ("f", Files[path])).ExecuteScalar()!;
    internal bool Release(int client, string path) => (bool)Command(Client(client), "select armory_release_lock(@f)", ("f", Files[path])).ExecuteScalar()!;
    internal void Break(string path)
    {
        Command(Mentor, "select armory_break_lock(@f)", ("f", Files[path])).ExecuteNonQuery();
    }
    internal (Guid Id, bool Advanced) Commit(int client, string path, Guid? parent, string hash, byte[] bytes)
    {
        Blobs.TryAdd(hash, bytes.ToArray());
        using var reader = Command(Client(client), "select * from armory_commit_version(@f,@p,@k,@h,@b)", ("f", Files[path]), ("p", parent is null ? DBNull.Value : parent), ("k", Key(hash)), ("h", hash), ("b", (long)bytes.Length)).ExecuteReader();
        Assert.True(reader.Read());
        return (reader.GetGuid(0), reader.GetBoolean(1));
    }
    internal Guid Side(int client, string path, Guid? parent, string hash, byte[] bytes)
    {
        Blobs.TryAdd(hash, bytes.ToArray());
        return (Guid)Command(Client(client), "select armory_save_side_version(@f,@p,@k,@h,@b,'simulation conflict')", ("f", Files[path]), ("p", parent is null ? DBNull.Value : parent), ("k", Key(hash)), ("h", hash), ("b", (long)bytes.Length)).ExecuteScalar()!;
    }
    internal bool Tombstone(int client, string path, Guid? parent) => (bool)Command(Client(client), "select armory_tombstone(@f,@p)", ("f", Files[path]), ("p", parent is null ? DBNull.Value : parent)).ExecuteScalar()!;
    internal Guid? Latest(string path)
    {
        var value = Command(Mentor, "select current_version_id from armory_files where id=@f", ("f", Files[path])).ExecuteScalar();
        return value is null or DBNull ? null : (Guid)value;
    }
    internal string? LatestHash(string path)
    {
        return Command(Mentor, "select v.content_sha256 from armory_files f left join armory_versions v on v.id=f.current_version_id where f.id=@f", ("f", Files[path])).ExecuteScalar() as string;
    }
    internal HashSet<string> VersionHashes()
    {
        using var reader = Command(Mentor, "select content_sha256 from armory_versions union select content_sha256 from armory_side_versions").ExecuteReader();
        HashSet<string> result = new(StringComparer.Ordinal);
        while (reader.Read()) result.Add(reader.GetString(0));
        return result;
    }
    internal (long Versions, long Sides, long Tombstones) Counts()
    {
        using var reader = Command(Mentor, "select (select count(*) from armory_versions),(select count(*) from armory_side_versions),(select count(*) from armory_tombstones)").ExecuteReader();
        Assert.True(reader.Read());
        return (reader.GetInt64(0), reader.GetInt64(1), reader.GetInt64(2));
    }

    private NpgsqlConnection Client(int number) => connections[number];
    private NpgsqlConnection Mentor => mentor ?? throw new InvalidOperationException("Simulation server has not been reset.");
    private NpgsqlConnection OpenNew(string email) => fixture.Open(email).GetAwaiter().GetResult();
    private static string Email(int client) => $"simulation-{client}@example.com";
    private static string Key(string hash) => $"blobs/sha256/{hash[..2]}/{hash[2..4]}/{hash}";
    private static NpgsqlCommand Command(NpgsqlConnection connection, string sql, params (string Name, object? Value)[] values)
    {
        var command = new NpgsqlCommand(sql, connection);
        foreach (var (name, value) in values) command.Parameters.AddWithValue(name, value ?? DBNull.Value);
        return command;
    }
    private void DisposeConnections() { foreach (var connection in connections.Values) connection.Dispose(); connections.Clear(); mentor?.Dispose(); mentor = null; }
    public void Dispose() => DisposeConnections();
}
