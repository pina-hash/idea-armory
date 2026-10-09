using System.Text.Json;
using System.Text.Json.Nodes;

namespace Armory.Telemetry.Tests;

// The incident files (docs/agent/TELEMETRY.md, "The incident file"): what they hold, how big
// they may be, how many are kept, and when they are written.
public sealed class IncidentTests
{
    private static readonly IncidentHeader Header = new("0.3.0", "Windows 11 Education 10.0.22631", "LAB-PC-07", "alex.kim@students.test");

    private static (IncidentReporter Reporter, FlightRecorder Recorder, ManualClock Clock, List<string> Log) Reporter(string folder,
        Scrubber? scrubber = null, Func<CancellationToken, Task<JsonNode?>>? snapshot = null, ManualClock? clock = null, Func<JsonNode?>? quick = null)
    {
        clock ??= new ManualClock();
        var recorder = new FlightRecorder(clock: clock);
        var log = new List<string>();
        var lastFlight = new LastFlight(Path.Combine(folder, "last-flight.json.gz"), recorder, scrubber) { Schedule = work => work() };
        var reporter = new IncidentReporter(recorder, new IncidentStore(folder), new IncidentSources
        {
            Header = () => Header,
            Snapshot = snapshot ?? (_ => Task.FromResult<JsonNode?>(new JsonObject { ["online"] = true, ["filesByStatus"] = new JsonObject { ["synced"] = 40 } })),
            LogTail = n => Enumerable.Range(1, 400).Select(i => $"2026-10-07T18:00:00.000Z line {i}").ToList(),
            QuickSnapshot = quick,
            Scrubber = scrubber ?? Scrubber.None,
            Log = log.Add,
        }, clock, lastFlight)
        { Schedule = work => work().GetAwaiter().GetResult() };
        return (reporter, recorder, clock, log);
    }

    [Fact]
    public void An_incident_holds_every_documented_field_and_is_named_by_its_time_and_kind()
    {
        using var temp = new TempFolder();
        var (reporter, recorder, clock, log) = Reporter(temp.Path);
        recorder.PassStart("loop");
        recorder.Rpc("armory_project_files", 340, 200, null);
        recorder.FileFailed("Robot/Plate.SLDPRT", new IOException("in use"));
        clock.Advance(TimeSpan.FromSeconds(75));
        recorder.PassEnd("loop", false, 75_000, 12, 0, 0, 0);

        var file = Assert.Single(reporter.Store.All());
        Assert.Equal("20261007T180115000Z-slowPass.json.gz", Path.GetFileName(file));
        Assert.Contains("incident: saved 20261007T180115000Z-slowPass.json.gz (slowPass)", log);
        var incident = reporter.Store.Read(file);
        foreach (var field in new[] { "schemaVersion", "id", "createdAt", "kind", "summary", "appVersion", "osVersion", "deviceName", "email", "projectId",
                     "feedback", "feedbackId", "trigger", "flight", "snapshot", "log" })
            Assert.True(incident.ContainsKey(field), "missing " + field);
        Assert.Equal(1, incident["schemaVersion"]!.GetValue<int>());
        Assert.Equal("slowPass", incident["kind"]!.GetValue<string>());
        Assert.Equal("0.3.0", incident["appVersion"]!.GetValue<string>());
        Assert.Equal("LAB-PC-07", incident["deviceName"]!.GetValue<string>());
        Assert.Equal("passEnd", incident["trigger"]!["kind"]!.GetValue<string>());
        Assert.Equal(75_000, incident["trigger"]!["ms"]!.GetValue<long>());
        var events = incident["flight"]!["events"]!.AsArray();
        Assert.Equal(["passStart", "rpc", "fileFailed", "passEnd"], events.Select(e => e!["kind"]!.GetValue<string>()));
        Assert.Equal("in use", events[2]!["message"]!.GetValue<string>());
        Assert.Equal(40, incident["snapshot"]!["filesByStatus"]!["synced"]!.GetValue<int>());
        var lines = incident["log"]!.AsArray();
        Assert.Equal(300, lines.Count);
        Assert.EndsWith("line 400", lines[^1]!.GetValue<string>());
    }

    [Fact]
    public void A_second_incident_of_a_kind_within_ten_minutes_is_not_saved()
    {
        using var temp = new TempFolder();
        var (reporter, recorder, clock, _) = Reporter(temp.Path);
        recorder.ReadOnlyBroken("Robot/A.SLDPRT");
        clock.Advance(TimeSpan.FromMinutes(5));
        recorder.ReadOnlyBroken("Robot/B.SLDPRT");
        recorder.RepairedCheckout("Robot/C.SLDPRT");
        Assert.Equal(2, reporter.Store.All().Count);
        clock.Advance(TimeSpan.FromMinutes(6));
        recorder.ReadOnlyBroken("Robot/B.SLDPRT");
        Assert.Equal(3, reporter.Store.All().Count);
        // A new run reads the files it finds: the throttle holds across a restart.
        var later = new ManualClock();
        later.Advance(TimeSpan.FromMinutes(11));
        var again = Reporter(temp.Path, clock: later);
        again.Recorder.RepairedCheckout("Robot/C.SLDPRT"); // the last one was 6 minutes ago
        Assert.Equal(3, reporter.Store.All().Count);
        later.Advance(TimeSpan.FromMinutes(5));
        again.Recorder.RepairedCheckout("Robot/C.SLDPRT");
        Assert.Equal(4, reporter.Store.All().Count);
    }

    [Fact]
    public void An_incident_file_stays_under_200_KB_by_trimming_the_oldest_first()
    {
        using var temp = new TempFolder();
        var (reporter, recorder, _, _) = Reporter(temp.Path);
        var random = new Random(7);
        string Noise(int n) => new(Enumerable.Range(0, n).Select(_ => (char)random.Next(33, 126)).ToArray());
        for (var i = 0; i < FlightRecorder.DefaultCapacity; i++)
            recorder.Exception("file " + i, new InvalidDataException(Noise(200)), fatal: false);
        recorder.PassEnd("loop", true, 61_000, 0, 0, 0, 0);
        var file = Assert.Single(reporter.Store.All());
        Assert.InRange(new FileInfo(file).Length, 1, IncidentDocument.MaximumFileBytes);
        var incident = reporter.Store.Read(file);
        var flight = incident["flight"]!;
        Assert.True(flight["trimmed"]!.GetValue<long>() > 0);
        // What is kept is the newest: the trigger is the last event, and it is still there.
        Assert.Equal("passEnd", flight["events"]!.AsArray()[^1]!["kind"]!.GetValue<string>());
        Assert.Equal(4001, flight["recorded"]!.GetValue<long>());
    }

    [Fact]
    public void At_most_twenty_files_are_kept_and_files_the_site_has_go_first()
    {
        using var temp = new TempFolder();
        var store = new IncidentStore(temp.Path);
        var t0 = new DateTimeOffset(2026, 10, 7, 18, 0, 0, TimeSpan.Zero);
        var first = store.Save(t0, GlitchKinds.Crash, [1, 2, 3]);
        var second = store.Save(t0.AddMinutes(1), GlitchKinds.SlowPass, [1]);
        store.Mark(second, IncidentStore.SentMark);
        for (var i = 2; i < 21; i++) store.Save(t0.AddMinutes(i), GlitchKinds.SlowAction, [1]);
        var all = store.All();
        Assert.Equal(IncidentStore.MaximumFiles, all.Count);
        Assert.Contains(first, all); // the sent one went instead of the oldest unsent one
        Assert.DoesNotContain(all, f => IncidentStore.IsMarked(f, IncidentStore.SentMark));
        store.Save(t0.AddMinutes(30), GlitchKinds.SlowAction, [1]);
        Assert.DoesNotContain(first, store.All());
        // Two incidents of a kind in the same millisecond keep both.
        var same = store.Save(t0.AddMinutes(30), GlitchKinds.SlowAction, [2]);
        Assert.EndsWith("-slowAction-2.json.gz", same);
        Assert.True(IncidentStore.TryParseName(Path.GetFileName(same), out var at, out var kind));
        Assert.Equal((t0.AddMinutes(30), GlitchKinds.SlowAction), (at, kind));
        Assert.Equal(IncidentStore.MaximumFiles, store.All().Count);
        Assert.False(IncidentStore.TryParseName("upload-wait.json", out _, out _));
    }

    [Fact]
    public void Known_secrets_and_secret_patterns_are_scrubbed_from_every_string()
    {
        using var temp = new TempFolder();
        const string access = "access-token-1234567890", refresh = "refresh-token-abcdefghij";
        var scrubber = new Scrubber(text => text.Replace("Bearer x", "Bearer [redacted]", StringComparison.Ordinal), () => [access, refresh, null, "short"]);
        var (reporter, recorder, _, _) = Reporter(temp.Path, scrubber,
            _ => Task.FromResult<JsonNode?>(new JsonObject { ["nested"] = new JsonArray("token " + access) }));
        recorder.Note("session", "Bearer x and " + refresh);
        recorder.Exception("loop", new InvalidOperationException("failed with " + access), fatal: true);
        var incident = File.ReadAllBytes(Assert.Single(reporter.Store.All()));
        var text = JsonSerializer.Serialize(IncidentDocument.Read(incident));
        Assert.DoesNotContain(access, text);
        Assert.DoesNotContain(refresh, text);
        Assert.DoesNotContain("Bearer x", text);
        Assert.Contains("[redacted]", text);
        Assert.Equal("a short word", scrubber.Scrub("a short word"));
    }

    [Fact]
    public void The_last_flight_is_written_at_most_once_a_minute_and_the_next_start_builds_the_crash_from_it()
    {
        using var temp = new TempFolder();
        var (reporter, recorder, clock, _) = Reporter(temp.Path);
        var lastFlight = reporter.LastFlight!;
        recorder.PassStart("loop");
        recorder.Rpc("armory_project_files", 120, 200, null);
        recorder.PassPhase("scan", 30);
        clock.Advance(TimeSpan.FromSeconds(59));
        recorder.PassEnd("loop", false, 59_000, 0, 0, 0, 0);
        Assert.Equal(1, lastFlight.Writes);
        clock.Advance(TimeSpan.FromSeconds(2));
        recorder.PassStart("loop");
        recorder.Transfer("download", 2048, 80, true, 200, null);
        Assert.Equal(2, lastFlight.Writes);
        Assert.True(File.Exists(lastFlight.Path));

        // The process died here. The next start finds the unclean end in the log.
        var next = Reporter(temp.Path);
        var path = next.Reporter.PreviousRunEnded("2026-10-07T18:01:01.000Z pass: moving 3 of 9 files (loop)");
        Assert.NotNull(path);
        Assert.False(File.Exists(lastFlight.Path));
        var incident = next.Reporter.Store.Read(path);
        Assert.Equal("crash", incident["kind"]!.GetValue<string>());
        Assert.Equal("previousRunEnded", incident["trigger"]!["kind"]!.GetValue<string>());
        Assert.Contains("pass: moving 3 of 9 files", incident["summary"]!.GetValue<string>());
        var events = incident["flight"]!["events"]!.AsArray().Select(e => e!["kind"]!.GetValue<string>()).ToList();
        Assert.Equal(["passStart", "rpc", "passPhase", "passEnd", "passStart"], events);
        // Another unclean end at once is held back: a crash incident was saved a moment ago.
        Assert.Null(Reporter(temp.Path).Reporter.PreviousRunEnded("x"));
    }

    [Fact]
    public void A_crash_the_process_will_not_survive_is_saved_on_the_spot()
    {
        using var temp = new TempFolder();
        var (reporter, recorder, _, _) = Reporter(temp.Path);
        reporter.Schedule = _ => throw new InvalidOperationException("nothing may be scheduled for a crash in progress");
        recorder.Rpc("armory_my_projects", 20, 200, null);
        var path = reporter.CrashNow("unhandled exception", new StackOverflowException("deep"));
        Assert.NotNull(path);
        var incident = reporter.Store.Read(path);
        Assert.Equal("crash", incident["kind"]!.GetValue<string>());
        Assert.True(incident["trigger"]!["fatal"]!.GetValue<bool>());
        Assert.Equal("unhandled exception", incident["trigger"]!["where"]!.GetValue<string>());
        Assert.Null(reporter.CrashNow("unhandled exception", "a string thrown by native code"));
    }

    // An engine that does not answer within half a second (0.3.3: 5 of 15 notes lost their
    // snapshot to a busy engine) gives way to what the window showed, said so, at once.
    [Fact]
    public async Task A_late_snapshot_never_holds_the_incident_back()
    {
        using var temp = new TempFolder();
        var (reporter, recorder, _, _) = Reporter(temp.Path, snapshot: ct => Task.Delay(Timeout.Infinite, ct).ContinueWith<JsonNode?>(_ => null, TaskScheduler.Default),
            quick: () => new JsonObject { ["connection"] = "signedIn", ["checkedOutHere"] = 2 });
        var watch = System.Diagnostics.Stopwatch.StartNew();
        var path = await reporter.WriteAsync(GlitchRules.UserReport("bug", "it froze"), new IncidentFeedback("bug", "it froze"));
        watch.Stop();
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(2), $"the incident waited {watch.Elapsed.TotalSeconds:F1} s for the engine");
        var incident = reporter.Store.Read(path!);
        Assert.Contains("did not answer", incident["snapshot"]!["engineBusy"]!.GetValue<string>());
        Assert.Equal(2, incident["snapshot"]!["checkedOutHere"]!.GetValue<int>());
        Assert.Equal("it froze", incident["feedback"]!["body"]!.GetValue<string>());
        Assert.Equal("userReport", incident["kind"]!.GetValue<string>());
    }

    // Send feedback (0.3.3): a note keeps what was tried and the area beside its words, scrubbed
    // the same way (another person's address masked, a known secret removed). A note composed for
    // sending at once (with a picture, never saved) is exactly the document a saved note holds.
    [Fact]
    public async Task A_note_keeps_what_was_tried_and_the_area_and_scrubs_them_like_its_words()
    {
        using var temp = new TempFolder();
        const string token = "access-token-1234567890";
        var scrubber = new Scrubber(null, () => [token], () => "alex.kim@students.test");
        var (reporter, _, _, _) = Reporter(temp.Path, scrubber);
        const string body = "Maria (maria.lopez@students.test) had Gearbox.SLDASM; my address is alex.kim@students.test.";
        const string tried = "Asked sam.lee@students.test, then pasted " + token;
        const string area = "File details: Gearbox.SLDASM";
        var path = await reporter.SaveNoteAsync("praise", body, tried, area);
        var saved = reporter.Store.Read(path!);
        var words = saved["feedback"]!;
        Assert.Equal("praise", words["kind"]!.GetValue<string>());
        Assert.Equal("Maria ([address]) had Gearbox.SLDASM; my address is alex.kim@students.test.", words["body"]!.GetValue<string>());
        Assert.Equal("Asked [address], then pasted [redacted]", words["tried"]!.GetValue<string>());
        Assert.Equal(area, words["area"]!.GetValue<string>());
        Assert.True(saved[IncidentDocument.NoteOnlyField]!.GetValue<bool>());

        var composed = await reporter.ComposeNoteAsync("praise", body, tried, area);
        Assert.Single(reporter.Store.All()); // composed, never written
        foreach (var made in new[] { saved, composed }) { made.Remove("id"); made.Remove("createdAt"); }
        Assert.Equal(saved.ToJsonString(), composed.ToJsonString());

        // A note without them keeps only kind and body, as before.
        var plain = reporter.Store.Read((await reporter.SaveNoteAsync("idea", "Dark mode."))!);
        Assert.Equal(["kind", "body"], plain["feedback"]!.AsObject().Select(p => p.Key));
    }
}
