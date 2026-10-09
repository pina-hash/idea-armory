using System.Runtime.Versioning;
using Microsoft.Win32;

namespace Armory.Agent.Tests;

// T4 (docs/agent/EXPLORER.md): the right-click menu's registry layout, exactly, and the
// compare-before-write that keeps Explorer from re-reading it for nothing. The layout and the
// changes are plain data and run anywhere; the registry tests use a private key on Windows.
public sealed class ShellVerbsTests
{
    private const string Vault = @"D:\Team Files\IDEA Armory";
    private const string App = @"C:\Users\Ana María\AppData\Local\Programs\IDEA Armory\";

    private static Dictionary<string, Dictionary<string, object>> ByKey(IEnumerable<ShellRegistryValue> layout) =>
        layout.GroupBy(v => v.Key, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.ToDictionary(v => v.Name, v => v.Value, StringComparer.OrdinalIgnoreCase), StringComparer.OrdinalIgnoreCase);

    private static IReadOnlyDictionary<string, IReadOnlyDictionary<string, object>> AsExisting(IEnumerable<ShellRegistryValue> layout) =>
        ByKey(layout).ToDictionary(p => p.Key, p => (IReadOnlyDictionary<string, object>)p.Value, StringComparer.OrdinalIgnoreCase);

    [Fact]
    public void The_layout_is_exactly_the_documented_one()
    {
        var keys = ByKey(ShellVerbs.Layout(Vault, App, []));
        const string shell = "\"C:\\Users\\Ana María\\AppData\\Local\\Programs\\IDEA Armory\\ArmoryShell.exe\"";
        Assert.Equal(new Dictionary<string, object>
        {
            ["MUIVerb"] = "IDEA Armory",
            ["Icon"] = "\"C:\\Users\\Ana María\\AppData\\Local\\Programs\\IDEA Armory\\IdeaArmory.exe\",0",
            ["AppliesTo"] = "System.ItemPathDisplay:~<\"D:\\Team Files\\IDEA Armory\\\"",
            ["ExtendedSubCommandsKey"] = "IDEAArmory.Menu",
            ["MultiSelectModel"] = "Player",
        }, keys[@"AllFilesystemObjects\shell\IDEAArmory"]);
        Assert.Equal(new Dictionary<string, object> { ["MUIVerb"] = "Check out", ["MultiSelectModel"] = "Player" }, keys[@"IDEAArmory.Menu\shell\01checkout"]);
        Assert.Equal(shell + " checkout \"%1\"", keys[@"IDEAArmory.Menu\shell\01checkout\command"][""]);
        Assert.Equal(new Dictionary<string, object> { ["MUIVerb"] = "Check out and open", ["MultiSelectModel"] = "Single" }, keys[@"IDEAArmory.Menu\shell\02checkoutopen"]);
        Assert.Equal(shell + " checkoutopen \"%1\"", keys[@"IDEAArmory.Menu\shell\02checkoutopen\command"][""]);
        Assert.Equal(new Dictionary<string, object> { ["MUIVerb"] = "Check in", ["MultiSelectModel"] = "Player" }, keys[@"IDEAArmory.Menu\shell\03checkin"]);
        Assert.Equal(shell + " checkin \"%1\"", keys[@"IDEAArmory.Menu\shell\03checkin\command"][""]);
        Assert.Equal(new Dictionary<string, object> { ["MUIVerb"] = "Undo check out", ["MultiSelectModel"] = "Player" }, keys[@"IDEAArmory.Menu\shell\04undo"]);
        Assert.Equal(shell + " undo \"%1\"", keys[@"IDEAArmory.Menu\shell\04undo\command"][""]);
        Assert.Equal(new Dictionary<string, object> { ["MUIVerb"] = "Show in Armory", ["MultiSelectModel"] = "Single", ["CommandFlags"] = 0x20 }, keys[@"IDEAArmory.Menu\shell\05show"]);
        Assert.Equal(shell + " show \"%1\"", keys[@"IDEAArmory.Menu\shell\05show\command"][""]);
        Assert.Equal(new Dictionary<string, object>
        {
            ["MUIVerb"] = "IDEA Armory",
            ["Icon"] = "\"C:\\Users\\Ana María\\AppData\\Local\\Programs\\IDEA Armory\\IdeaArmory.exe\",0",
            ["AppliesTo"] = "System.ItemPathDisplay:=\"D:\\Team Files\\IDEA Armory\" OR System.ItemPathDisplay:~<\"D:\\Team Files\\IDEA Armory\\\"",
            ["ExtendedSubCommandsKey"] = "IDEAArmory.BackgroundMenu",
        }, keys[@"Directory\Background\shell\IDEAArmory"]);
        Assert.Equal(new Dictionary<string, object> { ["MUIVerb"] = "Check in" }, keys[@"IDEAArmory.BackgroundMenu\shell\01checkin"]);
        Assert.Equal(shell + " checkin \"%V\"", keys[@"IDEAArmory.BackgroundMenu\shell\01checkin\command"][""]);
        Assert.Equal(new Dictionary<string, object> { ["MUIVerb"] = "Show in Armory" }, keys[@"IDEAArmory.BackgroundMenu\shell\02show"]);
        Assert.Equal(shell + " show \"%V\"", keys[@"IDEAArmory.BackgroundMenu\shell\02show\command"][""]);
        Assert.Equal(16, keys.Count);
        Assert.DoesNotContain(keys.Keys, k => k.Contains("forcecheckin", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Force_check_in_lists_only_the_allowed_projects_folders()
    {
        var keys = ByKey(ShellVerbs.Layout(Vault + @"\", App, ["Robot 2027", @"Mr. O'Neil's ""Class""", "robot 2027/", "", "Class 2026\\"]));
        var force = keys[@"IDEAArmory.Menu\shell\06forcecheckin"];
        Assert.Equal("Force check in", force["MUIVerb"]);
        Assert.Equal("Player", force["MultiSelectModel"]);
        Assert.Equal(0x20, force["CommandFlags"]);
        Assert.Equal(
            "System.ItemPathDisplay:=\"D:\\Team Files\\IDEA Armory\\Class 2026\" OR " +
            "System.ItemPathDisplay:~<\"D:\\Team Files\\IDEA Armory\\Class 2026\\\" OR " +
            "System.ItemPathDisplay:=\"D:\\Team Files\\IDEA Armory\\Mr. O'Neil's \"\"Class\"\"\" OR " +
            "System.ItemPathDisplay:~<\"D:\\Team Files\\IDEA Armory\\Mr. O'Neil's \"\"Class\"\"\\\" OR " +
            "System.ItemPathDisplay:=\"D:\\Team Files\\IDEA Armory\\Robot 2027\" OR " +
            "System.ItemPathDisplay:~<\"D:\\Team Files\\IDEA Armory\\Robot 2027\\\"", force["AppliesTo"]);
        Assert.EndsWith("ArmoryShell.exe\" forcecheckin \"%1\"", (string)keys[@"IDEAArmory.Menu\shell\06forcecheckin\command"][""]);
    }

    [Fact]
    public void Aqs_quotes_double_a_quote_and_or_is_in_capitals()
    {
        Assert.Equal("\"a \"\"b\"\" c\"", ShellVerbs.Quote("a \"b\" c"));
        Assert.Equal("System.ItemPathDisplay:~<\"C:\\IDEA\\Armory\\\"", ShellVerbs.Inside(@"C:\IDEA\Armory\\"));
        Assert.Equal("System.ItemPathDisplay:=\"C:\\IDEA\\Armory\"", ShellVerbs.Exactly("C:/IDEA/Armory/"));
        Assert.Equal("a OR b OR c", ShellVerbs.Or(["a", "b", "c"]));
    }

    [Fact]
    public void Nothing_changes_when_the_registry_already_holds_the_layout()
    {
        var layout = ShellVerbs.Layout(Vault, App, ["Robot 2027"]);
        Assert.Empty(ShellVerbs.Changes(AsExisting(layout), layout));
        // Key and value names in another case are the same key and value to the registry.
        var shouted = layout.Select(v => v with { Key = v.Key.ToUpperInvariant(), Name = v.Name.ToUpperInvariant() });
        Assert.Empty(ShellVerbs.Changes(AsExisting(shouted), layout));
    }

    [Fact]
    public void An_empty_registry_gets_every_value_and_nothing_else()
    {
        var layout = ShellVerbs.Layout(Vault, App, []);
        var changes = ShellVerbs.Changes(new Dictionary<string, IReadOnlyDictionary<string, object>>(), layout);
        Assert.All(changes, c => Assert.Equal(ShellRegistryAction.SetValue, c.Action));
        Assert.Equal(layout.Count, changes.Count);
    }

    [Fact]
    public void Changes_fix_a_value_drop_a_stale_value_and_remove_force_check_in_when_it_is_no_longer_allowed()
    {
        var before = ShellVerbs.Layout(Vault, App, ["Robot 2027"]).ToList();
        var existing = ByKey(before);
        existing[@"IDEAArmory.Menu\shell\01checkout"]["MUIVerb"] = "Check Out";
        existing[@"IDEAArmory.Menu\shell\05show"]["CommandFlags"] = "32";
        existing[@"AllFilesystemObjects\shell\IDEAArmory"]["Position"] = "Top";
        existing[@"IDEAArmory.Menu\shell\07old"] = new(StringComparer.OrdinalIgnoreCase) { ["MUIVerb"] = "Old" };
        existing[@"IDEAArmory.Menu\shell\07old\command"] = new(StringComparer.OrdinalIgnoreCase) { [""] = "old" };
        var changes = ShellVerbs.Changes(existing.ToDictionary(p => p.Key, p => (IReadOnlyDictionary<string, object>)p.Value, StringComparer.OrdinalIgnoreCase),
            ShellVerbs.Layout(Vault, App, []));
        Assert.Equal(new ShellRegistryChange[]
        {
            new(ShellRegistryAction.DeleteKey, @"IDEAArmory.Menu\shell\06forcecheckin"),
            new(ShellRegistryAction.DeleteKey, @"IDEAArmory.Menu\shell\07old"),
            new(ShellRegistryAction.DeleteValue, @"AllFilesystemObjects\shell\IDEAArmory", "Position"),
            new(ShellRegistryAction.SetValue, @"IDEAArmory.Menu\shell\01checkout", "MUIVerb", "Check out"),
            new(ShellRegistryAction.SetValue, @"IDEAArmory.Menu\shell\05show", "CommandFlags", 0x20),
        }, changes);
    }

    [Fact]
    public void A_new_vault_root_rewrites_only_the_values_that_name_it()
    {
        var before = ShellVerbs.Layout(@"C:\IDEA\Armory", App, []);
        var after = ShellVerbs.Layout(@"E:\Armory", App, []);
        var changes = ShellVerbs.Changes(AsExisting(before), after);
        Assert.Equal(2, changes.Count);
        Assert.All(changes, c => Assert.Equal("AppliesTo", c.Name));
    }

    [WindowsFact]
    [SupportedOSPlatform("windows")]
    public void The_registry_gets_the_layout_once_and_a_second_apply_writes_nothing()
    {
        var name = @"Software\IDEA Armory Tests\" + Guid.NewGuid().ToString("N");
        try
        {
            using var classes = Registry.CurrentUser.CreateSubKey(name, writable: true);
            var layout = ShellVerbs.Layout(Vault, App, ["Robot 2027"]);
            Assert.True(ShellVerbs.Write(classes, layout));
            Assert.False(ShellVerbs.Write(classes, layout));
            using (var show = classes.OpenSubKey(@"IDEAArmory.Menu\shell\05show"))
            {
                Assert.Equal(RegistryValueKind.DWord, show!.GetValueKind("CommandFlags"));
                Assert.Equal(0x20, show.GetValue("CommandFlags"));
            }
            using (var command = classes.OpenSubKey(@"IDEAArmory.Menu\shell\01checkout\command"))
                Assert.Equal(layout.Single(v => v.Key.EndsWith(@"01checkout\command", StringComparison.Ordinal)).Value, command!.GetValue(""));
            // Someone edits a value and adds a key: the next apply puts it back exactly.
            using (var checkout = classes.CreateSubKey(@"IDEAArmory.Menu\shell\01checkout", writable: true))
                checkout.SetValue("MUIVerb", "Hacked");
            classes.CreateSubKey(@"IDEAArmory.Menu\shell\09extra\command", writable: true).Dispose();
            Assert.True(ShellVerbs.Write(classes, layout));
            Assert.Empty(ShellVerbs.Changes(ShellVerbs.Read(classes), layout));
            Assert.Null(classes.OpenSubKey(@"IDEAArmory.Menu\shell\09extra"));
            // Force check in goes away when no project allows it any more.
            Assert.True(ShellVerbs.Write(classes, ShellVerbs.Layout(Vault, App, [])));
            Assert.Null(classes.OpenSubKey(@"IDEAArmory.Menu\shell\06forcecheckin"));
            Assert.True(ShellVerbs.Remove(classes));
            Assert.All(ShellVerbs.Roots, root => Assert.Null(classes.OpenSubKey(root)));
            Assert.False(ShellVerbs.Remove(classes));
        }
        finally
        {
            Registry.CurrentUser.DeleteSubKeyTree(name, throwOnMissingSubKey: false);
            using var parent = Registry.CurrentUser.OpenSubKey(@"Software\IDEA Armory Tests", writable: true);
            if (parent is not null && parent.SubKeyCount == 0 && parent.ValueCount == 0)
                Registry.CurrentUser.DeleteSubKey(@"Software\IDEA Armory Tests", throwOnMissingSubKey: false);
        }
    }
}
