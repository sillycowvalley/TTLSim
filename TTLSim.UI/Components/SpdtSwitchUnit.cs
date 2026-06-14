using System.ComponentModel;
using System.Drawing;
using TTLSim.UI.Model;

namespace TTLSim.UI.Components;

/// <summary>
/// SPDT (single-pole double-throw) latching switch. The common terminal (pin 2,
/// left) connects to one of two throws (pin 1 = throw A, top-right; pin 3 =
/// throw B, bottom-right). <see cref="ThrowB"/> selects which: false = COM-A,
/// true = COM-B. It is persistent state -- serialized with the schematic (in
/// the unit's switch-state field) and restored on load. In sim mode a left
/// click on the symbol flips it between the two throws.
///
/// Pin numbering matches <see cref="SpdtSwitchInput"/> and the 3-pin jumper so
/// the simulation model binds the same regardless of which symbol draws it.
/// </summary>
public sealed class SpdtSwitchUnit : Unit
{
    [Category("State")]
    [Description("Selected throw. False = COM connected to throw A (pin 1); true = COM connected to throw B (pin 3).")]
    public bool ThrowB { get; set; }

    public SpdtSwitchUnit(Device device, UnitSpec spec) : base(device, spec)
    {
        Size = new Size(6, 4);
        BuildPins(spec);
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

    /// <summary>Clickable extent for sim-mode interaction (the body box), rotation-aware.</summary>
    public Rectangle InteractiveBounds
    {
        get
        {
            var unrotated = new Rectangle(Position.X, Position.Y, Size.Width, Size.Height);

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
        // 1 = throw A (top-right), 2 = COM (mid-left), 3 = throw B (bottom-right).
        AddPin(new Pin("1", 1, new Point(Size.Width, 1), PinDirection.Right));
        AddPin(new Pin("2", 2, new Point(0, 2), PinDirection.Left));
        AddPin(new Pin("3", 3, new Point(Size.Width, 3), PinDirection.Right));
    }

    protected override void DrawShape(Graphics g, RenderContext ctx)
    {
        int p = ctx.GridPitch;

        // Contact positions: COM one cell in from the left at mid-height;
        // the two throws one cell in from the right, top and bottom.
        int comX = (Position.X + 1) * p;
        int throwX = (Position.X + 5) * p;
        int comY = (Position.Y + 2) * p;
        int aY = (Position.Y + 1) * p;
        int bY = (Position.Y + 3) * p;

        // Pin endpoints (outer edge of the bounding box).
        int comEndX = Position.X * p;
        int comEndY = (Position.Y + 2) * p;
        int throwEndX = (Position.X + Size.Width) * p;
        int aEndY = (Position.Y + 1) * p;
        int bEndY = (Position.Y + 3) * p;

        var color = Selected ? ctx.SelectedColor : ctx.ForegroundColor;
        using var leadPen = new Pen(color, 1.2f);
        using var pen = new Pen(color, 1.2f);
        using var fill = new SolidBrush(ctx.FillColor);
        using var activeFill = new SolidBrush(ctx.SelectedColor);

        // Leads from the pin endpoints to the contacts.
        g.DrawLine(leadPen, comEndX, comEndY, comX, comY);
        g.DrawLine(leadPen, throwX, aY, throwEndX, aEndY);
        g.DrawLine(leadPen, throwX, bY, throwEndX, bEndY);

        // Three contact terminals; highlight the selected throw.
        DrawContact(g, pen, fill, comX, comY);
        DrawContact(g, pen, ThrowB ? fill : activeFill, throwX, aY);
        DrawContact(g, pen, ThrowB ? activeFill : fill, throwX, bY);

        // Pivoting lever: hinged at COM, touching the selected throw.
        int activeY = ThrowB ? bY : aY;
        g.DrawLine(pen, comX, comY, throwX, activeY);

        using var pinBrush = new SolidBrush(ctx.PinColor);
        g.FillEllipse(pinBrush, comEndX - 2, comEndY - 2, 4, 4);
        g.FillEllipse(pinBrush, throwEndX - 2, aEndY - 2, 4, 4);
        g.FillEllipse(pinBrush, throwEndX - 2, bEndY - 2, 4, 4);
    }

    private static void DrawContact(Graphics g, Pen pen, Brush fill, int x, int y)
    {
        g.FillEllipse(fill, x - 3, y - 3, 6, 6);
        g.DrawEllipse(pen, x - 3, y - 3, 6, 6);
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