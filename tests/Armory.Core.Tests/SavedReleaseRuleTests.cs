using Armory.Core;

namespace Armory.Core.Tests;

// The engine's release decision (research section 1.7): the SolidWorks link's stamp for the
// exact content hash, and the file reader, combined.
public sealed class SavedReleaseRuleTests
{
    private static ReleaseStamp Stamp(int? year, DateTimeOffset? at = null, string hash = "h")
        => new(hash, year, "34.4.1", 2026, year, at ?? DateTimeOffset.UnixEpoch);

    [Theory]
    [InlineData(2025, 2025, 2025, false)]   // both known and equal
    [InlineData(2025, null, 2025, false)]   // stamp known, reader unknown
    [InlineData(null, 2025, 2025, false)]   // no year in the stamp, reader known
    [InlineData(2025, 2026, null, true)]    // they disagree: unknown, and a telemetry record
    [InlineData(2026, 2025, null, true)]
    [InlineData(null, null, null, false)]   // neither: unknown (the gate decides by mode)
    public void Combine_follows_the_table(int? stamped, int? read, int? expected, bool disagree)
    {
        var parsed = read is null ? (SolidWorksRelease?)null : new SolidWorksRelease(read.Value);
        Assert.Equal(expected, SavedReleaseRule.Combine(Stamp(stamped), parsed)?.Year);
        Assert.Equal(disagree, SavedReleaseRule.Disagree(Stamp(stamped), parsed));
    }

    [Fact]
    public void No_stamp_leaves_the_reader_alone()
    {
        Assert.Equal(new SolidWorksRelease(2024), SavedReleaseRule.Combine(null, new SolidWorksRelease(2024)));
        Assert.Null(SavedReleaseRule.Combine(null, null));
        Assert.False(SavedReleaseRule.Disagree(null, new SolidWorksRelease(2024)));
    }

    [Fact]
    public void A_year_before_1995_counts_as_unknown()
    {
        Assert.Equal(new SolidWorksRelease(2025), SavedReleaseRule.Combine(Stamp(1994), new SolidWorksRelease(2025)));
        Assert.Equal(new SolidWorksRelease(2025), SavedReleaseRule.Combine(Stamp(2025), new SolidWorksRelease(0)));
        Assert.False(SavedReleaseRule.Disagree(Stamp(0), new SolidWorksRelease(2025)));
    }

    [Theory]
    [InlineData(2025, 2025, 2025)]   // saved down as meant
    [InlineData(2026, 2025, null)]   // meant 2025, SolidWorks wrote 2026: no year
    [InlineData(2025, null, 2025)]   // a stamp on open: nothing was meant
    [InlineData(null, 2025, null)]   // SolidWorks could not say
    [InlineData(1990, null, null)]
    public void A_stamp_records_the_year_only_when_SolidWorks_wrote_what_was_meant(int? fromHistory, int? intended, int? expected)
        => Assert.Equal(expected, SavedReleaseRule.StampYear(fromHistory, intended));

    [Fact]
    public void Two_stamps_for_the_same_bytes_keep_the_newer_unless_their_years_differ()
    {
        var early = DateTimeOffset.UnixEpoch;
        var late = early.AddMinutes(5);
        Assert.Equal(Stamp(2025, late), SavedReleaseRule.Merge(Stamp(2025, early), Stamp(2025, late)));
        Assert.Equal(Stamp(2025, late), SavedReleaseRule.Merge(Stamp(2025, late), Stamp(2025, early)));
        Assert.Null(SavedReleaseRule.Merge(Stamp(2025, early), Stamp(2026, late)).Year);
        Assert.Null(SavedReleaseRule.Merge(Stamp(2026, late), Stamp(2025, early)).Year);
        // A stamp with no year never erases a year the same bytes were stamped with.
        Assert.Equal(2026, SavedReleaseRule.Merge(Stamp(2026, early), Stamp(null, late)).Year);
        Assert.Equal(late, SavedReleaseRule.Merge(Stamp(2026, early), Stamp(null, late)).At);
        // A stamp for other bytes simply replaces it (the engine keys them by hash).
        Assert.Equal(Stamp(2026, early, "other"), SavedReleaseRule.Merge(Stamp(2025, late), Stamp(2026, early, "other")));
        Assert.Equal(Stamp(2025), SavedReleaseRule.Merge(null, Stamp(2025)));
    }

    // ISldWorks.VersionHistory: the last entry names the release that wrote the file; its
    // bracket is a build date and is never read as the release.
    [Fact]
    public void The_last_entry_of_the_version_history_names_the_year()
    {
        Assert.Equal(2021, VersionHistory.LastYear(["13000[2020/296]", "14000[2021/166]"]));
        Assert.Equal(2026, VersionHistory.LastYear(["18000[2025/261]", "19000[2025/300]"]));   // 2026 built in 2025
        Assert.Equal(2025, VersionHistory.LastYear(["17000[2024/100]", "19000[2026/225]", "18000[2026/230]"])); // last, not largest
        Assert.Equal(2026, VersionHistory.LastYear([" 19000 "]));
        Assert.Equal(2026, VersionHistory.LastYear(["18800[2025/90]"]));                       // a 2026 beta
        Assert.Null(VersionHistory.LastYear([]));
        Assert.Null(VersionHistory.LastYear(null));
        Assert.Null(VersionHistory.LastYear(["abc"]));
        Assert.Null(VersionHistory.LastYear(["19000x"]));
        Assert.Null(VersionHistory.LastYear(["7000[2014/1]"]));                                  // before 2015's codes
        Assert.Null(VersionHistory.LastYear(["12345678901[1]"]));
    }

    // A save the link meant to save down whose year SolidWorks didn't confirm stays here in both
    // gate modes; a confirmed one, a stamp on open (nothing meant) and no stamp do not.
    [Fact]
    public void A_save_down_SolidWorks_did_not_confirm_is_unverified()
    {
        var at = DateTimeOffset.UnixEpoch;
        Assert.True(SavedReleaseRule.UnverifiedSaveDown(new ReleaseStamp("h", null, "34.4.1", 2026, 2025, at)));
        Assert.True(SavedReleaseRule.UnverifiedSaveDown(new ReleaseStamp("h", 1990, "34.4.1", 2026, 2025, at)));
        Assert.False(SavedReleaseRule.UnverifiedSaveDown(new ReleaseStamp("h", 2025, "34.4.1", 2026, 2025, at)));
        Assert.False(SavedReleaseRule.UnverifiedSaveDown(new ReleaseStamp("h", null, "34.4.1", 2026, null, at)));
        Assert.False(SavedReleaseRule.UnverifiedSaveDown(new ReleaseStamp("h", 2026, "34.4.1", 2026, null, at)));
        Assert.False(SavedReleaseRule.UnverifiedSaveDown(null));
        // The stamp the rule makes for a save down that wrote 2026 instead is exactly that.
        var stamp = new ReleaseStamp("h", SavedReleaseRule.StampYear(2026, 2025), "34.4.1", 2026, 2025, at);
        Assert.True(SavedReleaseRule.UnverifiedSaveDown(stamp));
    }
}
