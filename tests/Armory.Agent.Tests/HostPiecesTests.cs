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
}
