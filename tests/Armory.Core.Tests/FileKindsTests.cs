using Armory.Core;

namespace Armory.Core.Tests;

// The window's file icons (0.5.0, ledger 0437): every type the lab uses has a kind, the case of
// an extension never matters, and anything else is the generic file.
public sealed class FileKindsTests
{
    // The queue's list (docs/feedback/2026-10-26/QUEUE-0437.md in idea-app), word for word.
    public static readonly string[] LabTypes =
    [
        ".sldprt", ".sldasm", ".slddrw", ".slddrt", ".sldlfp", ".sldblk",
        ".eprt", ".easm", ".edrw",
        ".step", ".stp", ".iges", ".igs", ".x_t", ".x_b",
        ".stl", ".3mf", ".obj", ".gcode", ".bgcode",
        ".dxf", ".dwg", ".svg", ".ai", ".lbrn", ".lbrn2", ".xcs",
        ".pdf", ".docx", ".xlsx", ".pptx", ".txt", ".md", ".csv",
        ".png", ".jpg", ".jpeg", ".gif", ".bmp", ".webp",
        ".mp4", ".mov",
        ".zip",
    ];

    [Fact]
    public void Every_type_the_lab_uses_has_its_own_kind_and_never_the_generic_file()
    {
        foreach (var extension in LabTypes)
        {
            var kind = FileKinds.KindOf("Robot 2027/Drivetrain/Thing" + extension);
            Assert.NotEqual(FileKinds.Generic, kind);
            Assert.Equal(kind, FileKinds.KindOf("THING" + extension.ToUpperInvariant()));
            Assert.Contains(FileKinds.Kinds, k => k.Kind == kind);
        }
    }

    [Fact]
    public void SolidWorks_files_keep_the_three_kinds_the_window_always_had()
    {
        Assert.Equal("part", FileKinds.KindOf("Plate.SLDPRT"));
        Assert.Equal("asm", FileKinds.KindOf(@"C:\IDEA\Armory\Robot\Drive.SLDASM"));
        Assert.Equal("drw", FileKinds.KindOf("Plate.slddrw"));
        Assert.Equal("Assembly", FileKinds.WordOf("asm"));
    }

    [Fact]
    public void An_unknown_extension_no_extension_and_odd_names_are_the_generic_file()
    {
        foreach (var name in new[] { "notes.qqq", "README", ".gitignore", "trailing.", "", null, "folder.sldprt/inside", "a.b.c.unknown" })
            Assert.Equal(FileKinds.Generic, FileKinds.KindOf(name));
        Assert.Equal("File", FileKinds.WordOf(FileKinds.Generic));
        Assert.Equal("File", FileKinds.WordOf("no such kind"));
        Assert.Equal("mesh", FileKinds.KindOf("a.b.c.stl"));
    }

    [Fact]
    public void Each_extension_belongs_to_one_kind_and_each_kind_has_one_word()
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (kind, word, extensions) in FileKinds.Kinds)
        {
            Assert.False(string.IsNullOrWhiteSpace(word));
            Assert.Matches("^[a-z]+$", kind);
            foreach (var extension in extensions)
            {
                Assert.StartsWith(".", extension);
                Assert.Equal(extension.ToLowerInvariant(), extension);
                Assert.True(seen.Add(extension), extension + " is in two kinds");
            }
        }
        Assert.Equal(seen.Count, FileKinds.Table.Count);
        Assert.Single(FileKinds.Kinds, k => k.Kind == FileKinds.Generic);
        Assert.Equal(FileKinds.Kinds.Count, FileKinds.Kinds.Select(k => k.Kind).Distinct().Count());
    }
}
