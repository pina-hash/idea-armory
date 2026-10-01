using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using Xunit.Abstractions;

namespace Armory.Server.Tests;

[Collection("db")]
public sealed class ServerSimulationTests(DatabaseFixture database, ITestOutputHelper output)
{
    private static readonly string[] Paths = ["robot/plate.txt", "robot/bracket.txt", "class/design.txt"];

    [DatabaseFact]
    public void Seeded_scenarios_preserve_every_save_and_converge_against_postgres()
    {
        var specific = Environment.GetEnvironmentVariable("ARMORY_SERVER_SEED");
        var count = specific is not null ? 1 : Environment.GetEnvironmentVariable("ARMORY_SERVER_STRESS") == "1" ? 5_000 : 300;
        var start = specific is null ? 0 : int.Parse(specific, System.Globalization.CultureInfo.InvariantCulture);
        // The budget below measures the simulation alone: wait until no end-to-end run is using
        // the cluster (HeavyRunLock), then start the clock.
        var waited = Stopwatch.StartNew();
        using var heavy = HeavyRunLock.Exclusive();
        output.WriteLine($"SERVER SIMULATION waited {waited.Elapsed.TotalSeconds:F1}s for the end-to-end suite");
        var watch = Stopwatch.StartNew();
        using var server = new PostgresSimulationServer(database);
        for (var seed = start; seed < start + count; seed++)
        {
            try { RunSeed(server, seed); }
            catch (Exception error) { throw new InvalidOperationException($"REPRO: ARMORY_SERVER_SEED={seed} dotnet test --filter Seeded_scenarios_preserve_every_save_and_converge_against_postgres | {error.Message}", error); }
        }
        output.WriteLine($"SERVER SIMULATION scenarios={count} elapsed={watch.Elapsed.TotalSeconds:F3}s first_seed={start}");
        if (specific is null && count == 300) Assert.True(watch.Elapsed < TimeSpan.FromMinutes(5), $"Server simulation exceeded five minutes: {watch.Elapsed}.");
    }

    private static void RunSeed(PostgresSimulationServer server, int seed)
    {
        var random = new SimulationRandom(seed);
        var clientCount = 2 + random.Next(2);
        server.Reset(clientCount);
        List<(int Client, string Path, Guid? Parent, string Hash, byte[] Bytes)> pending = [];
        Dictionary<(int Client, string Path), Guid?> observed = [];
        Dictionary<string, byte[]> saved = new(StringComparer.Ordinal);
        HashSet<int> offline = [];
        var lastCounts = (Versions: 0L, Sides: 0L, Tombstones: 0L);
        foreach (var path in Paths)
        {
            var bytes = Encoding.UTF8.GetBytes("initial:" + path);
            server.AddFile(path, Hash(bytes), bytes);
            for (var client = 0; client < clientCount; client++) observed[(client, path)] = server.Latest(path);
        }
        for (var step = 0; step < 48; step++)
        {
            var client = random.Next(clientCount);
            var path = Paths[random.Next(Paths.Length)];
            switch (random.Next(9))
            {
                case 0:
                case 1:
                    var bytes = Encoding.UTF8.GetBytes($"seed={seed};step={step};device={client};path={path}");
                    var hash = Hash(bytes);
                    pending.Add((client, path, observed[(client, path)], hash, bytes));
                    saved[hash] = bytes;
                    break;
                case 2: offline.Add(client); break;
                case 3: offline.Remove(client); break;
                case 4: server.Crash(client); break;
                case 5: server.Break(path); break;
                case 6: Sync(server, client, path, pending, observed, offline); break;
                case 7: if (!offline.Contains(client)) server.Acquire(client, path); break;
                case 8:
                    if (!offline.Contains(client) && server.Acquire(client, path))
                    {
                        var parent = observed[(client, path)];
                        if (server.Tombstone(client, path, parent)) observed[(client, path)] = server.Latest(path);
                        server.Release(client, path);
                    }
                    break;
            }
            if (!offline.Contains(client)) Sync(server, client, path, pending, observed, offline);
            var counts = server.Counts();
            Assert.True(counts.Versions >= lastCounts.Versions && counts.Sides >= lastCounts.Sides && counts.Tombstones >= lastCounts.Tombstones, "a version was purged");
            lastCounts = counts;
            foreach (var versionHash in server.VersionHashes()) Assert.True(server.Blobs.ContainsKey(versionHash), "version bytes purged");
            foreach (var item in saved) Assert.True(server.Blobs.ContainsKey(item.Key) || pending.Any(p => p.Hash == item.Key), "saved bytes lost during interruption");
        }
        offline.Clear();
        foreach (var path in Paths) server.Break(path);
        while (pending.Count > 0)
        foreach (var client in Enumerable.Range(0, clientCount))
        foreach (var path in Paths) Sync(server, client, path, pending, observed, offline);
        for (var round = 0; round < 5; round++)
        foreach (var client in Enumerable.Range(0, clientCount))
        foreach (var path in Paths) Sync(server, client, path, pending, observed, offline);
        Assert.Empty(pending);
        var hashes = server.VersionHashes();
        foreach (var item in saved)
        {
            Assert.Contains(item.Key, hashes);
            Assert.Equal(item.Value, server.Blobs[item.Key]);
        }
        foreach (var client in Enumerable.Range(0, clientCount))
        foreach (var path in Paths) Assert.Equal(server.Latest(path), observed[(client, path)]);
    }

    private static void Sync(PostgresSimulationServer server, int client, string path,
        List<(int Client, string Path, Guid? Parent, string Hash, byte[] Bytes)> pending,
        Dictionary<(int Client, string Path), Guid?> observed, HashSet<int> offline)
    {
        if (offline.Contains(client)) return;
        var pendingIndex = pending.FindIndex(p => p.Client == client && p.Path == path);
        if (pendingIndex >= 0)
        {
            var save = pending[pendingIndex];
            pending.RemoveAt(pendingIndex);
            if (server.Acquire(client, path))
            {
                var shouldAdvance = server.Latest(path) == save.Parent;
                var result = server.Commit(client, path, save.Parent, save.Hash, save.Bytes);
                Assert.Equal(shouldAdvance, result.Advanced);
                Assert.True(result.Advanced ? server.Latest(path) == result.Id : server.Latest(path) != result.Id,
                    "shared file advanced without the commit result proving advancement");
                server.Release(client, path);
            }
            else
            {
                var result = server.Commit(client, path, save.Parent, save.Hash, save.Bytes);
                Assert.False(result.Advanced);
                Assert.NotEqual(result.Id, server.Latest(path));
            }
        }
        observed[(client, path)] = server.Latest(path);
    }

    private static string Hash(byte[] bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));
    private sealed class SimulationRandom(int seed)
    {
        private uint state = unchecked((uint)seed) + 0x9E3779B9u;
        internal int Next(int exclusive) { state ^= state << 13; state ^= state >> 17; state ^= state << 5; return (int)(state % (uint)exclusive); }
    }
}
