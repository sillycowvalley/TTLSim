using System;
using System.ComponentModel;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Globalization;
using TTLSim.UI.Model;

namespace TTLSim.UI.Components;

/// <summary>
/// Abstract clock signal source -- a rail-style standalone symbol like VCC/GND
/// but with a single output pin that emits a square wave at FrequencyHz. Used
/// as the canonical clock primitive in simulation; no analog timing network
/// required.
///
/// Visually: a rectangle with a square-wave glyph inside, pin on the right.
/// The formatted frequency ("1 MHz") is rendered above the body.
/// </summary>
public sealed class ClockSource : SchematicItem
{
    /// <summary>Frequency in hertz. Accepts free-form units on edit (e.g. "1MHz", "10k", "1e6").</summary>
    [Category("Signal")]
    [TypeConverter(typeof(FrequencyConverter))]
    [Description("Output frequency. Accepts units like Hz, kHz, MHz.")]
    public double FrequencyHz { get; set; } = 1_000_000.0;

    /// <summary>Fraction of each period the output is high. 0..1.</summary>
    [Category("Signal")]
    [Description("High fraction of each period (0..1). 0.5 = symmetric square wave.")]
    public double DutyCycle { get; set; } = 0.5;

    /// <summary>If true, the output starts high at tick 0.</summary>
    [Category("Signal")]
    [Description("If true, the first half-cycle is high; otherwise it starts low.")]
    public bool StartHigh { get; set; } = false;

    public ClockSource()
    {
        Size = new Size(8, 4);
        // Pin on the right edge, mid-height. The pin's Name ("CLK") is
        // rendered inside the body by DrawShape, so no separate Label is
        // needed -- unlike VCC/GND which use Label as their visible text.
        AddPin(new Pin("CLK", 0, new Point(8, 2), PinDirection.Right));
    }

    public override Rectangle RoutingBounds
    {
        get
        {
            var unrotated = new Rectangle(
                Position.X - 1, Position.Y - 1,
                Size.Width + 2, Size.Height + 2);

            if (Rotation == Rotation.R0 || Rotation == Rotation.R180)
                return unrotated;

            int cx = Position.X + Size.Width / 2;
            int cy = Position.Y + Size.Height / 2;
            int w = unrotated.Height;
            int h = unrotated.Width;
            return new Rectangle(cx - w / 2, cy - h / 2, w, h);
        }
    }

    public override void Draw(Graphics g, RenderContext ctx)
    {
        var state = g.Save();
        ApplyRotationTransform(g, ctx);
        DrawShape(g, ctx);
        g.Restore(state);

        DrawLabels(g, ctx);
    }

    private void DrawShape(Graphics g, RenderContext ctx)
    {
        int p = ctx.GridPitch;
        int x = Position.X * p;
        int y = Position.Y * p;
        int w = Size.Width * p;
        int h = Size.Height * p;

        // Reserve the rightmost cell for the pin stub; the body fills the rest.
        int stubCells = 1;
        int bodyW = w - stubCells * p;

        var color = Selected ? ctx.SelectedColor : ctx.ForegroundColor;
        using var pen = new Pen(color, 1.2f);
        using var glyphPen = new Pen(color, 1.0f);
        using var fillBrush = new SolidBrush(ctx.FillColor);
        using var pinBrush = new SolidBrush(color);

        // Body rectangle (excluding the pin stub area).
        g.FillRectangle(fillBrush, x, y, bodyW, h);
        g.DrawRectangle(pen, x, y, bodyW, h);

        // Square-wave glyph centred horizontally inside the body, in the upper
        // 2/3 of its height. Two full cycles. Pattern from left to right:
        // low, high, low, high, low.
        int glyphMargin = p;
        int gx0 = x + glyphMargin;
        int gx1 = x + bodyW - glyphMargin;
        int gw = gx1 - gx0;
        int gtop = y + (int)(h * 0.25f);
        int gbot = y + (int)(h * 0.70f);

        // Five vertical segments at 0, 1/4, 2/4, 3/4, 4/4 of glyph width.
        int s0 = gx0;
        int s1 = gx0 + gw / 4;
        int s2 = gx0 + gw / 2;
        int s3 = gx0 + 3 * gw / 4;
        int s4 = gx1;

        var prev = g.SmoothingMode;
        g.SmoothingMode = SmoothingMode.None;

        // low, rise, high, fall, low, rise, high, fall, low
        g.DrawLine(glyphPen, s0, gbot, s1, gbot);
        g.DrawLine(glyphPen, s1, gbot, s1, gtop);
        g.DrawLine(glyphPen, s1, gtop, s2, gtop);
        g.DrawLine(glyphPen, s2, gtop, s2, gbot);
        g.DrawLine(glyphPen, s2, gbot, s3, gbot);
        g.DrawLine(glyphPen, s3, gbot, s3, gtop);
        g.DrawLine(glyphPen, s3, gtop, s4, gtop);

        g.SmoothingMode = prev;

        // Pin stub from the right edge of the body to the pin dot, matching
        // the VCC/GND visual convention.
        int stubY = y + (Size.Height / 2) * p;
        int stubX0 = x + bodyW;
        int stubX1 = x + w;
        g.DrawLine(pen, stubX0, stubY, stubX1, stubY);
        g.FillEllipse(pinBrush, stubX1 - 2, stubY - 2, 4, 4);

        // Pin name inside the body, hard against the body's inner edge on
        // the pin's side. Drawn here (inside DrawShape) so the rotation
        // transform applied by Draw() carries the text with the body, the
        // same way ChipUnit does it. Anchoring uses pin.LocalPosition and
        // pin.LocalDirection so the position is correct at R0/R90/R180/R270.
        using var textBrush = new SolidBrush(ctx.ForegroundColor);
        var tightFormat = StringFormat.GenericTypographic;
        const float nameInset = 0.2f;
        foreach (var pin_ in Pins)
        {
            if (string.IsNullOrEmpty(pin_.Name)) continue;

            int py = (Position.Y + pin_.LocalPosition.Y) * p;
            int innerX = pin_.LocalDirection == PinDirection.Left ? x + p : x + bodyW;

            var nameSize = g.MeasureString(pin_.Name, ctx.PinFont, int.MaxValue, tightFormat);
            float nameX = pin_.LocalDirection == PinDirection.Left
                ? innerX + nameInset * p
                : innerX - nameInset * p - nameSize.Width;
            float nameY = py - nameSize.Height / 2;
            g.DrawString(pin_.Name, ctx.PinFont, textBrush, nameX, nameY, tightFormat);
        }
    }

    private void DrawLabels(Graphics g, RenderContext ctx)
    {
        int p = ctx.GridPitch;
        var b = Bounds;
        using var brush = new SolidBrush(ctx.ForegroundColor);

        string freq = FormatFrequency(FrequencyHz);
        var freqSize = g.MeasureString(freq, ctx.LabelFont);

        // R0/R180: body is horizontal, pin enters/exits from the side, so
        // the frequency label sits centred ABOVE the body without colliding
        // with the wire.
        //
        // R90/R270: body is vertical, pin/wire is on top or bottom. Move
        // the label LEFT of the body, vertically centred, to keep it clear
        // of the wire.
        float fx, fy;
        if (Rotation == Rotation.R0 || Rotation == Rotation.R180)
        {
            fx = b.X * p + (b.Width * p - freqSize.Width) / 2f;
            fy = b.Y * p - freqSize.Height - ctx.BodyGap;
        }
        else
        {
            fx = b.X * p - freqSize.Width - ctx.BodyGap;
            fy = b.Y * p + (b.Height * p - freqSize.Height) / 2f;
        }
        g.DrawString(freq, ctx.LabelFont, brush, fx, fy);
    }

    private void ApplyRotationTransform(Graphics g, RenderContext ctx)
    {
        if (Rotation == Rotation.R0) return;
        int p = ctx.GridPitch;
        float pivotX = Pivot.X * p;
        float pivotY = Pivot.Y * p;
        g.TranslateTransform(pivotX, pivotY);
        g.RotateTransform((float)(int)Rotation);
        g.TranslateTransform(-pivotX, -pivotY);
    }

    // ----------------------------------------------------- frequency formatting

    /// <summary>
    /// Format a frequency in hertz to a short SI-prefixed string:
    /// "10 Hz", "1 kHz", "3.579545 MHz", "1.5 GHz". Strips trailing zeros.
    /// </summary>
    public static string FormatFrequency(double hz)
    {
        if (hz <= 0) return "0 Hz";

        double value;
        string unit;
        if (hz >= 1e9) { value = hz / 1e9; unit = "GHz"; }
        else if (hz >= 1e6) { value = hz / 1e6; unit = "MHz"; }
        else if (hz >= 1e3) { value = hz / 1e3; unit = "kHz"; }
        else { value = hz; unit = "Hz"; }

        // Up to 6 decimal places, trimmed.
        string num = value.ToString("0.######", CultureInfo.InvariantCulture);
        return $"{num} {unit}";
    }

    /// <summary>
    /// Parse a free-form frequency string back to hertz. Accepts:
    ///   "1000", "1e6", "1 MHz", "1MHz", "10kHz", "10k", "3.579545 MHz".
    /// Returns null on failure.
    /// </summary>
    public static double? ParseFrequency(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        text = text.Trim();

        // Strip a trailing "Hz" (case-insensitive) if present.
        if (text.EndsWith("Hz", StringComparison.OrdinalIgnoreCase))
            text = text[..^2].TrimEnd();

        // Identify SI prefix on the last character if present.
        double scale = 1.0;
        if (text.Length > 0)
        {
            char suffix = text[^1];
            switch (suffix)
            {
                case 'k': case 'K': scale = 1e3; text = text[..^1].TrimEnd(); break;
                case 'M': scale = 1e6; text = text[..^1].TrimEnd(); break;
                case 'G': case 'g': scale = 1e9; text = text[..^1].TrimEnd(); break;
                case 'm': scale = 1e-3; text = text[..^1].TrimEnd(); break;
            }
        }

        if (!double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var value))
            return null;
        return value * scale;
    }
}
