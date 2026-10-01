using System.Text;
using System.Text.RegularExpressions;
using Armory.Agent.Engine.View;

namespace Armory.Agent.Tests;

// docs/agent/BRIDGE.md: wwwroot/bridge.js and AgentView.cs name the same messages. bridge.js
// sends only what PAGE_TO_HOST lists (buildMessage refuses anything else) and dispatches only
// what HOST_TO_PAGE lists, so those two plain string arrays are its sent and handled sets.
// These tests run on every platform and fail (never skip) while bridge.js is missing.
public sealed partial class AgentViewContractTests
{
    [Fact]
    public void Bridge_js_sends_exactly_the_page_to_host_messages()
    {
        var code = Code(BridgeJs());
        var sent = StringArray(code, "PAGE_TO_HOST");
        Assert.Equal(Sorted(BridgeMessages.PageToHost), Sorted(sent));
        Assert.Equal(sent.Count, sent.Distinct(StringComparer.Ordinal).Count());
        Assert.True(Gates(code, "PAGE_TO_HOST"), "bridge.js must refuse to send a type that PAGE_TO_HOST does not list (for example PAGE_TO_HOST.indexOf(type) < 0).");
    }

    [Fact]
    public void Bridge_js_handles_exactly_the_host_to_page_messages()
    {
        var code = Code(BridgeJs());
        var handled = StringArray(code, "HOST_TO_PAGE");
        Assert.Equal(Sorted(BridgeMessages.HostToPage), Sorted(handled));
        Assert.Equal(handled.Count, handled.Distinct(StringComparer.Ordinal).Count());
        Assert.True(Gates(code, "HOST_TO_PAGE"), "bridge.js must ignore a host message whose type HOST_TO_PAGE does not list (for example HOST_TO_PAGE.indexOf(message.type) < 0).");
    }

    [Fact]
    public void Every_message_type_literal_in_the_page_is_a_bridge_message()
    {
        var bridge = Code(BridgeJs());
        var known = BridgeMessages.PageToHost.Concat(BridgeMessages.HostToPage).ToHashSet(StringComparer.Ordinal);
        // Message objects written out in bridge.js itself, e.g. the demo's { type: 'view', ... }.
        foreach (Match literal in TypeProperty().Matches(bridge))
            Assert.True(known.Contains(literal.Groups["name"].Value), $"bridge.js builds a message of unknown type '{literal.Groups["name"].Value}'.");
        // The rest of the page talks through ArmoryBridge.send('<type>') and reads message.type.
        foreach (var file in PageScripts())
        {
            var code = Code(File.ReadAllText(file));
            foreach (Match send in SendCall().Matches(code))
                Assert.True(BridgeMessages.PageToHost.Contains(send.Groups["name"].Value),
                    $"{Path.GetFileName(file)} sends '{send.Groups["name"].Value}', which is not in BridgeMessages.PageToHost.");
            foreach (Match handled in TypeComparison().Matches(code))
                Assert.True(BridgeMessages.HostToPage.Contains(handled.Groups["name"].Value),
                    $"{Path.GetFileName(file)} handles '{handled.Groups["name"].Value}', which is not in BridgeMessages.HostToPage.");
        }
    }

    [Fact]
    public void Bridge_js_jsdoc_mirrors_the_message_lists()
    {
        var text = BridgeJs();
        var page = PageMessageTypedef().Match(text);
        if (page.Success)
            Assert.Equal(Sorted(BridgeMessages.PageToHost), Sorted(Literals(page.Groups["body"].Value)));
        var host = HostMessageTypedef().Match(text);
        if (host.Success)
            Assert.Equal(Sorted(BridgeMessages.HostToPage), Sorted(TypeProperty().Matches(host.Groups["body"].Value).Select(m => m.Groups["name"].Value).ToList()));
        Assert.True(page.Success || host.Success, "bridge.js should mirror the message types in JSDoc (@typedef ... PageMessageType / HostMessage).");
    }

    [Fact]
    public void The_host_reads_the_type_of_object_and_string_messages()
    {
        // The page posts objects (WebMessageAsJson is the object's JSON); a page that posts
        // JSON.stringify(...) arrives as a JSON string and is read the same way.
        Assert.True(Bridge.TryRead("{\"type\":\"openFile\",\"fileId\":\"8b0c3a5e-1f2d-4c6b-9a7e-2d3c4b5a6f70\"}", out var type, out _));
        Assert.Equal(BridgeMessages.OpenFile, type);
        Assert.True(Bridge.TryRead("\"{\\\"type\\\":\\\"ready\\\"}\"", out type, out _));
        Assert.Equal(BridgeMessages.Ready, type);
        Assert.False(Bridge.TryRead("{\"kind\":\"ready\"}", out _, out _));
        Assert.False(Bridge.TryRead("not json", out _, out _));
    }

    private static string BridgeJs()
    {
        var root = Repo.FindRoot();
        Assert.True(root is not null, "Could not find the repository root (Armory.sln) above " + AppContext.BaseDirectory + ".");
        var file = Path.Combine(root!, "src", "Armory.Agent", "wwwroot", "bridge.js");
        if (!File.Exists(file))
            Assert.Fail("src/Armory.Agent/wwwroot/bridge.js does not exist yet. The window's page must ship it (docs/agent/BRIDGE.md); " +
                "this contract test fails, rather than skips, until it does.");
        return File.ReadAllText(file);
    }

    private static IEnumerable<string> PageScripts()
    {
        var folder = Path.Combine(Repo.FindRoot()!, "src", "Armory.Agent", "wwwroot");
        return Directory.EnumerateFiles(folder, "*.js", SearchOption.TopDirectoryOnly)
            .Where(f => !Path.GetFileName(f).Equals("bridge.js", StringComparison.OrdinalIgnoreCase));
    }

    private static List<string> StringArray(string code, string name)
    {
        var match = Regex.Match(code, @"\b(?:var|let|const)\s+" + name + @"\s*=\s*(?:Object\.freeze\(\s*)?\[(?<items>[^\]]*)\]");
        Assert.True(match.Success, $"bridge.js must declare {name} as a plain array of string literals.");
        var items = match.Groups["items"].Value;
        var rest = StringLiteral().Replace(items, "");
        Assert.True(rest.All(c => c == ',' || char.IsWhiteSpace(c)), $"{name} in bridge.js must hold only string literals.");
        return Literals(items);
    }

    private static List<string> Literals(string text) => StringLiteral().Matches(text).Select(m => m.Groups["s"].Success ? m.Groups["s"].Value : m.Groups["d"].Value).ToList();

    private static bool Gates(string code, string name)
        => Regex.IsMatch(code, name + @"\s*\.\s*(?:indexOf|includes)\s*\(") || Regex.IsMatch(code, @"new\s+Set\s*\(\s*" + name + @"\b");

    private static List<string> Sorted(IEnumerable<string> values) => values.OrderBy(v => v, StringComparer.Ordinal).ToList();

    // JavaScript with comments removed; string and template literals are kept as written.
    private static string Code(string source)
    {
        var output = new StringBuilder(source.Length);
        for (var i = 0; i < source.Length; i++)
        {
            var c = source[i];
            if (c is '\'' or '"' or '`')
            {
                var start = i;
                for (i++; i < source.Length && source[i] != c; i++) if (source[i] == '\\') i++;
                output.Append(source, start, Math.Min(i, source.Length - 1) - start + 1);
            }
            else if (c == '/' && i + 1 < source.Length && source[i + 1] == '/')
            {
                while (i < source.Length && source[i] != '\n') i++;
                output.Append('\n');
            }
            else if (c == '/' && i + 1 < source.Length && source[i + 1] == '*')
            {
                var end = source.IndexOf("*/", i + 2, StringComparison.Ordinal);
                i = end < 0 ? source.Length : end + 1;
                output.Append(' ');
            }
            else output.Append(c);
        }
        return output.ToString();
    }

    [GeneratedRegex("'(?<s>[^'\\\\]*)'|\"(?<d>[^\"\\\\]*)\"")]
    private static partial Regex StringLiteral();

    [GeneratedRegex("\\btype\\s*:\\s*['\"](?<name>[A-Za-z]+)['\"]")]
    private static partial Regex TypeProperty();

    [GeneratedRegex("\\.send\\(\\s*['\"](?<name>[A-Za-z]+)['\"]")]
    private static partial Regex SendCall();

    [GeneratedRegex("\\.type\\s*===?\\s*['\"](?<name>[A-Za-z]+)['\"]")]
    private static partial Regex TypeComparison();

    [GeneratedRegex(@"@typedef\s*\{(?<body>[^}]*)\}\s*PageMessageType")]
    private static partial Regex PageMessageTypedef();

    [GeneratedRegex(@"@typedef\s*\{(?<body>(?:[^{}]|\{[^{}]*\})*)\}\s*HostMessage")]
    private static partial Regex HostMessageTypedef();
}
