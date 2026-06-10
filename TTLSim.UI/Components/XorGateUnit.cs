using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using TTLSim.UI.Model;

namespace TTLSim.UI.Components;

/// <summary>
/// N-input XOR. Identical to OR but with an extra concave curve drawn just
/// outside the back edge -- the standard distinguishing mark.
/// </summary>
public sealed class XorGateUnit : Unit
{
    public XorGateUnit(Device device, UnitSpec spec) : base(device, spec)
    {
        int n = spec.InputPins.Length;
        Size = new Size(8, Math.Max(4, n * 2));
        BuildPins(spec);
    }

    protected override void BuildPins(UnitSpec spec) => BuildLeftInputsRightOutput(spec);

    public override Rectangle RoutingBounds
    {
        get
        {
            var unrotated = new Rectangle(
                Position.X, Position.Y - 1, Size.Width + 1, Size.Height + 2);

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
        int leftX = Position.X * p;
        int rightX = (Position.X + Size.Width) * p;
        int topY = Position.Y * p;
        int bottomY = (Position.Y + Size.Height) * p;

        int bodyLeftX = leftX + p;
        int bodyTipX = rightX - 2 * p;

        using var fill = new SolidBrush(ctx.FillColor);
        using var outline = new Pen(Selected ? ctx.SelectedColor : ctx.ForegroundColor, 1.2f);

        int shift = p / 2;
        using var path = OrGateUnit.BuildOrPath(bodyLeftX + shift, bodyTipX, topY, bottomY);
        g.FillPath(fill, path);
        g.DrawPath(outline, path);

        // Extra back curve, offset to the left of the body's concave edge.
        int mid = (topY + bottomY) / 2;
        int curveDepth = (bottomY - topY) / 3;
        using var backPath = new GraphicsPath();
        backPath.AddBezier(
            new Point(bodyLeftX, bottomY),
            new Point(bodyLeftX + curveDepth, mid + curveDepth / 2),
            new Point(bodyLeftX + curveDepth, mid - curveDepth / 2),
            new Point(bodyLeftX, topY));
        g.DrawPath(outline, backPath);

        using var pinPen = new Pen(ctx.PinColor, 1f);
        using var pinBrush = new SolidBrush(ctx.PinColor);
        foreach (var pin in Pins)
        {
            int px = (Position.X + pin.LocalPosition.X) * p;
            int py = (Position.Y + pin.LocalPosition.Y) * p;

            if (pin.LocalDirection == PinDirection.Left)
                g.DrawLine(pinPen, px, py, bodyLeftX + p, py);
            else
                g.DrawLine(pinPen, bodyTipX, py, px, py);

            g.FillEllipse(pinBrush, px - 2, py - 2, 4, 4);
        }
    }

    protected override void DrawLabels(Graphics g, RenderContext ctx)
    {
        int p = ctx.GridPitch;
        var b = Bounds;
        float midX = (b.X + b.Width / 2f) * p;
        float midY = (b.Y + b.Height / 2f) * p;
        DrawDesignatorAndPartNumberCentred(g, ctx, midX, midY);
    }
}