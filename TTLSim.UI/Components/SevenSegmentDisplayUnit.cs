using System.Drawing;
using System.Drawing.Drawing2D;
using TTLSim.UI.Model;

namespace TTLSim.UI.Components;

/// <summary>
/// Seven-segment LED display unit. Eight functional pins along the bottom
/// edge (a, b, c, d, e, f, g, dp) and a common pin on the top edge. Polarity
/// (common-anode vs common-cathode) comes from the owning Device's
/// DisplayPartDefinition; the same class handles both.
///
/// The bottom-row layout is designed for routing: multiple digits placed
/// side by side can drop their segment connections downward into a shared
/// bus channel feeding the decoder/driver, with common pins going up to a
/// shared rail. Pin numbering is positional (1..9), not tied to any specific
/// physical package.
///
/// Segment layout (classic figure-8):
///
///      aaaa
///     f    b
///     f    b
///      gggg
///     e    c
///     e    c
///      dddd   dp
///
/// Until the simulator drives pin states the display renders in its
/// "all-off" state (faint outline only). The DrawSegment method accepts a
/// lit boolean so wiring it up later is a matter of feeding in actual values.
/// </summary>
public sealed class SevenSegmentDisplayUnit : Unit
{
    public SevenSegmentDisplayUnit(Device device, UnitSpec spec) : base(device, spec)
    {
        // 10 wide x 11 tall: 8 segment pins along the bottom (1..8 within a
        // 10-cell width gives 1-cell left/right margins), com on the right.
        // Stubs extend 1 cell below the body for the segment pins and 1 cell
        // to the right of the body for com; no stub on top or left, so the
        // inner drawn body fills the top-left and extends to col 9, row 10.
        Size = new Size(10, 11);
        BuildPins(spec);
    }

    /// <summary>The display polarity, inferred from the owning Device.</summary>
    public DisplayKind Polarity =>
        Device.Definition is DisplayPartDefinition dp ? dp.Kind : DisplayKind.CommonCathode;

    protected override void BuildPins(UnitSpec spec)
    {
        // Eight segment pins along the bottom edge at x = 1..8, all facing down.
        // Margins of 1 cell on left and right keep pins clear of body corners.
        AddPin(new Pin("a", 1, new Point(1, Size.Height), PinDirection.Down));
        AddPin(new Pin("b", 2, new Point(2, Size.Height), PinDirection.Down));
        AddPin(new Pin("c", 3, new Point(3, Size.Height), PinDirection.Down));
        AddPin(new Pin("d", 4, new Point(4, Size.Height), PinDirection.Down));
        AddPin(new Pin("e", 5, new Point(5, Size.Height), PinDirection.Down));
        AddPin(new Pin("f", 6, new Point(6, Size.Height), PinDirection.Down));
        AddPin(new Pin("g", 7, new Point(7, Size.Height), PinDirection.Down));
        AddPin(new Pin("dp", 8, new Point(8, Size.Height), PinDirection.Down));

        // Common pin on the right edge, vertically centred on the digit area
        // (which is inset slightly from the bottom to leave room for the
        // bottom-pin stub corridor).
        AddPin(new Pin("com", 9, new Point(Size.Width, (Size.Height - 1) / 2),
            PinDirection.Right));
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

    protected override void DrawShape(Graphics g, RenderContext ctx)
    {
        int p = ctx.GridPitch;

        // The outer rectangle (Position..Position+Size) reserves a 1-cell stub
        // row at the bottom (for the eight segment pins) and a 1-cell stub
        // column on the right (for the com pin). No stub on top or left, so
        // the inner body fills those edges.
        int x0 = Position.X * p;
        int y0 = Position.Y * p;
        int w = (Size.Width - 1) * p;
        int h = (Size.Height - 1) * p;

        var bodyColor = Selected ? ctx.SelectedColor : ctx.ForegroundColor;
        using var bodyPen = new Pen(bodyColor, 1.2f);
        using var bodyFill = new SolidBrush(ctx.FillColor);

        // Inner body rectangle.
        g.FillRectangle(bodyFill, x0, y0, w, h);
        g.DrawRectangle(bodyPen, x0, y0, w, h);

        // The figure-8 sits centred inside the body, leaving small horizontal
        // margins to keep the digit clear of the body edges.
        int digitLeft = x0 + (int)(w * 0.30f);
        int digitRight = x0 + (int)(w * 0.70f);
        int digitTop = y0 + (int)(h * 0.12f);
        int digitBot = y0 + (int)(h * 0.78f);
        int digitMidY = (digitTop + digitBot) / 2;

        // Segment thickness as a fraction of the smaller digit dimension.
        int dw = digitRight - digitLeft;
        int dh = digitBot - digitTop;
        int t = System.Math.Max(2, System.Math.Min(dw, dh) / 8);

        var segOff = Color.FromArgb(40, bodyColor);   // faint outline for off state
        var segOn = Color.Red;

        // Resolve current segment states. In edit mode (no provider) everything is off.
        (bool[] Segments, bool Dp)? state = ctx.SegmentProvider?.Invoke(this);
        bool[] segs = state?.Segments ?? new bool[7];
        bool dpLit = state?.Dp ?? false;

        DrawHSegment(g, digitLeft, digitTop, dw, t, lit: segs[0], segOn, segOff);            // a
        DrawVSegment(g, digitRight - t, digitTop, t, (dh / 2), lit: segs[1], segOn, segOff); // b
        DrawVSegment(g, digitRight - t, digitMidY, t, (dh / 2), lit: segs[2], segOn, segOff);// c
        DrawHSegment(g, digitLeft, digitBot - t, dw, t, lit: segs[3], segOn, segOff);        // d
        DrawVSegment(g, digitLeft, digitMidY, t, (dh / 2), lit: segs[4], segOn, segOff);     // e
        DrawVSegment(g, digitLeft, digitTop, t, (dh / 2), lit: segs[5], segOn, segOff);      // f
        DrawHSegment(g, digitLeft, digitMidY - t / 2, dw, t, lit: segs[6], segOn, segOff);   // g

        // Decimal point: a small dot to the bottom-right of the digit.
        int dpR = System.Math.Max(2, t);
        int dpX = digitRight + t;
        int dpY = digitBot - dpR;
        using var dpBrush = new SolidBrush(dpLit ? segOn : segOff);
        g.FillEllipse(dpBrush, dpX, dpY, dpR, dpR);

        // Pin stubs from the body edge to each pin dot, plus dot.
        using var pinPen = new Pen(bodyColor, 1.2f);
        using var pinBrush = new SolidBrush(ctx.PinColor);
        foreach (var pin in Pins)
        {
            int px = (Position.X + pin.LocalPosition.X) * p;
            int py = (Position.Y + pin.LocalPosition.Y) * p;

            int bodyEdgeX = pin.LocalDirection == PinDirection.Left
                ? x0
                : pin.LocalDirection == PinDirection.Right
                    ? x0 + w
                    : px;
            int bodyEdgeY = pin.LocalDirection == PinDirection.Up
                ? y0
                : pin.LocalDirection == PinDirection.Down
                    ? y0 + h
                    : py;

            g.DrawLine(pinPen, bodyEdgeX, bodyEdgeY, px, py);
            g.FillEllipse(pinBrush, px - 2, py - 2, 4, 4);

            DrawPinLabel(g, ctx, pin, x0, y0, w, h);
        }
    }

    private static void DrawHSegment(Graphics g, int x, int y, int length, int thickness,
                                     bool lit, Color onColor, Color offColor)
    {
        // Horizontal segment drawn as a chevron polygon so the ends taper
        // into adjacent vertical segments cleanly.
        int t = thickness;
        Point[] poly =
        {
            new(x + t / 2,        y),
            new(x + length - t / 2, y),
            new(x + length,       y + t / 2),
            new(x + length - t / 2, y + t),
            new(x + t / 2,        y + t),
            new(x,                y + t / 2),
        };

        var brush = new SolidBrush(lit ? onColor : offColor);
        g.FillPolygon(brush, poly);
        brush.Dispose();
    }

    private static void DrawVSegment(Graphics g, int x, int y, int thickness, int length,
                                     bool lit, Color onColor, Color offColor)
    {
        int t = thickness;
        Point[] poly =
        {
            new(x,         y + t / 2),
            new(x + t / 2, y),
            new(x + t,     y + t / 2),
            new(x + t,     y + length - t / 2),
            new(x + t / 2, y + length),
            new(x,         y + length - t / 2),
        };

        var brush = new SolidBrush(lit ? onColor : offColor);
        g.FillPolygon(brush, poly);
        brush.Dispose();
    }

    /// <summary>
    /// Draw a small pin name just inside the body adjacent to each stub.
    /// For top pins ('com') the label sits below the top edge; for bottom
    /// pins (segments) above the bottom edge; left/right pins inside the
    /// adjacent vertical edge.
    /// </summary>
    private static void DrawPinLabel(Graphics g, RenderContext ctx, Pin pin,
                                     int bodyX, int bodyY, int bodyW, int bodyH)
    {
        using var brush = new SolidBrush(ctx.ForegroundColor);
        var size = g.MeasureString(pin.Name, ctx.PinFont);

        int p = ctx.GridPitch;
        int px = (pin.Owner!.Position.X + pin.LocalPosition.X) * p;
        int py = (pin.Owner!.Position.Y + pin.LocalPosition.Y) * p;

        float lx, ly;
        switch (pin.LocalDirection)
        {
            case PinDirection.Down:
                // Pin on bottom edge; label sits inside the body just above it.
                lx = px - size.Width / 2;
                ly = bodyY + bodyH - size.Height - 1;
                break;
            case PinDirection.Up:
                // Pin on top edge; label sits inside the body just below it.
                lx = px - size.Width / 2;
                ly = bodyY + 1;
                break;
            case PinDirection.Left:
                lx = bodyX + 2;
                ly = py - size.Height / 2;
                break;
            default:  // Right
                lx = bodyX + bodyW - size.Width - 2;
                ly = py - size.Height / 2;
                break;
        }
        g.DrawString(pin.Name, ctx.PinFont, brush, lx, ly);
    }

    protected override void DrawLabels(Graphics g, RenderContext ctx)
    {
        int p = ctx.GridPitch;
        var b = Bounds;
        // Designator centres horizontally over the body, which fills the
        // top-left of the bounding box (no top inset).
        float midX = (b.X + (b.Width - 1) / 2f) * p;
        float bodyTopY = b.Y * p;
        DrawDesignatorAndValue(g, ctx, midX, bodyTopY);
    }
}