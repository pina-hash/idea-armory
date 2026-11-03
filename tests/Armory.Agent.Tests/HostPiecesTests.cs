using Armory.Agent.Engine.View;

namespace Armory.Agent.Tests;

// Host rules that need no window, so they run on every host.
public sealed class HostPiecesTests
{
    // armory_heartbeat refuses an app_version longer than 40 characters (22023), while feedback
    // and incidents take 64 (idea-app ARMORY.md, "The two version limits do not match"). The
    // version every call carries (AgentPaths.Version: the informational version before any "+",
    // which Armory.Agent.csproj's <Version> sets) stays within 40, so no heartbeat is ever refused
    // for it and nothing has to be cut.
    [Fact]
    public void The_app_version_fits_the_heartbeats_40_characters()
    {
        Assert.InRange(AgentPaths.Version.Length, 1, Armory.Client.TeamHeartbeat.MaximumVersionCharacters);
        Assert.Equal(AgentPaths.Version, Armory.Client.TeamHeartbeat.VersionFor(AgentPaths.Version));
        Assert.Equal(AgentPaths.Version, Armory.Client.FeedbackSender.FitVersion(AgentPaths.Version));
        Assert.Equal(40, Armory.Client.TeamHeartbeat.MaximumVersionCharacters);
        var root = Repo.FindRoot();
        Assert.NotNull(root);
        var project = System.Xml.Linq.XDocument.Load(Path.Combine(root, "src", "Armory.Agent", "Armory.Agent.csproj"));
        var versions = project.Descendants().Where(e => e.Name.LocalName is "Version" or "VersionPrefix" or "VersionSuffix" or "InformationalVersion")
            .Select(e => e.Value.Trim()).ToList();
        Assert.NotEmpty(versions);
        Assert.All(versions, v => Assert.InRange(v.Length, 1, Armory.Client.TeamHeartbeat.MaximumVersionCharacters));
        Assert.StartsWith(project.Descendants().First(e => e.Name.LocalName == "Version").Value.Trim(), AgentPaths.Version);
    }

    [Fact]
    public void Every_script_and_style_the_page_loads_carries_the_version()
    {
        var root = Repo.FindRoot();
        Assert.NotNull(root);
        var html = File.ReadAllText(Path.Combine(root, "src", "Armory.Agent", "wwwroot", "index.html"));
        var versioned = PageAssets.Versioned(html, "0.2.0");
        var references = System.Text.RegularExpressions.Regex.Matches(versioned, "(?:src|href)=\"(?<url>[^\"]+)\"")
            .Select(m => m.Groups["url"].Value).Where(url => url.Contains(".js", StringComparison.Ordinal) || url.Contains(".css", StringComparison.Ordinal)).ToArray();
        Assert.NotEmpty(references);
        Assert.All(references, url => Assert.EndsWith("?v=0.2.0", url));
        Assert.Contains("bridge.js?v=0.2.0", versioned);
        Assert.Contains("app.js?v=0.2.0", versioned);
        Assert.Equal("https://armory.local/index.html?v=0.2.0", PageAssets.StartPage("0.2.0").AbsoluteUri);
    }

    [Fact]
    public void Only_relative_unversioned_scripts_and_styles_are_versioned()
    {
        const string html = """
            <link rel="stylesheet" href="app.css" /><script src="bridge.js"></script><script src="sub/app.js"></script>
            <script src="https://example.com/x.js"></script><script src="//example.com/y.js"></script>
            <script src="done.js?v=1"></script><a href="#top">top</a><img src="data:image/png;base64,AAAA" /><a href="page.html">p</a>
            """;
        var versioned = PageAssets.Versioned(html, "1.2.3");
        Assert.Contains("href=\"app.css?v=1.2.3\"", versioned);
        Assert.Contains("src=\"bridge.js?v=1.2.3\"", versioned);
        Assert.Contains("src=\"sub/app.js?v=1.2.3\"", versioned);
        Assert.Contains("src=\"https://example.com/x.js\"", versioned);
        Assert.Contains("src=\"//example.com/y.js\"", versioned);
        Assert.Contains("src=\"done.js?v=1\"", versioned);
        Assert.Contains("href=\"#top\"", versioned);
        Assert.Contains("src=\"data:image/png;base64,AAAA\"", versioned);
        Assert.Contains("href=\"page.html\"", versioned);
    }

    [Fact]
    public void A_check_out_balloon_asks_once_per_opened_file_and_only_while_the_window_is_hidden()
    {
        var prompts = new CheckOutPrompts();
        Assert.True(prompts.ShouldOffer("Robot/Plate.SLDPRT", windowShowing: false));
        Assert.False(prompts.ShouldOffer("Robot/Plate.SLDPRT", windowShowing: false));
        Assert.False(prompts.ShouldOffer("robot/plate.sldprt", windowShowing: false));
        // The window's own card asked, so the tray does not ask again for this opening.
        Assert.False(prompts.ShouldOffer("Robot/Gear.SLDPRT", windowShowing: true));
        Assert.False(prompts.ShouldOffer("Robot/Gear.SLDPRT", windowShowing: false));
        Assert.False(prompts.ShouldOffer("", windowShowing: false));
        // Closed and opened again: it may ask again.
        prompts.KeepOnly(["Robot/Gear.SLDPRT"]);
        Assert.True(prompts.ShouldOffer("Robot/Plate.SLDPRT", windowShowing: false));
        Assert.False(prompts.ShouldOffer("Robot/Gear.SLDPRT", windowShowing: false));
    }

    [Fact]
    public void A_check_out_balloon_uses_the_plain_words_and_fits_the_tray()
    {
        var (title, text) = CheckOutPrompts.Words("Plate.SLDPRT", null);
        Assert.Equal("Check out Plate.SLDPRT to edit it?", title);
        Assert.Contains("check it out", text);
        var (taken, why) = CheckOutPrompts.Words("Plate.SLDPRT", "Maria Lopez on LAB-PC-07");
        Assert.Equal("Plate.SLDPRT is checked out by Maria Lopez on LAB-PC-07", taken);
        Assert.Contains("can't save changes", why);
        var (longTitle, _) = CheckOutPrompts.Words(new string('p', 80) + ".SLDPRT", null);
        Assert.Equal(CheckOutPrompts.TitleLimit, longTitle.Length);
        foreach (var word in new[] { "sync", "lock", "vault", "conflict", "hash", "journal" })
            Assert.DoesNotContain(word, (title + text + taken + why).ToLowerInvariant());
    }

    // 0.3.3: a run that had begun to quit (it logged "quitting", or that Windows was ending the
    // session) and was ended before "stopped" was stopped on purpose: never a crash incident, and
    // the next start says so in the log instead.
    [Fact]
    public void A_run_ended_while_quitting_is_not_a_crash()
    {
        string[] quit = ["2026-10-08T22:00:00.000Z started 0.3.1", "2026-10-08T22:00:32.619Z pass: ended after 28000 ms (loop), 0 downloaded, 0 uploaded, 0 kept copies, 0 refused",
            "2026-10-08T22:01:06.398Z quitting"];
        Assert.Null(AgentLog.UncleanEnd(quit));
        var end = AgentLog.EndOf(quit)!;
        Assert.Equal((quit[1], true, quit[2], false), (end.LastLine, end.Quitting, end.VeryLastLine, end.Asleep));
        Assert.Equal(DateTimeOffset.Parse("2026-10-08T22:01:06.398Z", System.Globalization.CultureInfo.InvariantCulture), end.LastAt);
        string[] sessionEnded = ["2026-10-08T22:00:00.000Z started 0.3.3", "2026-10-08T22:00:10.000Z " + AgentLog.SessionEndingLine + " (SystemShutdown)"];
        Assert.Null(AgentLog.UncleanEnd(sessionEnded));
        Assert.True(AgentLog.EndOf(sessionEnded)!.Quitting);
        // A crash after a run that quit cleanly is still a crash.
        Assert.Equal("2026-10-08T22:05:00.000Z pass: moving 3 of 3 files (loop)", AgentLog.UncleanEnd([.. quit, "2026-10-08T22:01:07.000Z stopped",
            "2026-10-08T22:04:00.000Z started 0.3.3", "2026-10-08T22:05:00.000Z pass: moving 3 of 3 files (loop)"]));

        var folder = Path.Combine(Path.GetTempPath(), "armory-log-" + Guid.NewGuid().ToString("N"));
        try
        {
            var first = new AgentLog(Path.Combine(folder, "agent.log"), Path.Combine(folder, "crash.log"));
            first.Info("started 0.3.3");
            first.Info(AgentLog.SessionEndingLine + " (Logoff)");
            var second = new AgentLog(Path.Combine(folder, "agent.log"), Path.Combine(folder, "crash.log"));
            Assert.Null(second.PreviousRunEndedUnexpectedly());
            Assert.True(second.PreviousRun()!.Quitting);
        }
        finally { Directory.Delete(folder, true); }
    }

    // 0.5.4 (incident 0269c86e, 2026-11-03): 0.5.1 in the tray had downloaded 0.5.3; a student
    // opened Armory, Velopack applied the update, which closed the running 0.5.1, and 0.5.3
    // reported its end as a crash ("Windows has no record of why it ended"). The new version's
    // install step now writes that it was put in place, and the next start reads it: an update,
    // not a crash. Lines after it are the installer's, never the run's.
    [Fact]
    public void A_run_closed_by_an_update_is_not_a_crash()
    {
        string[] updated = [
            "2026-11-03T17:34:47.786Z started 0.5.1",
            "2026-11-03T17:35:08.098Z pass: ended after 16939 ms (loop), 0 downloaded, 0 uploaded, 0 kept copies, 0 refused",
            "2026-11-03T17:35:55.054Z updates: " + Updates.ReadyWords("0.5.3"),
            "2026-11-03T18:17:08.379Z solidworks: a new document (not saved yet) changed",
            "2026-11-03T18:17:15.000Z " + Updates.UpdatedLineFor("0.5.4"),
            "2026-11-03T18:17:18.967Z updates: pointed the taskbar pin IDEA Armory.lnk at C:\\Users\\IDEA\\AppData\\Local\\IDEAArmory\\current\\IdeaArmory.exe"];
        var end = AgentLog.EndOf(updated)!;
        Assert.False(end.Quitting);
        Assert.Equal((updated[1], updated[3]), (end.LastLine, end.VeryLastLine));
        Assert.Equal(DateTimeOffset.Parse("2026-11-03T18:17:15Z", System.Globalization.CultureInfo.InvariantCulture), end.UpdatedAt);
        Assert.Equal(DateTimeOffset.Parse("2026-11-03T18:17:08.379Z", System.Globalization.CultureInfo.InvariantCulture), end.LastAt);
        var verdict = Armory.Telemetry.RunEndCause.Explain(end.LastAt, end.Asleep, [], null, end.UpdatedAt);
        Assert.False(verdict.Crash);
        Assert.Equal("Armory was updated at 2026-11-03 18:17 UTC, and the update closed the older copy that was still running.", verdict.Words);

        // Without the line, the same end is still a crash; and an update line from an earlier run
        // (before this run's "started") says nothing about this one.
        Assert.Null(AgentLog.EndOf(updated[..4])!.UpdatedAt);
        Assert.True(Armory.Telemetry.RunEndCause.Explain(end.LastAt, false, [], null, AgentLog.EndOf(updated[..4])!.UpdatedAt).Crash);
        Assert.Null(AgentLog.EndOf([updated[4], .. updated[..4]])!.UpdatedAt);

        // Through the files, as the install step and the next start write and read them.
        var folder = Path.Combine(Path.GetTempPath(), "armory-log-" + Guid.NewGuid().ToString("N"));
        try
        {
            var log = Path.Combine(folder, "agent.log");
            var crash = Path.Combine(folder, "crash.log");
            var older = new AgentLog(log, crash);
            older.Info("started 0.5.3");
            older.Info("pass: ended after 900 ms (loop), 0 downloaded, 0 uploaded, 0 kept copies, 0 refused");
            new AgentLog(log, crash).Info(Updates.UpdatedLineFor("0.5.4"));
            var next = new AgentLog(log, crash).PreviousRun()!;
            Assert.NotNull(next.UpdatedAt);
            Assert.False(next.Quitting);
            Assert.EndsWith("pass: ended after 900 ms (loop), 0 downloaded, 0 uploaded, 0 kept copies, 0 refused", next.VeryLastLine, StringComparison.Ordinal);
        }
        finally { Directory.Delete(folder, true); }
    }

    // 0.5.4: the first thumbnail lines of a start go with every incident and note, however far
    // agent.log's last lines have moved past them (0.5.3's never reached the site), under a
    // heading, within the same 300 lines; a tail that still holds them is sent as it is.
    [Fact]
    public void The_first_thumbnail_lines_go_with_every_report()
    {
        var folder = Path.Combine(Path.GetTempPath(), "armory-log-" + Guid.NewGuid().ToString("N"));
        try
        {
            var log = new AgentLog(Path.Combine(folder, "agent.log"), Path.Combine(folder, "crash.log"));
            log.Info("started 0.5.4");
            log.Info("thumbnail: the window asked Armory for its first picture (.SLDPRT)");
            log.Error("could not serve a thumbnail", new InvalidOperationException("gone"));
            for (var i = 0; i < AgentLog.KeptLines + 5; i++) log.Info($"thumbnail Part{i}.SLDPRT: Windows gave no picture (GetImage 0x8004B200)");
            Assert.Equal(log.Tail(300), log.TailWithKept(300));
            for (var i = 0; i < 400; i++) log.Info($"pass: ended after {i} ms (loop), 0 downloaded, 0 uploaded, 0 kept copies, 0 refused");

            Assert.DoesNotContain(log.Tail(300), l => l.Contains("thumbnail", StringComparison.Ordinal));
            var sent = log.TailWithKept(300).ToArray();
            Assert.Equal(300, sent.Length);
            Assert.Equal(AgentLog.KeptHeading, sent[0]);
            Assert.EndsWith("thumbnail: the window asked Armory for its first picture (.SLDPRT)", sent[1], StringComparison.Ordinal);
            Assert.EndsWith("could not serve a thumbnail: InvalidOperationException: gone", sent[2], StringComparison.Ordinal);
            Assert.Equal(AgentLog.KeptLines, sent.Count(l => l.Contains("thumbnail", StringComparison.Ordinal)));
            Assert.EndsWith("pass: ended after 399 ms (loop), 0 downloaded, 0 uploaded, 0 kept copies, 0 refused", sent[^1], StringComparison.Ordinal);
            Assert.Equal(log.Tail(300).ToArray()[^(300 - 1 - AgentLog.KeptLines)..], sent[(1 + AgentLog.KeptLines)..]);
        }
        finally { Directory.Delete(folder, true); }
        Assert.False(AgentLog.IsKept("pass: thumbnail"));
        Assert.Equal(["a"], AgentLog.WithKept(["a"], ["b"], 2));
    }

    // 0.4.0: a run whose last word about the power was "the computer is going to sleep" ended
    // while the computer slept (IDEA-06, 2026-10-20T23:13Z): the next start knows it.
    [Fact]
    public void A_run_that_ended_asleep_says_so()
    {
        string[] slept = ["2026-10-20T22:00:00.000Z started 0.3.3", "2026-10-20T23:10:00.000Z pass: ended after 15000 ms (loop), 0 downloaded, 0 uploaded, 0 kept copies, 0 refused",
            "2026-10-20T23:13:39.846Z " + AgentLog.SleepLine];
        var end = AgentLog.EndOf(slept)!;
        Assert.True(end.Asleep);
        Assert.False(end.Quitting);
        Assert.Equal(slept[2], end.VeryLastLine);
        string[] woke = [.. slept, "2026-10-21T06:00:00.000Z " + AgentLog.WakeLine, "2026-10-21T06:00:05.000Z pass: moving 1 of 3 files (loop)"];
        Assert.False(AgentLog.EndOf(woke)!.Asleep);
    }

    // v0.2.1: a run that ended without "stopped" (a stack overflow or a native crash, which no
    // handler can log) is named by the next start, with its last pass line.
    [Fact]
    public void A_run_that_died_without_a_word_is_named_by_the_next_start()
    {
        Assert.Null(AgentLog.UncleanEnd([]));
        Assert.Null(AgentLog.UncleanEnd(["2026-10-07T20:52:00.000Z started 0.2.1", "2026-10-07T20:53:00.000Z stopped"]));
        Assert.Equal("2026-10-07T20:52:30.000Z pass: moving 2,014 of 2,014 files (loop)", AgentLog.UncleanEnd([
            "2026-10-07T20:51:00.000Z started 0.2.1", "2026-10-07T20:51:30.000Z stopped",
            "2026-10-07T20:52:00.000Z started 0.2.1", "2026-10-07T20:52:01.000Z vault runtime started at C:\\IDEA\\Armory",
            "2026-10-07T20:52:30.000Z pass: moving 2,014 of 2,014 files (loop)", "2026-10-07T20:52:31.000Z window: open done"]));
        Assert.Equal("2026-10-07T20:52:01.000Z session loaded for maria@school.org", AgentLog.UncleanEnd([
            "2026-10-07T20:52:00.000Z started 0.2.1", "2026-10-07T20:52:01.000Z session loaded for maria@school.org"]));

        var folder = Path.Combine(Path.GetTempPath(), "armory-log-" + Guid.NewGuid().ToString("N"));
        try
        {
            var first = new AgentLog(Path.Combine(folder, "agent.log"), Path.Combine(folder, "crash.log"));
            Assert.Null(first.PreviousRunEndedUnexpectedly());
            first.Info("started 0.2.1");
            first.Info("pass: still going after 60 s, 400 downloaded, 0 uploaded so far (loop)");
            var second = new AgentLog(Path.Combine(folder, "agent.log"), Path.Combine(folder, "crash.log"));
            Assert.EndsWith("pass: still going after 60 s, 400 downloaded, 0 uploaded so far (loop)", second.PreviousRunEndedUnexpectedly());
            second.Info("started 0.2.1");
            second.Info("stopped");
            Assert.Null(new AgentLog(Path.Combine(folder, "agent.log"), Path.Combine(folder, "crash.log")).PreviousRunEndedUnexpectedly());
        }
        finally { Directory.Delete(folder, true); }
    }

    // The crash handler never throws, even for an error that can't describe itself.
    [Fact]
    public void A_crash_line_is_written_even_when_the_error_cannot_describe_itself()
    {
        var folder = Path.Combine(Path.GetTempPath(), "armory-log-" + Guid.NewGuid().ToString("N"));
        try
        {
            var log = new AgentLog(Path.Combine(folder, "agent.log"), Path.Combine(folder, "crash.log"));
            log.Crash("engine", new Unprintable());
            log.Crash("engine", null);
            var lines = File.ReadAllLines(Path.Combine(folder, "agent.log"));
            Assert.Equal(2, lines.Length);
            Assert.Contains("crash in engine (details in crash.log): Unprintable", lines[0]);
            Assert.Contains("could not be written", File.ReadAllText(Path.Combine(folder, "crash.log")));
        }
        finally { Directory.Delete(folder, true); }
    }

    private sealed class Unprintable : Exception
    {
        public override string ToString() => throw new InvalidOperationException("no words");
    }

    // 0.3.3, N1: every control says what it does on hover. The tray's items (each of the words an
    // item can show) and the WebView2-missing button each have one plain sentence, with none of
    // the words a student never reads (check-ui's copy rule) and no em dash.
    [Fact]
    public void Every_tray_item_and_the_webview2_button_say_what_they_do()
    {
        var jargon = new System.Text.RegularExpressions.Regex(@"\b(lock(s|ed|ing)?|unlock|conflict(s|ed|ing)?|sync(s|ed|ing)?|journal|side[ -]version|intents?|RPC|hash(es)?|vault)\b",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        var tips = HostTips.TrayItems.Select(item => HostTips.Tray(item, @"C:\IDEA\Armory")).Append(HostTips.GetWebView2).ToList();
        Assert.Equal(9, tips.Count);
        Assert.All(tips, tip =>
        {
            Assert.False(string.IsNullOrWhiteSpace(tip));
            Assert.EndsWith(".", tip);
            Assert.DoesNotContain('\u2014', tip);
            Assert.False(jargon.IsMatch(tip), tip);
        });
        Assert.Equal(tips.Count, tips.Distinct().Count());
        Assert.Equal(@"Open C:\IDEA\Armory in File Explorer.", HostTips.Tray(HostTips.OpenFolder, @"C:\IDEA\Armory"));
        Assert.Equal(string.Empty, HostTips.Tray("Using Armory: Jordan Reyes", @"C:\IDEA\Armory"));
    }

    // 0.3.3, N7: an identical view goes to the page once; a different one, any other message, and
    // the first view after the page loads again (or says ready) always go.
    [Fact]
    public void An_identical_view_is_posted_once()
    {
        var synced = BridgeMessages.ViewMessage(ShellViews.View(ShellViews.Row("Robot 2027/Drivetrain/Plate.SLDPRT")));
        var themed = synced.Replace("\"theme\":\"system\"", "\"theme\":\"idea\"", StringComparison.Ordinal);
        Assert.NotEqual(synced, themed);
        Assert.True(LastViewPosted.IsView(synced));
        var posted = new LastViewPosted();
        Assert.True(posted.Take(synced));
        Assert.False(posted.Take(synced));
        Assert.False(posted.Take(new string(synced.AsSpan())));
        Assert.True(posted.Take(themed));
        Assert.False(posted.Take(themed));
        var result = BridgeMessages.ActionResultMessage("r1", true, "Checked in Plate.SLDPRT.");
        Assert.False(LastViewPosted.IsView(result));
        Assert.True(posted.Take(result));
        Assert.True(posted.Take(result));
        posted.Forget();
        Assert.True(posted.Take(themed));
    }
}
