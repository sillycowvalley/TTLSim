using System.Drawing;
using TTLSim.UI.Model;

namespace TTLSim.UI.Components;

/// <summary>
/// Diode -- triangle body + cathode bar, 6 cells wide x 2 tall, two pins.
/// Pin 1 is the anode (left), pin 2 is the cathode (right). Default value
/// "1N5819" (Schottky); the user can edit it to "1N4148", "1N4007", or any
/// other part number through the property grid.
///
/// For simulation purposes the model lives in TTLSim.Chips.Passives.DiodeContact
/// and treats every diode as an idealised one-way pass: when the anode net
/// resolves HIGH, a weak HIGH is driven onto the cathode net; the diode never
/// drives the anode side. That's enough fidelity for the digital roles a
/// diode plays in TTL circuits -- wired-OR steering, pull-up boost, signal
/// gating -- without trying to be SPICE.
/// </summary>
public sealed class DiodeUnit : Unit
{
    private const string DefaultPartNumber = "1N5819";

    public DiodeUnit(Device device, UnitSpec spec) : base(device, spec)
    {
        Size = new Size(6, 2);
        BuildPins(spec);

        // First-time creation: stamp the default part number so the user has
        // something sensible visible until they pick their own.
        if (string.IsNullOrEmpty(device.Value))
            device.Value = DefaultPartNumber;
    }

    protected override void BuildPins(UnitSpec spec)
    {
        AddPin(new Pin("A", 1, new Point(0, 1), PinDirection.Left));        // anode
        AddPin(new Pin("K", 2, new Point(Size.Width, 1), PinDirection.Right)); // cathode
    }

    public override Rectangle RoutingBounds
    {
        get
        {
            // Same inflate as ResistorUnit -- the body sits centred at y=1
            // and the symbol's tallest features (triangle apex, cathode bar)
            // extend ~1 cell above and below the lead line.
            var unrotated = new Rectangle(
                Position.X, Position.Y - 1, Size.Width, Size.Height + 2);

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
        int midY = (Position.Y + 1) * p;

        // Body occupies the middle four cells; one cell of lead at each end.
        int triLeftX = (Position.X + 1) * p;
        int triRightX = (Position.X + 4) * p;
        int barX = triRightX;                  // cathode bar sits flush with triangle apex
        int half = (int)(p * 0.9f);

        using var leadPen = new Pen(Selected ? ctx.SelectedColor : ctx.ForegroundColor, 1.2f);
        int p1x = (Position.X + Pins[0].LocalPosition.X) * p;
        int p1y = (Position.Y + Pins[0].LocalPosition.Y) * p;
        int p2x = (Position.X + Pins[1].LocalPosition.X) * p;
        int p2y = (Position.Y + Pins[1].LocalPosition.Y) * p;
        g.DrawLine(leadPen, p1x, p1y, triLeftX, midY);
        g.DrawLine(leadPen, barX, midY, p2x, p2y);

        using var pen = new Pen(Selected ? ctx.SelectedColor : ctx.ForegroundColor, 1.2f);

        // Filled triangle pointing right (anode -> cathode direction).
        Point[] tri =
        {
            new(triLeftX, midY - half),
            new(triLeftX, midY + half),
            new(triRightX, midY)
        };
        using var fill = new SolidBrush(Selected ? ctx.SelectedColor : ctx.ForegroundColor);
        g.FillPolygon(fill, tri);
        g.DrawPolygon(pen, tri);

        // Cathode bar at the triangle's apex.
        g.DrawLine(pen, barX, midY - half, barX, midY + half);

        using var pinBrush = new SolidBrush(ctx.PinColor);
        g.FillEllipse(pinBrush, p1x - 2, p1y - 2, 4, 4);
        g.FillEllipse(pinBrush, p2x - 2, p2y - 2, 4, 4);
    }

    protected override void DrawLabels(Graphics g, RenderContext ctx)
    {
        int p = ctx.GridPitch;
        var b = Bounds;
        float midX = (b.X + b.Width / 2f) * p;
        float midY = (b.Y + b.Height / 2f) * p;
        float bodyTopY = b.Y * p;
        float bodyBottomY = (b.Y + b.Height) * p;
        float bodyLeftX = b.X * p;
        DrawPassiveLabels(g, ctx, midX, midY, bodyTopY, bodyBottomY, bodyLeftX);
    }
}