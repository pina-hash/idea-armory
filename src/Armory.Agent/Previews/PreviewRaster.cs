using System.Numerics;

namespace Armory.Agent.Previews;

// Small deterministic software rasterizer. Opaque paper and dark ink work in both themes.
// No GDI, GPU, fonts, native library, or third-party file handler is involved.
internal sealed class PreviewRaster(int size, PreviewBudget budget)
{
    internal const uint Paper = 0xEFF3F6FF, Ink = 0x234C63FF, Rapid = 0xABBBC5FF;
    internal readonly byte[] Pixels = new byte[size * size * 4];
    private readonly float[] depth = new float[size * size];
    private readonly int size = size;

    internal void Clear()
    {
        Array.Fill(depth, float.NegativeInfinity);
        for (var y = 0; y < size; y++)
            for (var x = 0; x < size; x++) Pixel(x, y, Paper);
    }

    private void Pixel(int x, int y, uint color)
    {
        if (x < 0 || y < 0 || x >= size || y >= size) return;
        var at = (y * size + x) * 4;
        Pixels[at] = (byte)(color >> 24);
        Pixels[at + 1] = (byte)(color >> 16);
        Pixels[at + 2] = (byte)(color >> 8);
        Pixels[at + 3] = (byte)color;
    }

    internal void Draw(Geometry geometry, bool yDown = false)
    {
        Clear();
        if (geometry.Faces.Count != 0) { Mesh(geometry); return; }
        var points = geometry.Lines.SelectMany(s => new[] { s.A, s.B }).ToArray();
        var fit = Fit(points, yDown);
        // Rapids first so a cutting move at the same location remains visible.
        foreach (var rapid in new[] { true, false })
            foreach (var line in geometry.Lines)
            {
                budget.Check();
                if (line.Rapid == rapid) Line(fit(line.A), fit(line.B), rapid ? Rapid : Ink);
            }
    }

    private Func<Point, Point> Fit(Point[] points, bool yDown)
    {
        if (points.Length == 0) throw new FormatException("No geometry.");
        var minX = points.Min(p => p.X); var maxX = points.Max(p => p.X);
        var minY = points.Min(p => p.Y); var maxY = points.Max(p => p.Y);
        var scale = (size - 24) / Math.Max(Math.Max(maxX - minX, maxY - minY), 1e-6);
        var cx = (minX + maxX) / 2; var cy = (minY + maxY) / 2;
        return p => new((p.X - cx) * scale + size / 2d, (p.Y - cy) * scale * (yDown ? 1 : -1) + size / 2d);
    }

    private void Line(Point a, Point b, uint color)
    {
        var steps = Math.Max(1, (int)Math.Ceiling(Math.Max(Math.Abs(b.X - a.X), Math.Abs(b.Y - a.Y))));
        for (var i = 0; i <= steps; i++)
        {
            budget.Check();
            var p = a + (b - a) * (i / (double)steps);
            Pixel((int)Math.Round(p.X), (int)Math.Round(p.Y), color);
        }
    }

    private static Point Project(Vector3 p) => new((p.X - p.Y) * 0.707106781, -(p.X + p.Y) * 0.40824829 + p.Z * 0.81649658);

    private void Mesh(Geometry geometry)
    {
        var fit = Fit(geometry.Faces.SelectMany(f => new[] { Project(f.A), Project(f.B), Project(f.C) }).ToArray(), false);
        foreach (var face in geometry.Faces)
        {
            budget.Check();
            var a = fit(Project(face.A)); var b = fit(Project(face.B)); var c = fit(Project(face.C));
            var area = Edge(a, b, c);
            if (Math.Abs(area) < 1e-8) continue;
            var normal = Vector3.Normalize(Vector3.Cross(face.B - face.A, face.C - face.A));
            if (!float.IsFinite(normal.X)) continue;
            var shade = 0.32 + 0.68 * Math.Abs(Vector3.Dot(normal, Vector3.Normalize(new Vector3(-0.3f, -0.5f, 1))));
            var color = ((uint)(40 + 42 * shade) << 24) | ((uint)(80 + 66 * shade) << 16) | ((uint)(100 + 66 * shade) << 8) | 255u;
            var az = face.A.X + face.A.Y + face.A.Z;
            var bz = face.B.X + face.B.Y + face.B.Z;
            var cz = face.C.X + face.C.Y + face.C.Z;
            var minX = Math.Clamp((int)Math.Floor(Math.Min(a.X, Math.Min(b.X, c.X))), 0, size - 1);
            var maxX = Math.Clamp((int)Math.Ceiling(Math.Max(a.X, Math.Max(b.X, c.X))), 0, size - 1);
            var minY = Math.Clamp((int)Math.Floor(Math.Min(a.Y, Math.Min(b.Y, c.Y))), 0, size - 1);
            var maxY = Math.Clamp((int)Math.Ceiling(Math.Max(a.Y, Math.Max(b.Y, c.Y))), 0, size - 1);
            for (var y = minY; y <= maxY; y++)
                for (var x = minX; x <= maxX; x++)
                {
                    budget.Check();
                    var p = new Point(x + 0.5, y + 0.5);
                    var u = Edge(b, c, p) / area; var v = Edge(c, a, p) / area; var w = 1 - u - v;
                    if (u < 0 || v < 0 || w < 0) continue;
                    var z = (float)(u * az + v * bz + w * cz);
                    if (z < depth[y * size + x]) continue;
                    depth[y * size + x] = z;
                    Pixel(x, y, color);
                }
        }
    }

    private static double Edge(Point a, Point b, Point p) => (p.X - a.X) * (b.Y - a.Y) - (p.Y - a.Y) * (b.X - a.X);

    internal void Tile(string type, string name, long bytes, string reason)
    {
        Clear();
        Text(type, 18, 22, 3);
        // Two name lines, with a visible ellipsis. The row and detail keep the original Unicode name.
        var title = Path.GetFileNameWithoutExtension(name).ToUpperInvariant();
        if (title.Length > 42) title = title[..39] + "...";
        Text(title[..Math.Min(21, title.Length)], 12, 72, 1);
        if (title.Length > 21) Text(title[21..], 12, 85, 1);
        var label = bytes >= 1024 * 1024 ? $"{bytes / (1024d * 1024):0.0} MB" : bytes >= 1024 ? $"{bytes / 1024d:0.0} KB" : $"{bytes} BYTES";
        Text(label, 12, 112, 1);
        Text(reason, 12, 150, 1);
    }

    // Original 5x7 bitmap alphabet. Fixed glyphs keep goldens independent of OS fonts.
    private const string Alphabet = "ABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789 .-_?";
    private static readonly string[] Glyphs = (
        "0E11111F111111 1E11111E11111E 0F10101010100F 1E11111111111E 1F10101E10101F 1F10101E101010 " +
        "0F10101711110F 1111111F111111 0E04040404040E 0101010111110E 11121418141211 1010101010101F " +
        "111B1515111111 11191513111111 0E11111111110E 1E11111E101010 0E11111115120D 1E11111E141211 " +
        "0F10100E01011E 1F040404040404 1111111111110E 11111111110A04 11111115151B11 11110A040A1111 " +
        "11110A04040404 1F01020408101F " +
        "0E11131519110E 040C040404040E 0E11010204081F 1E01010601011E 02060A121F0202 1F10101E01011E " +
        "0E10101E11110E 1F010204080808 0E11110E11110E 0E11110F01010E " +
        "00000000000000 00000000000C0C 0000001F000000 0000000000001F 0E110102040004").Split(' ');

    private void Text(string text, int left, int top, int scale)
    {
        foreach (var ch in text)
        {
            var index = Alphabet.IndexOf(ch);
            var glyph = Glyphs[index < 0 ? Alphabet.Length - 1 : index];
            for (var y = 0; y < 7; y++)
            {
                var bits = Convert.ToInt32(glyph.Substring(y * 2, 2), 16);
                for (var x = 0; x < 5; x++)
                    if ((bits & (1 << (4 - x))) != 0)
                        for (var dy = 0; dy < scale; dy++)
                            for (var dx = 0; dx < scale; dx++) Pixel(left + x * scale + dx, top + y * scale + dy, Ink);
            }
            left += 6 * scale;
        }
    }
}
