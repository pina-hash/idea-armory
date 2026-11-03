using System.Buffers.Binary;
using System.Globalization;
using System.Numerics;
using System.Text;

namespace Armory.Agent.Previews;

internal static class StlReader
{
    internal static void Read(Stream input, Geometry geometry)
    {
        Span<byte> header = stackalloc byte[84];
        if (input.Length >= 84)
        {
            input.ReadExactly(header);
            var count = BinaryPrimitives.ReadUInt32LittleEndian(header[80..]);
            // Binary headers may start with "solid". Exact byte length disambiguates them.
            if (84L + count * 50L == input.Length)
            {
                var stride = Math.Max(1L, (count + PreviewBudget.MaxTriangles - 1L) / PreviewBudget.MaxTriangles);
                Span<byte> triangle = stackalloc byte[50];
                for (long i = 0; i < count; i += stride)
                {
                    geometry.Budget.Check();
                    input.Position = 84 + i * 50;
                    input.ReadExactly(triangle);
                    Add(geometry, Vector(triangle[12..]), Vector(triangle[24..]), Vector(triangle[36..]));
                }
                return;
            }
        }
        input.Position = 0;
        using var reader = new StreamReader(input, Encoding.ASCII, false, 4096, leaveOpen: true);
        var vertices = new List<Vector3>(3);
        string? line;
        while ((line = geometry.Budget.Line(reader)) != null)
        {
            var parts = line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length == 0 || !parts[0].Equals("vertex", StringComparison.OrdinalIgnoreCase)) continue;
            if (parts.Length != 4) throw new FormatException("Invalid STL vertex.");
            vertices.Add(new(float.Parse(parts[1], CultureInfo.InvariantCulture), float.Parse(parts[2], CultureInfo.InvariantCulture), float.Parse(parts[3], CultureInfo.InvariantCulture)));
            if (vertices.Count != 3) continue;
            Add(geometry, vertices[0], vertices[1], vertices[2]);
            vertices.Clear();
        }
        if (vertices.Count != 0) throw new FormatException("Incomplete STL triangle.");
    }

    private static Vector3 Vector(ReadOnlySpan<byte> bytes) => new(
        BinaryPrimitives.ReadSingleLittleEndian(bytes), BinaryPrimitives.ReadSingleLittleEndian(bytes[4..]), BinaryPrimitives.ReadSingleLittleEndian(bytes[8..]));

    private static void Add(Geometry geometry, Vector3 a, Vector3 b, Vector3 c)
    {
        if (geometry.Faces.Count >= PreviewBudget.MaxTriangles) throw new PreviewLimitException();
        foreach (var p in new[] { a, b, c })
            if (!float.IsFinite(p.Z) || Math.Abs(p.Z) > 1e12 || !new Point(p.X, p.Y).Valid) throw new FormatException("Invalid STL coordinate.");
        geometry.Faces.Add(new(a, b, c));
    }
}
