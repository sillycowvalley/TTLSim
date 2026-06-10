using System.Drawing;
using System.Drawing.Drawing2D;
using TTLSim.UI.Model;

namespace TTLSim.UI.Components;

/// <summary>Resistor -- zigzag body, 6 cells wide x 2 tall, two pins.</summary>
public sealed class ResistorUnit : Unit
{
    public ResistorUnit(Device device, UnitSpec spec) : base(device, spec)
    {
        Size = new Size(6, 2);
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
        int leftX = (Position.X + 1) * p;
        int rightX = (Position.X + 5) * p;
        int midY = (Position.Y + 1) * p;

        using var leadPen = new Pen(Selected ? ctx.SelectedColor : ctx.ForegroundColor, 1.2f);
        int p1x = (Position.X + Pins[0].LocalPosition.X) * p;
        int p1y = (Position.Y + Pins[0].LocalPosition.Y) * p;
        int p2x = (Position.X + Pins[1].LocalPosition.X) * p;
        int p2y = (Position.Y + Pins[1].LocalPosition.Y) * p;
        g.DrawLine(leadPen, p1x, p1y, leftX, midY);
        g.DrawLine(leadPen, rightX, midY, p2x, p2y);

        using var pen = new Pen(Selected ? ctx.SelectedColor : ctx.ForegroundColor, 1.2f);
        int amp = p;
        int segments = 6;
        float dx = (rightX - leftX) / (float)segments;
        var path = new GraphicsPath();
        path.AddLine(leftX, midY, leftX + dx / 2, midY - amp);
        for (int i = 1; i < segments; i++)
        {
            float x = leftX + dx / 2 + dx * (i - 0.5f);
            int y = midY + ((i % 2 == 0) ? -amp : amp);
            path.AddLine(path.GetLastPoint(), new PointF(x, y));
        }
        path.AddLine(path.GetLastPoint(), new PointF(rightX, midY));
        g.DrawPath(pen, path);

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