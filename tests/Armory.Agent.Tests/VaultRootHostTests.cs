using Armory.Agent.Engine.View;
using Armory.TestSupport;
using static Armory.Agent.Tests.Lab;

namespace Armory.Agent.Tests;

// The Armory folder rule through the real AgentHost (0.4.2, the field round of 2026-10-22, item 1):
// Settings corrects a project folder to the Armory folder around it in one sentence; a computer
// started in one runs no engine there, shows one card (and no "outside every project" flood), and
// Fix it moves Armory back and runs it there.
public sealed class VaultRootHostTests
{
    private const string InProject = @"C:\IDEA\Armory\Robot 2027";

    private sealed class ArmoryAt(string folder) : IVaultFolderFacts
    {
        public bool HoldsArmory(string f) => string.Equals(f, folder, StringComparison.OrdinalIgnoreCase);
        public IReadOnlyCollection<string> Children(string f) => [];
    }

    [PostgresFact]
    public async Task Settings_corrects_a_project_folder_to_the_Armory_folder_around_it()
    {
        await using var lab = await StartAsync(shared: false);
        lab.Facts = new ArmoryAt(Shared);
        await lab.RestartAsync();
        lab.Browser.Next = Alex;
        await lab.Host.ConnectAsync();
        await Until(() => lab.View.Connection == Connections.SignedIn && lab.View.Projects.Count > 0, "Alex signed in with Robot 2027");

        var words = await lab.Host.SaveSettingsAsync(InProject, true, "idea");

        Assert.Equal(@"C:\IDEA\Armory\Robot 2027 is the Robot 2027 project folder inside your Armory folder, so Armory uses C:\IDEA\Armory, the folder that holds all your projects.", words);
        Assert.Equal(Shared, lab.Host.Settings.VaultRoot);
        Assert.Contains("\"vaultRoot\": \"C:\\\\IDEA\\\\Armory\"", File.ReadAllText(lab.Paths.SettingsFile));
        Assert.DoesNotContain(lab.View.Notices, n => n.Kind == NoticeKinds.VaultRoot);
        Assert.Equal(Shared, lab.Host.VaultFolder);
        // An ordinary folder is saved as it is, with nothing to say.
        Assert.Null(await lab.Host.SaveSettingsAsync(@"C:\IDEA\Armory2", true, "idea"));
        Assert.Equal(@"C:\IDEA\Armory2", lab.Host.Settings.VaultRoot);
    }

    [PostgresFact]
    public async Task A_computer_started_in_a_project_folder_runs_nothing_there_until_Fix_it()
    {
        await using var lab = await StartAsync(shared: false);
        lab.Browser.Next = Alex;
        await lab.Host.ConnectAsync();
        await Until(() => lab.View.Connection == Connections.SignedIn && lab.View.Projects.Count > 0, "Alex signed in with Robot 2027");
        // IDEA-06: settings.json names the project folder, from before the rule, and Armory ran
        // there already (its own records name the project).
        lab.Folder(InProject).State.Save(lab.Folder(Shared).State.Load()!);
        File.WriteAllText(lab.Paths.SettingsFile, "{\"vaultRoot\":\"C:\\\\IDEA\\\\Armory\\\\Robot 2027\",\"startAtSignIn\":true,\"theme\":\"idea\"}");
        lab.Facts = new ArmoryAt(Shared);
        var made = lab.RuntimesMade;
        await lab.RestartAsync();

        // No engine ran in it; one card, first, with Fix it, and the folder named.
        Assert.Equal(made, lab.RuntimesMade);
        var view = lab.View;
        Assert.Equal("Armory is waiting for you to fix its folder.", view.Sync.Line);
        Assert.Equal("Nothing moves until Armory's folder is fixed.", view.Sync.Now);
        var card = view.Notices[0];
        Assert.Equal(NoticeKinds.VaultRoot, card.Kind);
        Assert.Equal(NoticeTones.Bad, card.Tone);
        Assert.Equal("Armory's folder is inside the Robot 2027 project folder", card.Title);
        Assert.Equal(@"Fix it moves Armory back to C:\IDEA\Armory and puts every file where it belongs, without uploading anything twice or losing a change.", card.Detail);
        Assert.Equal(new NoticeActionView("Fix it", BridgeMessages.RepairVaultRoot, []), card.Action);
        Assert.Single(view.Notices, n => n.Kind == NoticeKinds.VaultRoot);
        Assert.DoesNotContain(view.Notices, n => n.Kind == NoticeKinds.OutsideProject);
        Assert.Empty(lab.Repairs);

        var fixedIt = await lab.Host.RepairVaultRootAsync();

        Assert.True(fixedIt.Ok, fixedIt.Message);
        Assert.Equal(@"Fixed. Armory uses C:\IDEA\Armory again, and every file is where it belongs.", fixedIt.Message);
        Assert.Equal([(InProject, Shared)], lab.Repairs);
        Assert.Equal(Shared, lab.Host.Settings.VaultRoot);
        Assert.Contains("\"vaultRoot\": \"C:\\\\IDEA\\\\Armory\"", File.ReadAllText(lab.Paths.SettingsFile));
        await Until(() => lab.Host.VaultFolder == Shared && lab.View.Connection == Connections.SignedIn, "Armory running in C:\\IDEA\\Armory");
        Assert.DoesNotContain(lab.View.Notices, n => n.Kind == NoticeKinds.VaultRoot);
        Assert.Equal(made + 1, lab.RuntimesMade);
        // Clicked again: nothing to fix, nothing folded twice.
        Assert.Equal("Armory's folder is fine. Nothing to fix.", (await lab.Host.RepairVaultRootAsync()).Message);
        Assert.Single(lab.Repairs);
    }

    [PostgresFact]
    public async Task Fix_it_stopped_part_way_runs_nothing_and_keeps_the_card()
    {
        await using var lab = await StartAsync(shared: false);
        File.WriteAllText(lab.Paths.SettingsFile, "{\"vaultRoot\":\"C:\\\\IDEA\\\\Armory\\\\Robot 2027\",\"startAtSignIn\":true,\"theme\":\"idea\"}");
        lab.Facts = new ArmoryAt(Shared);
        lab.RepairAnswer = new VaultRepairResult(1, 0, 0, null, "SolidWorks still has Plate.SLDPRT open. Close SolidWorks, then click Fix it again.");
        await lab.RestartAsync();
        var made = lab.RuntimesMade;

        var stopped = await lab.Host.RepairVaultRootAsync();

        Assert.False(stopped.Ok);
        Assert.Equal("SolidWorks still has Plate.SLDPRT open. Close SolidWorks, then click Fix it again.", stopped.Message);
        Assert.Equal(InProject, lab.Host.Settings.VaultRoot);
        Assert.Equal(made, lab.RuntimesMade);
        Assert.Equal(NoticeKinds.VaultRoot, lab.View.Notices[0].Kind);
        Assert.Equal("Armory is waiting for you to fix its folder.", lab.View.Sync.Line);
    }
}
