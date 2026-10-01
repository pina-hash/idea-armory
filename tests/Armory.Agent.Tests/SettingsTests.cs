using System.Text.Json;

namespace Armory.Agent.Tests;

public sealed class SettingsTests
{
    [Fact]
    public void Defaults_are_the_contract_vault_start_at_sign_in_and_system_theme()
    {
        using var folder = new TempFolder();
        var settings = new SettingsStore(folder.File("settings.json")).Load();
        Assert.Equal(@"C:\IDEA\Armory", settings.VaultRoot);
        Assert.True(settings.StartAtSignIn);
        Assert.Equal("system", settings.Theme);
        Assert.Equal(AgentSettings.Default, settings);
        Assert.False(File.Exists(folder.File("settings.json")));
    }

    [Fact]
    public void Saved_settings_round_trip_with_exactly_three_values()
    {
        using var folder = new TempFolder();
        var store = new SettingsStore(folder.File("settings.json"));
        var saved = new AgentSettings(@"D:\Team 5669\Armory", false, "spaceWhite");
        store.Save(saved);
        Assert.Equal(saved, store.Load());
        Assert.Equal(saved, new SettingsStore(folder.File("settings.json")).Load());
        using var document = JsonDocument.Parse(File.ReadAllBytes(folder.File("settings.json")));
        Assert.Equal(["startAtSignIn", "theme", "vaultRoot"], document.RootElement.EnumerateObject().Select(p => p.Name).Order(StringComparer.Ordinal));
        Assert.Equal(@"D:\Team 5669\Armory", document.RootElement.GetProperty("vaultRoot").GetString());
        Assert.False(document.RootElement.GetProperty("startAtSignIn").GetBoolean());
        Assert.Equal("spaceWhite", document.RootElement.GetProperty("theme").GetString());
        Assert.False(File.Exists(folder.File("settings.json.pending")));
    }

    [Theory]
    [InlineData("dark")]
    [InlineData("Idea")]
    [InlineData("space-white")]
    [InlineData("")]
    public void Unknown_theme_falls_back_to_system(string theme)
    {
        using var folder = new TempFolder();
        File.WriteAllText(folder.File("settings.json"), JsonSerializer.Serialize(new { vaultRoot = @"C:\IDEA\Armory", startAtSignIn = true, theme }));
        Assert.Equal("system", new SettingsStore(folder.File("settings.json")).Load().Theme);
        Assert.Equal("system", Themes.Normalize(theme));
    }

    [Fact]
    public void Theme_follows_windows_app_mode_unless_overridden()
    {
        Assert.Equal("spaceWhite", Themes.Effective("system", appsUseLightTheme: true));
        Assert.Equal("idea", Themes.Effective("system", appsUseLightTheme: false));
        Assert.Equal("idea", Themes.Effective("idea", appsUseLightTheme: true));
        Assert.Equal("spaceWhite", Themes.Effective("spaceWhite", appsUseLightTheme: false));
        Assert.Equal("idea", Themes.Effective("nonsense", appsUseLightTheme: false));
    }

    [Theory]
    [InlineData("{ not json")]
    [InlineData("[1, 2, 3]")]
    [InlineData("{\"vaultRoot\": 5, \"startAtSignIn\": \"yes\", \"theme\": null}")]
    public void Unreadable_values_fall_back_to_defaults(string json)
    {
        using var folder = new TempFolder();
        File.WriteAllText(folder.File("settings.json"), json);
        Assert.Equal(AgentSettings.Default, new SettingsStore(folder.File("settings.json")).Load());
    }

    [Theory]
    [InlineData(@"C:\IDEA\Armory", @"C:\IDEA\Armory")]
    [InlineData(@"d:/Team 5669/Armory/", @"D:\Team 5669\Armory")]
    [InlineData(@"  E:\CAD  ", @"E:\CAD")]
    public void Vault_roots_on_a_local_drive_are_accepted(string candidate, string expected)
    {
        Assert.True(AgentSettings.TryNormalizeVaultRoot(candidate, out var root, out var problem), problem);
        Assert.Equal(expected, root);
    }

    [Theory]
    [InlineData(@"C:\")]
    [InlineData(@"C:")]
    [InlineData(@"\\server\share\Armory")]
    [InlineData(@"Armory")]
    [InlineData(@"C:\IDEA\CON")]
    [InlineData(@"C:\IDEA\Armory.")]
    [InlineData(@"C:\IDEA\Arm|ory")]
    [InlineData("")]
    public void Unusable_vault_roots_are_refused_with_a_plain_reason(string candidate)
    {
        Assert.False(AgentSettings.TryNormalizeVaultRoot(candidate, out var root, out var problem));
        Assert.Null(root);
        Assert.False(string.IsNullOrWhiteSpace(problem));
        Assert.DoesNotContain("\u2014", problem);
    }

    [Fact]
    public void An_unusable_saved_vault_root_loads_as_the_default()
    {
        using var folder = new TempFolder();
        File.WriteAllText(folder.File("settings.json"), "{\"vaultRoot\":\"C:\\\\\",\"startAtSignIn\":false,\"theme\":\"idea\"}");
        Assert.Equal(new AgentSettings(@"C:\IDEA\Armory", false, "idea"), new SettingsStore(folder.File("settings.json")).Load());
    }

    [Fact]
    public void Command_line_flags_are_read_case_insensitively()
    {
        Assert.Equal(new AgentCommandLine(true, false, false), AgentCommandLine.Parse(["--background"]));
        Assert.Equal(new AgentCommandLine(false, true, false), AgentCommandLine.Parse(["--QUIT"]));
        Assert.Equal(new AgentCommandLine(false, false, true), AgentCommandLine.Parse(["--check"]));
        Assert.Equal(new AgentCommandLine(false, false, false), AgentCommandLine.Parse([]));
    }
}
