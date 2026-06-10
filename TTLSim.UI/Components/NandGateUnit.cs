using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using TTLSim.UI.Model;

namespace TTLSim.UI.Components;

/// <summary>
/// N-input NAND gate. Input count comes from spec.InputPins.Length, so the
/// same class handles 7400 (2-input), 7410 (3-input), 7420 (4-input), 7430
/// (8-input). Drawn as the classic D-shape body with an output bubble.
/// The body/bubble geometry is delegated to <see cref="GateGlyphs"/> so the
/// same shape is reused by the in-box gate glyphs drawn on DIP chip symbols.
/// </summary>
public sealed class NandGateUnit : Unit
{
    public NandGateUnit(Device device, UnitSpec spec) : base(device, spec)
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
            // Unrotated: inflate 1 cell top/bottom for pin stubs.
            var unrotated = new Rectangle(
                Position.X, Position.Y - 1, Size.Width + 1, Size.Height + 2);

            if (Rotation == Rotation.R0 || Rotation == Rotation.R180)
                return unrotated;

            // 90/270: swap dimensions around the unrotated centre.
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

        // Body occupies the area inset one cell on the left (pin stub) and two
        // cells on the right (one for the bubble, one for the output stub).
        int bodyLeftX = leftX + p;
        int bodyTipX = rightX - 2 * p;

        var body = RectangleF.FromLTRB(bodyLeftX, topY, bodyTipX, bottomY);

        using var fill = new SolidBrush(ctx.FillColor);
        using var outline = new Pen(Selected ? ctx.SelectedColor : ctx.ForegroundColor, 1.2f);

        GateGlyphs.DrawNand(g, outline, fill, body);

        float bubbleRightX = GateGlyphs.OutputTipX(body, inverting: true);

        using var pinPen = new Pen(ctx.PinColor, 1f);
        using var pinBrush = new SolidBrush(ctx.PinColor);
        foreach (var pin in Pins)
        {
            // Use LOCAL geometry, not WorldPosition -- the rotation
            // transform handles the rotation for us.
            int px = (Position.X + pin.LocalPosition.X) * p;
            int py = (Position.Y + pin.LocalPosition.Y) * p;

            if (pin.LocalDirection == PinDirection.Left)
                g.DrawLine(pinPen, px, py, bodyLeftX, py);
            else
                g.DrawLine(pinPen, bubbleRightX, py, px, py);

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