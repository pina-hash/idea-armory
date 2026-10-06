using System.Net;
using System.Security.Cryptography;
using System.Text;
using Armory.Client;
using Armory.Storage;
using Armory.TestSupport;

namespace Armory.Client.Tests;

// Contract v2 (server/sql/005_v2.sql, idea-app's 0232): the new calls, the null season, the
// archived flag, folder refusals, the deadlock resend and transfer progress.
public sealed partial class ClientTests
{
    // Records every value in order on the reporting thread (Progress<T> would post them out of order).
    private sealed class Recorder : IProgress<long>
    {
        private readonly List<long> values = [];
        public void Report(long value) { lock (values) values.Add(value); }
        public long[] Values { get { lock (values) return [.. values]; } }
    }

    // Answers RPCs (and token refreshes) from a script of (status, body) pairs and keeps every
    // request body, path and bearer token it saw.
    private sealed class CannedRpc(IEnumerable<(int Status, string Body)> answers) : HttpMessageHandler
    {
        private readonly Queue<(int Status, string Body)> script = new(answers);
        public List<string> Bodies { get; } = [];
        public List<string> Paths { get; } = [];
        public List<string?> Tokens { get; } = [];
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Bodies.Add(await request.Content!.ReadAsStringAsync(cancellationToken));
            Paths.Add(request.RequestUri!.AbsolutePath);
            Tokens.Add(request.Headers.Authorization?.Parameter);
            var (status, body) = script.Dequeue();
            return new HttpResponseMessage((HttpStatusCode)status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
        }
    }

    // Reads a PUT body once before sending it, as a handler that retries a request would: the
    // body is then sent again from the start.
    private sealed class ResendingHandler(HttpMessageHandler inner) : DelegatingHandler(inner)
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (request.Method == HttpMethod.Put && request.Content is not null) await request.Content.CopyToAsync(Stream.Null, cancellationToken);
            return await base.SendAsync(request, cancellationToken);
        }
    }

    private static ArmoryApi CannedApi(CannedRpc handler)
    {
        var http = new HttpClient(handler);
        var sessions = new SessionManager(http, new InMemorySecretStore());
        sessions.SignIn(new ArmorySession("https://supabase.armory.test", "anon-key", "access", "refresh", DateTimeOffset.UtcNow.AddHours(1), "alex.kim@students.test", Guid.Empty, "test PC"));
        return new ArmoryApi(new PostgrestClient(http, sessions));
    }

    private static void AssertRunningTotal(long[] values, long total)
    {
        Assert.True(values.Length > 2, $"Expected several progress reports, saw {values.Length}.");
        Assert.Equal(0, values[0]);
        Assert.Equal(total, values[^1]);
        for (var i = 1; i < values.Length; i++) Assert.True(values[i] >= values[i - 1], $"Progress went back from {values[i - 1]} to {values[i]}.");
    }

    [Fact]
    public async Task My_projects_tolerate_a_null_season_and_a_server_without_the_archived_flag()
    {
        var old = Guid.NewGuid(); var unseasoned = Guid.NewGuid();
        var handler = new CannedRpc([(200, $$"""
            [{"id":"{{old}}","name":"Robot 2027","season":2027,"role":"student","pinned_release":2025,"release_gate":"warn"},
             {"id":"{{unseasoned}}","name":"Robot","season":null,"role":"mentor","pinned_release":2025,"release_gate":"enforce","archived":true,"archived_at":"2026-10-06T22:00:00+00:00"}]
            """)]);
        var projects = await CannedApi(handler).MyProjectsAsync();
        Assert.Equal((old, (int?)2027, false), (projects[0].Id, projects[0].Season, projects[0].Archived)); // no "archived" key reads false
        Assert.Equal((unseasoned, (int?)null, true, ProjectReleaseGate.Enforce), (projects[1].Id, projects[1].Season, projects[1].Archived, projects[1].ReleaseGate));
    }

    [PostgresFact]
    public async Task Every_v2_rpc_round_trips_and_every_write_replays_from_its_receipt()
    {
        await using var env = await Env.StartAsync();
        var (_, mentor, _, _) = env.SignedIn("pina@ideabosco.test", admin: true);
        var (_, alex, _, _) = env.SignedIn("alex.kim@students.test");
        var (_, maria, _, _) = env.SignedIn("maria.lopez@students.test");
        async Task<T> Twice<T>(Func<Guid, Task<T>> call) { var op = Guid.NewGuid(); var first = await call(op); Assert.Equal(first, await call(op)); return first; }
        var project = await Twice(op => mentor.CreateProjectAsync("Robot", null, op));
        await mentor.AddMemberAsync(project, "alex.kim@students.test", MemberRole.Student, Guid.NewGuid());
        await mentor.AddMemberAsync(project, "maria.lopez@students.test", MemberRole.Student, Guid.NewGuid());
        var mine = Assert.Single(await alex.MyProjectsAsync());
        Assert.Equal(((int?)null, false), (mine.Season, mine.Archived));
        var (part, full) = await Twice(op => alex.AllocatePartNumberAsync(project, 1, null, op)); // no season anywhere: this year's numbers
        Assert.Matches(@"^5669-\d\d-0100$", part);
        Assert.False(full);
        Assert.True(await Twice(op => mentor.RenameProjectAsync(project, "Robot 2028", op)));
        Assert.False(await mentor.RenameProjectAsync(project, "Robot 2028", Guid.NewGuid()));
        Assert.Equal("Robot 2028", Assert.Single(await alex.MyProjectsAsync()).Name);
        Assert.True((await Assert.ThrowsAsync<ArmoryRpcException>(() => alex.RenameProjectAsync(project, "Mine now", Guid.NewGuid()))).IsForbidden);
        Assert.True(await Twice(op => mentor.SetProjectArchivedAsync(project, true, op)));
        Assert.True(Assert.Single(await alex.MyProjectsAsync()).Archived);
        Assert.True(await Twice(op => mentor.SetProjectArchivedAsync(project, false, op)));
        Assert.False(Assert.Single(await alex.MyProjectsAsync()).Archived);

        var alexPc = await alex.RegisterDeviceAsync("Alex laptop", Guid.NewGuid());
        var mariaPc = await maria.RegisterDeviceAsync("LAB-PC-07", Guid.NewGuid());
        var gear = await alex.CreateFileAsync(project, "Drive", "Gear.SLDPRT", alexPc, Guid.NewGuid());
        var shaft = await alex.CreateFileAsync(project, "Drive/Shaft", "Shaft.SLDPRT", alexPc, Guid.NewGuid());
        await alex.CreateFileAsync(project, "Other", "Plate.SLDPRT", alexPc, Guid.NewGuid());
        Assert.Empty(await alex.ProjectCheckoutsAsync(project));
        Assert.True(await maria.AcquireLockAsync(shaft, mariaPc, Guid.NewGuid()));
        var checkout = Assert.Single(await alex.ProjectCheckoutsAsync(project));
        Assert.Equal((shaft, "Drive/Shaft", "Shaft.SLDPRT", "maria.lopez@students.test", (string?)null, "LAB-PC-07"),
            (checkout.FileId, checkout.Folder, checkout.Name, checkout.HolderEmail, checkout.HolderName, checkout.DeviceName)); // no profile: no name
        Assert.InRange(checkout.Since, DateTimeOffset.UtcNow.AddMinutes(-5), DateTimeOffset.UtcNow.AddMinutes(5));

        var inUse = await Assert.ThrowsAsync<ArmoryRpcException>(() => alex.RenameFolderAsync(project, "Drive", "Powertrain", alexPc, Guid.NewGuid()));
        Assert.Equal((true, false, 500), (inUse.IsInUse, inUse.IsTransient, inUse.Status)); // PostgREST answers 55006 with 500
        var why = FolderRefusal.TryParse(inUse.Details)!;
        Assert.Equal((true, 1, "Shaft.SLDPRT"), (why.IsCheckedOut, why.Count, Assert.Single(why.Files).Name));
        Assert.True((await Assert.ThrowsAsync<ArmoryRpcException>(() => alex.DeleteFolderAsync(project, "Drive", alexPc, Guid.NewGuid()))).IsInUse);
        Assert.True(await maria.ReleaseLockAsync(shaft, mariaPc, Guid.NewGuid()));
        var exists = await Assert.ThrowsAsync<ArmoryRpcException>(() => alex.RenameFolderAsync(project, "Drive", "other", alexPc, Guid.NewGuid()));
        var target = FolderRefusal.TryParse(exists.Details)!;
        Assert.Equal((true, 1, "Plate.SLDPRT"), (target.IsTargetExists, target.Count, Assert.Single(target.Files).Name));

        Assert.Equal(2, await Twice(op => alex.RenameFolderAsync(project, "Drive", "Powertrain", alexPc, op)));
        Assert.Equal(2, await Twice(op => alex.DeleteFolderAsync(project, "Powertrain", alexPc, op)));
        Assert.Equal(0, await alex.DeleteFolderAsync(project, "Powertrain", alexPc, Guid.NewGuid()));
        Assert.True((await alex.ProjectFilesAsync(project)).Single(f => f.Id == gear).Deleted);
        Assert.Equal(gear, await Twice(op => maria.CreateFileAsync(project, "Drive", "gear.sldprt", mariaPc, op))); // the removed name comes back as the same file
        var revived = (await maria.ProjectFilesAsync(project)).Single(f => f.Id == gear);
        Assert.Equal((false, "Drive", "gear.sldprt"), (revived.Deleted, revived.Folder, revived.Name));
        var kinds = (await alex.ListChangesAsync(project, 0)).Select(c => c.Kind).ToArray();
        foreach (var kind in new[] { "project_renamed", "project_archived", "project_restored", "folder_renamed", "folder_deleted", "file_revived" })
            Assert.Single(kinds, k => k == kind);
    }

    [Fact]
    public void A_folder_refusal_reads_every_detail_shape_and_ignores_anything_else()
    {
        var names = FolderRefusal.TryParse("""{"names": ["Gear.SLDPRT", "Shaft.SLDPRT"], "total": 12, "reason": "checked_out"}""")!; // 0232
        Assert.Equal(("checked_out", (string?)null, 12, true, false), (names.Reason, names.Folder, names.Count, names.IsCheckedOut, names.IsTargetExists));
        Assert.Equal(new[] { "Gear.SLDPRT", "Shaft.SLDPRT" }, names.Files.Select(f => f.Name));
        Assert.All(names.Files, f => Assert.Null(f.HolderEmail));
        var id = Guid.NewGuid();
        var files = FolderRefusal.TryParse($$"""
            {"reason":"checked_out","folder":"Gearbox","count":3,"files":[{"file_id":"{{id}}","folder":"Gearbox","name":"Gear.SLDPRT",
             "holder_email":"maria.lopez@students.test","holder_name":"Maria Lopez","device_name":"LAB-PC-07","since":"2026-10-06T21:00:00+00:00"}, "Plain.SLDPRT"]}
            """)!; // lane A's design shape, with a bare name mixed in
        Assert.Equal(("Gearbox", 3), (files.Folder, files.Count));
        Assert.Equal(new FolderRefusalFile("Gear.SLDPRT", id, "Gearbox", "maria.lopez@students.test", "Maria Lopez", "LAB-PC-07", new DateTimeOffset(2026, 10, 6, 21, 0, 0, TimeSpan.Zero)), files.Files[0]);
        Assert.Equal(new FolderRefusalFile("Plain.SLDPRT"), files.Files[1]);
        var target = FolderRefusal.TryParse("""{"reason":"target_exists","names":["Plate.SLDPRT"]}""")!;
        Assert.Equal((true, 1), (target.IsTargetExists, target.Count)); // no total: the names count
        Assert.Equal("", FolderRefusal.TryParse("{}")!.Reason);
        foreach (var other in new[] { null, "", "   ", "not json", "[1,2]", "\"text\"", "42", "{\"reason\":" })
            Assert.Null(FolderRefusal.TryParse(other));
        Assert.True(new ArmoryRpcException(500, "55006", "in use", null, null).IsInUse);
        Assert.False(new ArmoryRpcException(409, "23505", "taken", null, null).IsInUse);
        Assert.False(new ArmoryRpcException(500, "55000", "immutable", null, null).IsInUse);
    }

    [Fact]
    public async Task A_call_rolled_back_by_a_deadlock_is_sent_again_with_the_same_body()
    {
        const string deadlock = """{"code":"40P01","message":"deadlock detected","details":null,"hint":null}""";
        const string serialization = """{"code":"40001","message":"could not serialize access","details":null,"hint":null}""";
        var handler = new CannedRpc([(500, deadlock), (500, serialization), (200, "3")]);
        var op = Guid.NewGuid();
        Assert.Equal(3, await CannedApi(handler).RenameFolderAsync(Guid.NewGuid(), "Drive", "Powertrain", Guid.NewGuid(), op));
        Assert.Equal(3, handler.Bodies.Count);
        Assert.Single(handler.Bodies.Distinct()); // the same operation id every time
        Assert.Contains(op.ToString(), handler.Bodies[0]);
        var always = new CannedRpc(Enumerable.Repeat((500, deadlock), 10));
        var error = await Assert.ThrowsAsync<ArmoryRpcException>(() => CannedApi(always).DeleteFolderAsync(Guid.NewGuid(), "Drive", Guid.NewGuid(), Guid.NewGuid()));
        Assert.Equal((true, false), (error.IsTransient, error.IsInUse));
        Assert.Equal(PostgrestClient.MaximumResends + 1, always.Bodies.Count);
        var refusal = new CannedRpc([(500, """{"code":"55006","message":"in use","details":"{\"reason\": \"checked_out\", \"names\": [\"Gear.SLDPRT\"], \"total\": 1}","hint":null}""")]);
        var refused = await Assert.ThrowsAsync<ArmoryRpcException>(() => CannedApi(refusal).DeleteFolderAsync(Guid.NewGuid(), "Drive", Guid.NewGuid(), Guid.NewGuid()));
        Assert.True(refused.IsInUse);
        Assert.Single(refusal.Bodies); // a refusal is an answer, never sent again
        Assert.Equal("Gear.SLDPRT", Assert.Single(FolderRefusal.TryParse(refused.Details)!.Files).Name);
        // A token that expires during a resend's wait is still refreshed once, and the call goes on.
        const string expired = """{"code":"PGRST301","message":"JWT expired","details":null,"hint":null}""";
        const string token = """{"access_token":"renewed","refresh_token":"refresh 2","expires_in":3600}""";
        var late = new CannedRpc([(500, deadlock), (401, expired), (200, token), (200, "5")]);
        Assert.Equal(5, await CannedApi(late).RenameFolderAsync(Guid.NewGuid(), "Drive", "Powertrain", Guid.NewGuid(), op));
        Assert.Equal(["/rest/v1/rpc/armory_rename_folder", "/rest/v1/rpc/armory_rename_folder", "/auth/v1/token", "/rest/v1/rpc/armory_rename_folder"], late.Paths);
        Assert.Equal(("access", "access", "renewed"), (late.Tokens[0], late.Tokens[1], late.Tokens[3]));
        Assert.Single(late.Bodies.Where(b => b.Contains(op.ToString())).Distinct()); // the same operation id every time
        // Only once per call: a second 401 after the refresh is an answer.
        var twice = new CannedRpc([(401, expired), (200, token), (401, expired)]);
        var unauthorized = await Assert.ThrowsAsync<ArmoryRpcException>(() => CannedApi(twice).DeleteFolderAsync(Guid.NewGuid(), "Drive", Guid.NewGuid(), Guid.NewGuid()));
        Assert.Equal((401, "PGRST301", 3), (unauthorized.Status, unauthorized.SqlState, twice.Paths.Count));
    }

    [PostgresFact]
    public async Task Blob_transfers_report_bytes_so_far_and_restart_from_zero_when_sent_again()
    {
        await using var env = await Env.StartAsync();
        var (_, mentor, _, _) = env.SignedIn("pina@ideabosco.test", admin: true);
        var project = await mentor.CreateProjectAsync("Robot", null, Guid.NewGuid());
        await mentor.AddMemberAsync(project, "alex.kim@students.test", MemberRole.Student, Guid.NewGuid());
        var (sessions, alex, blobs, _) = env.SignedIn("alex.kim@students.test");
        var bytes = RandomNumberGenerator.GetBytes(300_000);
        var hash = Hash(bytes);
        var up = new Recorder();
        Assert.True(await blobs.UploadAsync(project, hash, bytes.Length, () => new MemoryStream(bytes), progress: up));
        AssertRunningTotal(up.Values, bytes.Length);
        var skipped = new Recorder();
        Assert.False(await blobs.UploadAsync(project, hash, bytes.Length, () => throw new InvalidOperationException("must not reopen stored bytes"), progress: skipped));
        Assert.Empty(skipped.Values); // storage already had it: nothing was sent

        // A body sent a second time reports from 0 again and still arrives whole.
        using var storage = new HttpClient(new ResendingHandler(new FakeNetworkHandler(env.S3)));
        var resending = new BlobClient(env.Http, storage, env.Site.BaseUri, sessions);
        var other = RandomNumberGenerator.GetBytes(200_000);
        var again = new Recorder();
        Assert.True(await resending.UploadAsync(project, Hash(other), other.Length, () => new MemoryStream(other), progress: again));
        var values = again.Values;
        var restart = Array.IndexOf(values, 0L, 1);
        Assert.True(restart > 0, "The second sending did not report from 0.");
        Assert.Equal(other.Length, values[restart - 1]);
        AssertRunningTotal(values[restart..], other.Length);
        Assert.Contains(env.S3.Objects.Values, stored => stored.AsSpan().SequenceEqual(other));

        var device = await alex.RegisterDeviceAsync("laptop", Guid.NewGuid());
        var file = await alex.CreateFileAsync(project, "", "Gearbox.SLDASM", device, Guid.NewGuid());
        await alex.AcquireLockAsync(file, device, Guid.NewGuid());
        await alex.CommitVersionWithReleaseAsync(file, null, ContentObjectKey.FromHash(hash), hash, bytes.Length, device, Guid.NewGuid(), null);
        for (var attempt = 0; attempt < 2; attempt++) // every download starts again from 0
        {
            var down = new Recorder();
            var copy = new MemoryStream();
            await blobs.DownloadAsync(project, hash, bytes.Length, copy, progress: down);
            Assert.Equal(bytes, copy.ToArray());
            AssertRunningTotal(down.Values, bytes.Length);
        }
    }
}
