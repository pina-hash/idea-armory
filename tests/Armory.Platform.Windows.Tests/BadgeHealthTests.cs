namespace Armory.Platform.Windows.Tests;

public sealed class BadgeHealthTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 9, 12, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset SignedIn = Now.AddHours(-2);
    private static readonly DateTimeOffset Installed = Now.AddDays(-30);

    private static OverlayIdentifier Other(string name) => new(name, "{00000000-0000-0000-0000-000000000000}", @"C:\Program Files\Other\other.dll", true);

    private static IEnumerable<OverlayIdentifier> Ours(bool dllExists = true) =>
        BadgeHealth.Badges.Select(b => new OverlayIdentifier(b.Key, b.Clsid, @"C:\Program Files\IDEA Armory Badges\0.3.3\ArmoryBadges.dll", dllExists));

    private static OverlayIdentifier[] OneDrive(int count) => Enumerable.Range(1, count).Select(i => Other(" OneDrive" + i)).ToArray();

    private static readonly OverlayIdentifier[] Windows = [Other("EnhancedStorageShell"), Other("Offline Files"), Other("SharingPrivate")];

    private static BadgeHealthFacts Facts(IEnumerable<OverlayIdentifier> list, string? version = "0.3.3", DateTimeOffset? installed = null,
        DateTimeOffset? explorer = null, IReadOnlyDictionary<string, DateTimeOffset>? seen = null, string? format = "1", DateTimeOffset? now = null) =>
        new(version, format, installed ?? Installed, list.ToArray(), seen ?? new Dictionary<string, DateTimeOffset> { ["Attention"] = SignedIn.AddSeconds(5) },
            explorer ?? SignedIn, now ?? Now);

    [Fact]
    public void Not_installed_is_off_and_offers_turn_on()
    {
        var report = BadgeHealth.Decide(Facts(OneDrive(7).Concat(Windows), version: null, seen: new Dictionary<string, DateTimeOffset>()));
        Assert.Equal(BadgeHealthState.Off, report.State);
        Assert.Equal("off", report.Key);
        Assert.True(report.CanTurnOn);
        Assert.Equal("Armory's status isn't shown on file icons on this computer. Turning it on needs an administrator once.", report.Line);
    }

    [Fact]
    public void Loaded_this_session_is_on()
    {
        var report = BadgeHealth.Decide(Facts(Ours().Concat(OneDrive(7)).Concat(Windows)));
        Assert.Equal(BadgeHealthState.On, report.State);
        Assert.Equal("Armory's status shows on file icons.", report.Line);
        Assert.Equal(4, report.Shown);
        Assert.Empty(report.AppsAhead);
        Assert.False(report.CanTurnOn);
    }

    [Fact]
    public void Installed_after_explorer_started_waits_for_the_next_sign_in()
    {
        var report = BadgeHealth.Decide(Facts(Ours().Concat(OneDrive(7)), installed: SignedIn.AddMinutes(30), seen: new Dictionary<string, DateTimeOffset>()));
        Assert.Equal(BadgeHealthState.AfterSignIn, report.State);
        Assert.Equal("afterSignIn", report.Key);
        Assert.Equal("Armory's status shows on file icons after you sign out of Windows and back in.", report.Line);
        // A heartbeat from an earlier session does not count.
        var old = BadgeHealth.Decide(Facts(Ours(), installed: SignedIn.AddMinutes(30), seen: new Dictionary<string, DateTimeOffset> { ["Mine"] = SignedIn.AddDays(-1) }));
        Assert.Equal(BadgeHealthState.AfterSignIn, old.State);
        // No Explorer in this session (a service or a runner): after the next sign-in.
        Assert.Equal(BadgeHealthState.AfterSignIn, BadgeHealth.Decide(Facts(Ours()) with { ExplorerStarted = null }).State);
    }

    [Fact]
    public void Twelve_badges_ahead_is_crowded_and_names_the_apps()
    {
        OverlayIdentifier[] dropbox = [Other("   DropboxExt01"), Other("   DropboxExt02"), Other("   DropboxExt03"), Other("   DropboxExt04"), Other("   DropboxExt05")];
        var report = BadgeHealth.Decide(Facts(dropbox.Concat(OneDrive(7)).Concat(Ours()).Concat(Windows)));
        Assert.Equal(BadgeHealthState.Crowded, report.State);
        Assert.Equal("Windows isn't showing Armory's badges because 12 badges from other apps come first (Dropbox, OneDrive). Windows shows only 11.", report.Line);
        Assert.Equal(0, report.Shown);
        Assert.Equal(new[] { "Dropbox", "OneDrive" }, report.AppsAhead);
    }

    [Fact]
    public void Nine_badges_ahead_is_partial_with_two_of_ours_shown()
    {
        var ahead = OneDrive(7).Concat([Other("  DropboxExt1"), Other("  DropboxExt2")]);
        var report = BadgeHealth.Decide(Facts(ahead.Concat(Ours()).Concat(Windows)));
        Assert.Equal(BadgeHealthState.Partial, report.State);
        Assert.Equal("Windows shows only some of Armory's badges because 9 badges from other apps come first (OneDrive, Dropbox).", report.Line);
        Assert.Equal(2, report.Shown);
        // Seven ahead: all four of ours fit (7 + 4 = 11), Synced the last one in.
        Assert.Equal(BadgeHealthState.On, BadgeHealth.Decide(Facts(OneDrive(7).Concat(Ours()).Concat(Windows))).State);
        // Eight ahead: Synced, which sorts last of ours, is the one pushed out.
        var eight = BadgeHealth.Decide(Facts(OneDrive(7).Append(Other(" AccExtIco1")).Concat(Ours())));
        Assert.Equal(BadgeHealthState.Partial, eight.State);
        Assert.Equal(3, eight.Shown);
        Assert.Equal(new[] { "OneDrive", "AccExt" }, eight.AppsAhead);
    }

    [Fact]
    public void A_missing_dll_or_key_is_broken_and_offers_turn_on()
    {
        var missing = BadgeHealth.Decide(Facts(Ours(dllExists: false)));
        Assert.Equal(BadgeHealthState.Broken, missing.State);
        Assert.Equal("Armory's badges are installed, but a file is missing. Ask an administrator to turn them on again.", missing.Line);
        Assert.Contains("DLL is missing", missing.Detail);
        Assert.True(missing.CanTurnOn);
        var noSynced = BadgeHealth.Decide(Facts(Ours().Where(o => !o.Name.Contains("Synced"))));
        Assert.Equal(BadgeHealthState.Broken, noSynced.State);
        Assert.Contains("Synced badge is not registered", noSynced.Detail);
        var noVersion = BadgeHealth.Decide(Facts(Ours(), version: null));
        Assert.Equal(BadgeHealthState.Broken, noVersion.State);
        var newerFormat = BadgeHealth.Decide(Facts(Ours(), format: "2"));
        Assert.Equal(BadgeHealthState.Broken, newerFormat.State);
        Assert.Contains("format 2", newerFormat.Detail);
        var wrongClsid = BadgeHealth.Decide(Facts(Ours().Select(o => o.Name.Contains("Mine") ? o with { Clsid = "{11111111-1111-1111-1111-111111111111}" } : o)));
        Assert.Equal(BadgeHealthState.Broken, wrongClsid.State);
    }

    [Fact]
    public void Explorer_that_never_asked_for_our_icons_is_broken_after_ten_minutes()
    {
        var none = new Dictionary<string, DateTimeOffset>();
        var early = BadgeHealth.Decide(Facts(Ours(), explorer: Now.AddMinutes(-3), seen: none));
        Assert.Equal(BadgeHealthState.On, early.State);
        Assert.NotNull(early.Detail);
        var late = BadgeHealth.Decide(Facts(Ours(), explorer: Now.AddMinutes(-11), seen: none));
        Assert.Equal(BadgeHealthState.Broken, late.State);
        Assert.Equal("explorer.exe did not ask for Armory's badge icons", late.Detail);
    }

    [Fact]
    public void App_names_drop_spaces_numbers_and_ext_or_ico_once_each()
    {
        Assert.Equal(new[] { "OneDrive", "Dropbox", "AccExt", "GoogleDriveSynced", "Tortoise" },
            BadgeHealth.AppNames([" OneDrive1", " OneDrive7", "   DropboxExt01", " AccExtIco1", "GoogleDriveSynced", "Tortoise1", "onedrive2", "   "]));
    }

    [WindowsFact]
    public void This_computer_gives_a_report_without_changing_anything()
    {
        var facts = BadgeHealth.Read();
        var report = BadgeHealth.Decide(facts);
        Assert.False(string.IsNullOrWhiteSpace(report.Line));
        Assert.DoesNotContain('\u2014', report.Line);
        Assert.Equal(report.State, BadgeHealth.Check().State);
    }
}
