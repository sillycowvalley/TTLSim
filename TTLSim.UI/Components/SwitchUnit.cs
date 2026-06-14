using System.ComponentModel;
using System.Drawing;
using TTLSim.UI.Model;

namespace TTLSim.UI.Components;

/// <summary>
/// SPST latching switch. Two pins (1, 2) tied together when closed, open when
/// open. IsClosed is persistent state -- it serializes with the schematic and
/// is restored on load. In sim mode a left-click on the symbol toggles it.
///
/// When the owning device is the 2-pin jumper part ("jumper-2pin") this same
/// unit renders as a pin-header jumper instead of a lever switch -- see
/// <see cref="IsJumper"/>. The electrical model, persisted IsClosed state, and
/// click-to-toggle are identical; only the drawing and the clickable area
/// differ.
/// </summary>
public sealed class SwitchUnit : Unit
{
    [Category("State")]
    [Description("True when the switch is closed (conducting). Open-circuit when false.")]
    public bool IsClosed { get; set; }

    /// <summary>
    /// True when this unit belongs to the 2-pin jumper part rather than the
    /// SPST switch part. Derived from the part definition (which round-trips
    /// through PartIdentifier), so it needs no separate stored/serialized
    /// flag. Drives the jumper rendering and the clickable-area branch.
    /// </summary>
    [Browsable(false)]
    public bool IsJumper => Device.Definition.Identifier == "jumper-2pin";

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
    /// Clickable extent for sim-mode interaction. A switch's lever overshoots
    /// one cell above the body, so its target includes that cell; a jumper has
    /// no lever, so its target is just the body box. Rotation-aware, mirroring
    /// the Bounds rotation logic.
    /// </summary>
    public Rectangle InteractiveBounds
    {
        get
        {
            int topPad = IsJumper ? 0 : 1;
            var unrotated = new Rectangle(
                Position.X, Position.Y - topPad, Size.Width, Size.Height + topPad);

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
        if (IsJumper)
        {
            DrawJumper(g, ctx);
            return;
        }

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

    /// <summary>
    /// 2-pin jumper rendering: two header posts with a shunt conductor bridging
    /// them when closed. Same pins, same IsClosed state as the switch form.
    /// </summary>
    private void DrawJumper(Graphics g, RenderContext ctx)
    {
        int p = ctx.GridPitch;
        int postAx = (Position.X + 1) * p;
        int postBx = (Position.X + 5) * p;
        int postY = (Position.Y + 1) * p;
        int m = (int)(p * 0.8f);

        int p1x = (Position.X + Pins[0].LocalPosition.X) * p;
        int p1y = (Position.Y + Pins[0].LocalPosition.Y) * p;
        int p2x = (Position.X + Pins[1].LocalPosition.X) * p;
        int p2y = (Position.Y + Pins[1].LocalPosition.Y) * p;

        JumperGlyphs.DrawBody(g, ctx, Selected, postAx - m, postY - m, postBx + m, postY + m);
        JumperGlyphs.DrawLead(g, ctx, Selected, p1x, p1y, postAx, postY);
        JumperGlyphs.DrawLead(g, ctx, Selected, postBx, postY, p2x, p2y);

        // Shunt bridges the two posts only when closed; the highlight lives on
        // the posts, so the bar itself stays a plain conductor.
        if (IsClosed)
            JumperGlyphs.DrawShunt(g, ctx, Selected, postAx, postY, postBx, postY);

        // Both posts light up when closed (a 2-pin link has no "side"), matching
        // the SPST switch highlighting both contacts when closed.
        JumperGlyphs.DrawPost(g, ctx, postAx, postY, Selected, IsClosed);
        JumperGlyphs.DrawPost(g, ctx, postBx, postY, Selected, IsClosed);
        JumperGlyphs.DrawTerminal(g, ctx, p1x, p1y);
        JumperGlyphs.DrawTerminal(g, ctx, p2x, p2y);
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