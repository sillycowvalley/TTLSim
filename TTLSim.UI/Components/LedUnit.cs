using System.ComponentModel;
using System.Drawing;
using TTLSim.UI.Model;

namespace TTLSim.UI.Components;

/// <summary>Selectable LED body colour. The enum name round-trips through Device.Value.</summary>
public enum LedColor { Red, Green, Yellow, Blue, White }

/// <summary>LED -- triangle + cathode bar with two emission arrows, 4 cells wide x 2 tall.</summary>
public sealed class LedUnit : Unit
{
    public LedUnit(Device device, UnitSpec spec) : base(device, spec)
    {
        Size = new Size(4, 2);
        BuildPins(spec);
    }

    /// <summary>
    /// LED body colour, persisted via Device.Value (the enum name). Unrecognised
    /// or missing values fall back to Red.
    /// </summary>
    [Category("Identity")]
    public LedColor Color
    {
        get => System.Enum.TryParse<LedColor>(Device.Value, ignoreCase: true, out var c)
            ? c : LedColor.Red;
        set => Device.Value = value.ToString();
    }

    protected override void BuildPins(UnitSpec spec)
    {
        AddPin(new Pin("1", 1, new Point(0, 1), PinDirection.Left));
        AddPin(new Pin("2", 2, new Point(Size.Width, 1), PinDirection.Right));
    }

    public override Rectangle RoutingBounds
    {
        get
        {
            // Inflate up and to the right to cover the emission arrows
            // which extend ~1 cell above the body's top.
            var unrotated = new Rectangle(
                Position.X, Position.Y - 1, Size.Width + 1, Size.Height + 1);

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
        int leftX = (Position.X + 1) * p;
        int rightX = (Position.X + 3) * p;
        int midY = (Position.Y + 1) * p;
        int half = (int)(p * 0.9f);

        using var leadPen = new Pen(Selected ? ctx.SelectedColor : ctx.ForegroundColor, 1.2f);
        int p1x = (Position.X + Pins[0].LocalPosition.X) * p;
        int p1y = (Position.Y + Pins[0].LocalPosition.Y) * p;
        int p2x = (Position.X + Pins[1].LocalPosition.X) * p;
        int p2y = (Position.Y + Pins[1].LocalPosition.Y) * p;
        g.DrawLine(leadPen, p1x, p1y, leftX, midY);
        g.DrawLine(leadPen, rightX, midY, p2x, p2y);

        using var pen = new Pen(Selected ? ctx.SelectedColor : ctx.ForegroundColor, 1.2f);

        // Resolve colour and lit state. In edit mode (no provider) lit is false.
        Color ledColor = ToRgb(Color);
        bool lit = ctx.LedStateProvider?.Invoke(this) ?? false;

        Point[] tri =
        {
            new(leftX, midY - half),
            new(leftX, midY + half),
            new(rightX, midY)
        };

        // Soft glow halo behind a lit LED.
        if (lit)
        {
            using var glow = new SolidBrush(System.Drawing.Color.FromArgb(70, ledColor));
            int r = (int)(p * 2.2f);
            int cx = (leftX + rightX) / 2;
            g.FillEllipse(glow, cx - r, midY - r, r * 2, r * 2);
        }

        // Body fill: full colour when lit, a dim tint of it when off.
        Color bodyFill = lit
            ? ledColor
            : System.Drawing.Color.FromArgb(60, ledColor);
        using var fill = new SolidBrush(bodyFill);
        g.FillPolygon(fill, tri);
        g.DrawPolygon(pen, tri);

        g.DrawLine(pen, rightX, midY - half, rightX, midY + half);

        // Emission arrows -- two parallel, 45-degree, mitred heads.
        // Brighten to the LED colour when lit; otherwise the body outline colour.
        float arrowLen = p * 1.2f;
        float headLen = p * 0.5f;
        float dx = arrowLen * 0.7071f;
        float dy = -arrowLen * 0.7071f;

        using var arrowPen = lit
            ? new Pen(ledColor, 1.6f)
            : new Pen(pen.Color, 1.2f);

        PointF[] starts =
        {
            new(leftX + p * 0.4f, midY - half - 1),
            new(leftX + p * 1.4f, midY - half - 1)
        };

        foreach (var s in starts)
        {
            var tip = new PointF(s.X + dx, s.Y + dy);
            g.DrawLine(arrowPen, s.X, s.Y, tip.X, tip.Y);
            var head = new[]
            {
                new PointF(tip.X - headLen, tip.Y),
                tip,
                new PointF(tip.X, tip.Y + headLen)
            };
            g.DrawLines(arrowPen, head);
        }

        using var pinBrush = new SolidBrush(ctx.PinColor);
        g.FillEllipse(pinBrush, p1x - 2, p1y - 2, 4, 4);
        g.FillEllipse(pinBrush, p2x - 2, p2y - 2, 4, 4);
    }

    /// <summary>Map a LedColor to the RGB used for the body and glow.</summary>
    private static Color ToRgb(LedColor c) => c switch
    {
        LedColor.Green => System.Drawing.Color.FromArgb(0x2E, 0xCC, 0x40),
        LedColor.Yellow => System.Drawing.Color.FromArgb(0xFF, 0xCC, 0x00),
        LedColor.Blue => System.Drawing.Color.FromArgb(0x33, 0x99, 0xFF),
        LedColor.White => System.Drawing.Color.FromArgb(0xF0, 0xF0, 0xF0),
        _ => System.Drawing.Color.FromArgb(0xE0, 0x10, 0x10),  // Red
    };

    protected override void DrawLabels(Graphics g, RenderContext ctx)
    {
        int p = ctx.GridPitch;
        var b = Bounds;
        float midX = (b.X + b.Width / 2f) * p;
        float midY = (b.Y + b.Height / 2f) * p;

        // Arrows reach ~0.9p + 1.2p*0.7071 past one corner of the symbol.
        // Their direction depends on rotation, but the rotated Bounds
        // doesn't include them, so inflate uniformly to keep labels clear.
        float half = p * 0.9f;
        float arrowReach = p * 1.2f * 0.7071f;
        float overhang = half + arrowReach - p;  // amount past the bounds edge

        float bodyTopY = b.Y * p - overhang;
        float bodyBottomY = (b.Y + b.Height) * p + overhang;
        float bodyLeftX = b.X * p - overhang;
        DrawPassiveLabels(g, ctx, midX, midY, bodyTopY, bodyBottomY, bodyLeftX);
    }
}