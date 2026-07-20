using System;
using System.Drawing;
using TTLSim.UI.Model;

namespace TTLSim.UI.Components;

/// <summary>
/// Isolated resistor network in a DIP-16 package (e.g. Bourns 4116R-2):
/// eight independent resistors, element n between pin n and pin 17-n
/// (1-16, 2-15, ... 8-9). The pairing follows the physical DIP mirror, so
/// the two ends of every element sit directly opposite each other and each
/// element draws as one horizontal zigzag straight across the body.
///
/// Drawn as a DIP body outline (ChipUnit's convention: one cell of pin stub
/// each side, 2-cell pin pitch) with eight zigzag elements spanning it;
/// pins 1..8 run down the left edge, 9..16 up the right. The 2-cell pitch
/// and 10-cell pin span match the exported symbol's 20px pitch / +/-50 pin
/// tips, so exported wires land exactly on the pins. A pin-1 dot marks
/// orientation; the designator and value sit beyond the top edge,
/// rotation-aware so they stay clear of the pin columns. Electrically each
/// element is modelled exactly like a single resistor (see
/// ChipFactory.CreateIsolatedResistorNetwork): a pull-up/pull-down when one
/// end is on a power rail, a transparent series contact otherwise.
/// </summary>
public sealed class ResistorNetworkDip16Unit : Unit
{
    /// <summary>Independent resistor elements; pin count is twice this.</summary>
    private const int ResistorCount = 8;

    private const int PinCount = ResistorCount * 2;

    /// <summary>Vertical grid-cell pitch between adjacent pins (ChipUnit's
    /// DIP pitch, and 20px in the exported symbol).</summary>
    private const int PinPitch = 2;

    /// <summary>Top and bottom margin on the body, in grid cells.</summary>
    private const int VerticalMargin = 1;

    /// <summary>One cell of stub each side, so pin endpoints (LocalPosition)
    /// sit on the bounding-box edge, matching ChipUnit and the passives.</summary>
    private const int StubCells = 1;

    public ResistorNetworkDip16Unit(Device device, UnitSpec spec) : base(device, spec)
    {
        // Width 10 puts the two pin columns 10 cells (100 px) apart -- the
        // exported symbol's +/-50 pin tips -- so pin N's world position equals
        // its EasyEDA position and no diagonal wires appear. Height matches
        // ChipUnit's 16-pin body: 1-cell margins + 7 x 2-cell pitch = 16.
        Size = new Size(2 * StubCells + 8,
            VerticalMargin * 2 + (ResistorCount - 1) * PinPitch);
        BuildPins(spec);
    }

    protected override void BuildPins(UnitSpec spec)
    {
        // DIP mirror: pins 1..8 down the left edge, 9..16 up the right, so
        // pin n and pin 17-n share a row -- one row per resistor element.
        for (int pin = 1; pin <= PinCount; pin++)
        {
            bool left = pin <= ResistorCount;
            int row = left ? pin - 1 : PinCount - pin;
            int y = VerticalMargin + row * PinPitch;
            int x = left ? 0 : Size.Width;
            AddPin(new Pin(pin.ToString(), pin, new Point(x, y),
                left ? PinDirection.Left : PinDirection.Right));
        }
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
        var color = Selected ? ctx.SelectedColor : ctx.ForegroundColor;

        using var pen = new Pen(color, 1.2f);
        using var pinBrush = new SolidBrush(ctx.PinColor);

        int leftX = Position.X * p;                          // left pin endpoints
        int rightX = (Position.X + Size.Width) * p;          // right pin endpoints
        int bodyLeftX = leftX + StubCells * p;               // body edge = stub end
        int bodyRightX = rightX - StubCells * p;

        // DIP body outline.
        g.DrawRectangle(pen,
            bodyLeftX, Position.Y * p,
            bodyRightX - bodyLeftX, Size.Height * p);

        // Pin-1 orientation dot just inside the body's top-left corner.
        int dotR = Math.Max(2, p / 4);
        g.DrawEllipse(pen,
            bodyLeftX + p / 2 - dotR, Position.Y * p + p / 2 - dotR,
            dotR * 2, dotR * 2);

        int amp = (int)(p * 0.35f);

        for (int element = 1; element <= ResistorCount; element++)
        {
            int rowY = (Position.Y + VerticalMargin + (element - 1) * PinPitch) * p;

            // Pin stubs each side, then the element zigzag across the body.
            g.DrawLine(pen, leftX, rowY, bodyLeftX, rowY);
            g.DrawLine(pen, bodyRightX, rowY, rightX, rowY);
            DrawZigzag(g, pen, bodyLeftX, bodyRightX, rowY, amp);

            // Connection terminal dots at both outer pin endpoints.
            g.FillEllipse(pinBrush, leftX - 2, rowY - 2, 4, 4);
            g.FillEllipse(pinBrush, rightX - 2, rowY - 2, 4, 4);
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

        // Anchor at the middle of the body's TOP edge; the block stacks
        // outward (away from the body) so it clears both pin columns. The
        // anchor and outward direction rotate with the unit, so the labels
        // follow the pin-1 end: above at R0, right at R90, below at R180,
        // left at R270 -- always off the pin rows.
        float pivotX = Position.X + Size.Width / 2f;
        float pivotY = Position.Y + Size.Height / 2f;

        var (ax, ay) = RotateOffset(Rotation, 0f, -Size.Height / 2f);
        float anchorX = (pivotX + ax) * p;
        float anchorY = (pivotY + ay) * p;

        var (ox, oy) = RotateOffset(Rotation, 0f, -1f);   // outward = away from body

        if (Math.Abs(oy) > 0.5f)
        {
            // Top edge is horizontal: stack the block above (R0) or below
            // (R180), centred on the anchor X.
            float top = oy < 0f ? anchorY - gap - blockH : anchorY + gap;
            g.DrawString(DisplayDesignator, ctx.LabelFont, brush,
                anchorX - desigSize.Width / 2f, top);
            if (hasLabel)
                g.DrawString(Label, ctx.PinFont, brush,
                    anchorX - labelSize.Width / 2f, top + desigSize.Height + lineGap);
        }
        else
        {
            // Top edge is vertical: place the block beside it, centred on the
            // anchor Y -- to the right (R90) or to the left (R270).
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
