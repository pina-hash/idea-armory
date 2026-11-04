using System.Globalization;
using System.Xml;
using System.Xml.Linq;

namespace Armory.Agent.Previews;

// Shape outlines only. XML never resolves entities, DTDs, URLs, scripts or external images.
// Validate counts/depth/attribute lengths before building a bounded tree for group transforms.
internal static class ShapeReader
{
    private static XDocument Load(Stream input, Geometry g)
    {
        if (input.Length > 8 * 1024 * 1024) throw new PreviewLimitException();
        var settings = new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null, MaxCharactersInDocument = 8 * 1024 * 1024 };
        var count = 0;
        using (var reader = XmlReader.Create(input, settings))
            while (reader.Read())
            {
                g.Budget.Check();
                if (++count > 40_000 || reader.Depth > 64 || reader.Value.Length > 128 * 1024 || reader.AttributeCount > 64) throw new PreviewLimitException();
                if (reader.MoveToFirstAttribute())
                    do { if (reader.Value.Length > 128 * 1024) throw new PreviewLimitException(); } while (reader.MoveToNextAttribute());
                reader.MoveToElement();
            }
        input.Position = 0;
        using var final = XmlReader.Create(input, settings);
        return XDocument.Load(final, LoadOptions.None);
    }

    private static string Attribute(XElement e, string name, string fallback = "") => e.Attribute(name)?.Value ?? fallback;
    private static double Number(XElement e, string name, double fallback = 0)
    {
        var text = e.Attribute(name)?.Value;
        return text == null ? fallback : new Numbers(text).Number();
    }

    internal static void Svg(Stream input, Geometry g)
    {
        var document = Load(input, g);
        if (document.Root?.Name.LocalName != "svg") throw new FormatException("Not an SVG.");
        WalkSvg(document.Root, g, Transform.Identity);
    }

    private static void WalkSvg(XElement e, Geometry g, Transform parent)
    {
        g.Budget.Check();
        var kind = e.Name.LocalName;
        if (kind is "defs" or "clipPath" or "mask" or "symbol" or "metadata" or "pattern" or "script" or "style") return;
        var style = Attribute(e, "style").Replace(" ", "", StringComparison.Ordinal);
        if (Attribute(e, "display") == "none" || Attribute(e, "visibility") == "hidden" || style.Contains("display:none", StringComparison.Ordinal) || style.Contains("visibility:hidden", StringComparison.Ordinal)) return;
        var transform = parent.Then(VectorPath.ParseTransform(Attribute(e, "transform")));
        Point P(double x, double y) => transform.Apply(new(x, y));
        switch (kind)
        {
            case "line": g.Line(P(Number(e, "x1"), Number(e, "y1")), P(Number(e, "x2"), Number(e, "y2"))); break;
            case "rect":
                Rectangle(Number(e, "x"), Number(e, "y"), Number(e, "width"), Number(e, "height"), transform, g);
                break;
            case "circle": case "ellipse":
                var rx = Number(e, kind == "circle" ? "r" : "rx"); var ry = Number(e, kind == "circle" ? "r" : "ry");
                g.Arc(new(Number(e, "cx"), Number(e, "cy")), rx, ry, 0, Math.Tau, transform.Apply);
                break;
            case "polyline": case "polygon":
                var points = Numbers.All(Attribute(e, "points"));
                if (points.Length % 2 != 0) throw new FormatException("Unpaired shape coordinate.");
                for (var i = 2; i < points.Length; i += 2) g.Line(P(points[i - 2], points[i - 1]), P(points[i], points[i + 1]));
                if (kind == "polygon" && points.Length >= 4) g.Line(P(points[^2], points[^1]), P(points[0], points[1]));
                break;
            case "path": VectorPath.Read(Attribute(e, "d"), g, transform); break;
        }
        foreach (var child in e.Elements()) WalkSvg(child, g, transform);
    }

    private static void Rectangle(double x, double y, double width, double height, Transform t, Geometry g)
    {
        var a = t.Apply(new(x, y)); var b = t.Apply(new(x + width, y));
        var c = t.Apply(new(x + width, y + height)); var d = t.Apply(new(x, y + height));
        g.Line(a, b); g.Line(b, c); g.Line(c, d); g.Line(d, a);
    }

    internal static void LightBurn(Stream input, Geometry g)
    {
        var document = Load(input, g);
        if (document.Root?.Name.LocalName != "LightBurnProject") throw new FormatException("Not a LightBurn project.");
        foreach (var shape in document.Root.Elements("Shape")) WalkLightBurn(shape, g, Transform.Identity);
    }

    private static void WalkLightBurn(XElement e, Geometry g, Transform parent)
    {
        g.Budget.Check();
        var transform = parent;
        if (e.Element("XForm") is { } xform)
        {
            var n = Numbers.All(xform.Value);
            if (n.Length != 6) throw new FormatException("Invalid LightBurn transform.");
            transform = parent.Then(new(n[0], n[1], n[2], n[3], n[4], n[5]));
        }
        switch (Attribute(e, "Type"))
        {
            case "Group":
                foreach (var child in e.Element("Children")?.Elements("Shape") ?? e.Elements("Shape")) WalkLightBurn(child, g, transform);
                break;
            case "Rect":
                var width = Number(e, "W"); var height = Number(e, "H");
                Rectangle(-width / 2, -height / 2, width, height, transform, g);
                break;
            case "Ellipse": g.Arc(new(), Number(e, "Rx"), Number(e, "Ry"), 0, Math.Tau, transform.Apply); break;
            case "Path": LightBurnPath(e, g, transform); break;
        }
    }

    private sealed record BurnVertex(Point P, Point? Incoming = null, Point? Outgoing = null);

    private static void LightBurnPath(XElement e, Geometry g, Transform transform)
    {
        var vertices = new List<BurnVertex>();
        if (e.Element("VertList") is { } list)
        {
            var n = new Numbers(list.Value);
            while (!n.End)
            {
                g.Budget.Check();
                if (n.Take() != 'V') throw new FormatException("Invalid LightBurn vertex.");
                var p = n.Point();
                double? c0x = null, c0y = null, c1x = null, c1y = null;
                while (!n.End && n.Peek != 'V')
                {
                    if (n.Take() != 'c') throw new FormatException("Invalid LightBurn control point.");
                    var control = n.Take(); var axis = n.Take(); var number = n.Number();
                    switch ((control, axis))
                    {
                        case ('0', 'x'): c0x = number; break; case ('0', 'y'): c0y = number; break;
                        case ('1', 'x'): c1x = number; break; case ('1', 'y'): c1y = number; break;
                        default: throw new FormatException("Invalid LightBurn control coordinate.");
                    }
                }
                vertices.Add(new(p, c0x != null && c0y != null ? new(c0x.Value, c0y.Value) : null,
                    c1x != null && c1y != null ? new(c1x.Value, c1y.Value) : null));
            }
        }
        else
            foreach (var v in e.Elements("V"))
                vertices.Add(new(new(Number(v, "vx"), Number(v, "vy")),
                    v.Attribute("c0x") != null ? new(Number(v, "c0x"), Number(v, "c0y")) : null,
                    v.Attribute("c1x") != null ? new(Number(v, "c1x"), Number(v, "c1y")) : null));

        void Primitive(char kind, int a, int b)
        {
            if (a < 0 || b < 0 || a >= vertices.Count || b >= vertices.Count) throw new FormatException("LightBurn vertex index out of range.");
            var from = vertices[a]; var to = vertices[b];
            if (kind == 'L') g.Line(transform.Apply(from.P), transform.Apply(to.P));
            else if (kind == 'B') g.Cubic(from.P, from.Outgoing ?? from.P, to.Incoming ?? to.P, to.P, transform.Apply);
            else throw new FormatException("Unknown LightBurn primitive.");
        }
        if (e.Element("PrimList") is { } primitives)
        {
            var n = new Numbers(primitives.Value);
            while (!n.End)
            {
                g.Budget.Check();
                var kind = n.Take(); var a = n.Number(); var b = n.Number();
                if (a != Math.Truncate(a) || b != Math.Truncate(b)) throw new FormatException("Invalid LightBurn index.");
                Primitive(kind, checked((int)a), checked((int)b));
            }
        }
        else
            foreach (var p in e.Elements("P"))
                Primitive(Attribute(p, "T", "L")[0], (int)Number(p, "p0"), (int)Number(p, "p1"));
    }
}
