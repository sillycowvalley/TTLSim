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
/// When the owning device is the 3-pin jumper part ("jumper-3pin") this same
/// unit renders as a linear inline jumper instead of a lever switch -- COM in
/// the centre, throws A and B at the two ends (twice the 2-pin jumper's width)
/// -- see <see cref="IsJumper"/>. The electrical model, persisted ThrowB state,
/// and click-to-flip are identical; only the drawing and pin placement differ.
///
/// Pin numbering matches <see cref="SpdtSwitchInput"/> and the jumper so the
/// simulation model binds the same regardless of which symbol draws it.
/// </summary>
public sealed class SpdtSwitchUnit : Unit
{
    [Category("State")]
    [Description("Selected throw. False = COM connected to throw A (pin 1); true = COM connected to throw B (pin 3).")]
    public bool ThrowB { get; set; }

    /// <summary>
    /// True when this unit belongs to the 3-pin jumper part rather than the
    /// SPDT switch part. Derived from the part definition (which round-trips
    /// through PartIdentifier), so it needs no separate stored/serialized flag.
    /// </summary>
    [Browsable(false)]
    public bool IsJumper => Device.Definition.Identifier == "jumper-3pin";

    public SpdtSwitchUnit(Device device, UnitSpec spec) : base(device, spec)
    {
        // The jumper form is a linear inline 3-pin part -- twice the 2-pin
        // jumper's width, same height. The switch form keeps the lever box.
        Size = IsJumper ? new Size(12, 2) : new Size(6, 4);
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
        if (IsJumper)
        {
            // Linear inline jumper: throw A (pin 1) at the left end, COM (pin 2)
            // tapped from the centre pointing down, throw B (pin 3) at the right
            // end. Pin NUMBERS match the switch and the sim model; only the
            // physical positions differ.
            AddPin(new Pin("1", 1, new Point(0, 1), PinDirection.Left));
            AddPin(new Pin("2", 2, new Point(Size.Width / 2, 2), PinDirection.Down));
            AddPin(new Pin("3", 3, new Point(Size.Width, 1), PinDirection.Right));
            return;
        }

        // SPDT switch: 1 = throw A (top-right), 2 = COM (mid-left), 3 = throw B (bottom-right).
        AddPin(new Pin("1", 1, new Point(Size.Width, 1), PinDirection.Right));
        AddPin(new Pin("2", 2, new Point(0, 2), PinDirection.Left));
        AddPin(new Pin("3", 3, new Point(Size.Width, 3), PinDirection.Right));
    }

    protected override void DrawShape(Graphics g, RenderContext ctx)
    {
        if (IsJumper)
        {
            DrawJumper(g, ctx);
            return;
        }

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

        // Highlight every terminal on the live path: COM is always connected,
        // plus whichever throw is selected. The open throw stays plain. Matches
        // the SPST switch, which lights both ends of a closed connection.
        DrawContact(g, pen, activeFill, comX, comY);
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

    /// <summary>
    /// Linear 3-pin jumper rendering: throw A and throw B posts at the left and
    /// right ends, COM post in the centre with its lead tapping out the bottom.
    /// A shunt conductor bridges COM to the selected throw. Same pins and ThrowB
    /// state as the SPDT switch form.
    /// </summary>
    private void DrawJumper(Graphics g, RenderContext ctx)
    {
        int p = ctx.GridPitch;
        int midY = (Position.Y + 1) * p;
        int postAx = (Position.X + 1) * p;
        int postComX = (Position.X + Size.Width / 2) * p;
        int postBx = (Position.X + Size.Width - 1) * p;
        int m = (int)(p * 0.8f);

        // Pin endpoints: A at the left edge, B at the right edge, COM out the
        // bottom-centre.
        int aEndX = Position.X * p;
        int bEndX = (Position.X + Size.Width) * p;
        int comEndY = (Position.Y + 2) * p;

        JumperGlyphs.DrawBody(g, ctx, Selected, postAx - m, midY - m, postBx + m, midY + m);

        // Leads: A from the left edge, B from the right edge, COM dropping down.
        JumperGlyphs.DrawLead(g, ctx, Selected, aEndX, midY, postAx, midY);
        JumperGlyphs.DrawLead(g, ctx, Selected, postBx, midY, bEndX, midY);
        JumperGlyphs.DrawLead(g, ctx, Selected, postComX, midY, postComX, comEndY);

        // Shunt bridges COM to the selected throw (SPDT is on-on, so always
        // connected). The bar is a plain conductor; the live terminals are shown
        // by the highlighted posts.
        int selX = ThrowB ? postBx : postAx;
        JumperGlyphs.DrawShunt(g, ctx, Selected, postComX, midY, selX, midY);

        // COM is always on the live path, so it highlights too; the open throw
        // is the only plain post (mirrors the SPDT switch contacts).
        JumperGlyphs.DrawPost(g, ctx, postAx, midY, Selected, active: !ThrowB);
        JumperGlyphs.DrawPost(g, ctx, postComX, midY, Selected, active: true);
        JumperGlyphs.DrawPost(g, ctx, postBx, midY, Selected, active: ThrowB);

        JumperGlyphs.DrawTerminal(g, ctx, aEndX, midY);
        JumperGlyphs.DrawTerminal(g, ctx, postComX, comEndY);
        JumperGlyphs.DrawTerminal(g, ctx, bEndX, midY);
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