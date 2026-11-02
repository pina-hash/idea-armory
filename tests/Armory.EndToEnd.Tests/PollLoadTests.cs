using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using Armory.Agent.Engine;
using Armory.Client;
using Xunit.Abstractions;

namespace Armory.EndToEnd.Tests;

// The load one running Armory puts on the shared website database (0.5.3). On 2026-11-02 the
// lab's apps made about 324 requests a minute, about 74% of every request the website's
// database answered. This harness runs the real engine's loop, turn by turn, against a fake
// server that only counts and answers, on a simulated clock: each turn's wait is skipped by
// moving the clock. It needs no database and no network, and runs on every host.
//
// It reports requests a minute by kind under idle, active, hidden and 5xx conditions, for the
// 0.5.2 schedule (its intervals and its full re-reads, rebuilt here from options) and the 0.5.3
// one. A pass takes no simulated time here, so the 0.5.2 numbers are its ceiling: a lab
// computer whose quiet pass took 13 to 20 seconds made fewer passes than this.
public sealed class PollLoadTests(ITestOutputHelper output)
{
    private const int Projects = 4; // the lab's measured mean was 3.75 projects per pass
    private static readonly TimeSpan Simulated = TimeSpan.FromMinutes(60);

    public enum Condition { Idle, Active, Hidden, ServerFailing }

    // The 0.5.2 schedule: 2 and 10 seconds whatever happened, the project list every pass, every
    // file list every minute, no back off when the site fails.
    private static EngineOptions Before => new()
    {
        VaultRoot = World.Root, ActivePollInterval = TimeSpan.FromSeconds(2), IdlePollInterval = TimeSpan.FromSeconds(10),
        HiddenPollInterval = TimeSpan.FromSeconds(10), OfflinePollLongest = TimeSpan.FromSeconds(10), LivePollInterval = TimeSpan.Zero,
        ProjectListEvery = TimeSpan.Zero, FilesSafetyRefresh = TimeSpan.FromSeconds(60), QuietProjectEvery = TimeSpan.Zero,
    };
    private static EngineOptions After => new() { VaultRoot = World.Root };

    [Theory]
    [InlineData(Condition.Idle, 4)]
    [InlineData(Condition.Active, 7)]
    [InlineData(Condition.Hidden, 2.5)]
    [InlineData(Condition.ServerFailing, 0.5)]
    public async Task One_app_makes_far_fewer_requests_a_minute(Condition condition, double ceiling)
    {
        var before = await PerMinuteAsync(Before, condition);
        var after = await PerMinuteAsync(After, condition);
        output.WriteLine($"{condition}: 0.5.2 {Describe(before)} | 0.5.3 {Describe(after)}");
        Assert.True(after.Total <= ceiling, $"{condition}: {after.Total:F2} requests a minute, more than {ceiling}");
        Assert.True(after.Total < before.Total / 3, $"{condition}: {after.Total:F2} is not a third of {before.Total:F2}");
    }

    // The lab on 2026-11-02 (about 20 apps), as the queue's target counts it: armory_my_projects,
    // armory_list_changes and armory_project_files together. A plausible mix of what the apps were
    // doing: most behind SolidWorks or locked, a few in view, one moving its own files, each pass
    // taking 8 seconds (lab computers measured 13 to 20, fast ones 1 to 2). The 0.5.2 schedule
    // modeled this way lands near the 324 a minute measured that afternoon.
    [Fact]
    public async Task The_lab_falls_from_about_324_to_under_50_requests_a_minute()
    {
        (Condition Condition, int Apps)[] lab = [(Condition.Hidden, 14), (Condition.Idle, 5), (Condition.Active, 1)];
        double beforeTotal = 0, afterTotal = 0;
        foreach (var (condition, apps) in lab)
        {
            beforeTotal += apps * (await PerMinuteAsync(Before, condition, LabPass)).Polling;
            afterTotal += apps * (await PerMinuteAsync(After, condition, LabPass)).Polling;
        }
        output.WriteLine($"lab of 20 (14 hidden, 5 in view, 1 active), {Projects} projects each, {LabPass.TotalSeconds:F0} s a pass: 0.5.2 {beforeTotal:F0} a minute, 0.5.3 {afterTotal:F0} a minute");
        Assert.InRange(beforeTotal, 250, 450);
        Assert.InRange(afterTotal, 0, 50);
    }

    [Fact]
    public async Task The_change_cursor_only_moves_forward_and_a_moved_feed_reads_its_files()
    {
        var server = new CountingServer(Projects);
        await using var app = await AppAsync(After, server);
        await app.TurnAsync();
        var files = server.Count("armory_project_files");
        server.ChangeIn(server.ProjectIds[1]);
        await app.TurnAsync();
        Assert.Equal(files + 1, server.Count("armory_project_files"));
        Assert.True(server.LastAfter(server.ProjectIds[1]) >= 0);
        await app.TurnAsync();
        // The next look asks only after the change it was given, never from the start again.
        Assert.Equal(server.Cursor, server.LastAfter(server.ProjectIds[1]));
        Assert.Equal(files + 1, server.Count("armory_project_files"));
    }

    private static string Describe(Rates r) => $"{r.Total:F2}/min (my_projects {r.MyProjects:F2}, list_changes {r.ListChanges:F2}, project_files {r.ProjectFiles:F2}, other {r.Other:F2})";

    private sealed record Rates(double MyProjects, double ListChanges, double ProjectFiles, double Other)
    {
        public double Polling => MyProjects + ListChanges + ProjectFiles;
        public double Total => Polling + Other;
    }

    private static readonly TimeSpan LabPass = TimeSpan.FromSeconds(8);

    // passTime: how long each pass takes on the simulated clock (zero: the ceiling).
    private static async Task<Rates> PerMinuteAsync(EngineOptions options, Condition condition, TimeSpan passTime = default)
    {
        var server = new CountingServer(Projects) { Failing = condition == Condition.ServerFailing };
        await using var app = await AppAsync(options, server, looking: condition != Condition.Hidden);
        var start = app.Clock.GetUtcNow();
        // The first look (sign-in, every project read once) is not the steady state.
        await app.TurnAsync();
        server.Reset();
        var from = app.Clock.GetUtcNow();
        while (app.Clock.GetUtcNow() - from < Simulated)
        {
            if (condition == Condition.Active) app.Engine.OwnWorkForTest();
            var wait = await app.TurnAsync();
            app.Clock.Advance(passTime + (wait ?? TimeSpan.FromMilliseconds(1)));
        }
        var minutes = (app.Clock.GetUtcNow() - from).TotalMinutes;
        Assert.True(app.Clock.GetUtcNow() > start);
        return new(server.Count("armory_my_projects") / minutes, server.Count("armory_list_changes") / minutes,
            server.Count("armory_project_files") / minutes, server.Other / minutes);
    }

    private static async Task<App> AppAsync(EngineOptions options, CountingServer server, bool looking = true)
    {
        var clock = new SimulatedClock();
        var http = new HttpClient(server);
        var sessions = new SessionManager(http, new InMemorySecretStore(), clock);
        sessions.SignIn(new ArmorySession(CountingServer.Url, "anon", "access", "refresh", clock.GetUtcNow().AddYears(5), "student@school.test", Guid.NewGuid(), "IDEA-01"));
        var root = Path.Combine(Path.GetTempPath(), "armory-load-" + Guid.NewGuid().ToString("N"));
        var engine = new SyncEngine(options, new EngineDependencies
        {
            Files = new PortableVaultFileSystem(root), Journal = new MemoryJournalStore(), Snapshots = new MemorySnapshotStore(), State = new MemoryStateStore(),
            Sessions = sessions, Api = new ArmoryApi(new PostgrestClient(http, sessions)),
            Blobs = new BlobClient(http, http, new Uri("http://site.test/"), sessions), Clock = clock,
        });
        engine.SetAttention(looking);
        await Task.Yield();
        return new App(engine, clock, root, http);
    }

    private sealed class App(SyncEngine engine, SimulatedClock clock, string root, HttpClient http) : IAsyncDisposable
    {
        public SyncEngine Engine { get; } = engine;
        public SimulatedClock Clock { get; } = clock;
        public Task<TimeSpan?> TurnAsync() => Engine.LoopTurnForTestAsync();
        public async ValueTask DisposeAsync()
        {
            await Engine.DisposeAsync();
            http.Dispose();
            try { Directory.Delete(root, true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
    }

    // A clock that moves only when told, timestamps included (the engine times its reads with them).
    internal sealed class SimulatedClock : TimeProvider
    {
        private long ticks = new DateTimeOffset(2026, 11, 2, 21, 0, 0, TimeSpan.Zero).UtcTicks;
        public override DateTimeOffset GetUtcNow() => new(Interlocked.Read(ref ticks), TimeSpan.Zero);
        public override long GetTimestamp() => Interlocked.Read(ref ticks);
        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
        public void Advance(TimeSpan by) => Interlocked.Add(ref ticks, by.Ticks);
    }

    // PostgREST as the engine's loop sees it: a student in N quiet, empty projects. Counts every
    // call by function. Failing: every call answers 503 (the site down behind its gateway).
    private sealed class CountingServer : HttpMessageHandler
    {
        public const string Url = "http://supabase.test";
        private readonly Dictionary<string, int> counts = new(StringComparer.Ordinal);
        private readonly Dictionary<Guid, long> lastAfter = [];
        private readonly Dictionary<Guid, long> feed = [];
        public CountingServer(int projects)
        {
            ProjectIds = Enumerable.Range(0, projects).Select(_ => Guid.NewGuid()).ToArray();
        }
        public Guid[] ProjectIds { get; }
        public bool Failing { get; set; }
        public long Cursor { get; private set; }
        public int Other { get; private set; }
        public int Count(string function) { lock (counts) return counts.GetValueOrDefault(function); }
        public long LastAfter(Guid project) { lock (counts) return lastAfter.GetValueOrDefault(project, -1); }
        public void ChangeIn(Guid project) { lock (counts) feed[project] = ++Cursor; }
        public void Reset() { lock (counts) { counts.Clear(); Other = 0; } }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var function = request.RequestUri!.AbsolutePath.Split('/').Last();
            var body = request.Content is null ? null : JsonNode.Parse(await request.Content.ReadAsStringAsync(cancellationToken)) as JsonObject;
            lock (counts)
            {
                counts[function] = counts.GetValueOrDefault(function) + 1;
                if (function is not ("armory_my_projects" or "armory_list_changes" or "armory_project_files")) Other++;
            }
            if (Failing) return new HttpResponseMessage(HttpStatusCode.ServiceUnavailable) { Content = new StringContent("<html>down</html>") };
            string answer = function switch
            {
                "armory_my_projects" => new JsonArray(ProjectIds.Select((id, i) => (JsonNode)new JsonObject
                {
                    ["id"] = id.ToString(), ["name"] = "Project " + (i + 1), ["role"] = "student", ["pinned_release"] = 2025,
                    ["release_gate"] = "warn", ["archived"] = false, ["can_take_back"] = false,
                }).ToArray()).ToJsonString(),
                "armory_list_changes" => Changes(body),
                "armory_project_files" => "[]",
                _ => "null",
            };
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(answer, Encoding.UTF8, "application/json") };
        }

        private string Changes(JsonObject? body)
        {
            var project = Guid.Parse(body!["p_project"]!.GetValue<string>());
            var after = body["p_after"]!.GetValue<long>();
            lock (counts)
            {
                lastAfter[project] = after;
                if (!feed.TryGetValue(project, out var cursor) || cursor <= after) return "[]";
                return new JsonArray(new JsonObject
                {
                    ["cursor"] = cursor, ["project_id"] = project.ToString(), ["kind"] = "file_created", ["entity_id"] = Guid.NewGuid().ToString(),
                    ["payload"] = new JsonObject(), ["created_at"] = "2026-11-02T21:00:00Z",
                }).ToJsonString();
            }
        }
    }
}
