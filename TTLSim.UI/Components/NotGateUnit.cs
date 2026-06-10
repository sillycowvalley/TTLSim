using System.Drawing;
using TTLSim.UI.Model;

namespace TTLSim.UI.Components;

/// <summary>
/// Inverter. Triangle pointing right, output bubble. Always one input.
/// Smaller footprint than the multi-input gates: 6 wide x 4 tall.
/// </summary>
public sealed class NotGateUnit : Unit
{
    public NotGateUnit(Device device, UnitSpec spec) : base(device, spec)
    {
        Size = new Size(6, 4);
        BuildPins(spec);
    }

    protected override void BuildPins(UnitSpec spec)
    {
        AddPin(new Pin($"{spec.InputPins[0]}", spec.InputPins[0],
            new Point(0, Size.Height / 2), PinDirection.Left));
        AddPin(new Pin($"{spec.OutputPin}", spec.OutputPin,
            new Point(Size.Width, Size.Height / 2), PinDirection.Right));
    }

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
        int midY = (topY + bottomY) / 2;

        int bodyLeftX = leftX + p;
        int bodyTipX = rightX - 2 * p;
        int bubbleDiameter = p;
        int bubbleLeftX = bodyTipX;
        int bubbleRightX = bubbleLeftX + bubbleDiameter;

        using var fill = new SolidBrush(ctx.FillColor);
        using var outline = new Pen(Selected ? ctx.SelectedColor : ctx.ForegroundColor, 1.2f);

        Point[] triangle =
        {
            new(bodyLeftX, topY),
            new(bodyLeftX, bottomY),
            new(bodyTipX,  midY)
        };
        g.FillPolygon(fill, triangle);
        g.DrawPolygon(outline, triangle);

        if (IsSchmitt)
        {
            DrawSchmittSymbol(g, outline, bodyLeftX, bodyTipX, midY, p);
        }

        g.FillEllipse(fill,
            bubbleLeftX, midY - bubbleDiameter / 2,
            bubbleDiameter, bubbleDiameter);
        g.DrawEllipse(outline,
            bubbleLeftX, midY - bubbleDiameter / 2,
            bubbleDiameter, bubbleDiameter);

        using var pinPen = new Pen(ctx.PinColor, 1f);
        using var pinBrush = new SolidBrush(ctx.PinColor);
        foreach (var pin in Pins)
        {
            int px = (Position.X + pin.LocalPosition.X) * p;
            int py = (Position.Y + pin.LocalPosition.Y) * p;

            if (pin.LocalDirection == PinDirection.Left)
                g.DrawLine(pinPen, px, py, bodyLeftX, py);
            else
                g.DrawLine(pinPen, bubbleRightX, py, px, py);

            g.FillEllipse(pinBrush, px - 2, py - 2, 4, 4);
        }
    }

    /// <summary>
    /// Draw the IEEE Std 91 hysteresis glyph inside the inverter triangle.
    /// Two parallel back-leaning diagonals connected by horizontal stubs
    /// that overhang at the outer ends -- an open Z. Drawn with a finer
    /// pen than the body outline so it reads as inscribed detail.
    /// </summary>
    private static void DrawSchmittSymbol(Graphics g, Pen sourcePen,
        int bodyLeftX, int bodyTipX, int midY, int p)
    {
        float glyphW = p * 0.9f;
        float glyphH = p * 0.55f;
        float cx = bodyLeftX + (bodyTipX - bodyLeftX) * 0.40f;
        float cy = midY;

        float lean = glyphW * 0.35f;
        float halfW = glyphW / 2f;
        float halfH = glyphH / 2f;
        float stub = lean * 0.7f;

        PointF leftDiagBot = new(cx - halfW + lean, cy + halfH);
        PointF leftDiagTop = new(cx - halfW, cy - halfH);
        PointF rightDiagBot = new(cx + halfW, cy + halfH);
        PointF rightDiagTop = new(cx + halfW - lean, cy - halfH);

        PointF botRight = new(rightDiagBot.X + stub, rightDiagBot.Y);
        PointF topLeft = new(leftDiagTop.X - stub, leftDiagTop.Y);

        float strokeWidth = System.Math.Max(0.75f, sourcePen.Width * 0.65f);
        using var pen = new Pen(sourcePen.Color, strokeWidth)
        {
            StartCap = System.Drawing.Drawing2D.LineCap.Square,
            EndCap = System.Drawing.Drawing2D.LineCap.Round,
            LineJoin = System.Drawing.Drawing2D.LineJoin.Round
        };

        // Lower edge: bottom-stub end -> bottom-left corner -> up left diagonal.
        PointF[] lowerEdge = { botRight, leftDiagBot, leftDiagTop };

        // Upper edge: top-stub end -> top-right corner -> down right diagonal.
        PointF[] upperEdge = { topLeft, rightDiagTop, rightDiagBot };

        g.DrawLines(pen, lowerEdge);
        g.DrawLines(pen, upperEdge);
    }

    protected override void DrawLabels(Graphics g, RenderContext ctx)
    {
        int p = ctx.GridPitch;
        var b = Bounds;
        float midX = (b.X + b.Width / 2f) * p;
        float topY = b.Y * p;
        float labelHeight = MeasureGateLabelHeight(g, ctx);
        DrawDesignatorAndPartNumber(g, ctx, midX, topY - labelHeight - ctx.BodyGap);
    }
}
