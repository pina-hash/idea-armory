using System.Text.RegularExpressions;
using Armory.Core;

namespace Armory.Agent.Tests;

// The window's file icons (0.5.0, ledger 0437): the page's FILE_KINDS and KIND_WORD are
// Armory.Core's FileKinds table exactly, every kind has its drawing in index.html (made by
// tools/agent-icon/make_file_icons.py), every type the lab uses maps to a drawing that is not the
// generic file, an unknown one maps to the generic file, and a picture Windows makes is asked for
// every kind Windows usually pictures (it still wins over the icon). Runs on every platform.
public sealed partial class FileIconsTests
{
    private static string Page(string name) => File.ReadAllText(Path.Combine(Repo.FindRoot()!, "src", "Armory.Agent", "wwwroot", name));

    // The object literal assigned to `var name = { ... };` in app.js, as key -> value strings.
    private static Dictionary<string, string> Literal(string code, string name)
    {
        var start = code.IndexOf("var " + name + " = {", StringComparison.Ordinal);
        Assert.True(start >= 0, "app.js has no " + name);
        var end = code.IndexOf("};", start, StringComparison.Ordinal);
        var body = code[start..end];
        return Pair().Matches(body).ToDictionary(m => m.Groups["k"].Value, m => m.Groups["v"].Value, StringComparer.Ordinal);
    }

    [GeneratedRegex(@"'?(?<k>[.\w]+)'?\s*:\s*'(?<v>[^']*)'")]
    private static partial Regex Pair();

    private static string IconOf(string kind) => kind is "part" or "asm" or "drw" or "file" ? "i-" + kind : "i-k-" + kind;

    [Fact]
    public void The_page_s_kind_table_and_words_are_core_s_and_every_kind_has_a_drawing()
    {
        var app = Page("app.js");
        var kinds = Literal(app, "FILE_KINDS");
        Assert.Equal(FileKinds.Table.OrderBy(k => k.Key, StringComparer.Ordinal), kinds.OrderBy(k => k.Key, StringComparer.Ordinal));
        var words = Literal(app, "KIND_WORD");
        Assert.Equal(FileKinds.Kinds.Select(k => (k.Kind, k.Word)).OrderBy(k => k.Kind, StringComparer.Ordinal), words.Select(w => (w.Key, w.Value)).OrderBy(k => k.Key, StringComparer.Ordinal));
        var index = Page("index.html");
        foreach (var (kind, _, _) in FileKinds.Kinds)
            Assert.Matches("<symbol id=\"" + Regex.Escape(IconOf(kind)) + "\" viewBox=\"0 0 20 20\"><path d=\"[^\"]+\" fill=\"none\" stroke=\"currentColor\"", index);
        // The drawings are the script's: between its markers, one symbol per kind and nothing else.
        var begin = index.IndexOf("<!-- file kinds: made by tools/agent-icon/make_file_icons.py", StringComparison.Ordinal);
        var end = index.IndexOf("<!-- /file kinds -->", StringComparison.Ordinal);
        Assert.True(begin >= 0 && end > begin, "index.html's file kind markers are missing");
        Assert.Equal(FileKinds.Kinds.Count, Regex.Matches(index[begin..end], "<symbol ").Count);
        Assert.Equal(FileKinds.Kinds.Count, Regex.Matches(index, "<symbol id=\"i-(k-[a-z]+|part|asm|drw|file)\"").Count);
    }

    [Fact]
    public void Every_lab_type_gets_its_own_drawing_and_an_unknown_one_the_generic_file()
    {
        var index = Page("index.html");
        // The queue's list (docs/feedback/2026-10-26/QUEUE-0437.md in idea-app).
        string[] lab =
        [
            ".sldprt", ".sldasm", ".slddrw", ".slddrt", ".sldlfp", ".sldblk", ".eprt", ".easm", ".edrw", ".step", ".stp", ".iges", ".igs", ".x_t", ".x_b",
            ".stl", ".3mf", ".obj", ".gcode", ".bgcode", ".dxf", ".dwg", ".svg", ".ai", ".lbrn", ".lbrn2", ".xcs", ".pdf", ".docx", ".xlsx", ".pptx",
            ".txt", ".md", ".csv", ".png", ".jpg", ".jpeg", ".gif", ".bmp", ".webp", ".mp4", ".mov", ".zip",
        ];
        foreach (var extension in lab)
        {
            var kind = FileKinds.KindOf("Robot 2027/Thing" + extension);
            Assert.NotEqual("i-file", IconOf(kind));
            Assert.Contains("id=\"" + IconOf(kind) + "\"", index);
        }
        Assert.Equal("i-file", IconOf(FileKinds.KindOf("notes.qqq")));
        Assert.Equal("i-file", IconOf(FileKinds.KindOf("README")));
    }

    [Fact]
    public void Windows_is_asked_for_a_picture_of_every_kind_it_usually_pictures_and_the_icon_shows_until_one_comes()
    {
        var app = Page("app.js");
        var start = app.IndexOf("var THUMB_KINDS = {", StringComparison.Ordinal);
        Assert.True(start >= 0);
        var asked = Regex.Matches(app[start..app.IndexOf("};", start, StringComparison.Ordinal)], @"(\w+):\s*1").Select(m => m.Groups[1].Value).ToHashSet(StringComparer.Ordinal);
        foreach (var kind in new[] { "part", "asm", "drw", "edrawing", "mesh", "pdf", "image", "video" }) Assert.Contains(kind, asked);
        foreach (var kind in new[] { "text", "code", "archive", "cad", "laser", "file" }) Assert.DoesNotContain(kind, asked);
        Assert.All(asked, kind => Assert.Contains(FileKinds.Kinds, k => k.Kind == kind));
        // The row draws the kind's icon first and lays the picture over it once Windows answers.
        Assert.Contains("'<span class=\"row-icon thumb-slot\">' + icon(o.glyph || kindIcon(kindOf(o.name)), 'thumb-glyph')", app);
    }
}
