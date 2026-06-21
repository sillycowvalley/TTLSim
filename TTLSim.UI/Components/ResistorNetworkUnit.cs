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
/// dot at its outer endpoint. Electrically each element is modelled exactly like
/// a single resistor (see ChipFactory.CreateForUnits): a pull-up/pull-down when
/// the common pin is on a power rail, a transparent series contact otherwise.
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

    /// <summary>Span in grid cells from the pin stub to the bus rail, plus a
    /// trailing cell so the bounding box is symmetric and the rotation pivot
    /// lands on the body centre.</summary>
    private const int BodyWidth = 4;
    private const int FarSpacerCells = 1;

    public ResistorNetworkUnit(Device device, UnitSpec spec) : base(device, spec)
    {
        // Pins sit at rows 1..9. Box height pinCount + 1 = 10 is already even,
        // so the rotation pivot (Height / 2) lands on an integer cell with one
        // padding cell above pin 1 and one below pin 9.
        Size = new Size(StubCells + BodyWidth + FarSpacerCells, PinCount + 1);
        BuildPins(spec);
    }

    protected override void BuildPins(UnitSpec spec)
    {
        // All nine pins on the LEFT edge, rows 1..9, pin 1 (common) at the top.
        for (int pin = 1; pin <= PinCount; pin++)
            AddPin(new Pin(pin.ToString(), pin, new Point(0, pin), PinDirection.Left));
    }

    public override Rectangle RoutingBounds
    {
        get
        {
            // One cell of slack all round so wires have room to turn into the
            // left-edge pins -- same approach as HeaderOutputUnit.
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

        int leftX = Position.X * p;                          // outer pin-endpoint column
        int resistorStartX = leftX + StubCells * p;          // pin stub meets the resistor
        int busX = leftX + (StubCells + BodyWidth - 1) * p;  // common bus rail

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
        var b = Bounds;
        // Designator + value sit above the symbol, centred horizontally --
        // same placement the SevenSegment and other tall passives use.
        float midX = (b.X + b.Width / 2f) * p;
        float bodyTopY = b.Y * p;
        DrawDesignatorAndValue(g, ctx, midX, bodyTopY);
    }
}