using System.Globalization;
using System.Text;

namespace Armory.Agent.Previews;

// A view of programmed motion, not a machine simulator. No macros, expressions, canned
// cycles, cutter compensation, or machine work-offset tables are evaluated.
internal static class GCodeReader
{
    internal static void Read(Stream input, Geometry g)
    {
        using var reader = new StreamReader(input, Encoding.UTF8, true, 4096, leaveOpen: true);
        var position = new double[3]; var offset = new double[3];
        var absolute = true; var absoluteCenter = false;
        var units = 1d; var motion = 0; var plane = 17;
        string? line;
        while ((line = g.Budget.Line(reader)) != null)
        {
            var words = Words(line);
            var reset = false; var nonMotion = false;
            foreach (var (letter, value) in words)
                if (letter == 'G')
                {
                    switch (value)
                    {
                        case 0: case 1: case 2: case 3: motion = (int)value; break;
                        case 17: case 18: case 19: plane = (int)value; break;
                        case 20: units = 25.4; break;
                        case 21: units = 1; break;
                        case 90: absolute = true; break;
                        case 91: absolute = false; break;
                        case 90.1: absoluteCenter = true; break;
                        case 91.1: absoluteCenter = false; break;
                        case 92: reset = true; break;
                        case 4: case 10: case 28: case 30: case 53: nonMotion = true; break;
                        case 80: motion = -1; break;
                        case >= 81 and <= 89: throw new FormatException("Canned cycle is not a simple toolpath.");
                    }
                }
            double? Value(char letter) => words.Where(w => w.Letter == letter).Select(w => (double?)w.Value).LastOrDefault();
            if (reset)
            {
                for (var i = 0; i < 3; i++) if (Value("XYZ"[i]) is { } v) offset[i] = position[i] - v * units;
                continue;
            }
            if (nonMotion || motion < 0) continue;
            var next = (double[])position.Clone();
            var hasAxis = false;
            for (var i = 0; i < 3; i++)
                if (Value("XYZ"[i]) is { } v)
                {
                    hasAxis = true;
                    next[i] = v * units + (absolute ? offset[i] : position[i]);
                }
            if (!hasAxis && !(motion is 2 or 3 && (Value('I') != null || Value('J') != null || Value('K') != null))) continue;
            if (motion is 0 or 1) g.Line(new(position[0], position[1]), new(next[0], next[1]), motion == 0);
            else
            {
                // Ordered axes give the conventional positive normal for G17, G18 and G19.
                var (u, v, w) = plane == 18 ? (2, 0, 1) : plane == 19 ? (1, 2, 0) : (0, 1, 2);
                var a = new Point(position[u], position[v]); var b = new Point(next[u], next[v]);
                Point center;
                if (Value('R') is { } r) center = RadiusCenter(a, b, r * units, motion == 2);
                else
                {
                    var cu = Value("IJK"[u]); var cv = Value("IJK"[v]);
                    if (cu == null && cv == null) throw new FormatException("Arc has no center.");
                    center = new(absoluteCenter ? (cu ?? (position[u] - offset[u]) / units) * units + offset[u] : a.X + (cu ?? 0) * units,
                        absoluteCenter ? (cv ?? (position[v] - offset[v]) / units) * units + offset[v] : a.Y + (cv ?? 0) * units);
                }
                var radius = Math.Sqrt((a.X - center.X) * (a.X - center.X) + (a.Y - center.Y) * (a.Y - center.Y));
                var endRadius = Math.Sqrt((b.X - center.X) * (b.X - center.X) + (b.Y - center.Y) * (b.Y - center.Y));
                if (Math.Abs(radius - endRadius) > Math.Max(0.05, radius * 0.01)) throw new FormatException("Arc endpoints disagree.");
                var start = Math.Atan2(a.Y - center.Y, a.X - center.X);
                var sweep = Sweep(start, Math.Atan2(b.Y - center.Y, b.X - center.X), motion == 2);
                var steps = Math.Clamp((int)Math.Ceiling(Math.Abs(sweep) * 16), 1, 128);
                var previous = new Point(position[0], position[1]);
                for (var i = 1; i <= steps; i++)
                {
                    var t = i / (double)steps; var angle = start + sweep * t;
                    var at = new double[3];
                    at[u] = center.X + radius * Math.Cos(angle); at[v] = center.Y + radius * Math.Sin(angle);
                    at[w] = position[w] + (next[w] - position[w]) * t;
                    var p = new Point(at[0], at[1]); g.Line(previous, p); previous = p;
                }
            }
            position = next;
        }
    }

    private static double Sweep(double start, double end, bool clockwise)
    {
        var sweep = (end - start + Math.Tau) % Math.Tau;
        if (clockwise) return sweep < 1e-10 ? -Math.Tau : sweep - Math.Tau;
        return sweep < 1e-10 ? Math.Tau : sweep;
    }

    private static Point RadiusCenter(Point a, Point b, double r, bool clockwise)
    {
        var d = b - a; var length = Math.Sqrt(d.X * d.X + d.Y * d.Y);
        if (length == 0 || length > 2 * Math.Abs(r)) throw new FormatException("Invalid radius arc.");
        var height = Math.Sqrt(Math.Max(0, r * r - length * length / 4));
        var direction = (clockwise ? -1 : 1) * (r < 0 ? -1 : 1);
        return (a + b) * 0.5 + new Point(-d.Y, d.X) * (height / length * direction);
    }

    private static List<(char Letter, double Value)> Words(string line)
    {
        var result = new List<(char, double)>();
        var comment = 0;
        for (var i = 0; i < line.Length;)
        {
            var c = line[i++];
            if (c == ';' && comment == 0) break;
            if (c == '(') { comment++; continue; }
            if (c == ')') { comment = Math.Max(0, comment - 1); continue; }
            if (comment != 0 || !char.IsAsciiLetter(c)) continue;
            c = char.ToUpperInvariant(c);
            while (i < line.Length && char.IsWhiteSpace(line[i])) i++;
            var start = i;
            while (i < line.Length && (char.IsAsciiDigit(line[i]) || line[i] is '+' or '-' or '.')) i++;
            if (i == start) continue;
            if (!double.TryParse(line.AsSpan(start, i - start), NumberStyles.Float, CultureInfo.InvariantCulture, out var number) || !double.IsFinite(number))
                throw new FormatException("Invalid G-code number.");
            result.Add((c, number));
        }
        return result;
    }
}
