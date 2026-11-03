namespace Armory.Agent.Tests;

public sealed class StableLauncherTests
{
    private const string Root = @"C:\Users\student\AppData\Local\IDEAArmory";
    private const string Exe = Root + @"\current\IdeaArmory.exe";
    private const string Launcher = Root + @"\IDEA Armory.exe";

    [Fact]
    public void The_launcher_is_outside_current_and_forwards_to_Velopack()
    {
        bool Exists(string path) => path.Equals(Root + @"\Update.exe", StringComparison.OrdinalIgnoreCase) || path.Equals(Launcher, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(Launcher, StableLauncher.For(Exe, Exists));
        Assert.Equal(Launcher, StableLauncher.For(Exe.Replace('\\', '/'), Exists));
        Assert.Equal(Exe, StableLauncher.For(Exe, _ => false));
        const string legacy = @"C:\Users\student\AppData\Local\Programs\IDEA Armory\IdeaArmory.exe";
        Assert.Equal(legacy, StableLauncher.For(legacy, Exists));
    }

    [Fact]
    public void An_update_cannot_leave_a_stable_pin_with_a_version_folder_icon_or_old_arguments()
    {
        var pins = new[]
        {
            new TaskbarPins.Pin("icon.lnk", Launcher, ShellIdentity.AppId, Exe),
            new TaskbarPins.Pin("args.lnk", Launcher, ShellIdentity.AppId, Launcher, Arguments: "--processStart IdeaArmory.exe"),
            new TaskbarPins.Pin("good.lnk", Launcher, ShellIdentity.AppId, Launcher, Arguments: ""),
        };
        Assert.Equal(["icon.lnk", "args.lnk"], TaskbarPins.Plan(pins, Launcher, _ => true).Select(f => f.File));
    }
}
