namespace Armory.Agent.Tests;

// 0.4.0: Armory updates itself (Updates.cs). What decides where a copy came from, which versions
// are newer, and what the feed is, without Windows or the network.
public sealed class UpdatesTests
{
    [Fact]
    public void A_copy_Velopack_installed_is_known_by_its_current_folder_and_Update_exe()
    {
        var root = @"C:\Users\IDEA\AppData\Local\IDEAArmory";
        bool Exists(string path) => path.Equals(root + @"\Update.exe", StringComparison.OrdinalIgnoreCase);
        Assert.True(Updates.IsVelopackCopy(root + @"\current\IdeaArmory.exe", Exists));
        Assert.True(Updates.IsVelopackCopy(root.ToLowerInvariant() + @"\CURRENT\ideaarmory.exe", Exists));
        // The older install, a developer's build, another program, no Update.exe: not one.
        Assert.False(Updates.IsVelopackCopy(@"C:\Users\IDEA\AppData\Local\Programs\IDEA Armory\IdeaArmory.exe", Exists));
        Assert.False(Updates.IsVelopackCopy(@"C:\src\idea-armory\src\Armory.Agent\bin\Debug\IdeaArmory.exe", Exists));
        Assert.False(Updates.IsVelopackCopy(root + @"\current\ArmoryShell.exe", Exists));
        Assert.False(Updates.IsVelopackCopy(@"D:\Other\current\IdeaArmory.exe", Exists));
        Assert.False(Updates.IsVelopackCopy(null, Exists));
    }

    [Fact]
    public void Versions_compare_by_their_numbers()
    {
        Assert.Equal(new Version(0, 4, 1), Updates.Release("0.4.1"));
        Assert.Equal(new Version(0, 4, 1), Updates.Release("0.4.1-beta.2"));
        Assert.Equal(new Version(1, 0, 0), Updates.Release("1.0"));
        Assert.True(Updates.Release("0.4.10") > Updates.Release("0.4.9"));
        Assert.True(Updates.Release("0.4.0") > Updates.Release("0.3.3"));
        Assert.Null(Updates.Release("unknown"));
        Assert.Null(Updates.Release(""));
    }

    // The feed is GitHub's latest-release download address, never the rate-limited API.
    [Fact]
    public void The_feed_is_the_latest_release_and_never_the_GitHub_API()
    {
        Assert.Equal("https://github.com/pina-hash/idea-armory/releases/latest/download/", Updates.Feed.AbsoluteUri);
        Assert.Equal("https://github.com/pina-hash/idea-armory/releases/latest/download/releases.win.json", new Uri(Updates.Feed, "releases.win.json").AbsoluteUri);
        Assert.Equal("https://github.com/pina-hash/idea-armory/releases/latest/download/IDEAArmory-win-Setup.exe", new Uri(Updates.Feed, Updates.SetupName).AbsoluteUri);
        Assert.DoesNotContain("api.github.com", Updates.Feed.Host, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void The_words_say_when_the_new_version_starts()
    {
        Assert.Equal("Armory 0.4.1 is ready. It starts the next time Armory opens.", Updates.ReadyWords("0.4.1"));
        Assert.Equal("Armory 0.4.1 is installing. Armory closes for a moment, then opens again by itself or at your next sign-in.", Updates.MovingWords("0.4.1"));
        foreach (var words in new[] { Updates.ReadyWords("0.4.1"), Updates.MovingWords("0.4.1") })
            Assert.DoesNotContain("\u2014", words);
    }

    // Retiring the older install removes both of its Apps entries, and nothing of the vault's.
    [Fact]
    public void Retiring_the_older_install_names_both_of_its_Apps_entries()
    {
        Assert.Equal(2, LegacyInstall.UninstallKeys.Length);
        Assert.Contains(@"Software\Microsoft\Windows\CurrentVersion\Uninstall\IDEA Armory", LegacyInstall.UninstallKeys);
        Assert.Contains(@"Software\Microsoft\Windows\CurrentVersion\Uninstall\{28A1D010-82E3-4294-9676-83AC0AA1F5D3}_is1", LegacyInstall.UninstallKeys);
        Assert.EndsWith(@"Programs" + Path.DirectorySeparatorChar + "IDEA Armory", ShellIdentity.InstallFolder("X"));
    }
}
