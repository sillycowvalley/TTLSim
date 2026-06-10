using System.ComponentModel;
using System.Drawing;
using TTLSim.UI.Model;

namespace TTLSim.UI.Components;

/// <summary>
/// SPST latching switch. Two pins (1, 2) tied together when closed, open when
/// open. IsClosed is persistent state -- it serializes with the schematic and
/// is restored on load. In sim mode a left-click on the symbol toggles it.
/// </summary>
public sealed class SwitchUnit : Unit
{
    [Category("State")]
    [Description("True when the switch is closed (conducting). Open-circuit when false.")]
    public bool IsClosed { get; set; }

    public SwitchUnit(Device device, UnitSpec spec) : base(device, spec)
    {
        Size = new Size(6, 2);
        BuildPins(spec);
    }

    public override Rectangle RoutingBounds
    {
        get
        {
            // Pad around the symbol so wires route clear of the lever, which
            // swings up above the body when the switch is open.
            const int pad = 2;

            var unrotated = new Rectangle(
                Position.X, Position.Y - pad, Size.Width, Size.Height + 2 * pad);

            if (Rotation == Rotation.R0 || Rotation == Rotation.R180)
                return unrotated;

            int cx = Position.X + Size.Width / 2;
            int cy = Position.Y + Size.Height / 2;
            int w = unrotated.Height;
            int h = unrotated.Width;
            return new Rectangle(cx - w / 2, cy - h / 2, w, h);
        }
    }

    /// <summary>
    /// Clickable extent for sim-mode interaction: the Size box plus one cell on
    /// top for the lever's open-state overshoot. Rotation-aware, mirroring the
    /// Bounds rotation logic.
    /// </summary>
    public Rectangle InteractiveBounds
    {
        get
        {
            // One extra cell above the body; nothing added on the other sides.
            var unrotated = new Rectangle(
                Position.X, Position.Y - 1, Size.Width, Size.Height + 1);

            if (Rotation == Rotation.R0 || Rotation == Rotation.R180)
                return unrotated;

            int cx = Position.X + Size.Width / 2;
            int cy = Position.Y + Size.Height / 2;
            int w = unrotated.Height;
            int h = unrotated.Width;
            return new Rectangle(cx - w / 2, cy - h / 2, w, h);
        }
    }

    protected override void BuildPins(UnitSpec spec)
    {
        AddPin(new Pin("1", 1, new Point(0, 1), PinDirection.Left));
        AddPin(new Pin("2", 2, new Point(Size.Width, 1), PinDirection.Right));
    }

    protected override void DrawShape(Graphics g, RenderContext ctx)
    {
        int p = ctx.GridPitch;
        int leftX = (Position.X + 1) * p;
        int rightX = (Position.X + 5) * p;
        int midY = (Position.Y + 1) * p;
        int lift = (int)(p * 0.9f);   // how far the open lever swings up

        // Leads from pin endpoints to the contacts.
        using var leadPen = new Pen(Selected ? ctx.SelectedColor : ctx.ForegroundColor, 1.2f);
        int p1x = (Position.X + Pins[0].LocalPosition.X) * p;
        int p1y = (Position.Y + Pins[0].LocalPosition.Y) * p;
        int p2x = (Position.X + Pins[1].LocalPosition.X) * p;
        int p2y = (Position.Y + Pins[1].LocalPosition.Y) * p;
        g.DrawLine(leadPen, p1x, p1y, leftX, midY);
        g.DrawLine(leadPen, rightX, midY, p2x, p2y);

        using var pen = new Pen(Selected ? ctx.SelectedColor : ctx.ForegroundColor, 1.2f);
        using var fill = new SolidBrush(IsClosed ? ctx.SelectedColor : ctx.FillColor);

        // Two contact terminals.
        g.FillEllipse(fill, leftX - 3, midY - 3, 6, 6);
        g.DrawEllipse(pen, leftX - 3, midY - 3, 6, 6);
        g.FillEllipse(fill, rightX - 3, midY - 3, 6, 6);
        g.DrawEllipse(pen, rightX - 3, midY - 3, 6, 6);

        // Pivoting lever -- hinged at the left contact. Closed: lies flat,
        // bridging both contacts. Open: far end lifts up AND stops short of
        // the right contact, so there's a clear gap.
        Point hinge = new(leftX, midY);
        Point tip = IsClosed
            ? new Point(rightX, midY)
            : new Point(rightX - (int)(p * 0.7f), midY - lift);
        g.DrawLine(pen, hinge, tip);

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

        float leverReach = p * 0.9f;   // matches `lift` in DrawShape

        float bodyTopY = b.Y * p - leverReach;
        float bodyBottomY = (b.Y + b.Height) * p + leverReach;
        float bodyLeftX = b.X * p - leverReach;
        DrawPassiveLabels(g, ctx, midX, midY, bodyTopY, bodyBottomY, bodyLeftX);
    }
}