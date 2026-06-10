using System.Drawing;
using TTLSim.UI.Model;

namespace TTLSim.UI.Components;

/// <summary>Non-polarised capacitor -- two parallel plates, 4 cells wide x 2 tall.</summary>
public sealed class CapacitorUnit : Unit
{
    public CapacitorUnit(Device device, UnitSpec spec) : base(device, spec)
    {
        Size = new Size(4, 2);
        BuildPins(spec);
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
        int midX = (Position.X + 2) * p;
        int midY = (Position.Y + 1) * p;
        int plateGap = p / 2;
        int plateHeight = (int)(p * 1.6f);

        using var leadPen = new Pen(Selected ? ctx.SelectedColor : ctx.ForegroundColor, 1.2f);
        int p1x = (Position.X + Pins[0].LocalPosition.X) * p;
        int p1y = (Position.Y + Pins[0].LocalPosition.Y) * p;
        int p2x = (Position.X + Pins[1].LocalPosition.X) * p;
        int p2y = (Position.Y + Pins[1].LocalPosition.Y) * p;
        g.DrawLine(leadPen, p1x, p1y, midX - plateGap, midY);
        g.DrawLine(leadPen, midX + plateGap, midY, p2x, p2y);

        using var pen = new Pen(Selected ? ctx.SelectedColor : ctx.ForegroundColor, 1.4f);
        g.DrawLine(pen, midX - plateGap, midY - plateHeight, midX - plateGap, midY + plateHeight);
        g.DrawLine(pen, midX + plateGap, midY - plateHeight, midX + plateGap, midY + plateHeight);

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

        // Plates extend ~0.6p past the bounding box. At R0/R180 that means
        // ~0.6p above the top; at R90/R270 it means ~0.6p left of the box.
        float plateOverhang = p * 0.6f;
        float bodyTopY = b.Y * p - plateOverhang;
        float bodyBottomY = (b.Y + b.Height) * p + plateOverhang;
        float bodyLeftX = b.X * p - plateOverhang;
        DrawPassiveLabels(g, ctx, midX, midY, bodyTopY, bodyBottomY, bodyLeftX);
    }
}