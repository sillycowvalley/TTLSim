using System;
using System.ComponentModel;
using System.Drawing;
using TTLSim.UI.Model;

namespace TTLSim.UI.Components;

/// <summary>
/// Multi-position DIP switch: N independent SPST contacts in one DIP body.
/// Pin numbering follows the physical package (DIP convention): pins 1..N run
/// down the left side, N+1..2N up the right side, so contact k ties pin k to
/// pin 2N+1-k -- the two pins sit on the same row of the body.
///
/// Geometry matches ChipUnit's DIP conventions (2-cell pin pitch, 1-cell
/// vertical margin, body inset one cell each side for the pin stubs) at the
/// skinny 0.3" body width, so the exported symbol's 20px pitch lines up with
/// the canvas exactly, the same way the DIP chip symbols do.
///
/// PositionsClosed is persistent per-position state -- it serializes with the
/// schematic (UnitDto.SwitchPositions as a '0'/'1' string) and is restored on
/// load. In sim mode a left-click on a row toggles that position; the property
/// grid exposes the same state as an editable pattern string.
/// </summary>
public sealed class DipSwitchUnit : Unit
{
    /// <summary>Vertical grid-cell pitch between adjacent positions (DIP pin pitch).</summary>
    private const int PinPitch = 2;

    /// <summary>Top and bottom margin on the body, in grid cells.</summary>
    private const int VerticalMargin = 1;

    /// <summary>Body width in grid cells -- the skinny 0.3" DIP body,
    /// matching the 2114 / GAL class of chips.</summary>
    private const int BodyWidth = 8;

    private readonly bool[] positionsClosed;

    /// <summary>Number of switch positions (contacts). Pin count is 2x this.</summary>
    [Browsable(false)]
    public int Positions { get; }

    /// <summary>Per-position closed state, index 0 = position 1 (top row).
    /// Mutated in place by sim-mode clicks and the property grid.</summary>
    [Browsable(false)]
    public bool[] PositionsClosed => positionsClosed;

    /// <summary>
    /// Property-grid view of the per-position state: one '0' (open) or '1'
    /// (closed) per position, position 1 first, e.g. "0110". Short or
    /// malformed input leaves unmentioned positions unchanged.
    /// </summary>
    [Category("State")]
    [Description("Per-position state, one '0' (open) or '1' (closed) per position, position 1 first. Example: 0110")]
    public string ClosedPattern
    {
        get
        {
            var chars = new char[Positions];
            for (int i = 0; i < Positions; i++)
                chars[i] = positionsClosed[i] ? '1' : '0';
            return new string(chars);
        }
        set
        {
            if (value is null) return;
            for (int i = 0; i < Positions && i < value.Length; i++)
            {
                if (value[i] == '0') positionsClosed[i] = false;
                else if (value[i] == '1') positionsClosed[i] = true;
                // any other character: leave that position unchanged
            }
        }
    }

    public DipSwitchUnit(Device device, UnitSpec spec, DipSwitchPartDefinition definition)
        : base(device, spec)
    {
        Positions = definition.Positions;
        positionsClosed = new bool[Positions];

        int bodyHeight = VerticalMargin * 2 + (Positions - 1) * PinPitch;
        // Width includes a one-cell stub on each side so pin endpoints
        // (LocalPosition) sit on the bounding-box edge, matching ChipUnit.
        Size = new Size(BodyWidth + 2, bodyHeight);
        BuildPins(spec);
    }

    protected override void BuildPins(UnitSpec spec)
    {
        // DIP layout: pins 1..N down the left side, N+1..2N up the right side.
        // Contact k's two pins (k and 2N+1-k) land on the same row.
        for (int n = 1; n <= Positions; n++)
        {
            int y = VerticalMargin + (n - 1) * PinPitch;
            AddPin(new Pin(n.ToString(), n, new Point(0, y), PinDirection.Left));

            int rightPin = 2 * Positions + 1 - n;
            AddPin(new Pin(rightPin.ToString(), rightPin,
                new Point(Size.Width, y), PinDirection.Right));
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

    /// <summary>
    /// Clickable extent for sim-mode interaction: the body box (no lever
    /// overshoot -- sliders live inside the body). Rotation-aware, mirroring
    /// the Bounds rotation logic used by the other interactive units.
    /// </summary>
    public Rectangle InteractiveBounds
    {
        get
        {
            var unrotated = new Rectangle(
                Position.X, Position.Y, Size.Width, Size.Height);

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
    /// Which position (0-based) a grid-space point lands on, or null if it
    /// isn't near any contact row. Rotation-independent: each position's two
    /// pins carry their own rotated world positions, so the row midpoint is
    /// computed from those rather than from any transform math. The threshold
    /// is half the pin pitch, so adjacent rows never overlap.
    /// </summary>
    public int? PositionAt(PointF gridPoint)
    {
        int? best = null;
        float bestDist = float.MaxValue;
        const float threshold = PinPitch / 2f;

        for (int n = 1; n <= Positions; n++)
        {
            Pin? left = null, right = null;
            foreach (var pin in Pins)
            {
                if (pin.Number == n) left = pin;
                if (pin.Number == 2 * Positions + 1 - n) right = pin;
            }
            if (left is null || right is null) continue;

            float mx = (left.WorldPosition.X + right.WorldPosition.X) / 2f;
            float my = (left.WorldPosition.Y + right.WorldPosition.Y) / 2f;

            // Distance along the row axis is bounded by the body; the
            // discriminating axis is across the rows. Use the full 2D
            // distance to the row midpoint projected onto the cross-row
            // axis: with rows PinPitch apart, comparing the cross-axis
            // component alone is equivalent to nearest-midpoint with the
            // half-pitch threshold below.
            float dx = gridPoint.X - mx;
            float dy = gridPoint.Y - my;
            // Row axis is left->right pin direction; cross axis perpendicular.
            float rx = right.WorldPosition.X - left.WorldPosition.X;
            float ry = right.WorldPosition.Y - left.WorldPosition.Y;
            float len = MathF.Sqrt(rx * rx + ry * ry);
            if (len < 0.001f) continue;
            float cross = MathF.Abs(dx * (-ry / len) + dy * (rx / len));

            if (cross < bestDist)
            {
                bestDist = cross;
                best = n - 1;
            }
        }

        return bestDist <= threshold ? best : null;
    }

    protected override void DrawShape(Graphics g, RenderContext ctx)
    {
        int p = ctx.GridPitch;
        int leftX = Position.X * p;
        int rightX = (Position.X + Size.Width) * p;
        int topY = Position.Y * p;
        int bottomY = (Position.Y + Size.Height) * p;

        // Body is inset by one cell on each side -- the outer cell is the pin stub.
        int bodyLeftX = leftX + p;
        int bodyRightX = rightX - p;

        using var fill = new SolidBrush(ctx.FillColor);
        using var outline = new Pen(Selected ? ctx.SelectedColor : ctx.ForegroundColor, 1.2f);
        using var leadPen = new Pen(Selected ? ctx.SelectedColor : ctx.ForegroundColor, 1.2f);

        // Body rectangle.
        g.FillRectangle(fill, bodyLeftX, topY, bodyRightX - bodyLeftX, bottomY - topY);
        g.DrawRectangle(outline, bodyLeftX, topY, bodyRightX - bodyLeftX, bottomY - topY);

        // Pin-1 orientation dot, ChipUnit style: top-left corner of the body.
        using (var dot = new SolidBrush(ctx.ForegroundColor))
            g.FillEllipse(dot, bodyLeftX + p * 0.3f, topY + p * 0.3f, p * 0.4f, p * 0.4f);

        // Per-position stubs and slider glyphs.
        using var knobClosed = new SolidBrush(ctx.SelectedColor);
        using var knobOpen = new SolidBrush(ctx.FillColor);
        using var track = new Pen(ctx.ForegroundColor, 1.0f);
        using var pinBrush = new SolidBrush(ctx.PinColor);

        for (int n = 1; n <= Positions; n++)
        {
            int rowY = (Position.Y + VerticalMargin + (n - 1) * PinPitch) * p;

            // Pin stubs from the bounding-box edge to the body.
            g.DrawLine(leadPen, leftX, rowY, bodyLeftX, rowY);
            g.DrawLine(leadPen, bodyRightX, rowY, rightX, rowY);

            // Pin endpoint dots at the bounding-box edge, matching the IC
            // and switch convention (4px dot centred on the pin endpoint).
            g.FillEllipse(pinBrush, leftX - 2, rowY - 2, 4, 4);
            g.FillEllipse(pinBrush, rightX - 2, rowY - 2, 4, 4);

            // Slider track: a horizontal slot centred on the row, with the
            // knob at the LEFT end when open and the RIGHT end when closed
            // (right = ON, the common DIP-switch convention). Closed knobs
            // use the highlight colour, matching the SPST switch's closed
            // contact highlighting.
            float trackHalf = p * 1.6f;
            float knob = p * 0.9f;
            float cx = (bodyLeftX + bodyRightX) / 2f;
            g.DrawRectangle(track,
                cx - trackHalf, rowY - knob / 2, trackHalf * 2, knob);

            bool closed = positionsClosed[n - 1];
            float knobX = closed ? cx + trackHalf - knob : cx - trackHalf;
            g.FillRectangle(closed ? knobClosed : knobOpen,
                knobX, rowY - knob / 2, knob, knob);
            g.DrawRectangle(track, knobX, rowY - knob / 2, knob, knob);
        }
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