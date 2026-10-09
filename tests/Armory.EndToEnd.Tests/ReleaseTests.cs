using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json.Nodes;
using Armory.Agent.Engine.View;
using Armory.Client;
using Armory.Core;
using Armory.Core.Tests;
using Armory.TestSupport;
using Xunit.Abstractions;
using static Armory.EndToEnd.Tests.ScenarioTests;

namespace Armory.EndToEnd.Tests;

// The SolidWorks year, end to end with the real file reader (SolidWorksSavedReleaseReader) on
// synthetic containers built in code (SwContainer; never a CAD file): the gate's private
// draft and its words, the SolidWorks link's stamps, and B5 (files uploaded before the reader).
public sealed class ReleaseTests(ITestOutputHelper output)
{
    private const string Bracket = "Robot 2027/Drivetrain/Bracket.SLDPRT", Notes = "Robot 2027/Drivetrain/Notes.txt";

    // A part last saved in release `code` (18000 is 2025, 19000 is 2026), with its own bytes.
    private static byte[] Part(int code, int seed = 0)
        => SwContainer.Typical(code, [17000, code]).With("Contents/Config-0", [.. BitConverter.GetBytes(seed), .. new byte[64]]).Build();

    private static string HashOf(byte[] bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));

    private static async Task<Team> TeamWithReaderAsync()
    {
        var t = await TeamAsync();
        t.A.ReleaseReader = new SolidWorksSavedReleaseReader();
        t.A.Restart();
        return t;
    }

    private static Task<long> ReleaseRows(Team t, string hash, int? year)
        => year is { } y
            ? t.World.CountAsync("select count(*) from armory_version_releases r join armory_versions v on v.id=r.version_id where v.content_sha256=@h and r.saved_release=@y and r.release_checked", ("h", hash), ("y", (short)y))
            : t.World.CountAsync("select count(*) from armory_version_releases r join armory_versions v on v.id=r.version_id where v.content_sha256=@h and not r.release_checked", ("h", hash));

    private static Task<long> AnywhereOnServer(Team t, string hash)
        => t.World.CountAsync("select (select count(*) from armory_versions where content_sha256=@h) + (select count(*) from armory_side_versions where content_sha256=@h)", ("h", hash));

    private static string Refusal(Computer c, string path) => Assert.Single(c.NoticeItems, n => n.Card.Kind == NoticeKinds.CantSend && n.Item.Path == path).Item.Detail!;

    // A 2026 part is a private draft in both modes: nothing of it reaches the server, its bytes
    // stay as they are, and the words say what this computer can do. Saved again as 2025 it
    // uploads, checked; the 2026 save stays a private draft (never an "earlier save, kept").
    [PostgresFact]
    public async Task A_2026_part_stays_a_private_draft_and_the_words_say_what_this_computer_can_do()
    {
        await using var t = await TeamWithReaderAsync();
        var draft = Part(19000);
        t.A.Write(Plate, draft);
        await t.A.SyncAsync();
        // No SolidWorks link has said what this computer runs.
        Assert.Equal("Saved in SolidWorks 2026, and Robot 2027 uses SolidWorks 2025. It stays on this computer only until it is saved in SolidWorks 2025.", Refusal(t.A, Plate));
        Assert.Equal(0, t.World.Supabase.RpcCount("armory_create_file"));
        Assert.Equal(0, t.World.Supabase.RpcCount("armory_commit_version_with_release"));
        Assert.Equal(0, t.World.Supabase.RpcCount("armory_save_side_version_with_release"));
        Assert.Equal(0, await AnywhereOnServer(t, HashOf(draft)));
        Assert.Equal(draft, t.A.Read(Plate));
        Assert.False(t.A.Disk.IsReadOnly(Plate));

        // SolidWorks 2026 SP4 here, with the link able to save down: open it and click Save.
        t.A.Engine.SolidWorksAttached("34.4.1");
        await t.A.SyncAsync();
        Assert.Equal("Saved in SolidWorks 2026, and Robot 2027 uses SolidWorks 2025. Open it in SolidWorks 2026 and click Save: Armory saves it as 2025, then it uploads by itself.", Refusal(t.A, Plate));
        // SolidWorks 2026 before Service Pack 3 has no Save to Version.
        t.A.Engine.SolidWorksAttached("34.2.0");
        await t.A.SyncAsync();
        Assert.Equal("Saved in SolidWorks 2026, and Robot 2027 uses SolidWorks 2025. It stays on this computer only until it is saved in SolidWorks 2025. " +
            "Update SolidWorks 2026 to Service Pack 3 or newer so Armory can save it in 2025.", Refusal(t.A, Plate));
        // SolidWorks did not save down when asked (its license): it stays here, and says so.
        t.A.Engine.SolidWorksAttached("34.4.1", saveDownWorks: false);
        await t.A.SyncAsync();
        Assert.Equal("Saved in SolidWorks 2026, and Robot 2027 uses SolidWorks 2025. It stays on this computer only until it is saved in SolidWorks 2025. " +
            "SolidWorks on this computer couldn't save it in 2025. Ask a CAD lead or a mentor what to do.", Refusal(t.A, Plate));
        t.A.Engine.SolidWorksDetached();

        // Enforce refuses it the same way.
        Assert.True(await t.Mentor.Api.SetReleaseGateAsync(t.Project, ProjectReleaseGate.Enforce, Guid.NewGuid()));
        await t.A.SyncAsync();
        Assert.StartsWith("Saved in SolidWorks 2026, and Robot 2027 uses SolidWorks 2025.", Refusal(t.A, Plate));
        Assert.Equal(0, await AnywhereOnServer(t, HashOf(draft)));

        // Saved again in 2025: it uploads, its year checked.
        var good = Part(18000);
        t.A.Write(Plate, good);
        await t.A.SyncTimesAsync(2);
        var file = await t.FileId("Plate.SLDPRT");
        Assert.Equal(HashOf(good), await t.CurrentHash(file));
        Assert.Equal(1, await ReleaseRows(t, HashOf(good), 2025));
        Assert.DoesNotContain(t.A.NoticeItems, n => n.Card.Kind == NoticeKinds.CantSend);
        // The 2026 save was journaled before the 2025 one: it is kept here as a private draft
        // and never sent, not even as an earlier save.
        Assert.Equal(0, await AnywhereOnServer(t, HashOf(draft)));
        Assert.Equal(2025, t.A.Row(Plate).SavedRelease);
        Assert.False(t.A.Row(Plate).NewerThanPin);
    }

    // Warn takes a file whose year can't be read, marked "release not checked"; Enforce keeps it
    // here. The reader never guesses: a container whose two release fields disagree is unknown.
    [PostgresFact]
    public async Task An_unreadable_year_is_not_checked_in_warn_and_kept_here_in_enforce()
    {
        await using var t = await TeamWithReaderAsync();
        var mixed = SwContainer.Typical(18000, [17000, 19000]).Build();
        t.A.Write(Plate, mixed);
        await t.A.SyncAsync();
        Assert.Equal(1, await ReleaseRows(t, HashOf(mixed), null));
        Assert.Null(t.A.Row(Plate).SavedRelease);
        Assert.True(t.A.Row(Plate).ReleaseNotChecked);

        Assert.True(await t.Mentor.Api.SetReleaseGateAsync(t.Project, ProjectReleaseGate.Enforce, Guid.NewGuid()));
        var other = SwContainer.Typical(19000, [19000, 18000]).Build();
        t.A.Write(Bracket, other);
        await t.A.SyncAsync();
        Assert.Equal("The SolidWorks year it was saved in is unknown, and Robot 2027 only takes files whose year Armory can check. It stays on this computer.", Refusal(t.A, Bracket));
        Assert.Equal(0, await AnywhereOnServer(t, HashOf(other)));
    }

    // The SolidWorks link's stamp counts for exactly the bytes it names: it decides a file the
    // reader can't place, a stamp for other bytes changes nothing, a stamp that disagrees with
    // the reader makes the year unknown (recorded), and stamps are pruned 30 days after their
    // bytes reached the server.
    [PostgresFact]
    public async Task A_stamp_counts_for_exactly_its_bytes_and_is_pruned_after_its_commit()
    {
        await using var t = await TeamWithReaderAsync();
        Assert.True(await t.Mentor.Api.SetReleaseGateAsync(t.Project, ProjectReleaseGate.Enforce, Guid.NewGuid()));
        // A saved-down part whose two fields disagree (19000 in its history, 18000 in its names).
        var savedDown = SwContainer.Typical(18000, [17000, 19000]).Build();
        t.A.Write(Plate, savedDown);
        await t.A.SyncAsync();
        Assert.Contains("unknown", Refusal(t.A, Plate), StringComparison.Ordinal);

        var now = t.A.Clock.GetUtcNow();
        Assert.True(await RecordAsync(t.A, new ReleaseStamp(HashOf(Part(18000, 99)), 2025, "34.4.1", 2026, 2025, now))); // other bytes
        await t.A.SyncAsync();
        Assert.Contains("unknown", Refusal(t.A, Plate), StringComparison.Ordinal);
        Assert.False(await RecordAsync(t.A, new ReleaseStamp("not a hash", 2025, "34.4.1", 2026, 2025, now)));

        Assert.True(await RecordAsync(t.A, new ReleaseStamp(HashOf(savedDown).ToUpperInvariant(), 2025, "34.4.1", 2026, 2025, now)));
        Assert.Equal(2025, Stamps(t.A)[HashOf(savedDown)]?["stamp"]?["year"]?.GetValue<int>()); // durable before the next pass
        await t.A.SyncTimesAsync(2);
        var file = await t.FileId("Plate.SLDPRT");
        Assert.Equal(HashOf(savedDown), await t.CurrentHash(file));
        Assert.Equal(1, await ReleaseRows(t, HashOf(savedDown), 2025));
        Assert.NotNull(Stamps(t.A)[HashOf(savedDown)]?["committed"]);

        // A stamp that disagrees with the reader: unknown, kept here in Enforce, and recorded.
        var newer = Part(19000, 1);
        t.A.Write(Bracket, newer);
        Assert.True(await RecordAsync(t.A, new ReleaseStamp(HashOf(newer), 2025, "34.4.1", 2026, 2025, now)));
        await t.A.SyncAsync();
        Assert.Contains("unknown", Refusal(t.A, Bracket), StringComparison.Ordinal);
        Assert.Contains(t.A.Flight.Snapshot(), e => e.Name == "release disagreement" && e.Detail!.Contains("stamp 2025, reader 2026", StringComparison.Ordinal));
        Assert.Equal(0, await AnywhereOnServer(t, HashOf(newer)));

        // 30 days after its commit the committed stamp goes; the uncommitted one stays 90 days.
        t.A.Clock.Advance(TimeSpan.FromDays(31));
        await t.A.SyncAsync();
        Assert.False(Stamps(t.A).ContainsKey(HashOf(savedDown)));
        Assert.True(Stamps(t.A).ContainsKey(HashOf(newer)));
        t.A.Clock.Advance(TimeSpan.FromDays(60));
        await t.A.SyncAsync();
        Assert.False(Stamps(t.A).ContainsKey(HashOf(newer)));
    }

    // B5 (research section 6): files uploaded before the reader (Warn, "release not checked")
    // are read on a computer whose copy is the same bytes, once per content hash, even across a
    // restart. A 2026 one is marked on its row and detail, counted for its project, and listed
    // in one notice whose words fit what this computer is.
    [PostgresFact]
    public async Task Files_uploaded_before_the_reader_are_read_here_and_a_newer_one_is_flagged()
    {
        await using var t = await TeamAsync();
        // Alex's computer is 0.3.2: no reader, so everything goes up "release not checked".
        var newer = Part(19000);
        var good = Part(18000);
        t.A.Write(Plate, newer);
        t.A.Write(Bracket, good);
        t.A.Write(Notes, "notes");
        await t.A.SyncAsync();
        Assert.Equal(1, await ReleaseRows(t, HashOf(newer), null));
        Assert.Equal(1, await ReleaseRows(t, HashOf(good), null));
        Assert.Null(t.A.Row(Plate).SavedRelease);

        var reader = new CountingReader();
        t.B.ReleaseReader = reader;
        t.B.Restart();
        await t.B.SyncAsync();
        Assert.Equal((2026, true), (t.B.Row(Plate).SavedRelease, t.B.Row(Plate).NewerThanPin));
        Assert.Equal((2025, false), (t.B.Row(Bracket).SavedRelease, t.B.Row(Bracket).NewerThanPin));
        Assert.Equal((null, false), (t.B.Row(Notes).SavedRelease, t.B.Row(Notes).NewerThanPin));
        var project = t.B.Engine.View.Projects.Single();
        Assert.Equal((2025, 1), (project.PinnedRelease, project.NewerThanPinCount));
        var detail = (await t.B.Engine.GetFileDetailAsync(await t.FileId("Plate.SLDPRT")))!;
        Assert.Equal((2026, true, true), (detail.SavedRelease, detail.NewerThanPin, detail.ReleaseNotChecked));

        var card = t.B.Card(NoticeKinds.NewerRelease)!;
        Assert.Equal("Plate.SLDPRT in Robot 2027 was saved in SolidWorks 2026", card.Title);
        Assert.Equal(NoticeTones.Look, card.Tone); // no link says this computer can't fix it
        Assert.Equal(SyncStates.Attention, t.B.Engine.View.Sync.State);
        Assert.Equal("People on SolidWorks 2025 computers can look at these files but can't change them, and drawings won't open. Someone with SolidWorks 2026 can fix them: " +
            "check it out in Armory, open it in SolidWorks 2026, click Save so Armory saves it as SolidWorks 2025, then check it in.", card.Detail);
        Assert.Equal("Saved in SolidWorks 2026. Robot 2027 uses SolidWorks 2025.", Assert.Single(card.Items).Detail);

        // On a SolidWorks 2025 computer it is news, with the 2025 words.
        t.B.Engine.SolidWorksAttached("33.5.0");
        await t.B.SyncAsync();
        card = t.B.Card(NoticeKinds.NewerRelease)!;
        Assert.Equal(NoticeTones.Info, card.Tone);
        Assert.StartsWith("You can open parts and assemblies to look (SolidWorks 2025 SP5 shows them as a future version), but you can't change them here", card.Detail);
        // On a SolidWorks 2026 computer that saves down, it needs this student, with the steps.
        t.B.Engine.SolidWorksAttached("34.4.1");
        await t.B.SyncAsync();
        card = t.B.Card(NoticeKinds.NewerRelease)!;
        Assert.Equal(NoticeTones.Look, card.Tone);
        Assert.Equal("People on SolidWorks 2025 computers can't change these files. You can fix them here: 1. Check one out in Armory. 2. Open it in SolidWorks 2026. " +
            "3. Click Save. Armory saves it as SolidWorks 2025 for you. 4. Check it in.", card.Detail);

        // Each content hash was read once, and never again: not by later passes, not after a restart.
        Assert.Equal(2, reader.Reads);
        await t.B.SyncTimesAsync(3);
        t.B.Restart();
        await t.B.SyncTimesAsync(2);
        Assert.Equal(2, reader.Reads);
        Assert.Equal(2026, t.B.Row(Plate).SavedRelease);

        // The team's version keeps its year here after the copy here changes: it was read in
        // those very bytes (a save while checked out is a kept copy; the shared version stays).
        Assert.True((await t.B.CheckOutAsync(Bracket)).Ok);
        t.B.Save(Bracket, Part(18000, 5));
        await t.B.SyncAsync();
        Assert.Equal((2025, false), (t.B.Row(Bracket).SavedRelease, t.B.Row(Bracket).NewerThanPin));

        // Fixed (saved as 2025 and checked in): the notice goes, and the year is the server's.
        Assert.True((await t.A.CheckOutAsync(Plate)).Ok);
        var fixedPart = Part(18000, 7);
        t.A.Save(Plate, fixedPart);
        t.A.ReleaseReader = new SolidWorksSavedReleaseReader();
        t.A.Restart();
        Assert.True((await t.A.CheckInAsync(Plate)).Ok);
        Assert.Equal(1, await ReleaseRows(t, HashOf(fixedPart), 2025));
        await t.B.SyncAsync();
        Assert.Null(t.B.Card(NoticeKinds.NewerRelease));
        Assert.Equal((2025, false), (t.B.Row(Plate).SavedRelease, t.B.Row(Plate).NewerThanPin));
        Assert.Equal(0, t.B.Engine.View.Projects.Single().NewerThanPinCount);
    }

    // Only bytes that change are read: a pass over 1,500 synced SolidWorks files reads none of
    // them, and gets no slower with the reader than without it. The one-time B5 read of files
    // uploaded before the reader (a first pass, against the same pass on a computer without the
    // reader) is measured and printed.
    [PostgresFact]
    public async Task A_pass_over_1500_synced_files_reads_nothing_and_is_not_slower()
    {
        const int Files = 1500;
        await using var t = await TeamAsync();
        for (var i = 0; i < Files; i++) t.A.Write($"Robot 2027/Bulk/Part-{i:D4}.SLDPRT", Part(i % 3 == 0 ? 17000 : 18000, i));
        await t.A.SyncTimesAsync(2);
        Assert.Equal(Files, await t.World.CountAsync("select count(*) from armory_files where project_id=@p", ("p", t.Project)));

        // B, with the reader, receives them: one read per file, once (B5).
        var reader = new CountingReader();
        t.B.ReleaseReader = reader;
        t.B.Restart();
        var first = Stopwatch.StartNew();
        await t.B.SyncAsync();
        first.Stop();
        Assert.Equal(Files, reader.Reads);
        Assert.Equal(Files, t.B.Engine.View.Projects.Single().Folders.SelectMany(f => f.Files).Count(r => r.SavedRelease is 2024 or 2025));
        // The same first pass on a computer without the reader, for comparison.
        var c = await t.World.ComputerAsync("student C lab PC", Maria);
        var plain = Stopwatch.StartNew();
        await c.SyncAsync();
        plain.Stop();
        Assert.Equal(Files, Directory.EnumerateFiles(c.Disk.Full("Robot 2027/Bulk")).Count());

        async Task<double> SteadyMs(int passes)
        {
            await t.B.SyncAsync(); // warm
            var watch = Stopwatch.StartNew();
            for (var i = 0; i < passes; i++) await t.B.SyncAsync();
            return watch.Elapsed.TotalMilliseconds / passes;
        }
        var withReader = await SteadyMs(5);
        Assert.Equal(Files, reader.Reads); // a steady pass reads nothing
        t.B.ReleaseReader = null;
        t.B.Restart();
        var without = await SteadyMs(5);
        t.B.ReleaseReader = reader;
        t.B.Restart();
        var again = await SteadyMs(5);
        Assert.Equal(Files, reader.Reads); // after a restart too: the years are kept with the records

        // Uploads read only the files that changed, once each.
        for (var i = 0; i < 10; i++) t.B.Write($"Robot 2027/Bulk/New-{i}.SLDPRT", Part(18000, 10_000 + i));
        await t.B.SyncTimesAsync(2);
        Assert.Equal(Files + 10, reader.Reads);
        Assert.Equal(10, await t.World.CountAsync("select count(*) from armory_version_releases where saved_release=2025 and release_checked"));

        output.WriteLine(string.Create(CultureInfo.InvariantCulture,
            $"RELEASE files={Files} first_pass_with_b5_s={first.Elapsed.TotalSeconds:F1} first_pass_without_reader_s={plain.Elapsed.TotalSeconds:F1} b5_reads={Files} " +
            $"steady_pass_ms_with_reader={withReader:F0} steady_pass_ms_without={without:F0} steady_pass_ms_with_reader_after_restart={again:F0}"));
        // Generous: the claim is "not meaningfully slower"; the numbers above are the measurement.
        Assert.True(Math.Min(withReader, again) < without * 1.5 + 100, $"steady pass {withReader:F0} ms and {again:F0} ms with the reader, {without:F0} ms without");
    }

    private static Task<bool> RecordAsync(Computer c, ReleaseStamp stamp) => c.Engine.RecordReleaseStampAsync(stamp);

    // The stamps in this computer's saved state document, by hash.
    private static Dictionary<string, JsonNode?> Stamps(Computer c)
    {
        var document = JsonNode.Parse(c.State.Load()!)!;
        return (document["releaseStamps"]?.AsObject() ?? []).ToDictionary(p => p.Key, p => p.Value);
    }

    // The real reader, counting what it reads.
    private sealed class CountingReader : ISavedReleaseReader
    {
        private int reads;
        public int Reads => Volatile.Read(ref reads);
        public ValueTask<SolidWorksRelease?> ReadAsync(Stream content, CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref reads);
            return ValueTask.FromResult(SolidWorksFileRelease.Read(content, cancellationToken));
        }
    }
}
