using Armory.Core;

namespace Armory.Core.Tests;

public sealed class VersionAndPartTests
{
    [Theory]
    [InlineData(2024, true)][InlineData(2025, true)][InlineData(2026, false)][InlineData(0, false)]
    public void Saved_release_gate(int year, bool allowed)
        => Assert.Equal(allowed, SolidWorksVersionGate.UploadProblem(new(year), new(2025)) is null);

    [Fact]
    public void Unknown_release_and_invalid_pin_fail_closed()
    {
        Assert.NotNull(SolidWorksVersionGate.UploadProblem(null, new(2025)));
        Assert.NotNull(SolidWorksVersionGate.UploadProblem(new(2025), new(0)));
    }

    [Theory]
    [InlineData(2024, false)][InlineData(2025, false)][InlineData(2026, true)]
    public void Pin_only_increases(int year, bool allowed) => Assert.Equal(allowed, SolidWorksVersionGate.TryRaise(new(2025), new(year), out _));

    [Fact]
    public void Rollover_warns_a_season_early_and_beyond_two_releases()
    {
        var gaps = SolidWorksVersionGate.RolloverGaps(new Dictionary<string, SolidWorksRelease> { ["old"] = new(2023), ["class"] = new(2024), ["laptop"] = new(2026) });
        Assert.Equal(["class", "laptop", "old"], gaps.Select(g => g.Installation));
        Assert.Equal([2, 0, 3], gaps.Select(g => g.ReleasesBehind));
        Assert.True(gaps[0].WarnNextSeason);
        Assert.False(gaps[0].ExceedsBackSaveRange);
        Assert.True(gaps[2].ExceedsBackSaveRange);
        Assert.Empty(SolidWorksVersionGate.RolloverGaps(new Dictionary<string, SolidWorksRelease>()));
    }

    [Theory]
    [InlineData("5669", "5669-26-0307")][InlineData("IDEA209H", "IDEA209H-26-0307")]
    public void Formats_and_parses_team_and_class_patterns(string prefix, string expected)
    {
        var pattern = new PartNumberPattern(prefix);
        Assert.True(PartNumbers.TryFormat(new(26, 3, 7), pattern, out var text));
        Assert.Equal(expected, text);
        Assert.True(PartNumbers.TryParse(text, pattern, out var number));
        Assert.Equal(new(26, 3, 7), number);
    }

    [Theory]
    [InlineData("5669-26-123")][InlineData("5669-26-12345")][InlineData("5669-2X-1234")]
    [InlineData("5669-26-１２３４")][InlineData("5669-26-1234 ")][InlineData("other-26-1234")]
    [InlineData("5669-2611234")][InlineData(null)]
    public void Rejects_invalid_numbers(string? text) => Assert.False(PartNumbers.TryParse(text, new(), out _));

    [Fact]
    public void Allocation_fills_holes_and_respects_season_and_subsystem()
    {
        Assert.True(PartNumbers.TryNext(26, 3, ["5669-26-0300", "5669-26-0302", "5669-25-0301", "5669-26-0401", "invalid"], new(), out var next, out _));
        Assert.Equal("5669-26-0301", next);
        var all = Enumerable.Range(0, 100).Select(i => $"5669-26-03{i:D2}");
        Assert.False(PartNumbers.TryNext(26, 3, all, new(), out _, out var problem));
        Assert.Contains("full", problem);
    }

    [Fact]
    public void Configurable_widths_and_invalid_configuration()
    {
        var pattern = new PartNumberPattern("CLASS", 4, 1, 3);
        Assert.True(PartNumbers.TryFormat(new(2026, 2, 123), pattern, out var text));
        Assert.Equal("CLASS-2026-2123", text);
        Assert.True(PartNumbers.TryParse(text, pattern, out _));
        Assert.False(PartNumbers.TryFormat(new(-1, 0, 0), new(), out _));
        Assert.False(PartNumbers.TryFormat(new(100, 0, 0), new(), out _));
        Assert.False(PartNumbers.TryNext(26, 100, [], new(), out _, out _));
        Assert.False(PartNumbers.TryNext(26, 1, [], new("a-b"), out _, out _));
        Assert.False(PartNumbers.TryParse("anything", new("A", 0), out _));
    }
}
