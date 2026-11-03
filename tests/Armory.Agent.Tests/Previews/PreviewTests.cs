using System.Buffers.Binary;
using System.Diagnostics;
using System.IO.Compression;
using System.Numerics;
using System.Text;
using Armory.Agent.Previews;
using Xunit.Abstractions;

namespace Armory.Agent.Tests.Previews;

public sealed class PreviewTests(ITestOutputHelper output)
{
    public static TheoryData<string> Fixtures => new(
        Directory.GetFiles(Path.Combine(AppContext.BaseDirectory, "Previews", "Fixtures"))
            .Where(ContentPreviews.Supports).Select(Path.GetFileName).Select(n => n!));

    private static string Fixture(string name) => Path.Combine(AppContext.BaseDirectory, "Previews", "Fixtures", name);

    [Theory, MemberData(nameof(Fixtures))]
    public void Every_extension_has_a_reviewed_golden_picture(string name)
    {
        var renderer = new ContentPreviews();
        var png = renderer.Make(Fixture(name), ShellThumbnails.Size);
        Assert.NotNull(png);
        Assert.Equal(0, renderer.Fallbacks);
        var pixels = Decode(png);
        Assert.Equal(192 * 192 * 4, pixels.Length);
        Assert.True(InkPixels(pixels) > 80, "A real visible picture is required.");
        AssertGolden(name, png);
    }

    private static void AssertGolden(string name, byte[] png)
    {
        var golden = Path.Combine(AppContext.BaseDirectory, "Previews", "Golden", name + ".png");
        if (Environment.GetEnvironmentVariable("ARMORY_UPDATE_PREVIEW_GOLDENS") == "1")
        {
            var root = new DirectoryInfo(AppContext.BaseDirectory);
            while (root != null && !File.Exists(Path.Combine(root.FullName, "Armory.sln"))) root = root.Parent;
            Assert.NotNull(root);
            golden = Path.Combine(root.FullName, "tests", "Armory.Agent.Tests", "Previews", "Golden", name + ".png");
            File.WriteAllBytes(golden, png);
        }
        Assert.True(File.Exists(golden), "Missing golden: " + name);
        Assert.Equal(Decode(File.ReadAllBytes(golden)), Decode(png));
    }

    [Fact]
    public void Both_stl_encodings_and_both_lightburn_encodings_describe_the_same_geometry()
    {
        var renderer = new ContentPreviews();
        Assert.Equal(renderer.Make(Fixture("bracket-ascii.stl"), 192), renderer.Make(Fixture("bracket-binary.stl"), 192));
        Assert.Equal(renderer.Make(Fixture("badge-legacy.lbrn"), 192), renderer.Make(Fixture("badge-compact.lbrn2"), 192));
        var mesh = Read("bracket-binary.stl");
        Assert.Equal(24, mesh.Faces.Count);
        Assert.Contains(mesh.Faces, f => f.A == new Vector3(40, 24, 6) || f.B == new Vector3(40, 24, 6) || f.C == new Vector3(40, 24, 6));
        var colors = Decode(renderer.Make(Fixture("bracket-binary.stl"), 192)!).Chunk(4).Select(Convert.ToHexString).Distinct().Count();
        Assert.True(colors >= 4, "At least three shaded faces and paper.");
    }

    [Fact]
    public void Dxf_has_each_requested_entity_including_bulges_and_a_curved_spline()
    {
        var g = Read("plate.dxf");
        Assert.Contains(g.Lines, s => s.A == new Point(0, 0) && s.B == new Point(70, 0));
        Assert.Contains(g.Lines, s => s.A == new Point(18, 30) && s.B == new Point(24, 36)); // POLYLINE
        Assert.Contains(g.Lines, s => Math.Abs(s.A.X - 11) < 1e-8 && Math.Abs(s.A.Y - 8) < 1e-8); // CIRCLE
        Assert.Contains(g.Lines, s => s.A.Y < 8 && s.A.X > 18 && s.A.X < 52); // LWPOLYLINE bulge
        Assert.Contains(g.Lines, s => s.A.X > 30 && s.A.X < 55 && s.A.Y > 30); // SPLINE
        Assert.True(g.Lines.Count > 500);
    }

    [Fact]
    public void Gcode_tracks_modal_units_relative_moves_offsets_centers_and_dim_rapids()
    {
        var inch = Read("inch-relative.nc");
        Assert.Contains(inch.Lines, s => s.B == new Point(25.4, 12.7));
        Assert.Contains(inch.Lines, s => s.A == new Point(25.4, 12.7) && s.B == new Point(0, 12.7));
        var offset = Read("offset-center.tap");
        Assert.Contains(offset.Lines, s => s.A == new Point(10, 10) && s.B == new Point(40, 10));
        Assert.Contains(offset.Lines, s => s.A.X > 15 && s.A.Y < 20); // offset arc, not a straight chord
        var pixels = Decode(new ContentPreviews().Make(Fixture("pocket.gcode"), 192)!);
        var colors = pixels.Chunk(4).Select(p => BinaryPrimitives.ReadUInt32BigEndian(p)).ToHashSet();
        Assert.Contains(PreviewRaster.Rapid, colors); Assert.Contains(PreviewRaster.Ink, colors);
    }

    [Fact]
    public void Svg_applies_transforms_curves_and_ignores_definitions_and_hidden_shapes()
    {
        var g = Read("badge.svg");
        Assert.True(g.Lines.Count > 250);
        Assert.All(g.Lines, s => { Assert.InRange(s.A.X, 0, 100); Assert.InRange(s.A.Y, 0, 80); });
        using var data = new MemoryStream(Encoding.UTF8.GetBytes("<svg><g transform='translate(10 20) scale(2)'><path d='M1 2h3v4z'/></g></svg>"));
        var transformed = new Geometry(new()); ShapeReader.Svg(data, transformed);
        Assert.Equal(new Point(12, 24), transformed.Lines[0].A);
        Assert.Equal(new Point(18, 24), transformed.Lines[0].B);
        Assert.Equal(new Point(18, 32), transformed.Lines[1].B);
    }

    [Fact]
    public async Task Content_cache_reuses_copies_but_rechecks_same_length_same_timestamp_changes()
    {
        using var folder = new TempFolder();
        var a = Path.Combine(folder.Root, "a.svg"); var b = Path.Combine(folder.Root, "b.svg");
        File.WriteAllText(a, "<svg><path d='M0 0L4 8L8 0Z'/></svg>"); File.Copy(a, b);
        var renderer = new ContentPreviews();
        var one = renderer.Make(a, 192); Assert.NotNull(one);
        Assert.Same(one, renderer.Make(b, 192)); Assert.Equal(1, renderer.Rendered);
        File.SetLastWriteTimeUtc(a, DateTime.UtcNow.AddDays(-1));
        Assert.Same(one, renderer.Make(a, 192)); Assert.Equal(1, renderer.Rendered);
        using var provider = new ShellThumbnails();
        var simultaneous = await Task.WhenAll(provider.GetAsync(a), provider.GetAsync(a), provider.GetAsync(b));
        var first = simultaneous[0]; Assert.NotNull(first);
        Assert.All(simultaneous, p => Assert.Same(first, p));
        var time = File.GetLastWriteTimeUtc(a);
        File.WriteAllText(a, "<svg><path d='M0 0L8 8L8 0Z'/></svg>"); File.SetLastWriteTimeUtc(a, time);
        var changed = await provider.GetAsync(a); Assert.NotNull(changed);
        Assert.NotEqual(first, changed);
        Assert.NotEqual(one, renderer.Make(a, 192)); Assert.Equal(2, renderer.Rendered);
    }

    [Fact]
    public void Content_cache_has_a_bounded_number_of_versions()
    {
        using var folder = new TempFolder();
        var file = folder.File("versions.svg");
        var renderer = new ContentPreviews();
        for (var i = 0; i <= ShellThumbnails.Kept; i++)
        {
            File.WriteAllText(file, $"<svg><!-- original version {i} --><path d='M0 0L4 8L8 0Z'/></svg>");
            Assert.NotNull(renderer.Make(file, 192));
        }
        Assert.Equal(ShellThumbnails.Kept + 1, renderer.Rendered);
        File.WriteAllText(file, "<svg><!-- original version 0 --><path d='M0 0L4 8L8 0Z'/></svg>");
        Assert.NotNull(renderer.Make(file, 192));
        Assert.Equal(ShellThumbnails.Kept + 2, renderer.Rendered);
    }

    [Theory]
    [InlineData("part.igs")][InlineData("part.3mf")][InlineData("job.bgcode")][InlineData("job.xcs")][InlineData("file.txt")]
    public void Scope_is_only_the_requested_extensions(string name) => Assert.False(ContentPreviews.Supports(name));

    [Fact]
    public void Step_is_a_name_and_size_tile_and_not_pretend_geometry()
    {
        using var folder = new TempFolder();
        var a = Path.Combine(folder.Root, "short-name.step"); File.Copy(Fixture("original-bracket.step"), a);
        var b = Path.Combine(folder.Root, "different-name.step"); File.Copy(a, b);
        var renderer = new ContentPreviews();
        Assert.NotEqual(renderer.Make(a, 192), renderer.Make(b, 192));
        Assert.NotEqual(renderer.Make(a, 192), renderer.Make(Fixture("small-part.stp"), 192));
    }

    [Fact]
    public void Hostile_inputs_are_bounded_and_do_not_poison_the_next_picture()
    {
        using var folder = new TempFolder();
        var renderer = new ContentPreviews();
        foreach (var (name, text) in new[] {
            ("entity.svg", "<!DOCTYPE svg [<!ENTITY x SYSTEM 'file:///not-read'>]><svg>&x;</svg>"),
            ("deep.svg", "<svg>" + string.Concat(Enumerable.Repeat("<g>", 70)) + string.Concat(Enumerable.Repeat("</g>", 70)) + "</svg>"),
            ("huge-attribute.svg", "<svg><path d='" + new string(' ', 140_000) + "'/></svg>"),
            ("bad.dxf", "0\nSECTION\n2\nENTITIES\n0\nLINE\n10\nNaN\n0\nEOF\n"),
            ("long.gcode", new string('X', 20_000)),
            ("bad.lbrn2", "<LightBurnProject><Shape Type='Path'><VertList>V0 0V1 1</VertList><PrimList>L0 99999999999</PrimList></Shape></LightBurnProject>"),
            ("bad.stl", "solid x\nvertex NaN 0 0\nvertex 0 0 0\nvertex 1 1 1\nendsolid\n") })
        {
            var file = Path.Combine(folder.Root, name); File.WriteAllText(file, text);
            var watch = Stopwatch.StartNew();
            Assert.NotNull(renderer.Make(file, 192));
            Assert.True(watch.Elapsed < TimeSpan.FromSeconds(5), name);
        }
        var huge = Path.Combine(folder.Root, "oversize.stl");
        using (var stream = File.Create(huge)) stream.SetLength(PreviewBudget.MaxFileBytes + 1);
        Assert.Null(renderer.Make(huge, 192));
        Assert.NotNull(renderer.Make(Fixture("bracket-binary.stl"), 192));
        Assert.Throws<PreviewLimitException>(() => new PreviewBudget(TimeSpan.Zero).Check());
        var geometry = new Geometry(new());
        Assert.Throws<PreviewLimitException>(() => { for (var i = 0; i <= PreviewBudget.MaxSegments; i++) geometry.Line(new(), new(1, 1)); });
    }

    [Fact]
    public async Task Largest_synthetic_fixtures_are_measured_and_100MB_mesh_does_not_block_the_caller()
    {
        using var folder = new TempFolder();
        var mesh = LargeFixtures.Mesh(folder.Root);
        Assert.Equal(100_000_084, new FileInfo(mesh).Length);
        using var provider = new ShellThumbnails();
        var call = Stopwatch.StartNew();
        var pending = provider.GetAsync(mesh);
        Assert.True(call.Elapsed < TimeSpan.FromSeconds(1), "GetAsync performed rendering or hashing on its caller.");
        var png = await pending.WaitAsync(TimeSpan.FromSeconds(8)); Assert.NotNull(png);
        output.WriteLine($"100MB STL via provider: {call.Elapsed.TotalMilliseconds:F2} ms; process peak working set {Process.GetCurrentProcess().PeakWorkingSet64} bytes");
        foreach (var file in LargeFixtures.Linework(folder.Root).Append(mesh).Concat(new[] { Fixture("original-bracket.step"), Fixture("small-part.stp") }))
        {
            var renderer = new ContentPreviews();
            var before = GC.GetAllocatedBytesForCurrentThread(); var watch = Stopwatch.StartNew();
            var picture = renderer.Make(file, 192); watch.Stop(); Assert.NotNull(picture);
            var allocated = GC.GetAllocatedBytesForCurrentThread() - before;
            var colors = Decode(picture).Chunk(4).Select(Convert.ToHexString).Distinct().Count();
            if (Path.GetExtension(file) == ".stl")
            {
                Assert.True(colors > 2, "Large STL fell back to a tile.");
                Assert.True(InkPixels(Decode(picture)) > 2500, "The sampled dense mesh must read as a surface.");
            }
            Assert.Equal(0, renderer.Fallbacks);
            AssertGolden(Path.GetFileName(file), picture);
            output.WriteLine($"{Path.GetFileName(file)} | {new FileInfo(file).Length} bytes | {watch.Elapsed.TotalMilliseconds:F2} ms | {allocated} allocated bytes | {picture.Length} PNG bytes | {colors} colors");
            Assert.True(watch.Elapsed < TimeSpan.FromSeconds(5));
            Assert.InRange(allocated, 0, 32 * 1024 * 1024);
            var cached = renderer.Make(file, 192); Assert.Same(picture, cached); Assert.Equal(1, renderer.Rendered);
        }
    }

    private static Geometry Read(string name)
    {
        using var input = File.OpenRead(Fixture(name)); var g = new Geometry(new());
        switch (Path.GetExtension(name))
        {
            case ".stl": StlReader.Read(input, g); break;
            case ".dxf": DxfReader.Read(input, g); break;
            case ".gcode": case ".nc": case ".tap": GCodeReader.Read(input, g); break;
            case ".svg": ShapeReader.Svg(input, g); break;
            default: ShapeReader.LightBurn(input, g); break;
        }
        return g;
    }

    private static int InkPixels(byte[] pixels) => pixels.Chunk(4).Count(p => p[0] != (PreviewRaster.Paper >> 24));

    private static byte[] Decode(byte[] png)
    {
        Assert.Equal(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 }, png[..8]);
        using var packed = new MemoryStream();
        var width = 0; var height = 0;
        for (var at = 8; at < png.Length;)
        {
            var length = BinaryPrimitives.ReadInt32BigEndian(png.AsSpan(at));
            var type = Encoding.ASCII.GetString(png, at + 4, 4);
            if (type == "IHDR") { width = BinaryPrimitives.ReadInt32BigEndian(png.AsSpan(at + 8)); height = BinaryPrimitives.ReadInt32BigEndian(png.AsSpan(at + 12)); }
            if (type == "IDAT") packed.Write(png, at + 8, length);
            at += length + 12;
        }
        packed.Position = 0;
        using var inflater = new ZLibStream(packed, CompressionMode.Decompress);
        var pixels = new byte[width * height * 4];
        for (var y = 0; y < height; y++) { Assert.Equal(0, inflater.ReadByte()); inflater.ReadExactly(pixels.AsSpan(y * width * 4, width * 4)); }
        Assert.Equal(-1, inflater.ReadByte()); return pixels;
    }
}
