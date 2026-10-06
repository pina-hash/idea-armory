namespace Armory.Agent.Tests;

// Host rules that need no window, so they run on every host.
public sealed class HostPiecesTests
{
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
}
