using System.ComponentModel;
using System.Drawing;
using TTLSim.UI.Model;

namespace TTLSim.UI.Components;

/// <summary>
/// SPST momentary pushbutton. The 2-pin variant ("button") ties pins 1 and 2
/// together when pressed and is open when released. The 4-pin breadboard
/// variant ("button-4") is the same momentary contact with each terminal
/// doubled onto two legs: terminal A = pins 1,2 (top), terminal B = pins 3,4
/// (bottom). Pressing bridges A to B; the two legs of a terminal are always
/// common. Which variant this unit is comes from the owning device's
/// definition (round-tripped through PartIdentifier), so no separate stored
/// flag is needed -- the same pattern as <see cref="SwitchUnit.IsJumper"/>.
///
/// IsPressed is exposed on the property grid so the user can toggle it
/// manually; in sim mode a click on the symbol presses and releases it.
/// </summary>
public sealed class ButtonUnit : Unit
{
    [Category("State")]
    [Description("True while the button is held down. Open-circuit when false.")]
    public bool IsPressed { get; set; }

    /// <summary>
    /// True when this unit belongs to the 4-pin breadboard pushbutton part
    /// ("button-4") rather than the 2-pin part ("button"). Derived from the
    /// part definition, so it needs no separate stored/serialized flag. Drives
    /// the pin layout, body size, and rendering branch.
    /// </summary>
    [Browsable(false)]
    public bool IsFourPin => Device.Definition.Identifier == "button-4";

    public ButtonUnit(Device device, UnitSpec spec) : base(device, spec)
    {
        // Both variants share the wide 6x2 body. The 4-pin just doubles the
        // legs on each side, so its default orientation matches the 2-pin.
        Size = new Size(6, 2);
        BuildPins(spec);
    }

    public override Rectangle RoutingBounds
    {
        get
        {
            // Pad generously around the button so wires route clear of the
            // plunger and the press target. More margin than the other
            // passives -- the button is interactive and wants elbow room.
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
    /// top for the plunger's released-state overshoot. Rotation-aware, mirroring
    /// the Bounds rotation logic.
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
        if (IsFourPin)
        {
            // 2-pin layout with a doubled leg per side. Terminal A (left) =
            // pins 1,2; terminal B (right) = pins 3,4 -- the same electrical
            // pairing as before, just drawn left/right like the 2-pin button
            // so the default orientation matches it. Pin 1 and pin 3 remain on
            // opposite terminals, so existing wiring keeps its meaning.
            AddPin(new Pin("1", 1, new Point(0, 0), PinDirection.Left));
            AddPin(new Pin("2", 2, new Point(0, Size.Height), PinDirection.Left));
            AddPin(new Pin("3", 3, new Point(Size.Width, 0), PinDirection.Right));
            AddPin(new Pin("4", 4, new Point(Size.Width, Size.Height), PinDirection.Right));
            return;
        }

        AddPin(new Pin("1", 1, new Point(0, 1), PinDirection.Left));
        AddPin(new Pin("2", 2, new Point(Size.Width, 1), PinDirection.Right));
    }

    protected override void DrawShape(Graphics g, RenderContext ctx)
    {
        if (IsFourPin)
        {
            DrawFourPin(g, ctx);
            return;
        }

        int p = ctx.GridPitch;
        int leftX = (Position.X + 1) * p;
        int rightX = (Position.X + 5) * p;
        int midY = (Position.Y + 1) * p;
        int half = (int)(p * 1.0f);

        // Leads from pin endpoints to the contacts.
        using var leadPen = new Pen(Selected ? ctx.SelectedColor : ctx.ForegroundColor, 1.2f);
        int p1x = (Position.X + Pins[0].LocalPosition.X) * p;
        int p1y = (Position.Y + Pins[0].LocalPosition.Y) * p;
        int p2x = (Position.X + Pins[1].LocalPosition.X) * p;
        int p2y = (Position.Y + Pins[1].LocalPosition.Y) * p;
        g.DrawLine(leadPen, p1x, p1y, leftX, midY);
        g.DrawLine(leadPen, rightX, midY, p2x, p2y);

        using var pen = new Pen(Selected ? ctx.SelectedColor : ctx.ForegroundColor, 1.2f);
        using var fill = new SolidBrush(IsPressed ? ctx.SelectedColor : ctx.FillColor);

        // Two contact terminals.
        g.FillEllipse(fill, leftX - 3, midY - 3, 6, 6);
        g.DrawEllipse(pen, leftX - 3, midY - 3, 6, 6);
        g.FillEllipse(fill, rightX - 3, midY - 3, 6, 6);
        g.DrawEllipse(pen, rightX - 3, midY - 3, 6, 6);

        // Moving bar -- hinged at the left contact. Pressed: lies flat across
        // both contacts (closed). Released: the whole bar lifts clear of both
        // contacts and its right end pulls back, so the circuit reads open.
        int barLift = IsPressed ? 0 : half;
        int barY = midY - barLift;
        g.DrawLine(pen, leftX, barY, rightX, barY);

        // Plunger -- vertical stub rising from the bar's centre. Its TOP stays
        // fixed; the bar moving up to meet it on press is the visible travel.
        int plungerTop = barY - half;          // fixed cap height
        int plungerX = (leftX + rightX) / 2;
        g.DrawLine(pen, plungerX, barY, plungerX, plungerTop);

        using var pinBrush = new SolidBrush(ctx.PinColor);
        g.FillEllipse(pinBrush, p1x - 2, p1y - 2, 4, 4);
        g.FillEllipse(pinBrush, p2x - 2, p2y - 2, 4, 4);
    }

    /// <summary>
    /// 4-pin tactile glyph. Identical to the 2-pin button -- two contacts on
    /// the left and right with the moving bar and plunger on top -- except each
    /// side has two converging legs (pins 1,2 on the left contact; pins 3,4 on
    /// the right). Pressed: the bar lies flat across both contacts (closed).
    /// Released: it lifts clear (open).
    /// </summary>
    private void DrawFourPin(Graphics g, RenderContext ctx)
    {
        int p = ctx.GridPitch;
        int leftX = (Position.X + 1) * p;
        int rightX = (Position.X + 5) * p;
        int midY = (Position.Y + 1) * p;
        int half = (int)(p * 1.0f);

        // Pin endpoints (four corners).
        int p1x = (Position.X + Pins[0].LocalPosition.X) * p;
        int p1y = (Position.Y + Pins[0].LocalPosition.Y) * p;
        int p2x = (Position.X + Pins[1].LocalPosition.X) * p;
        int p2y = (Position.Y + Pins[1].LocalPosition.Y) * p;
        int p3x = (Position.X + Pins[2].LocalPosition.X) * p;
        int p3y = (Position.Y + Pins[2].LocalPosition.Y) * p;
        int p4x = (Position.X + Pins[3].LocalPosition.X) * p;
        int p4y = (Position.Y + Pins[3].LocalPosition.Y) * p;

        var color = Selected ? ctx.SelectedColor : ctx.ForegroundColor;
        using var pen = new Pen(color, 1.2f);
        using var fill = new SolidBrush(IsPressed ? ctx.SelectedColor : ctx.FillColor);

        // Leads: pins 1,2 converge on the left contact; pins 3,4 on the right.
        g.DrawLine(pen, p1x, p1y, leftX, midY);
        g.DrawLine(pen, p2x, p2y, leftX, midY);
        g.DrawLine(pen, p3x, p3y, rightX, midY);
        g.DrawLine(pen, p4x, p4y, rightX, midY);

        // The two fixed contacts.
        g.FillEllipse(fill, leftX - 3, midY - 3, 6, 6);
        g.DrawEllipse(pen, leftX - 3, midY - 3, 6, 6);
        g.FillEllipse(fill, rightX - 3, midY - 3, 6, 6);
        g.DrawEllipse(pen, rightX - 3, midY - 3, 6, 6);

        // Moving bar across both contacts; lifts up when released. Plunger
        // stub rises from the bar centre to a fixed cap height.
        int barLift = IsPressed ? 0 : half;
        int barY = midY - barLift;
        g.DrawLine(pen, leftX, barY, rightX, barY);

        int plungerTop = barY - half;
        int plungerX = (leftX + rightX) / 2;
        g.DrawLine(pen, plungerX, barY, plungerX, plungerTop);

        using var pinBrush = new SolidBrush(ctx.PinColor);
        g.FillEllipse(pinBrush, p1x - 2, p1y - 2, 4, 4);
        g.FillEllipse(pinBrush, p2x - 2, p2y - 2, 4, 4);
        g.FillEllipse(pinBrush, p3x - 2, p3y - 2, 4, 4);
        g.FillEllipse(pinBrush, p4x - 2, p4y - 2, 4, 4);
    }

    protected override void DrawLabels(Graphics g, RenderContext ctx)
    {
        int p = ctx.GridPitch;
        var b = Bounds;
        float midX = (b.X + b.Width / 2f) * p;
        float midY = (b.Y + b.Height / 2f) * p;

        float plungerReach = p * 0.7f;  // matches `half` in DrawShape

        float bodyTopY = b.Y * p - plungerReach;
        float bodyBottomY = (b.Y + b.Height) * p + plungerReach;
        float bodyLeftX = b.X * p - plungerReach;
        DrawPassiveLabels(g, ctx, midX, midY, bodyTopY, bodyBottomY, bodyLeftX);
    }
}