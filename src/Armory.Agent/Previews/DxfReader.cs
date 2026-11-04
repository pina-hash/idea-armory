using System.Globalization;
using System.Text;

namespace Armory.Agent.Previews;

// ASCII DXF's ENTITIES section only. BLOCK/INSERT, text, hatches and binary DXF are not
// interpreted. Entity records, vertices and spline degree are bounded before allocation.
internal static class DxfReader
{
    private readonly record struct Pair(int Code, string Value);
    private readonly record struct Vertex(Point Point, double Bulge);

    internal static void Read(Stream input, Geometry geometry)
    {
        using var reader = new StreamReader(input, Encoding.UTF8, true, 4096, leaveOpen: true);
        var record = new List<Pair>();
        var section = "";
        var kind = "";
        List<Vertex>? polyline = null;
        var closed = false;
        void Flush()
        {
            if (kind == "SECTION") { section = record.FirstOrDefault(p => p.Code == 2).Value; return; }
            if (kind == "ENDSEC") { section = ""; return; }
            if (section != "ENTITIES") return;
            if (kind == "POLYLINE")
            {
                polyline = [];
                closed = ((int)Number(record, 70) & 1) != 0;
            }
            else if (kind == "VERTEX" && polyline != null)
            {
                if (polyline.Count >= PreviewBudget.MaxSegments) throw new PreviewLimitException();
                polyline.Add(new(Coordinate(record, 10), Number(record, 42)));
            }
            else if (kind == "SEQEND" && polyline != null)
            {
                Polyline(polyline, closed, geometry);
                polyline = null;
            }
            else Entity(kind, record, geometry);
        }
        string? code;
        while ((code = geometry.Budget.Line(reader)) != null)
        {
            var value = geometry.Budget.Line(reader) ?? throw new FormatException("Unpaired DXF group.");
            var pair = new Pair(int.Parse(code.Trim(), CultureInfo.InvariantCulture), value.Trim());
            if (pair.Code == 0)
            {
                Flush();
                record.Clear();
                kind = pair.Value.ToUpperInvariant();
            }
            else
            {
                if (record.Count >= 32_768) throw new PreviewLimitException();
                record.Add(pair);
            }
        }
        Flush();
    }

    private static double Number(List<Pair> pairs, int code, double fallback = 0)
    {
        var value = pairs.FirstOrDefault(p => p.Code == code).Value;
        return value == null ? fallback : double.Parse(value, CultureInfo.InvariantCulture);
    }
    private static Point Coordinate(List<Pair> pairs, int code) => new(Number(pairs, code), Number(pairs, code + 10));

    private static void Entity(string kind, List<Pair> pairs, Geometry g)
    {
        switch (kind)
        {
            case "LINE": g.Line(Coordinate(pairs, 10), Coordinate(pairs, 11)); break;
            case "CIRCLE": g.Arc(Coordinate(pairs, 10), Number(pairs, 40), Number(pairs, 40), 0, Math.Tau); break;
            case "ARC":
                var start = Number(pairs, 50) * Math.PI / 180;
                var end = Number(pairs, 51) * Math.PI / 180;
                g.Arc(Coordinate(pairs, 10), Number(pairs, 40), Number(pairs, 40), start, (end - start + Math.Tau) % Math.Tau);
                break;
            case "LWPOLYLINE":
                var vertices = new List<Vertex>();
                foreach (var p in pairs)
                {
                    g.Budget.Check();
                    if (p.Code == 10) vertices.Add(new(new(double.Parse(p.Value, CultureInfo.InvariantCulture), 0), 0));
                    else if (p.Code is 20 or 42 && vertices.Count != 0)
                    {
                        var v = vertices[^1]; var n = double.Parse(p.Value, CultureInfo.InvariantCulture);
                        vertices[^1] = p.Code == 20 ? v with { Point = v.Point with { Y = n } } : v with { Bulge = n };
                    }
                }
                Polyline(vertices, ((int)Number(pairs, 70) & 1) != 0, g);
                break;
            case "SPLINE": Spline(pairs, g); break;
        }
    }

    private static void Polyline(List<Vertex> vertices, bool closed, Geometry g)
    {
        for (var i = 0; i < vertices.Count - (closed ? 0 : 1); i++)
        {
            var a = vertices[i]; var b = vertices[(i + 1) % vertices.Count];
            if (Math.Abs(a.Bulge) < 1e-10) { g.Line(a.Point, b.Point); continue; }
            var delta = b.Point - a.Point;
            var length = Math.Sqrt(delta.X * delta.X + delta.Y * delta.Y);
            if (length < 1e-10) continue;
            var center = (a.Point + b.Point) * 0.5 + new Point(-delta.Y, delta.X) * ((1 - a.Bulge * a.Bulge) / (4 * a.Bulge));
            var radius = length * (1 + a.Bulge * a.Bulge) / (4 * Math.Abs(a.Bulge));
            g.Arc(center, radius, radius, Math.Atan2(a.Point.Y - center.Y, a.Point.X - center.X), 4 * Math.Atan(a.Bulge));
        }
    }

    private static void Spline(List<Pair> pairs, Geometry g)
    {
        var controls = Coordinates(pairs, 10);
        if (controls.Count < 2)
        {
            // Fit-point-only exports have no knot/control net. Their fit points form the approximation.
            var fits = Coordinates(pairs, 11);
            for (var i = 1; i < fits.Count; i++) g.Line(fits[i - 1], fits[i]);
            return;
        }
        var degree = (int)Number(pairs, 71, 3);
        if (degree is < 1 or > 10 || controls.Count <= degree) throw new FormatException("Invalid spline degree.");
        var knots = pairs.Where(p => p.Code == 40).Select(p => double.Parse(p.Value, CultureInfo.InvariantCulture)).ToArray();
        var weights = pairs.Where(p => p.Code == 41).Select(p => double.Parse(p.Value, CultureInfo.InvariantCulture)).ToArray();
        if (knots.Length != controls.Count + degree + 1 || knots.Any(k => !double.IsFinite(k)) ||
            knots.Zip(knots.Skip(1)).Any(k => k.First > k.Second) || (weights.Length != 0 && weights.Length != controls.Count))
            throw new FormatException("Invalid spline knots or weights.");
        var from = knots[degree]; var to = knots[controls.Count];
        if (to <= from) throw new FormatException("Empty spline domain.");
        Point At(double t)
        {
            g.Budget.Check(degree * degree);
            var span = degree;
            while (span < controls.Count - 1 && t >= knots[span + 1]) span++;
            var points = new (double X, double Y, double W)[degree + 1];
            for (var j = 0; j <= degree; j++)
            {
                var index = span - degree + j; var w = weights.Length == 0 ? 1 : weights[index];
                points[j] = (controls[index].X * w, controls[index].Y * w, w);
            }
            for (var r = 1; r <= degree; r++)
                for (var j = degree; j >= r; j--)
                {
                    var k = span - degree + j;
                    var denominator = knots[k + degree - r + 1] - knots[k];
                    var a = denominator == 0 ? 0 : (t - knots[k]) / denominator;
                    var p = points[j - 1]; var q = points[j];
                    points[j] = (p.X * (1 - a) + q.X * a, p.Y * (1 - a) + q.Y * a, p.W * (1 - a) + q.W * a);
                }
            var result = points[degree];
            return new(result.X / result.W, result.Y / result.W);
        }
        var count = Math.Min(2048, controls.Count * 24);
        var previous = At(from);
        for (var i = 1; i <= count; i++)
        {
            var next = At(from + (to - from) * i / count);
            g.Line(previous, next); previous = next;
        }
    }

    private static List<Point> Coordinates(List<Pair> pairs, int code)
    {
        var points = new List<Point>();
        foreach (var p in pairs)
            if (p.Code == code) points.Add(new(double.Parse(p.Value, CultureInfo.InvariantCulture), 0));
            else if (p.Code == code + 10 && points.Count != 0) points[^1] = points[^1] with { Y = double.Parse(p.Value, CultureInfo.InvariantCulture) };
        return points;
    }
}
