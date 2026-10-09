using System.Runtime.Versioning;
using System.Text.Json;
using Armory.Agent.Engine.View;
using Armory.Platform.Windows;
using Microsoft.Win32;

namespace Armory.Agent.Tests;

// The host's shell rules (AgentHost.Shell.cs): the badges' pace, the folders Force check in
// shows on, the opened files a notification names, the badges row in Settings, Armory's
// identity for notifications and links, and the command line.
public sealed class HostShellTests
{
    private static readonly DateTimeOffset Start = new(2026, 10, 9, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Badges_are_published_at_most_every_500_ms_and_a_change_meanwhile_is_never_lost()
    {
        var pace = new PublishPace(PublishPace.Every);
        // The first change starts one at once; nine more in the next 100 ms start nothing.
        Assert.Equal(Start, pace.Ask(Start));
        for (var i = 1; i < 10; i++) Assert.Null(pace.Ask(Start + TimeSpan.FromMilliseconds(i * 10)));
        pace.Started(Start + TimeSpan.FromMilliseconds(100));
        // Changes while it runs: one more after it, 500 ms after this one began.
        Assert.Null(pace.Ask(Start + TimeSpan.FromMilliseconds(150)));
        Assert.Null(pace.Ask(Start + TimeSpan.FromMilliseconds(160)));
        Assert.Equal(Start + TimeSpan.FromMilliseconds(600), pace.Finished(Start + TimeSpan.FromMilliseconds(200)));
        pace.Started(Start + TimeSpan.FromMilliseconds(600));
        // Nothing changed meanwhile: nothing more.
        Assert.Null(pace.Finished(Start + TimeSpan.FromMilliseconds(650)));
        // A change long after starts at once again.
        Assert.Equal(Start + TimeSpan.FromSeconds(5), pace.Ask(Start + TimeSpan.FromSeconds(5)));
        // A change soon after one waits for the rest of the 500 ms.
        var soon = new PublishPace(PublishPace.Every);
        soon.Ask(Start);
        soon.Started(Start);
        Assert.Null(soon.Finished(Start + TimeSpan.FromMilliseconds(50)));
        Assert.Equal(Start + TimeSpan.FromMilliseconds(500), soon.Ask(Start + TimeSpan.FromMilliseconds(100)));
    }

    [Fact]
    public void Force_check_in_shows_on_the_folders_of_projects_this_account_may_force_check_in()
    {
        var mentor = ShellViews.View(ShellViews.Row("Robot 2027/Arm/Plate.SLDPRT"), ShellViews.Row("Class 2026/Lesson.SLDPRT"));
        Assert.Equal(["Class 2026", "Robot 2027"], HostShell.ForceCheckInFolders(mentor));
        var student = ShellViews.View([ShellViews.Row("Robot 2027/Arm/Plate.SLDPRT")], canTakeBack: false);
        Assert.Empty(HostShell.ForceCheckInFolders(student));
        var signedOut = ShellViews.View([ShellViews.Row("Robot 2027/Arm/Plate.SLDPRT")], Connections.SignedOut);
        Assert.Empty(HostShell.ForceCheckInFolders(signedOut));
        // A project with no files yet is its name.
        var empty = mentor with { Projects = [.. mentor.Projects, new ProjectView(Guid.NewGuid().ToString(), "Outreach", false, "mentor", true, [], 2025, 0)] };
        Assert.Equal(["Class 2026", "Outreach", "Robot 2027"], HostShell.ForceCheckInFolders(empty));
    }

    [Fact]
    public void An_opened_file_is_named_with_who_has_it_and_one_checked_out_here_asks_nothing()
    {
        var view = ShellViews.View(ShellViews.Row("Robot 2027/Plate.SLDPRT"), ShellViews.Row("Robot 2027/Gear.SLDPRT", ShellViews.HeldBy("Maria Lopez")),
            ShellViews.Row("Robot 2027/Hub.SLDPRT", ShellViews.OnMyOther()), ShellViews.Row("Robot 2027/Arm.SLDPRT", ShellViews.Mine));
        var prompts = HostShell.OpenPrompts(view, ["robot 2027/plate.sldprt", "Robot 2027/Gear.SLDPRT", "Robot 2027/Hub.SLDPRT", "Robot 2027/Arm.SLDPRT", "Robot 2027/Gone.SLDPRT"]);
        Assert.Equal(
        [
            new OpenPromptInfo("Robot 2027/Plate.SLDPRT", "Plate.SLDPRT", true, null),
            new OpenPromptInfo("Robot 2027/Gear.SLDPRT", "Gear.SLDPRT", false, "Maria Lopez on LAB-PC-07"),
            new OpenPromptInfo("Robot 2027/Hub.SLDPRT", "Hub.SLDPRT", false, "you on LAPTOP-9"),
        ], prompts);
        Assert.Empty(HostShell.OpenPrompts(view, []));
    }

    [Fact]
    public void The_settings_view_carries_the_badges_row_with_the_states_badge_health_names()
    {
        var settings = new SettingsView(@"C:\IDEA\Armory", true, "system", new BadgesView(BadgesStates.Off, BadgeHealth.OffLine));
        using var json = JsonDocument.Parse(JsonSerializer.Serialize(settings, BridgeMessages.Json));
        Assert.Equal(["vaultRoot", "startAtSignIn", "theme", "badges", "sharedComputer"], json.RootElement.EnumerateObject().Select(p => p.Name));
        var badges = json.RootElement.GetProperty("badges");
        Assert.Equal(["state", "line"], badges.EnumerateObject().Select(p => p.Name));
        Assert.Equal("off", badges.GetProperty("state").GetString());
        Assert.Equal("Armory's status isn't shown on file icons on this computer. Turning it on needs an administrator once.", badges.GetProperty("line").GetString());
        // Before the host has checked: null, and the page draws no row.
        Assert.Equal(JsonValueKind.Null, JsonDocument.Parse(JsonSerializer.Serialize(new SettingsView(@"C:\IDEA\Armory", true, "system"), BridgeMessages.Json)).RootElement.GetProperty("badges").ValueKind);
        // The page's words for the states are BadgeHealth's keys, one for one.
        string[] states = [BadgesStates.Off, BadgesStates.On, BadgesStates.AfterSignIn, BadgesStates.Crowded, BadgesStates.Partial, BadgesStates.Broken];
        Assert.Equal(states, Enum.GetValues<BadgeHealthState>().Select(s => new BadgeHealthReport(s, "", null, 0, []).Key));
        Assert.Equal([true, false, false, false, false, true], Enum.GetValues<BadgeHealthState>().Select(s => new BadgeHealthReport(s, "", null, 0, []).CanTurnOn));
        Assert.Equal("Nothing changed. This one step needs an administrator's password.", HostShell.PasswordNeeded);
        Assert.Equal(1223, HostShell.Canceled);
    }

    [Fact]
    public void Command_line_keeps_its_flags_and_a_link_as_windows_gave_it()
    {
        Assert.Equal(new AgentCommandLine(true, false, false), AgentCommandLine.Parse(["--BACKGROUND"]));
        Assert.Equal(new AgentCommandLine(false, true, false), AgentCommandLine.Parse(["--quit"]));
        Assert.Equal(new AgentCommandLine(false, false, true), AgentCommandLine.Parse([" --Check "]));
        Assert.Equal(new AgentCommandLine(false, false, false), AgentCommandLine.Parse([]));
        const string link = "IDEA-Armory:act?t=Qm9yZWQ3T2dfZXhhbXBsZQ&a=checkout";
        Assert.Equal(new AgentCommandLine(false, false, false, link), AgentCommandLine.Parse([link]));
        // A link is a click: the window opens even beside --background.
        Assert.Equal(new AgentCommandLine(false, false, false, link), AgentCommandLine.Parse(["--background", " " + link + " "]));
        Assert.Equal(new AgentCommandLine(false, false, false, "idea-armory:"), AgentCommandLine.Parse(["idea-armory:", "idea-armory:second"]));
        Assert.Null(AgentCommandLine.Parse([@"C:\IDEA\Armory\Plate.SLDPRT"]).Link);
    }

    [Fact]
    public void Armory_names_itself_and_its_link_scheme_with_exactly_these_values()
    {
        var layout = ShellIdentity.Layout(@"C:\Users\ana\AppData\Local\Programs\IDEA Armory\");
        Assert.Equal(
        [
            new ShellRegistryValue(@"AppUserModelId\IdeaBosco.Armory", "DisplayName", "IDEA Armory"),
            new ShellRegistryValue(@"AppUserModelId\IdeaBosco.Armory", "IconUri", @"C:\Users\ana\AppData\Local\Programs\IDEA Armory\Assets\armory.ico"),
            new ShellRegistryValue("idea-armory", "", "URL:IDEA Armory"),
            new ShellRegistryValue("idea-armory", "URL Protocol", ""),
            new ShellRegistryValue(@"idea-armory\DefaultIcon", "", "\"C:\\Users\\ana\\AppData\\Local\\Programs\\IDEA Armory\\IdeaArmory.exe\",0"),
            new ShellRegistryValue(@"idea-armory\shell\open\command", "", "\"C:\\Users\\ana\\AppData\\Local\\Programs\\IDEA Armory\\IdeaArmory.exe\" \"%1\""),
        ], layout);
        // Compare before write: nothing when it all matches, only what differs otherwise.
        var existing = layout.ToDictionary(v => (v.Key, v.Name), v => v.Value);
        Assert.Empty(ShellIdentity.Changes(existing, layout));
        existing[("idea-armory\\shell\\open\\command", "")] = "\"D:\\Old\\IdeaArmory.exe\" \"%1\"";
        existing.Remove(("idea-armory", "URL Protocol"));
        Assert.Equal([layout[3], layout[5]], ShellIdentity.Changes(existing, layout));
    }

    [Fact]
    public void Only_the_installed_copy_takes_the_registrations()
    {
        const string install = @"C:\Users\ana\AppData\Local\Programs\IDEA Armory";
        var real = new AgentPaths(@"C:\Users\ana\AppData\Local\IDEA Armory", false);
        Assert.True(ShellIdentity.IsInstalledCopy(real, install + @"\IdeaArmory.exe", install));
        Assert.True(ShellIdentity.IsInstalledCopy(real, install.ToLowerInvariant() + @"\ideaarmory.exe", install + @"\"));
        Assert.False(ShellIdentity.IsInstalledCopy(new AgentPaths(@"C:\Temp\Test", true), install + @"\IdeaArmory.exe", install));
        Assert.False(ShellIdentity.IsInstalledCopy(real, @"C:\src\idea-armory\src\Armory.Agent\bin\Debug\IdeaArmory.exe", install));
        Assert.False(ShellIdentity.IsInstalledCopy(real, @"C:\Program Files\dotnet\dotnet.exe", install));
        Assert.False(ShellIdentity.IsInstalledCopy(real, null, install));
        Assert.Equal(Path.Combine("L", "Programs", "IDEA Armory"), ShellIdentity.InstallFolder("L"));
    }

    // A private key stands for HKCU\Software\Classes; it is removed afterwards.
    [WindowsFact]
    [SupportedOSPlatform("windows")]
    public void The_registration_is_written_once_and_then_left_alone()
    {
        var name = @"Software\IDEA Armory Tests\" + Guid.NewGuid().ToString("N");
        try
        {
            using var classes = Registry.CurrentUser.CreateSubKey(name, writable: true);
            var layout = ShellIdentity.Layout(@"C:\Users\ana\AppData\Local\Programs\IDEA Armory");
            Assert.True(ShellIdentity.Apply(classes, layout));
            Assert.False(ShellIdentity.Apply(classes, layout));
            using (var command = classes.OpenSubKey(@"idea-armory\shell\open\command"))
                Assert.Equal("\"C:\\Users\\ana\\AppData\\Local\\Programs\\IDEA Armory\\IdeaArmory.exe\" \"%1\"", command!.GetValue(""));
            using (var scheme = classes.OpenSubKey("idea-armory"))
                Assert.Equal("", scheme!.GetValue("URL Protocol"));
            using (var appId = classes.CreateSubKey(ShellIdentity.AppIdKey, writable: true)) appId.SetValue("DisplayName", "Something else");
            Assert.True(ShellIdentity.Apply(classes, layout));
            using (var appId = classes.OpenSubKey(ShellIdentity.AppIdKey)) Assert.Equal("IDEA Armory", appId!.GetValue("DisplayName"));
        }
        finally { Registry.CurrentUser.DeleteSubKeyTree(@"Software\IDEA Armory Tests", throwOnMissingSubKey: false); }
    }

    // The flash-drive install's shortcut has no AppUserModelID; Armory gives it one, only when
    // it points to this IdeaArmory.exe.
    [WindowsFact]
    [SupportedOSPlatform("windows")]
    public void The_start_menu_shortcut_gets_the_app_id_only_when_it_points_here()
    {
        using var folder = new TempFolder();
        var exe = folder.File("IdeaArmory.exe");
        File.WriteAllBytes(exe, [0x4D, 0x5A]);
        var other = folder.File("Other.exe");
        File.WriteAllBytes(other, [0x4D, 0x5A]);
        string Shortcut(string name, string target)
        {
            var path = folder.File(name);
            dynamic shell = Activator.CreateInstance(Type.GetTypeFromProgID("WScript.Shell")!)!;
            dynamic link = shell.CreateShortcut(path);
            link.TargetPath = target;
            link.Save();
            return path;
        }
        var ours = Shortcut("IDEA Armory.lnk", exe);
        var theirs = Shortcut("Other.lnk", other);
        // As Armory does it: on a thread of its own apartment.
        Exception? failed = null;
        var thread = new Thread(() =>
        {
            try
            {
                Assert.True(ShortcutAppId.Stamp(ours, exe, ShellIdentity.AppId));
                Assert.Equal(ShellIdentity.AppId, ShortcutAppId.ReadAppId(ours));
                Assert.False(ShortcutAppId.Stamp(ours, exe, ShellIdentity.AppId));
                Assert.False(ShortcutAppId.Stamp(theirs, exe, ShellIdentity.AppId));
                Assert.Null(ShortcutAppId.ReadAppId(theirs));
                Assert.False(ShortcutAppId.Stamp(folder.File("Missing.lnk"), exe, ShellIdentity.AppId));
            }
            catch (Exception error) { failed = error; }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        if (failed is not null) throw failed;
    }
}
