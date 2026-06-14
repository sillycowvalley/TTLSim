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
/// unit renders as a 3-post pin-header jumper instead of a lever switch -- see
/// <see cref="IsJumper"/>. The electrical model, persisted ThrowB state, and
/// click-to-flip are identical; only the drawing differs.
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

    /// <summary>
    /// 3-pin jumper rendering: three header posts (COM, A, B) with a shunt
    /// conductor bridging COM to the selected throw. Same pins, same ThrowB
    /// state as the SPDT switch form.
    /// </summary>
    private void DrawJumper(Graphics g, RenderContext ctx)
    {
        int p = ctx.GridPitch;
        int comX = (Position.X + 1) * p, comY = (Position.Y + 2) * p;
        int aX = (Position.X + 5) * p, aY = (Position.Y + 1) * p;
        int bX = (Position.X + 5) * p, bY = (Position.Y + 3) * p;
        int m = (int)(p * 0.8f);

        int comEndX = Position.X * p, comEndY = (Position.Y + 2) * p;
        int throwEndX = (Position.X + Size.Width) * p;
        int aEndY = (Position.Y + 1) * p, bEndY = (Position.Y + 3) * p;

        JumperGlyphs.DrawBody(g, ctx, Selected, comX - m, aY - m, bX + m, bY + m);
        JumperGlyphs.DrawLead(g, ctx, Selected, comEndX, comEndY, comX, comY);
        JumperGlyphs.DrawLead(g, ctx, Selected, aX, aY, throwEndX, aEndY);
        JumperGlyphs.DrawLead(g, ctx, Selected, bX, bY, throwEndX, bEndY);

        // Shunt bridges COM to the selected throw (SPDT is on-on, so always
        // connected). The bar is a plain conductor; the selected side is shown
        // by highlighting that throw's post, like the SPDT switch contacts.
        int selX = ThrowB ? bX : aX;
        int selY = ThrowB ? bY : aY;
        JumperGlyphs.DrawShunt(g, ctx, Selected, comX, comY, selX, selY);

        JumperGlyphs.DrawPost(g, ctx, comX, comY, Selected, active: false);
        JumperGlyphs.DrawPost(g, ctx, aX, aY, Selected, active: !ThrowB);
        JumperGlyphs.DrawPost(g, ctx, bX, bY, Selected, active: ThrowB);

        JumperGlyphs.DrawTerminal(g, ctx, comEndX, comEndY);
        JumperGlyphs.DrawTerminal(g, ctx, throwEndX, aEndY);
        JumperGlyphs.DrawTerminal(g, ctx, throwEndX, bEndY);
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