using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Text.Json;

namespace TTLSim.UI.Export;

/// <summary>
/// The chip-label vector font: Arial outlines recovered from Grant Searle's
/// chip label sheet, plus synthesized glyphs (currently '='), carried as an
/// embedded JSON resource (VectorFont.json). See Chip_Labels.md for
/// provenance, format, and the missing-glyph policy.
///
/// Text is rendered as FILLED PATHS, never as font text, so the output PDF
/// needs no font embedding and prints identically everywhere. Glyph paths
/// are TrueType quadratic Beziers in font units (2048/em, y-up, baseline
/// at y = 0); <see cref="AppendTextOps"/> converts quadratics to the cubic
/// operators PDF understands via the standard 2/3 control-point rule.
/// </summary>
public sealed class LabelVectorFont
{
    /// <summary>One path command: "M" (2 coords), "L" (2), "Q" (4), "Z" (0).</summary>
    private readonly record struct PathCommand(char Op, double A, double B, double C, double D);

    private sealed record Glyph(double Advance, PathCommand[] Path);

    private readonly Dictionary<char, Glyph> glyphs = new();
    private readonly double unitsPerEm;

    /// <summary>Arial cap height as a fraction of the em. Used for vertical
    /// centring and the end-row border clamp (Chip_Labels.md §2).</summary>
    public const double CapHeightEm = 0.716;

    /// <summary>Slash descender depth as a fraction of the em ('/' is the only
    /// descending character in pin names). Used by the end-row border clamp.</summary>
    public const double DescenderEm = 0.21;

    private LabelVectorFont(double unitsPerEm) => this.unitsPerEm = unitsPerEm;

    /// <summary>
    /// Load the "regular" face from the embedded VectorFont.json resource.
    /// The resource is located by filename suffix so the project's default
    /// namespace/folder prefix doesn't matter.
    /// </summary>
    public static LabelVectorFont LoadEmbedded()
    {
        Assembly assembly = typeof(LabelVectorFont).Assembly;
        string? resourceName = assembly.GetManifestResourceNames()
            .FirstOrDefault(n => n.EndsWith("VectorFont.json", StringComparison.OrdinalIgnoreCase));
        if (resourceName is null)
            throw new InvalidOperationException(
                "Embedded resource VectorFont.json not found. Add it to the project " +
                "with Build Action = Embedded Resource.");

        using Stream stream = assembly.GetManifestResourceStream(resourceName)!;
        return Load(stream);
    }

    /// <summary>Load the "regular" face from a VectorFont.json stream.</summary>
    public static LabelVectorFont Load(Stream jsonStream)
    {
        using JsonDocument doc = JsonDocument.Parse(jsonStream);
        JsonElement regular = doc.RootElement.GetProperty("regular");
        var font = new LabelVectorFont(regular.GetProperty("unitsPerEm").GetDouble());

        foreach (JsonProperty entry in regular.GetProperty("glyphs").EnumerateObject())
        {
            char ch = entry.Name[0];
            double advance = entry.Value.GetProperty("advance").GetDouble();
            var commands = new List<PathCommand>();
            foreach (JsonElement cmd in entry.Value.GetProperty("path").EnumerateArray())
            {
                char op = cmd[0].GetString()![0];
                double a = 0, b = 0, c = 0, d = 0;
                int length = cmd.GetArrayLength();
                if (length > 1) a = cmd[1].GetDouble();
                if (length > 2) b = cmd[2].GetDouble();
                if (length > 3) c = cmd[3].GetDouble();
                if (length > 4) d = cmd[4].GetDouble();
                commands.Add(new PathCommand(op, a, b, c, d));
            }
            font.glyphs[ch] = new Glyph(advance, commands.ToArray());
        }
        return font;
    }

    /// <summary>Width of <paramref name="text"/> in points at the given size.
    /// Unknown characters count as half an em (they render as a gap).</summary>
    public double MeasureText(string text, double sizePt)
    {
        double widthUnits = 0;
        foreach (char ch in text)
            widthUnits += glyphs.TryGetValue(ch, out Glyph? g) ? g.Advance : unitsPerEm * 0.5;
        return widthUnits * sizePt / unitsPerEm;
    }

    /// <summary>
    /// Append PDF path-fill operators drawing <paramref name="text"/> as
    /// filled outlines. Origin (x, y) is the baseline start in PDF points
    /// (y-up); rotationDeg rotates about the origin (90 = reading
    /// bottom-to-top). Caller sets the fill colour ("rg") beforehand.
    /// Unknown characters advance the pen by half an em and draw nothing --
    /// per the missing-glyph policy the fix is to add the glyph to
    /// VectorFont.json, never to substitute.
    /// </summary>
    public void AppendTextOps(StringBuilder ops, string text, double x, double y,
        double sizePt, double rotationDeg = 0)
    {
        double scale = sizePt / unitsPerEm;
        double radians = rotationDeg * Math.PI / 180.0;
        double cos = Math.Cos(radians);
        double sin = Math.Sin(radians);
        double penX = 0;

        foreach (char ch in text)
        {
            if (!glyphs.TryGetValue(ch, out Glyph? glyph))
            {
                penX += unitsPerEm * 0.5;
                continue;
            }

            double startUnits = penX;
            (double px, double py) Transform(double gx, double gy)
            {
                double lx = (startUnits + gx) * scale;
                double ly = gy * scale;
                return (x + lx * cos - ly * sin, y + lx * sin + ly * cos);
            }

            double curX = 0, curY = 0;
            foreach (PathCommand cmd in glyph.Path)
            {
                switch (cmd.Op)
                {
                    case 'M':
                        {
                            curX = cmd.A; curY = cmd.B;
                            (double px, double py) = Transform(cmd.A, cmd.B);
                            AppendOp(ops, px, py, "m");
                            break;
                        }
                    case 'L':
                        {
                            curX = cmd.A; curY = cmd.B;
                            (double px, double py) = Transform(cmd.A, cmd.B);
                            AppendOp(ops, px, py, "l");
                            break;
                        }
                    case 'Q':
                        {
                            // Quadratic -> cubic: c1 = start + 2/3(ctrl - start),
                            // c2 = end + 2/3(ctrl - end).
                            double c1x = curX + 2.0 / 3.0 * (cmd.A - curX);
                            double c1y = curY + 2.0 / 3.0 * (cmd.B - curY);
                            double c2x = cmd.C + 2.0 / 3.0 * (cmd.A - cmd.C);
                            double c2y = cmd.D + 2.0 / 3.0 * (cmd.B - cmd.D);
                            (double p1x, double p1y) = Transform(c1x, c1y);
                            (double p2x, double p2y) = Transform(c2x, c2y);
                            (double p3x, double p3y) = Transform(cmd.C, cmd.D);
                            ops.Append(Fmt(p1x)).Append(' ').Append(Fmt(p1y)).Append(' ')
                               .Append(Fmt(p2x)).Append(' ').Append(Fmt(p2y)).Append(' ')
                               .Append(Fmt(p3x)).Append(' ').Append(Fmt(p3y)).Append(" c\n");
                            curX = cmd.C; curY = cmd.D;
                            break;
                        }
                    case 'Z':
                        ops.Append("h\n");
                        break;
                }
            }
            penX += glyph.Advance;
        }
        ops.Append("f\n");   // fill, nonzero winding
    }

    private static void AppendOp(StringBuilder ops, double x, double y, string op) =>
        ops.Append(Fmt(x)).Append(' ').Append(Fmt(y)).Append(' ').Append(op).Append('\n');

    /// <summary>Two-decimal, invariant-culture number formatting for PDF ops.</summary>
    internal static string Fmt(double value) =>
        Math.Round(value, 2).ToString("0.##", CultureInfo.InvariantCulture);
}