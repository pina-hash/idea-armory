using System.Globalization;

namespace Armory.Agent.Previews;

// Bounded numeric scanner shared by SVG paths/transforms and LightBurn lists.
internal sealed class Numbers(string text)
{
    internal int At;
    internal bool End { get { Space(); return At >= text.Length; } }
    internal char Peek { get { Space(); return At < text.Length ? text[At] : '\0'; } }
    internal char Take() { Space(); return At < text.Length ? text[At++] : '\0'; }
    internal void Space() { while (At < text.Length && (char.IsWhiteSpace(text[At]) || text[At] == ',')) At++; }
    internal double Number()
    {
        Space();
        var start = At;
        if (At < text.Length && text[At] is '+' or '-') At++;
        while (At < text.Length && char.IsAsciiDigit(text[At])) At++;
        if (At < text.Length && text[At] == '.') { At++; while (At < text.Length && char.IsAsciiDigit(text[At])) At++; }
        if (At < text.Length && text[At] is 'e' or 'E')
        {
            At++;
            if (At < text.Length && text[At] is '+' or '-') At++;
            while (At < text.Length && char.IsAsciiDigit(text[At])) At++;
        }
        if (At == start || !double.TryParse(text.AsSpan(start, At - start), NumberStyles.Float, CultureInfo.InvariantCulture, out var value) ||
            !double.IsFinite(value) || Math.Abs(value) > 1e12) throw new FormatException("Invalid shape number.");
        return value;
    }
    internal Point Point() => new(Number(), Number());
    internal static double[] All(string text)
    {
        var n = new Numbers(text); var values = new List<double>();
        while (!n.End)
        {
            if (values.Count >= 16_384) throw new PreviewLimitException();
            values.Add(n.Number());
        }
        return [.. values];
    }
}

internal static class VectorPath
{
    internal static void Read(string text, Geometry g, Transform transform)
    {
        var n = new Numbers(text);
        var current = new Point(); var first = current; var previousControl = current;
        var command = '\0'; var previous = '\0';
        while (!n.End)
        {
            g.Budget.Check();
            if (char.IsAsciiLetter(n.Peek)) command = n.Take();
            else if (command == '\0' || char.ToUpperInvariant(command) == 'Z') throw new FormatException("Missing path command.");
            var relative = char.IsLower(command);
            var start = current;
            Point P() => n.Point() + (relative ? start : new Point());
            switch (char.ToUpperInvariant(command))
            {
                case 'M': current = P(); first = current; command = relative ? 'l' : 'L'; break;
                case 'L': current = P(); g.Line(transform.Apply(start), transform.Apply(current)); break;
                case 'H': current = current with { X = n.Number() + (relative ? start.X : 0) }; g.Line(transform.Apply(start), transform.Apply(current)); break;
                case 'V': current = current with { Y = n.Number() + (relative ? start.Y : 0) }; g.Line(transform.Apply(start), transform.Apply(current)); break;
                case 'C':
                    var c1 = P(); var c2 = P(); current = P();
                    g.Cubic(start, c1, c2, current, transform.Apply); previousControl = c2;
                    break;
                case 'S':
                    c1 = previous is 'C' or 'S' ? start * 2 - previousControl : start;
                    c2 = P(); current = P();
                    g.Cubic(start, c1, c2, current, transform.Apply); previousControl = c2;
                    break;
                case 'Q': case 'T':
                    var control = char.ToUpperInvariant(command) == 'Q' ? P() : previous is 'Q' or 'T' ? start * 2 - previousControl : start;
                    current = P();
                    g.Cubic(start, start + (control - start) * (2d / 3), current + (control - current) * (2d / 3), current, transform.Apply);
                    previousControl = control;
                    break;
                case 'A':
                    var rx = Math.Abs(n.Number()); var ry = Math.Abs(n.Number()); var angle = n.Number();
                    // SVG permits adjacent one-digit flags ("01"), so don't read them as a number.
                    var large = Flag(n); var sweep = Flag(n); current = P();
                    Arc(start, current, rx, ry, angle * Math.PI / 180, large, sweep, g, transform);
                    break;
                case 'Z': current = first; g.Line(transform.Apply(start), transform.Apply(first)); break;
                default: throw new FormatException("Unsupported SVG path command.");
            }
            previous = char.ToUpperInvariant(command);
        }
    }

    private static bool Flag(Numbers n) => n.Take() switch { '0' => false, '1' => true, _ => throw new FormatException("Invalid SVG arc flag.") };

    internal static void Arc(Point a, Point b, double rx, double ry, double phi, bool large, bool sweep, Geometry g, Transform transform)
    {
        if (a == b) return;
        if (rx == 0 || ry == 0) { g.Line(transform.Apply(a), transform.Apply(b)); return; }
        var cos = Math.Cos(phi); var sin = Math.Sin(phi);
        var half = (a - b) * 0.5;
        var x = cos * half.X + sin * half.Y; var y = -sin * half.X + cos * half.Y;
        var lambda = x * x / (rx * rx) + y * y / (ry * ry);
        if (lambda > 1) { var scale = Math.Sqrt(lambda); rx *= scale; ry *= scale; }
        var multiplier = Math.Sqrt(Math.Max(0, (rx * rx * ry * ry - rx * rx * y * y - ry * ry * x * x) / (rx * rx * y * y + ry * ry * x * x))) * (large == sweep ? -1 : 1);
        var cx = multiplier * rx * y / ry; var cy = -multiplier * ry * x / rx;
        var center = new Point(cos * cx - sin * cy, sin * cx + cos * cy) + (a + b) * 0.5;
        var start = Math.Atan2((y - cy) / ry, (x - cx) / rx);
        var end = Math.Atan2((-y - cy) / ry, (-x - cx) / rx);
        var delta = (end - start + Math.Tau) % Math.Tau;
        if (!sweep) delta -= Math.Tau;
        var ellipse = transform.Then(new(cos, sin, -sin, cos, center.X, center.Y));
        g.Arc(new(), rx, ry, start, delta, ellipse.Apply);
    }

    internal static Transform ParseTransform(string text)
    {
        var result = Transform.Identity;
        var at = 0;
        while (at < text.Length)
        {
            while (at < text.Length && (char.IsWhiteSpace(text[at]) || text[at] == ',')) at++;
            if (at == text.Length) break;
            var start = at;
            while (at < text.Length && char.IsAsciiLetter(text[at])) at++;
            var name = text[start..at];
            while (at < text.Length && char.IsWhiteSpace(text[at])) at++;
            if (at >= text.Length || text[at++] != '(') throw new FormatException("Invalid transform.");
            start = at;
            while (at < text.Length && text[at] != ')') at++;
            if (at >= text.Length) throw new FormatException("Unclosed transform.");
            var n = Numbers.All(text[start..at++]);
            var local = name switch
            {
                "matrix" when n.Length == 6 => new Transform(n[0], n[1], n[2], n[3], n[4], n[5]),
                "translate" when n.Length is 1 or 2 => new(1, 0, 0, 1, n[0], n.Length == 2 ? n[1] : 0),
                "scale" when n.Length is 1 or 2 => new(n[0], 0, 0, n.Length == 2 ? n[1] : n[0], 0, 0),
                "rotate" when n.Length is 1 or 3 => Rotation(n),
                "skewX" when n.Length == 1 => new(1, 0, Math.Tan(n[0] * Math.PI / 180), 1, 0, 0),
                "skewY" when n.Length == 1 => new(1, Math.Tan(n[0] * Math.PI / 180), 0, 1, 0, 0),
                _ => throw new FormatException("Unsupported transform.")
            };
            result = result.Then(local);
        }
        return result;
    }

    private static Transform Rotation(double[] n)
    {
        var radians = n[0] * Math.PI / 180; var cos = Math.Cos(radians); var sin = Math.Sin(radians);
        var x = n.Length == 3 ? n[1] : 0; var y = n.Length == 3 ? n[2] : 0;
        return new(cos, sin, -sin, cos, x - cos * x + sin * y, y - sin * x - cos * y);
    }
}
