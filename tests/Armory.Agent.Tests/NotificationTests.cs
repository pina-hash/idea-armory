using System.Xml.Linq;
using Armory.Agent.Engine.View;

namespace Armory.Agent.Tests;

// A clock that moves only when a test says so.
internal sealed class ManualTime(DateTimeOffset start) : TimeProvider
{
    internal DateTimeOffset Now { get; set; } = start;
    public override DateTimeOffset GetUtcNow() => Now;
}

// The link a notification's button carries (ProtocolLink): exactly one form is a link; anything
// else only ever opens the window.
public sealed class ProtocolLinkTests
{
    private const string Token = "Qm9yZWQ3T2dfZXhhbXBsZQ";

    [Fact]
    public void Only_the_exact_link_is_accepted_and_anything_else_only_opens_the_window()
    {
        Assert.True(ProtocolLink.TryParse("idea-armory:act?t=" + Token + "&a=checkout", out var link));
        Assert.Equal(new ProtocolLink(Token, ProtocolLink.CheckOut), link);
        Assert.True(ProtocolLink.TryParse("IDEA-ARMORY:act?t=" + Token + "&a=show", out link));
        Assert.Equal(new ProtocolLink(Token, ProtocolLink.Show), link);
        Assert.Equal("idea-armory:act?t=" + Token + "&a=checkout", ProtocolLink.Format(Token, ProtocolLink.CheckOut));
        Assert.True(ProtocolLink.Format(Token, ProtocolLink.CheckOut).Length <= ProtocolLink.MaxLength);
        foreach (var refused in new[]
        {
            "idea-armory:", "idea-armory:act", "idea-armory:act?t=" + Token, "idea-armory:act?t=" + Token + "&a=checkout/",
            "idea-armory:act?t=" + Token + "&a=checkout&x=1", "idea-armory:act?t=" + Token + "&a=delete", "idea-armory:act?t=" + Token + "&a=CHECKOUT",
            "idea-armory:act?t=" + Token[..21] + "&a=show", "idea-armory:act?t=" + Token + "A&a=show", "idea-armory:act?t=" + Token[..21] + "%&a=show",
            "idea-armory:act?t=" + Token[..21] + "\"&a=show", "idea-armory:act?t=" + Token[..21] + " &a=show", "idea-armory:ACT?t=" + Token + "&a=show",
            "idea-armory:act?a=show&t=" + Token, "idea-armory://act?t=" + Token + "&a=show", "https://ideabosco.com/act?t=" + Token + "&a=show",
            "idea-armory:act?t=" + Token + "&a=show" + new string('x', 60), "", "--background",
        })
        {
            Assert.False(ProtocolLink.TryParse(refused, out var none), refused);
            Assert.Null(none);
            Assert.Equal(ProtocolLink.OpenOnly, ProtocolLink.ForForwarding(refused));
        }
        Assert.False(ProtocolLink.TryParse(null, out _));
        // A second launch hands over the link as Armory writes it.
        Assert.Equal("idea-armory:act?t=" + Token + "&a=show", ProtocolLink.ForForwarding("  IDEA-Armory:act?t=" + Token + "&a=show "));
        Assert.True(ProtocolLink.IsLink("Idea-Armory:anything"));
        Assert.False(ProtocolLink.IsLink(@"C:\IDEA\Armory\idea-armory:x"));
    }

    [Fact]
    public void The_forwarded_link_is_a_line_the_pipe_takes()
    {
        var line = ShellLine.Format(ShellVerb.Uri, ProtocolLink.ForForwarding("idea-armory:act?t=" + Token + "&a=checkout"), 42);
        Assert.True(ShellLine.TryParse(line, DateTimeOffset.UnixEpoch, out var request));
        Assert.Equal(ShellVerb.Uri, request!.Verb);
        Assert.Equal("idea-armory:act?t=" + Token + "&a=checkout", request.Path);
        Assert.True(ShellLine.TryParse(ShellLine.Format(ShellVerb.Uri, ProtocolLink.OpenOnly, 1), DateTimeOffset.UnixEpoch, out _));
        Assert.True(ShellVerbNames.IsSingle(ShellVerb.Uri));
    }
}

public sealed class ToastTokensTests
{
    private static readonly DateTimeOffset Start = new(2026, 10, 9, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void A_token_answers_once_and_never_after_thirty_minutes()
    {
        var time = new ManualTime(Start);
        var tokens = new ToastTokens(time);
        var token = tokens.Issue(ProtocolLink.CheckOut, ["Robot 2027/Plate.SLDPRT"], "tag1");
        Assert.Equal(ProtocolLink.TokenLength, token.Length);
        Assert.All(token, c => Assert.True(ProtocolLink.IsTokenChar(c)));
        Assert.True(ProtocolLink.TryParse(ProtocolLink.Format(token, ProtocolLink.CheckOut), out var link));
        Assert.True(tokens.TryTake(link!, out var ticket));
        Assert.Equal(["Robot 2027/Plate.SLDPRT"], ticket!.Paths);
        Assert.Equal("tag1", ticket.Tag);
        Assert.False(tokens.TryTake(link!, out _));

        var late = tokens.Issue(ProtocolLink.CheckOut, ["Robot 2027/Gear.SLDPRT"]);
        time.Now += ToastTokens.Lifetime;
        var onTime = tokens.Issue(ProtocolLink.Show, []);
        time.Now += TimeSpan.FromSeconds(1);
        Assert.False(tokens.TryTake(new ProtocolLink(late, ProtocolLink.CheckOut), out _));
        Assert.True(tokens.TryTake(new ProtocolLink(onTime, ProtocolLink.Show), out _));
        // Named with another action than it was made for: nothing, and it is gone.
        var show = tokens.Issue(ProtocolLink.Show, []);
        Assert.False(tokens.TryTake(new ProtocolLink(show, ProtocolLink.CheckOut), out _));
        Assert.False(tokens.TryTake(new ProtocolLink(show, ProtocolLink.Show), out _));
        Assert.False(tokens.TryTake(new ProtocolLink("AAAAAAAAAAAAAAAAAAAAAA", ProtocolLink.Show), out _));
        Assert.Throws<ArgumentException>(() => tokens.Issue("delete", []));
    }

    [Fact]
    public void At_most_two_hundred_live_and_none_after_clear()
    {
        var tokens = new ToastTokens(new ManualTime(Start));
        var first = tokens.Issue(ProtocolLink.Show, []);
        var all = Enumerable.Range(0, ToastTokens.MostLive).Select(_ => tokens.Issue(ProtocolLink.Show, [])).ToArray();
        Assert.Equal(ToastTokens.MostLive, tokens.Count);
        Assert.False(tokens.TryTake(new ProtocolLink(first, ProtocolLink.Show), out _));
        Assert.True(tokens.TryTake(new ProtocolLink(all[0], ProtocolLink.Show), out _));
        Assert.Equal(ToastTokens.MostLive, all.Distinct().Count());
        tokens.Clear();
        Assert.Equal(0, tokens.Count);
        Assert.False(tokens.TryTake(new ProtocolLink(all[^1], ProtocolLink.Show), out _));
    }
}

public sealed class ToastXmlTests
{
    private const string Link = "idea-armory:act?t=Qm9yZWQ3T2dfZXhhbXBsZQ&a=checkout";
    private const string Show = "idea-armory:act?t=AAAAAAAAAAAAAAAAAAAAAA&a=show";

    [Fact]
    public void Names_are_escaped_and_every_link_opens_by_protocol()
    {
        var file = new OpenPromptInfo("Robot 2027/<Arm> & \"Co\".SLDPRT", "<Arm> & \"Co\".SLDPRT", true, null);
        var (title, text) = ToastWords.Ask(file);
        var xml = ToastXml.Build(new ToastContent("0123456789abcdef", ToastXml.OpenGroup, title, text, Show,
            [new(ToastWords.CheckOutAndReopen, Link), new(ToastWords.NotNow, null)]));
        Assert.Contains("&lt;Arm&gt; &amp; \"Co\"", xml);
        Assert.Contains("&amp;a=checkout", xml);
        var toast = XElement.Parse(xml);
        Assert.Equal("toast", toast.Name.LocalName);
        Assert.Equal(Show, (string?)toast.Attribute("launch"));
        Assert.Equal("protocol", (string?)toast.Attribute("activationType"));
        var texts = toast.Descendants("text").Select(t => t.Value).ToArray();
        Assert.Equal(["Check out <Arm> & \"Co\".SLDPRT to edit it?", "SolidWorks opened it read-only. Check it out, then close it in SolidWorks and open it again to save changes."], texts);
        Assert.Equal("ToastGeneric", (string?)toast.Descendants("binding").Single().Attribute("template"));
        var actions = toast.Descendants("action").ToArray();
        Assert.Equal(2, actions.Length);
        Assert.Equal(("Check out and reopen", "protocol", Link), ((string)actions[0].Attribute("content")!, (string)actions[0].Attribute("activationType")!, (string)actions[0].Attribute("arguments")!));
        Assert.Equal(("Not now", "system", "dismiss"), ((string)actions[1].Attribute("content")!, (string)actions[1].Attribute("activationType")!, (string)actions[1].Attribute("arguments")!));
        Assert.Equal("true", (string?)toast.Element("audio")?.Attribute("silent"));
        // No buttons: no actions element at all.
        Assert.Null(XElement.Parse(ToastXml.Build(new ToastContent("t", ToastXml.AnswerGroup, "Checked in Plate.SLDPRT.", null, Show, []))).Element("actions"));
    }

    [Fact]
    public void A_tag_names_the_same_files_in_any_case_or_order_and_no_file()
    {
        var tag = ToastXml.TagFor(["Robot 2027/Plate.SLDPRT", "Robot 2027/Gear.SLDPRT"]);
        Assert.Matches("^[0-9a-f]{16}$", tag);
        Assert.Equal(tag, ToastXml.TagFor(["robot 2027\\gear.sldprt", "ROBOT 2027/PLATE.SLDPRT"]));
        Assert.NotEqual(tag, ToastXml.TagFor(["Robot 2027/Plate.SLDPRT"]));
        Assert.DoesNotContain("Plate", tag);
    }

    [Fact]
    public void The_words_say_who_has_a_file_and_an_answer_is_split_at_its_first_sentence()
    {
        Assert.Equal(("Plate.SLDPRT is checked out by Maria Lopez on LAB-PC-07", "You can look at it, but you can't save changes until it's checked in."),
            ToastWords.Ask(new OpenPromptInfo("R/Plate.SLDPRT", "Plate.SLDPRT", false, "Maria Lopez on LAB-PC-07")));
        Assert.Equal(("SolidWorks opened 1,203 files you haven't checked out", "Open Armory to check out the ones you'll change."), ToastWords.Group(1203));
        Assert.Equal(("Checked out 3 of 5 files.", "Maria Lopez has 2 of them checked out."), ToastWords.Answer("Checked out 3 of 5 files. Maria Lopez has 2 of them checked out."));
        Assert.Equal(("Checked in Plate.SLDPRT.", null), ToastWords.Answer("Checked in Plate.SLDPRT."));
        Assert.Equal(("Ask Mr. Pina to turn it on.", null), ToastWords.Answer("Ask Mr. Pina to turn it on."));
        foreach (var word in new[] { "sync", "lock", "vault", "conflict", "hash", "journal" })
            Assert.DoesNotContain(word, (ToastWords.Ask(new OpenPromptInfo("a", "a", true, null)).Text + ToastWords.Group(2).Text).ToLowerInvariant());
    }
}

// A stand-in for Windows' notifications.
internal sealed class FakeToasts : IToastPlatform
{
    internal ToastSetting SettingNow { get; set; } = ToastSetting.Enabled;
    internal bool Throws { get; set; }
    internal readonly List<ToastContent> Shown = [];
    internal readonly List<string> Removed = [];
    internal int Cleared;

    public ToastSetting Setting => Throws ? throw new System.Runtime.InteropServices.COMException("Element not found.") : SettingNow;
    public void Show(ToastContent toast)
    {
        if (Throws) throw new System.Runtime.InteropServices.COMException("No notification platform.");
        Shown.Add(toast);
    }
    public void Remove(string tag, string group)
    {
        if (Throws) throw new System.Runtime.InteropServices.COMException("No notification platform.");
        Removed.Add(group + "/" + tag);
    }
    public void Clear()
    {
        if (Throws) throw new System.Runtime.InteropServices.COMException("No notification platform.");
        Cleared++;
    }
}

public sealed class NotifierTests
{
    private static readonly DateTimeOffset Start = new(2026, 10, 9, 12, 0, 0, TimeSpan.Zero);
    private const string Launch = "idea-armory:act?t=AAAAAAAAAAAAAAAAAAAAAA&a=show";
    private static readonly ToastContent Question = new("0123456789abcdef", ToastXml.OpenGroup, "Check out Plate.SLDPRT to edit it?", "Words.", Launch, []);

    [Fact]
    public void Notifications_off_means_no_toast_and_no_balloon()
    {
        var toasts = new FakeToasts { SettingNow = ToastSetting.Off };
        var balloons = new List<string>();
        var log = new List<string>();
        var notifier = new Notifier(toasts, (title, text, _) => balloons.Add(title + ": " + text), log.Add, new ManualTime(Start));
        Assert.Equal(NotifiedBy.Nothing, notifier.Ask(Question, ("Check out Plate.SLDPRT to edit it?", "Click here.")));
        Assert.Equal(NotifiedBy.Nothing, notifier.Answer(new ActionResult(true, "Checked in Plate.SLDPRT."), Launch));
        Assert.Empty(toasts.Shown);
        Assert.Empty(balloons);
        Assert.Single(log);
    }

    [Fact]
    public void Only_a_failing_notification_api_or_a_copy_without_identity_uses_the_tray_balloon()
    {
        var balloons = new List<(string Title, string Text, bool Warning)>();
        var log = new List<string>();
        var failing = new Notifier(new FakeToasts { Throws = true }, (t, x, w) => balloons.Add((t, x, w)), log.Add, new ManualTime(Start));
        Assert.Equal(NotifiedBy.Balloon, failing.Ask(Question, ("Check out Plate.SLDPRT to edit it?", "Click here to open Armory and check it out.")));
        Assert.Equal(NotifiedBy.Balloon, failing.Answer(new ActionResult(false, "Close Plate.SLDPRT in SolidWorks first."), Launch));
        failing.Withdraw("t", ToastXml.OpenGroup);
        failing.ClearAll();
        Assert.Equal([("Check out Plate.SLDPRT to edit it?", "Click here to open Armory and check it out.", false), ("IDEA Armory", "Close Plate.SLDPRT in SolidWorks first.", true)], balloons);
        Assert.Single(log);
        var none = new Notifier(null, (t, x, w) => balloons.Add((t, x, w)), log.Add);
        Assert.Equal(NotifiedBy.Balloon, none.Answer(new ActionResult(true, "Checked in Plate.SLDPRT."), Launch));
        var working = new FakeToasts();
        Assert.Equal(NotifiedBy.Toast, new Notifier(working, (t, x, w) => balloons.Add((t, x, w)), log.Add).Ask(Question, ("x", "y")));
        Assert.Equal(Question, Assert.Single(working.Shown));
        Assert.Equal(3, balloons.Count);
    }

    [Fact]
    public void An_answer_within_six_seconds_of_the_last_replaces_it()
    {
        var time = new ManualTime(Start);
        var toasts = new FakeToasts();
        var notifier = new Notifier(toasts, (_, _, _) => { }, _ => { }, time);
        notifier.Answer(new ActionResult(true, "Checked out Plate.SLDPRT. Close it in SolidWorks and open it again to save changes."), Launch);
        time.Now += TimeSpan.FromSeconds(5);
        notifier.Answer(new ActionResult(true, "Checked in Gear.SLDPRT."), Launch);
        time.Now += Notifier.ReplaceWithin;
        notifier.Answer(new ActionResult(false, "Nothing there is checked out by you."), Launch);
        Assert.Equal(3, toasts.Shown.Count);
        Assert.Equal(toasts.Shown[0].Tag, toasts.Shown[1].Tag);
        Assert.NotEqual(toasts.Shown[1].Tag, toasts.Shown[2].Tag);
        Assert.All(toasts.Shown, t => Assert.Equal(ToastXml.AnswerGroup, t.Group));
        Assert.Equal(("Checked out Plate.SLDPRT.", "Close it in SolidWorks and open it again to save changes."), (toasts.Shown[0].Title, toasts.Shown[0].Text));
    }
}

// Which opened files ask, and when (OpenAsks): never in the window's place, once per open,
// opens close together as one.
public sealed class OpenAsksTests
{
    private static readonly DateTimeOffset Start = new(2026, 10, 9, 12, 0, 0, TimeSpan.Zero);
    private static OpenPromptInfo Open(string name, bool free = true) => new("Robot 2027/" + name, name, free, free ? null : "Maria Lopez on LAB-PC-07");

    [Fact]
    public void One_open_asks_once_after_a_moment_and_again_only_after_it_closed()
    {
        var asks = new OpenAsks();
        Assert.Empty(asks.Update([Open("Plate.SLDPRT")], windowShowing: false, Start));
        Assert.Null(asks.Due(Start + TimeSpan.FromSeconds(1), windowShowing: false));
        var ask = asks.Due(Start + OpenAsks.Gather, windowShowing: false);
        Assert.Equal(["Robot 2027/Plate.SLDPRT"], ask!.Files.Select(f => f.Path));
        Assert.Equal(ToastXml.TagFor(["Robot 2027/Plate.SLDPRT"]), ask.Tag);
        // Still open: no second question.
        asks.Update([Open("Plate.SLDPRT")], false, Start + TimeSpan.FromSeconds(5));
        Assert.Null(asks.NextDue);
        // Closed (or checked out): its notification is withdrawn; opened again: it asks again.
        Assert.Equal([ask.Tag], asks.Update([], false, Start + TimeSpan.FromSeconds(6)));
        asks.Update([Open("Plate.SLDPRT")], false, Start + TimeSpan.FromSeconds(7));
        Assert.NotNull(asks.Due(Start + TimeSpan.FromSeconds(9), false));
    }

    [Fact]
    public void Opens_close_together_are_one_group_and_the_window_asks_in_their_place()
    {
        var asks = new OpenAsks();
        var at = Start;
        for (var i = 0; i < 5; i++)
        {
            asks.Update(Enumerable.Range(0, i + 1).Select(n => Open($"Part{n}.SLDPRT", free: n != 2)).ToArray(), false, at);
            at += TimeSpan.FromSeconds(1);
        }
        Assert.Null(asks.Due(at, false));
        var group = asks.Due(at - TimeSpan.FromSeconds(1) + OpenAsks.Gather, false);
        Assert.Equal(5, group!.Files.Count);
        // Opens that keep coming are asked about by 10 seconds after the first.
        var steady = new OpenAsks();
        for (var i = 0; i < 20; i++) steady.Update(Enumerable.Range(0, i + 1).Select(n => Open($"P{n}.SLDPRT")).ToArray(), false, Start + TimeSpan.FromSeconds(i));
        Assert.Equal(Start + OpenAsks.GatherAtMost, steady.NextDue);
        // While the window shows, its card asks: no notification now, and none later for that open.
        var shown = new OpenAsks();
        shown.Update([Open("Plate.SLDPRT")], windowShowing: true, Start);
        Assert.Null(shown.NextDue);
        shown.Update([Open("Plate.SLDPRT")], windowShowing: false, Start + TimeSpan.FromSeconds(3));
        Assert.Null(shown.NextDue);
        // Opened while hidden, but the window came up before the question was due.
        var raced = new OpenAsks();
        raced.Update([Open("Gear.SLDPRT")], false, Start);
        Assert.Null(raced.Due(Start + OpenAsks.Gather, windowShowing: true));
        Assert.Null(raced.NextDue);
    }
}
