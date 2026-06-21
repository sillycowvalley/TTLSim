using System;
using System.Drawing;
using TTLSim.UI.Model;

namespace TTLSim.UI.Components;

/// <summary>
/// Bussed resistor network in a SIP-9 package (e.g. Bourns 4609X-101): nine
/// pins down the left edge. Pin 1 is the common bus; pins 2..9 each connect to
/// that bus through one resistor, so eight resistors share a single common
/// terminal.
///
/// Drawn as a common bus rail with eight zigzag elements branching to it -- no
/// package outline. Pin 1 runs straight to the bus; each pin carries a terminal
/// dot at its outer endpoint. The bus sits one cell from the outer edge; the
/// designator and value are drawn in that cell -- on the outer side of the bus,
/// away from the pins -- centred along the rail and rotation-aware so they stay
/// on the bus-outer side under rotation. Electrically each element is modelled
/// exactly like a single resistor (see ChipFactory.CreateForUnits): a
/// pull-up/pull-down when the common pin is on a power rail, a transparent
/// series contact otherwise.
/// </summary>
public sealed class ResistorNetworkUnit : Unit
{
    /// <summary>Resistor count (pins 2..9); pin 1 is the shared common.</summary>
    private const int ResistorCount = 8;

    /// <summary>Total pin count including the common.</summary>
    private const int PinCount = ResistorCount + 1;

    /// <summary>One cell of stub on the pin side, so each pin's LocalPosition
    /// sits at the outer edge of the bounding box (matching HeaderOutputUnit
    /// and the gate units).</summary>
    private const int StubCells = 1;

    public ResistorNetworkUnit(Device device, UnitSpec spec) : base(device, spec)
    {
        // Width: pin stub, the zigzag span, the bus rail, and one cell beyond
        // the bus for the designator/label. Height: pins on rows 1..9 with one
        // cell of margin each end; pinCount + 1 = 10 is even so the rotation
        // pivot lands on an integer cell.
        Size = new Size(6, PinCount + 1);
        BuildPins(spec);
    }

    protected override void BuildPins(UnitSpec spec)
    {
        // All nine pins on the LEFT edge, rows 1..9, pin 1 (common) at the top.
        for (int pin = 1; pin <= PinCount; pin++)
            AddPin(new Pin(pin.ToString(), pin, new Point(0, pin), PinDirection.Left));
    }

    /// <summary>Column (cells from the unit's left edge) of the common bus rail:
    /// one cell in from the outer edge, leaving that last cell for the label.</summary>
    private int BusColumn => Size.Width - 1;

    public override Rectangle RoutingBounds
    {
        get
        {
            float pivotX = Position.X + Size.Width / 2f;
            float pivotY = Position.Y + Size.Height / 2f;

            // Slack on the pin side and the two rail ends for wire turn-in, but
            // TIGHT on the bus-outer side so only the single label cell sits
            // beyond the rail. Rotating the corners about the pivot keeps the
            // tight side aligned with the bus under rotation.
            float l = Position.X - 1;                       // pin side (slack)
            float r = Position.X + Size.Width;              // bus-outer side (tight)
            float t = Position.Y - 1;                       // rail end (slack)
            float b = Position.Y + Size.Height + 1;         // rail end (slack)

            float minX = float.MaxValue, minY = float.MaxValue;
            float maxX = float.MinValue, maxY = float.MinValue;
            foreach (var (cx, cy) in new[] { (l, t), (r, t), (r, b), (l, b) })
            {
                var (rx, ry) = RotateOffset(Rotation, cx - pivotX, cy - pivotY);
                float wx = pivotX + rx, wy = pivotY + ry;
                minX = Math.Min(minX, wx); maxX = Math.Max(maxX, wx);
                minY = Math.Min(minY, wy); maxY = Math.Max(maxY, wy);
            }

            return new Rectangle(
                (int)Math.Round(minX), (int)Math.Round(minY),
                (int)Math.Round(maxX - minX), (int)Math.Round(maxY - minY));
        }
    }

    protected override void DrawShape(Graphics g, RenderContext ctx)
    {
        int p = ctx.GridPitch;
        var color = Selected ? ctx.SelectedColor : ctx.ForegroundColor;

        using var pen = new Pen(color, 1.2f);
        using var pinBrush = new SolidBrush(ctx.PinColor);

        int leftX = Position.X * p;                  // outer pin-endpoint column
        int resistorStartX = leftX + StubCells * p;  // pin stub meets the resistor
        int busX = leftX + BusColumn * p;            // common bus rail

        int firstRowY = (Position.Y + 1) * p;
        int lastRowY = (Position.Y + PinCount) * p;

        // Common bus rail.
        g.DrawLine(pen, busX, firstRowY, busX, lastRowY);

        int amp = (int)(p * 0.35f);

        for (int pin = 1; pin <= PinCount; pin++)
        {
            int rowY = (Position.Y + pin) * p;

            // Pin stub from the outer endpoint to where the resistor begins.
            g.DrawLine(pen, leftX, rowY, resistorStartX, rowY);

            if (pin == 1)
                g.DrawLine(pen, resistorStartX, rowY, busX, rowY);   // common: straight to the bus
            else
                DrawZigzag(g, pen, resistorStartX, busX, rowY, amp); // element: resistor to the bus

            // Connection terminal dot at the outer end of the pin.
            g.FillEllipse(pinBrush, leftX - 2, rowY - 2, 4, 4);
        }
    }

    /// <summary>Horizontal resistor zigzag from x0 to x1 centred on yMid.</summary>
    private static void DrawZigzag(Graphics g, Pen pen, int x0, int x1, int yMid, int amp)
    {
        const int segments = 6;
        float dx = (x1 - x0) / (float)segments;

        var pts = new PointF[segments + 2];
        pts[0] = new PointF(x0, yMid);
        for (int i = 0; i < segments; i++)
        {
            float x = x0 + dx * (i + 0.5f);
            float y = yMid + ((i % 2 == 0) ? -amp : amp);
            pts[i + 1] = new PointF(x, y);
        }
        pts[segments + 1] = new PointF(x1, yMid);

        g.DrawLines(pen, pts);
    }

    protected override void DrawLabels(Graphics g, RenderContext ctx)
    {
        int p = ctx.GridPitch;
        using var brush = new SolidBrush(ctx.ForegroundColor);

        var desigSize = g.MeasureString(DisplayDesignator, ctx.LabelFont);
        bool hasLabel = !string.IsNullOrEmpty(Label);
        SizeF labelSize = hasLabel ? g.MeasureString(Label, ctx.PinFont) : SizeF.Empty;

        float lineGap = hasLabel ? ctx.LineGap : 0f;
        float gap = ctx.BodyGap;
        float blockH = desigSize.Height + (hasLabel ? lineGap + labelSize.Height : 0f);

        // Anchor at the bus, centred along the rail; the block sits just beyond
        // the bus on its outer side (the pin -> bus direction), in the one cell
        // of label space. The anchor and outward direction are rotated so the
        // labels follow the bus-outer side: right at R0, below at R90, left at
        // R180, above at R270.
        float pivotX = Position.X + Size.Width / 2f;
        float pivotY = Position.Y + Size.Height / 2f;
        float busOffsetX = BusColumn - Size.Width / 2f;     // bus column relative to the pivot

        var (ax, ay) = RotateOffset(Rotation, busOffsetX, 0f);
        float anchorX = (pivotX + ax) * p;
        float anchorY = (pivotY + ay) * p;

        var (ox, oy) = RotateOffset(Rotation, 1f, 0f);      // outward = pin -> bus

        if (Math.Abs(oy) > 0.5f)
        {
            // Bus is horizontal: stack the block above (R270) or below (R90),
            // centred on the anchor X.
            float top = oy < 0f ? anchorY - gap - blockH : anchorY + gap;
            g.DrawString(DisplayDesignator, ctx.LabelFont, brush,
                anchorX - desigSize.Width / 2f, top);
            if (hasLabel)
                g.DrawString(Label, ctx.PinFont, brush,
                    anchorX - labelSize.Width / 2f, top + desigSize.Height + lineGap);
        }
        else
        {
            // Bus is vertical: place the block beside it, centred on the anchor
            // Y -- to the right (R0) or to the left (R180).
            float top = anchorY - blockH / 2f;
            if (ox > 0f)
            {
                float x = anchorX + gap;
                g.DrawString(DisplayDesignator, ctx.LabelFont, brush, x, top);
                if (hasLabel)
                    g.DrawString(Label, ctx.PinFont, brush, x, top + desigSize.Height + lineGap);
            }
            else
            {
                float right = anchorX - gap;
                g.DrawString(DisplayDesignator, ctx.LabelFont, brush,
                    right - desigSize.Width, top);
                if (hasLabel)
                    g.DrawString(Label, ctx.PinFont, brush,
                        right - labelSize.Width, top + desigSize.Height + lineGap);
            }
        }
    }

    /// <summary>Rotate a pivot-relative (dx, dy) offset by the unit's rotation,
    /// clockwise, matching Unit.Draw's transform.</summary>
    private static (float X, float Y) RotateOffset(Rotation rot, float dx, float dy) => rot switch
    {
        Rotation.R90 => (-dy, dx),
        Rotation.R180 => (-dx, -dy),
        Rotation.R270 => (dy, -dx),
        _ => (dx, dy),
    };
}