using Armory.Core;

namespace Armory.Core.Tests;

public sealed class NamingTests
{
    [Theory]
    [InlineData("a<b")][InlineData("a>b")][InlineData("a:b")][InlineData("a\"b")]
    [InlineData("a/b")][InlineData("a\\b")][InlineData("a|b")][InlineData("a?b")][InlineData("a*b")]
    [InlineData("a\0b")][InlineData("a\u007fb")][InlineData(".")][InlineData("..")]
    [InlineData("")][InlineData("tail.")][InlineData("tail ")][InlineData("CON.txt")]
    [InlineData("prn")][InlineData("AUX.asm")][InlineData("NUL")][InlineData("COM1")]
    [InlineData("com9.txt")][InlineData("LPT1")][InlineData("lpt9.a")][InlineData("COM¹")]
    [InlineData("CON .txt")]
    public void Invalid_name_is_reported(string name)
    {
        Assert.False(VaultPath.TryValidateName(name, out var reason));
        Assert.False(string.IsNullOrWhiteSpace(reason));
    }

    [Theory]
    [InlineData("../a")][InlineData("a/./b")][InlineData("/root")][InlineData("C:\\a")]
    [InlineData("a//b")][InlineData("a/")][InlineData("\\\\server\\file")]
    public void Paths_cannot_escape_vault(string value) => Assert.False(VaultPath.TryCreate(value, out _, out _));

    [Theory]
    [InlineData("a😀.SLDPRT")][InlineData("COM10.txt")][InlineData("part file.txt")][InlineData("零件.txt")]
    public void Valid_unicode_and_names_pass(string name) => Assert.True(VaultPath.TryValidateName(name, out _));

    [Fact]
    public void Malformed_surrogates_are_reported_without_exception()
    {
        Assert.False(VaultPath.TryCreate("bad\ud800.txt", out _, out var reason));
        Assert.Contains("surrogate", reason);
        Assert.Null(new VaultIndex().Find("bad\ud800"));
    }

    [Fact]
    public void Case_and_NFC_have_equal_identity_and_hash()
    {
        var a = Fixtures.Path("Parts/Café.SLDPRT");
        var b = Fixtures.Path("parts\\cafe\u0301.sldprt");
        Assert.Equal(a, b);
        Assert.Equal(a.GetHashCode(), b.GetHashCode());
        Assert.Equal(0, a.CompareTo(b));
        Assert.True(a.TryToWindowsPath(out var windowsPath, out _));
        Assert.Equal(@"C:\IDEA\Armory\Parts\Café.SLDPRT", windowsPath);
    }

    [Fact]
    public void Full_path_length_counts_UTF16_including_root_and_emoji()
    {
        var name = "😀.txt";
        var length = @"C:\IDEA\Armory".Length + 1 + name.Length;
        Assert.True(VaultPath.TryCreate(name, out _, out _, maxWindowsPathLength: length));
        Assert.False(VaultPath.TryCreate(name, out _, out _, maxWindowsPathLength: length - 1));
        Assert.False(VaultPath.TryCreate(new string('x', 240), out _, out _));
        Assert.False(VaultPath.TryCreate("a", out _, out _, maxWindowsPathLength: 0));
    }

    [Fact]
    public void Global_index_covers_projects_COTS_extensions_and_all_collisions()
    {
        var index = new VaultIndex();
        Assert.True(index.TryAdd(Fixtures.Path("COTS/Plate.SLDPRT"), out _));
        Assert.False(index.TryAdd(Fixtures.Path("Project/plate.sldprt"), out var problem));
        Assert.Contains("COTS", problem);
        Assert.True(index.TryAdd(Fixtures.Path("Project/Plate.SLDDRW"), out _));
        Assert.False(index.IsFree("PLATE.SLDPRT"));
        Assert.True(index.IsFree("new.txt"));
        Assert.False(index.IsFree("a/b"));
        Assert.False(index.TryAdd(default, out _));
        var collisions = index.FindImportCollisions([Fixtures.Path("a/plate.sldprt"), Fixtures.Path("a/new.txt"), Fixtures.Path("b/NEW.txt")]);
        Assert.Equal(2, collisions.Count);
        Assert.All(collisions, c => Assert.Equal(2, c.Paths.Count));
    }

    [Fact]
    public void Index_normalizes_lookup_names()
    {
        var index = new VaultIndex();
        var path = Fixtures.Path("a/café.txt");
        Assert.True(index.TryAdd(path, out _));
        Assert.Equal(path, index.Find("CAFE\u0301.TXT"));
    }

    [Fact]
    public void Server_import_reports_every_invalid_name_and_does_not_rename()
    {
        var report = new VaultIndex().AnalyzeImport(["project/CON.txt", "project/trailing.", "a/file.txt", "b/FILE.txt"]);
        Assert.Equal(2, report.InvalidPaths.Count);
        Assert.Contains(report.InvalidPaths, p => p.Path == "project/CON.txt");
        Assert.All(report.InvalidPaths, p => Assert.Contains("lead", p.Problem));
        Assert.Single(report.Collisions);
        Assert.False(VaultPath.TryFromSegments(["robot", "a/b"], out _, out _));
        Assert.True(VaultPath.TryFromSegments(["robot", "😀.txt"], out _, out _));
        Assert.False(default(VaultPath).TryToWindowsPath(out _, out _));
        Assert.False(Fixtures.Path().TryToWindowsPath(out _, out _, maxWindowsPathLength: 2));
    }
}
