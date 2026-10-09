using System.Text.Json.Nodes;
using Armory.Client;
using Armory.Telemetry;
using Armory.TestSupport;
using Npgsql;

namespace Armory.EndToEnd.Tests;

// Send feedback, the same as the website's (0.3.3, idea-app 0235 "Send feedback, the same as the
// website's"), end to end: a student's computer with the window's FeedbackSender over its own
// network, the fake Supabase's PostgREST and Storage, and the stand-in that copies 0235's SQL.
public sealed class FeedbackTests
{
    private const string Alex = ScenarioTests.Alex;

    private static byte[] Png(int bytes, byte fill = 7)
    {
        var png = Enumerable.Repeat(fill, bytes).ToArray();
        new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A }.CopyTo(png, 0);
        return png;
    }

    private static FeedbackSender SenderFor(Computer c, SubmitLimiter limiter, List<string> log)
        => new(c.Api, new FeedbackScreenshots(c.Http, c.Sessions, c.Flight), c.Sessions, "0.3.3", limiter, c.Clock, line => { lock (log) log.Add(line); });

    private static async Task<object?[]> NoteAsync(World world, Guid id)
        => (await world.QueryAsync($"select kind, body, tried, area, screenshot_path, app_version, device_name from public.armory_app_feedback where id = '{id}'",
            r => Enumerable.Range(0, 7).Select(i => r.IsDBNull(i) ? null : r.GetValue(i)).ToArray())).Single();

    [PostgresFact]
    public async Task A_note_goes_with_its_picture_a_refused_picture_is_left_out_on_request_and_the_hours_limit_holds()
    {
        await using var world = await World.StartAsync();
        world.Latency = LatencyProfile.School;
        await ArmoryV3StandIn.ApplyFeedbackV2Async(world.Database);
        var alex = await world.ComputerAsync("student A laptop", Alex);
        var log = new List<string>();
        var limiter = new SubmitLimiter(clock: alex.Clock);
        var sender = SenderFor(alex, limiter, log);
        var context = new JsonObject { ["view"] = "files", ["online"] = true };

        // Upload, then the note naming it.
        var shot = Png(300_000);
        var sent = Assert.IsType<FeedbackResult.Sent>(await sender.SendAsync(new FeedbackNote("bug", "Check in spun on Gearbox.SLDASM.", "Restarted Armory.", "Files", shot, context)));
        var row = await NoteAsync(world, sent.Id);
        var key = (string)row[4]!;
        Assert.Equal(new object?[] { "bug", "Check in spun on Gearbox.SLDASM.", "Restarted Armory.", "Files", key, "0.3.3", alex.Sessions.Current!.DeviceName }, row);
        Assert.StartsWith(world.Supabase.UserIdFor(Alex) + "/", key);
        Assert.Equal(shot, world.Supabase.StoredObjects["armory-feedback-shots/" + key].Bytes);
        var mine = Assert.Single((await alex.Api.MyAppFeedbackAsync())!);
        Assert.Equal((sent.Id, true, AppFeedbackNote.New, "Files"), (mine.Id, mine.HasScreenshot, mine.Status, mine.Area));
        Assert.Contains(alex.Flight.Snapshot(), e => e.Kind == FlightKind.Transfer && e.Name == "screenshot" && e.Ok && e.Bytes == 300_000);

        // The site says the picture is not there (Storage lost it): the note is not sent, the
        // window offers it without the picture, and that goes.
        await using (var c = await world.Database.OpenAsync())
            await new NpgsqlCommand("""
                create function storage.test_lose() returns trigger language plpgsql security definer as $$ begin delete from storage.objects where id = new.id; return null; end $$;
                create trigger test_lose after insert on storage.objects for each row execute function storage.test_lose();
                """, c).ExecuteNonQueryAsync();
        var note = new FeedbackNote("idea", "Show who is online.", null, "Team", Png(1000, 9), context);
        var refused = Assert.IsType<FeedbackResult.ScreenshotRefused>(await sender.SendAsync(note));
        Assert.Equal(("not_found", true), (refused.Reason, refused.CanSendWithoutPicture));
        Assert.Equal("Your note wasn't sent: the screenshot didn't reach the website. You can send it without the picture.", refused.Message);
        Assert.Single(await world.QueryAsync("select id from public.armory_app_feedback", r => r.GetGuid(0)));
        var withoutPicture = Assert.IsType<FeedbackResult.Sent>(await sender.SendAsync(note with { Screenshot = null }));
        Assert.Equal(new object?[] { "idea", "Show who is online.", null, "Team", null }, (await NoteAsync(world, withoutPicture.Id))[..5]);

        // Storage refuses a picture over the bucket's limit (a lead lowered it): the same offer.
        await using (var c = await world.Database.OpenAsync())
            await new NpgsqlCommand("drop trigger test_lose on storage.objects; update storage.buckets set file_size_limit = 1000 where id = 'armory-feedback-shots'", c).ExecuteNonQueryAsync();
        var large = Assert.IsType<FeedbackResult.TooLarge>(await sender.SendAsync(new FeedbackNote("bug", "Big picture.", Screenshot: Png(5000, 3))));
        Assert.Equal(("screenshot", true), (large.Field, large.CanSendWithoutPicture));

        // Offline: nothing sent, said plainly.
        alex.Offline = true;
        Assert.IsType<FeedbackResult.Offline>(await sender.SendAsync(new FeedbackNote("bug", "Offline note.")));
        alex.Offline = false;

        // 20 notes an hour for the account, the five-argument form's included: the 21st is PT429,
        // and nothing is sent again (by the window or by the uploader) before retry_after_seconds.
        for (var i = 0; i < 18; i++) await alex.Api.SubmitAppFeedbackAsync("idea", "note " + i, "0.3.3", null, []);
        Assert.Equal(20, await world.CountAsync("select count(*) from public.armory_app_feedback"));
        var limited = Assert.IsType<FeedbackResult.RateLimited>(await sender.SendAsync(new FeedbackNote("praise", "One too many.", Screenshot: Png(100, 5))));
        Assert.InRange(limited.RetryAfter.TotalSeconds, 3000, 3600);
        Assert.StartsWith("You've sent a lot of feedback this hour. Try again in ", limited.Message);
        var calls = world.Supabase.RpcCount(ArmoryApi.SubmitFeedbackRpc);
        var uploads = world.Supabase.StorageUploads;
        Assert.IsType<FeedbackResult.RateLimited>(await sender.SendAsync(new FeedbackNote("praise", "Still too many.", Screenshot: Png(100, 6))));
        Assert.Equal((calls, uploads), (world.Supabase.RpcCount(ArmoryApi.SubmitFeedbackRpc), world.Supabase.StorageUploads));
        Assert.True(new IncidentUploader(alex.Api, new IncidentStore(Path.Combine(world.Temp, "incidents")), () => false, alex.Clock, limiter: limiter)
            .IsWaiting(ArmoryApi.SubmitFeedbackRpc));
        Assert.Equal(20, await world.CountAsync("select count(*) from public.armory_app_feedback"));
        Assert.DoesNotContain(log, l => l.Contains(alex.Sessions.Current!.AccessToken, StringComparison.Ordinal));
    }

    // Storage busy (a database timeout in a 400 body, as Storage answers it, or a 503): the window
    // hears "offline, try again", never "picture refused", and the same picture sent again goes
    // once, named by one note.
    [PostgresFact]
    public async Task A_busy_storage_is_tried_again_and_the_picture_goes_once()
    {
        await using var world = await World.StartAsync();
        world.Latency = LatencyProfile.School;
        await ArmoryV3StandIn.ApplyFeedbackV2Async(world.Database);
        var alex = await world.ComputerAsync("student A laptop", Alex);
        var sender = SenderFor(alex, new SubmitLimiter(clock: alex.Clock), []);
        var shot = Png(80_000, 4);
        var note = new FeedbackNote("bug", "Check in spun.", "Restarted Armory.", "Home > Robot 2027 > Drivetrain", shot);

        world.Supabase.FailStorage("544", "DatabaseTimeout", "Database timeout");
        var busy = await sender.SendAsync(note);
        Assert.IsType<FeedbackResult.Offline>(busy);
        Assert.False(busy.CanSendWithoutPicture);
        // The same, answered with Storage's real status (503 read only).
        world.Supabase.StorageRealStatus = true;
        world.Supabase.FailStorage("503", "DatabaseReadOnly", "Database is read only");
        Assert.IsType<FeedbackResult.Offline>(await sender.SendAsync(note));
        world.Supabase.StorageRealStatus = false;
        Assert.Empty(world.Supabase.StoredObjects);
        Assert.Equal(0, await world.CountAsync("select count(*) from public.armory_app_feedback"));

        var sent = Assert.IsType<FeedbackResult.Sent>(await sender.SendAsync(note));
        var stored = Assert.Single(world.Supabase.StoredObjects);
        Assert.Equal(shot, stored.Value.Bytes);
        Assert.Equal(3, world.Supabase.StorageUploads); // two answered busy, one stored
        var row = await NoteAsync(world, sent.Id);
        Assert.Equal(new object?[] { "bug", "Check in spun.", "Restarted Armory.", "Home > Robot 2027 > Drivetrain", stored.Value.Name }, row[..5]);
        Assert.Equal(1, await world.CountAsync("select count(*) from public.armory_app_feedback"));
    }

    // 0235's functions without its storage half (the migration's NOTICE path): the picture is
    // refused as not available, the window offers the note without it, and that goes through the
    // eight-argument form with every other field.
    [PostgresFact]
    public async Task A_site_with_0235_but_no_bucket_offers_and_sends_the_note_without_the_picture()
    {
        await using var world = await World.StartAsync();
        world.Latency = LatencyProfile.School;
        await ArmoryV3StandIn.ApplyFeedbackV2Async(world.Database);
        await ArmoryV3StandIn.DropFeedbackShotsBucketAsync(world.Database);
        var alex = await world.ComputerAsync("student A laptop", Alex);
        var sender = SenderFor(alex, new SubmitLimiter(clock: alex.Clock), []);
        var note = new FeedbackNote("praise", "Check in all is fast now.", "Checked in 200 files.", "Settings", Png(2000), new JsonObject { ["view"] = "settings" });

        var refused = Assert.IsType<FeedbackResult.ScreenshotRefused>(await sender.SendAsync(note));
        Assert.Equal(("not_available", true), (refused.Reason, refused.CanSendWithoutPicture));
        Assert.Equal("Your note wasn't sent: the website isn't ready for screenshots yet. You can send it without the picture.", refused.Message);
        Assert.Equal(0, await world.CountAsync("select count(*) from public.armory_app_feedback"));

        var sent = Assert.IsType<FeedbackResult.Sent>(await sender.SendAsync(note with { Screenshot = null }));
        Assert.Equal(new object?[] { "praise", "Check in all is fast now.", "Checked in 200 files.", "Settings", null }, (await NoteAsync(world, sent.Id))[..5]);
        Assert.False(sender.NewFieldsMissing);
        Assert.Empty(world.Supabase.StoredObjects);
    }

    // "Your feedback": each status in plain words, newest first; a note the admin marked spam reads
    // closed, never spam. There are no replies, so there is nothing else to show.
    [PostgresFact]
    public async Task Your_feedback_shows_each_status_and_never_spam()
    {
        await using var world = await World.StartAsync();
        world.Latency = LatencyProfile.School;
        await ArmoryV3StandIn.ApplyFeedbackV2Async(world.Database);
        var alex = await world.ComputerAsync("student A laptop", Alex);
        var sender = SenderFor(alex, new SubmitLimiter(clock: alex.Clock), []);
        List<Guid> ids = [];
        foreach (var (kind, words) in new[] { ("bug", "Spins."), ("idea", "Dark mode."), ("praise", "Fast now."), ("other", "Thanks.") })
        {
            ids.Add(Assert.IsType<FeedbackResult.Sent>(await sender.SendAsync(new FeedbackNote(kind, words, Area: "Home"))).Id);
            await Task.Delay(15); // created_at orders them
        }
        await ArmoryV3StandIn.SetFeedbackStatusAsync(world.Database, ids[0], "seen");
        await ArmoryV3StandIn.SetFeedbackStatusAsync(world.Database, ids[1], "resolved");
        await ArmoryV3StandIn.SetFeedbackStatusAsync(world.Database, ids[2], "spam");

        var notes = (await alex.Api.MyAppFeedbackAsync())!;
        Assert.Equal(Enumerable.Reverse(ids), notes.Select(n => n.Id));
        Assert.Equal(["other", "praise", "idea", "bug"], notes.Select(n => n.Kind));
        Assert.Equal([AppFeedbackNote.New, AppFeedbackNote.Closed, AppFeedbackNote.Resolved, AppFeedbackNote.Seen], notes.Select(n => n.Status));
        Assert.Equal(["Not read yet", "Closed", "Done", "Read by the IDEA team"], notes.Select(n => AppFeedbackNote.StatusWords(n.Status)));
        Assert.All(notes, n => Assert.Equal(("Home", alex.Sessions.Current!.DeviceName), (n.Area, n.DeviceName)));
        Assert.All(notes.Skip(1), n => Assert.NotNull(n.ReviewedAt));
    }

    // A site before 0235: the five-argument form takes the note without the new fields (praise
    // goes as other), there is no bucket for the picture (offered without it), and "Your
    // feedback" is not available.
    [PostgresFact]
    public async Task A_site_before_0235_takes_the_note_without_the_new_fields()
    {
        await using var world = await World.StartAsync();
        await ArmoryV3StandIn.ApplyReportsAsync(world.Database);
        var alex = await world.ComputerAsync("student A laptop", Alex);
        var log = new List<string>();
        var sender = SenderFor(alex, new SubmitLimiter(clock: alex.Clock), log);

        var praise = Assert.IsType<FeedbackResult.SentWithoutNewFields>(await sender.SendAsync(new FeedbackNote("praise", "Love the new check in.", "Nothing.", "Files")));
        Assert.Equal(("other", true, false), (praise.Kind, praise.KindChanged, praise.LeftOutPicture));
        Assert.Equal(new object?[] { "other", "Love the new check in." }, (await world.QueryAsync($"select kind, body from public.armory_app_feedback where id = '{praise.Id}'",
            r => new object?[] { r.GetString(0), r.GetString(1) })).Single());
        // Only the picture can be lost: what was tried, the area and the kind asked for are in the
        // context the admin's list shows whole.
        var kept = JsonNode.Parse((await world.QueryAsync($"select context::text from public.armory_app_feedback where id = '{praise.Id}'", r => r.GetString(0))).Single())!;
        Assert.Equal(("praise", "Nothing.", "Files"), (kept["askedKind"]!.GetValue<string>(), kept["tried"]!.GetValue<string>(), kept["area"]!.GetValue<string>()));
        Assert.Contains(log, l => l.Contains("no eight-argument armory_submit_app_feedback", StringComparison.Ordinal));
        Assert.Null(await alex.Api.MyAppFeedbackAsync());

        // Within the hour the wide form is not asked again, and a picture is not uploaded at all.
        var wide = world.Supabase.RpcCount(ArmoryApi.SubmitFeedbackRpc);
        var bug = Assert.IsType<FeedbackResult.SentWithoutNewFields>(await sender.SendAsync(new FeedbackNote("bug", "Spins.", Screenshot: Png(200))));
        Assert.Equal(("bug", false, true), (bug.Kind, bug.KindChanged, bug.LeftOutPicture));
        Assert.Equal(wide + 1, world.Supabase.RpcCount(ArmoryApi.SubmitFeedbackRpc));
        Assert.Equal(0, world.Supabase.StorageUploads);

        // An hour later the wide form is asked for again; the site still lacks the bucket, so the
        // picture is refused before any note, and the note goes without it.
        alex.Clock.Advance(FeedbackSender.WideMissingRetry);
        var noBucket = Assert.IsType<FeedbackResult.ScreenshotRefused>(await sender.SendAsync(new FeedbackNote("bug", "Again.", Screenshot: Png(200))));
        Assert.Equal("not_available", noBucket.Reason);
        Assert.Equal("Your note wasn't sent: the website isn't ready for screenshots yet. You can send it without the picture.", noBucket.Message);
        Assert.IsType<FeedbackResult.SentWithoutNewFields>(await sender.SendAsync(new FeedbackNote("bug", "Again.")));
        Assert.Equal(3, await world.CountAsync("select count(*) from public.armory_app_feedback"));
    }
}
