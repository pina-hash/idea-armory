using System.Buffers.Binary;
using System.Text;

namespace Armory.Agent.Tests.Previews;

// Synthetic data only. Generate large cases on demand, never commit lab/student files.
internal static class LargeFixtures
{
    internal static string Mesh(string folder)
    {
        var file = Path.Combine(folder, "100MB-surface.stl");
        using var output = File.Create(file);
        var header = new byte[84];
        Encoding.ASCII.GetBytes("Original generated tessellated surface").CopyTo(header, 0);
        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(80), 2_000_000);
        output.Write(header);
        var triangle = new byte[50];
        void Face(float ax, float ay, float az, float bx, float by, float bz, float cx, float cy, float cz)
        {
            var values = new[] { ax, ay, az, bx, by, bz, cx, cy, cz };
            for (var i = 0; i < values.Length; i++) BinaryPrimitives.WriteSingleLittleEndian(triangle.AsSpan(12 + i * 4), values[i]);
            output.Write(triangle);
        }
        for (var y = 0; y < 1000; y++)
            for (var x = 0; x < 1000; x++)
            {
                var z = (x / 50 % 2 == 0 ? x % 50 : 50 - x % 50) * 3;
                var nz = ((x + 1) / 50 % 2 == 0 ? (x + 1) % 50 : 50 - (x + 1) % 50) * 3;
                Face(x, y, z, x + 1, y, nz, x + 1, y + 1, nz);
                Face(x, y, z, x + 1, y + 1, nz, x, y + 1, z);
            }
        return file;
    }

    internal static IEnumerable<string> Linework(string folder)
    {
        var dxf = new StringBuilder("0\nSECTION\n2\nENTITIES\n");
        var gcode = new StringBuilder("; Synthetic, preview only\nG21 G90\nG0 X0 Y0\n");
        var svg = new StringBuilder("<svg xmlns=\"http://www.w3.org/2000/svg\">");
        var burn = new StringBuilder("<LightBurnProject><Shape Type=\"Group\"><Children>");
        for (var i = 0; i < 3000; i++)
        {
            var x = i % 60; var y = i / 60;
            dxf.Append($"0\nLINE\n10\n{x}\n20\n{y}\n11\n{x + 1}\n21\n{y + 1}\n");
            gcode.Append($"G1 X{x} Y{y}\nX{x + 1} Y{y + 1}\n");
            svg.Append($"<rect x=\"{x}\" y=\"{y}\" width=\"0.8\" height=\"0.8\"/>");
            burn.Append($"<Shape Type=\"Rect\" W=\"0.8\" H=\"0.8\"><XForm>1 0 0 1 {x} {y}</XForm></Shape>");
        }
        dxf.Append("0\nENDSEC\n0\nEOF\n"); svg.Append("</svg>"); burn.Append("</Children></Shape></LightBurnProject>");
        foreach (var (name, data) in new[] { ("large.dxf", dxf), ("large.gcode", gcode), ("large.nc", gcode), ("large.tap", gcode),
                     ("large.svg", svg), ("large.lbrn", burn), ("large.lbrn2", burn) })
        {
            var file = Path.Combine(folder, name);
            File.WriteAllText(file, data.ToString());
            yield return file;
        }
    }
}
