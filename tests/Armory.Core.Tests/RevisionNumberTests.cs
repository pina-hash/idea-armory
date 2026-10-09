using Armory.Core;

namespace Armory.Core.Tests;

// ISldWorks.RevisionNumber ("major.minor.hotfix", year = major + 1992), which the SolidWorks
// link reads to know which SolidWorks is running.
public sealed class RevisionNumberTests
{
    [Theory]
    [InlineData("33.5.0", 2025)]
    [InlineData("34.4.1", 2026)]
    [InlineData("34.0.0", 2026)]
    [InlineData("23.-3.0", 2015)]   // a pre-release build has a negative minor
    [InlineData("13.0.0", 2005)]
    [InlineData(" 35.1.0 ", 2027)]
    public void A_revision_names_its_release(string revision, int year)
    {
        Assert.Equal(new SolidWorksRelease(year), SolidWorksRelease.FromRevisionNumber(revision));
        Assert.Equal(year, SolidWorksRevision.Parse(revision)!.Value.Year);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    [InlineData("abc")]
    [InlineData("13")]
    [InlineData("33.5")]
    [InlineData("33.5.0.1")]
    [InlineData("33.x.0")]
    [InlineData("2.0.0")]
    [InlineData("-34.4.1")]
    [InlineData("34..1")]
    public void Anything_else_is_no_revision(string? revision)
    {
        Assert.Null(SolidWorksRelease.FromRevisionNumber(revision));
        Assert.Null(SolidWorksRevision.Parse(revision));
    }

    [Fact]
    public void The_service_pack_is_the_minor_number()
    {
        Assert.Equal(new SolidWorksRevision(34, 4, 1), SolidWorksRevision.Parse("34.4.1"));
        Assert.Equal(-3, SolidWorksRevision.Parse("23.-3.0")!.Value.Minor);
    }

    [Theory]
    [InlineData("34.4.1", "SolidWorks 2026 SP4.1")]
    [InlineData("34.3.0", "SolidWorks 2026 SP3")]
    [InlineData("34.0.0", "SolidWorks 2026 SP0")]
    [InlineData("33.5.0", "SolidWorks 2025 SP5")]
    [InlineData("23.-3.0", "SolidWorks 2015 beta")]
    public void A_revision_is_named_as_SolidWorks_names_itself(string revision, string name)
        => Assert.Equal(name, SolidWorksRevision.Parse(revision)!.Value.DisplayName);
}
