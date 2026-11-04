using System.Diagnostics;
using System.Numerics;

namespace Armory.Agent.Previews;

// Limits apply to parsing AND drawing. No parser allocates from a count supplied by a file.
internal sealed class PreviewBudget(TimeSpan? duration = null)
{
    internal const long MaxFileBytes = 128L * 1024 * 1024;
    internal const int MaxSegments = 80_000, MaxTriangles = 24_000;
    internal static readonly TimeSpan Duration = TimeSpan.FromSeconds(2);
    private readonly long started = Stopwatch.GetTimestamp();
    private readonly TimeSpan duration = duration ?? Duration;
    private int operations;

    internal void Check(int cost = 1)
    {
        operations += cost;
        if (operations > 12_000_000 || Stopwatch.GetElapsedTime(started) > duration)
            throw new PreviewLimitException();
    }

    // StreamReader.ReadLine alone can allocate a file-sized string. Bound it before allocation.
    internal string? Line(TextReader reader)
    {
        Check();
        var line = new System.Text.StringBuilder();
        for (var i = 0; i < 16_384; i++)
        {
            var c = reader.Read();
            if (c < 0) return line.Length == 0 ? null : line.ToString();
            if (c == '\n') return line.ToString().TrimEnd('\r');
            line.Append((char)c);
        }
        throw new PreviewLimitException();
    }
}

internal sealed class PreviewLimitException : Exception;

internal readonly record struct Point(double X, double Y)
{
    internal bool Valid => double.IsFinite(X) && double.IsFinite(Y) && Math.Abs(X) <= 1e12 && Math.Abs(Y) <= 1e12;
    public static Point operator +(Point a, Point b) => new(a.X + b.X, a.Y + b.Y);
    public static Point operator -(Point a, Point b) => new(a.X - b.X, a.Y - b.Y);
    public static Point operator *(Point a, double b) => new(a.X * b, a.Y * b);
}

internal readonly record struct Segment(Point A, Point B, bool Rapid = false);
internal readonly record struct Face(Vector3 A, Vector3 B, Vector3 C);

internal sealed class Geometry(PreviewBudget budget)
{
    internal readonly List<Segment> Lines = [];
    internal readonly List<Face> Faces = [];
    internal PreviewBudget Budget { get; } = budget;

    internal void Line(Point a, Point b, bool rapid = false)
    {
        Budget.Check();
        if (!a.Valid || !b.Valid) throw new FormatException("Nonfinite or excessive coordinates.");
        if (Lines.Count >= PreviewBudget.MaxSegments) throw new PreviewLimitException();
        Lines.Add(new(a, b, rapid));
    }

    internal void Arc(Point center, double rx, double ry, double from, double sweep, Func<Point, Point>? transform = null, bool rapid = false)
    {
        if (!double.IsFinite(sweep) || Math.Abs(sweep) > Math.Tau * 2) throw new FormatException("Invalid arc.");
        var count = Math.Clamp((int)Math.Ceiling(Math.Abs(sweep) * 16), 1, 128);
        Point At(double angle)
        {
            var p = new Point(center.X + rx * Math.Cos(angle), center.Y + ry * Math.Sin(angle));
            return transform?.Invoke(p) ?? p;
        }
        var previous = At(from);
        for (var i = 1; i <= count; i++)
        {
            var next = At(from + sweep * i / count);
            Line(previous, next, rapid);
            previous = next;
        }
    }

    internal void Cubic(Point a, Point b, Point c, Point d, Func<Point, Point>? transform = null)
    {
        var previous = transform?.Invoke(a) ?? a;
        for (var i = 1; i <= 24; i++)
        {
            var t = i / 24d;
            var u = 1 - t;
            var next = a * (u * u * u) + b * (3 * u * u * t) + c * (3 * u * t * t) + d * (t * t * t);
            next = transform?.Invoke(next) ?? next;
            Line(previous, next);
            previous = next;
        }
    }
}

// SVG and LightBurn both store affine transforms as a b c d e f.
internal readonly record struct Transform(double A, double B, double C, double D, double E, double F)
{
    internal static readonly Transform Identity = new(1, 0, 0, 1, 0, 0);
    internal Point Apply(Point p) => new(A * p.X + C * p.Y + E, B * p.X + D * p.Y + F);
    internal Transform Then(Transform local) => new(
        A * local.A + C * local.B, B * local.A + D * local.B,
        A * local.C + C * local.D, B * local.C + D * local.D,
        A * local.E + C * local.F + E, B * local.E + D * local.F + F);
}
