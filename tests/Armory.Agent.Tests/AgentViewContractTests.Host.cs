using System.Reflection;
using System.Text.Json;
using System.Text.RegularExpressions;
using Armory.Agent.Engine.View;

namespace Armory.Agent.Tests;

// The host's half of the bridge (docs/agent/BRIDGE.md): Bridge.cs answers every type the
// page sends, reads exactly the fields bridge.js sends for it, and the C# records the host
// serializes carry the field names bridge.js documents (and the page reads). The page's
// demo states are held to the same JSDoc typedefs by tools/agent-ui/check-ui.mjs, so a
// field the demo invents and the host never sends shows up on one side or the other.
public sealed partial class AgentViewContractTests
{
    [Fact]
    public void Every_page_to_host_type_has_a_case_in_the_host_bridge()
    {
        var source = File.ReadAllText(Path.Combine(Repo.FindRoot()!, "src", "Armory.Agent", "Bridge.cs"));
        var constants = typeof(BridgeMessages).GetFields(BindingFlags.Public | BindingFlags.Static)
            .Where(f => f.IsLiteral && f.FieldType == typeof(string))
            .ToDictionary(f => f.Name, f => (string)f.GetRawConstantValue()!, StringComparer.Ordinal);
        var cases = CaseLabel().Matches(Code(source)).Select(m => m.Groups["name"].Value).ToList();
        foreach (var name in cases) Assert.True(constants.ContainsKey(name), $"Bridge.cs has a case for BridgeMessages.{name}, which is not a message constant.");
        var handled = cases.Select(n => constants[n]).ToList();
        Assert.Equal(handled.Count, handled.Distinct(StringComparer.Ordinal).Count());
        var missing = BridgeMessages.PageToHost.Except(handled, StringComparer.Ordinal).ToList();
        Assert.True(missing.Count == 0, "Bridge.cs drops these page messages in its default branch: " + string.Join(", ", missing) + ".");
        Assert.Equal(Sorted(BridgeMessages.PageToHost), Sorted(handled.Intersect(BridgeMessages.PageToHost, StringComparer.Ordinal)));
        Assert.Equal(Sorted(BridgeMessages.PageToHost), Sorted(Bridge.MessageRecords.Keys));
    }

    [Fact]
    public void The_host_message_records_carry_the_fields_bridge_js_requires()
    {
        var code = Code(BridgeJs());
        var required = RequiredFields(code);
        var actions = StringArray(code, "ACTIONS");
        // Asks (0.3.3) carry a requestId too, answered by their own message (windowShot, myFeedback).
        var asks = StringArray(code, "ASKS");
        Assert.Empty(actions.Intersect(asks));
        foreach (var type in required.Keys.Concat(actions).Concat(asks))
            Assert.True(BridgeMessages.PageToHost.Contains(type), $"bridge.js names fields for '{type}', which is not a page-to-host message.");
        foreach (var type in BridgeMessages.PageToHost)
        {
            var sent = (required.TryGetValue(type, out var fields) ? fields : []).ToList();
            if (actions.Contains(type) || asks.Contains(type)) sent.Add("requestId");
            var record = Bridge.MessageRecords[type];
            var read = record is null ? [] : JsonNames(record);
            Assert.True(Sorted(sent).SequenceEqual(Sorted(read)),
                $"'{type}': bridge.js sends {{{string.Join(", ", Sorted(sent))}}}, Bridge.cs reads {{{string.Join(", ", Sorted(read))}}} ({record?.Name ?? "no record"}).");
        }
    }

    [Fact]
    public void The_host_view_records_have_the_fields_bridge_js_documents()
    {
        // The view records: every documented object whose name ends in View (DemoRoute and
        // the like are the page's own).
        var typedefs = Typedefs(BridgeJs()).Where(t => t.Key.EndsWith("View", StringComparison.Ordinal)).ToDictionary(t => t.Key, t => t.Value, StringComparer.Ordinal);
        Assert.True(typedefs.Count >= 15, "bridge.js should document every view record as a JSDoc @typedef {object} with its @property list.");
        var records = typeof(AgentView).Assembly.GetTypes()
            .Where(t => t.Namespace == typeof(AgentView).Namespace && t.IsClass && t.IsSealed && t.Name.EndsWith("View", StringComparison.Ordinal))
            .ToDictionary(t => t.Name, StringComparer.Ordinal);
        var differing = new SortedDictionary<string, string>(StringComparer.Ordinal);
        foreach (var (name, fields) in typedefs)
        {
            if (!records.TryGetValue(name, out var record)) { differing[name] = "bridge.js documents it; the host has no such record"; continue; }
            var host = JsonNames(record);
            if (!Sorted(host).SequenceEqual(Sorted(fields)))
                differing[name] = $"the host sends {{{string.Join(", ", Sorted(host))}}}, bridge.js documents {{{string.Join(", ", Sorted(fields))}}}";
        }
        foreach (var name in records.Keys.Where(n => !typedefs.ContainsKey(n))) differing[name] = "the host has it; bridge.js does not document it";

        // AgentView is the v2 view (the engine builds it), so every record matches bridge.js.
        var report = string.Join("\n", differing.Select(d => d.Key + ": " + d.Value));
        Assert.True(differing.Count == 0, "Every view record must match bridge.js. Differing now:\n" + report);
    }

    [Fact]
    public void The_host_messages_have_the_fields_the_page_reads()
    {
        var members = HostMessageMembers(BridgeJs());
        var activity = new ActivityView("Downloading 1 of 2 files, 10 KB left, less than a minute",
            null, new DirectionView(1, 2, 10240, 20480, 5120, null, "Downloading 1 of 2 files, 10 KB left, less than a minute"), null,
            new WaitingView(1, "1 file is waiting to upload. It uploads when this computer is back online."),
            [new ActiveTransferView("Robot/Plate.SLDPRT", "Plate.SLDPRT", Directions.Download, 10240, 20480)],
            [new ActivityLineView("2026-10-08T18:24:58.0000000Z", "Downloaded Plate.SLDPRT (20 KB)")]);
        var note = new FeedbackNoteView("0f000000-0000-0000-0000-000000000001", "2026-10-09T15:02:11Z", "bug", "Spins.", null, "Home", true, "0.3.3",
            "LAB-PC-07", "seen", "Read by the IDEA team", "2026-10-09T18:40:00Z");
        foreach (var (type, json) in new[]
        {
            (BridgeMessages.Activity, BridgeMessages.ActivityMessage(activity)),
            (BridgeMessages.ActionResult, BridgeMessages.ActionResultMessage("r7", false, "Close Plate.SLDPRT in SolidWorks first.")),
            (BridgeMessages.ActionResult, BridgeMessages.ActionResultMessage("r8", false, "Your note wasn't sent.", ActionResult.WithoutPicture)),
            (BridgeMessages.WindowShot, BridgeMessages.WindowShotMessage("r9", new WindowShotView(true, new string('a', 32), "https://armory.local/shot/x.png", 1120, 760, 219113, false, null))),
            (BridgeMessages.MyFeedback, BridgeMessages.MyFeedbackMessage("r10", new FeedbackListView(FeedbackListView.Shown, true, null, [note]))),
            // File Explorer's answers come as an actionResult too, and Show in Armory as reveal.
            (BridgeMessages.ActionResult, BridgeMessages.ActionResultMessage(BridgeMessages.ShellRequest, true, "Checked in 3 files.")),
            (BridgeMessages.Reveal, BridgeMessages.RevealMessage("Robot/Drivetrain/Plate.SLDPRT")),
        })
        {
            using var document = JsonDocument.Parse(json);
            var keys = document.RootElement.EnumerateObject().Select(p => p.Name).ToList();
            Assert.Equal(type, document.RootElement.GetProperty("type").GetString());
            Assert.True(members.TryGetValue(type, out var fields), $"bridge.js's HostMessage has no '{type}' member.");
            Assert.True(Sorted(keys).SequenceEqual(Sorted(fields)), $"'{type}': the host sends {{{string.Join(", ", Sorted(keys))}}}, the page reads {{{string.Join(", ", Sorted(fields))}}}.");
        }
    }

    private static List<string> JsonNames(Type record)
    {
        var policy = BridgeMessages.Json.PropertyNamingPolicy!;
        return record.GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Where(p => p.Name != "EqualityContract")
            .Select(p => policy.ConvertName(p.Name)).ToList();
    }

    // REQUIRED = { openFile: ['fileId'], ... } in bridge.js.
    private static Dictionary<string, List<string>> RequiredFields(string code)
    {
        var block = Regex.Match(code, @"\b(?:var|let|const)\s+REQUIRED\s*=\s*\{(?<body>[^}]*)\}");
        Assert.True(block.Success, "bridge.js must declare REQUIRED as a plain object of string arrays.");
        return Regex.Matches(block.Groups["body"].Value, @"(?<type>\w+)\s*:\s*\[(?<items>[^\]]*)\]")
            .ToDictionary(m => m.Groups["type"].Value, m => Literals(m.Groups["items"].Value), StringComparer.Ordinal);
    }

    // Every "@typedef {object} Name" block in bridge.js and its "@property {...} field" names.
    private static Dictionary<string, List<string>> Typedefs(string text)
    {
        var found = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        foreach (Match block in Regex.Matches(text, @"/\*\*(?<body>.*?)\*/", RegexOptions.Singleline))
        {
            var body = block.Groups["body"].Value;
            var name = Regex.Match(body, @"@typedef\s*\{object\}\s*(?<name>\w+)");
            if (!name.Success) continue;
            found[name.Groups["name"].Value] = Regex.Matches(body, @"@property\s*\{[^}]*\}\s*(?<field>\w+)").Select(m => m.Groups["field"].Value).ToList();
        }
        return found;
    }

    // HostMessage = { type: 'view', view: AgentView } | ...: each member's type and fields.
    private static Dictionary<string, List<string>> HostMessageMembers(string text)
    {
        var host = HostMessageTypedef().Match(text);
        Assert.True(host.Success, "bridge.js must document HostMessage as a union of { type: '...', ... } objects.");
        var members = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        foreach (Match member in Regex.Matches(host.Groups["body"].Value, @"\{(?<fields>[^{}]*)\}"))
        {
            var fields = Regex.Matches(member.Groups["fields"].Value, @"(?<name>\w+)\s*:").Select(m => m.Groups["name"].Value).ToList();
            var type = TypeProperty().Match(member.Groups["fields"].Value);
            if (type.Success) members[type.Groups["name"].Value] = fields;
        }
        return members;
    }

    [GeneratedRegex(@"\bcase\s+BridgeMessages\.(?<name>\w+)\s*:")]
    private static partial Regex CaseLabel();
}
