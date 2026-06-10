using System;
using System.Drawing;
using TTLSim.UI.Model;

namespace TTLSim.UI.Components;

/// <summary>
/// Polarised electrolytic capacitor -- one straight plate (positive),
/// one curved plate (negative), with a "+" glyph at the positive pin.
/// 4 cells wide x 2 tall, modelled on <see cref="CapacitorUnit"/> for
/// body geometry and on <see cref="DiodeUnit"/> for the polarity
/// convention: pin 1 is the positive terminal (anode-style), pin 2 is
/// the negative.
///
/// The simulator treats this exactly like the non-polarised
/// <see cref="CapacitorUnit"/> -- a no-op for digital simulation. The
/// polarity matters only for the schematic capture and the resulting
/// PCB layout: getting the polarity reversed on a real electrolytic
/// causes the cap to fail catastrophically, so the symbol distinguishes
/// it clearly from the non-polarised case.
///
/// Designator prefix is "C" (shared with <see cref="CapacitorUnit"/>),
/// not "CP" or "CE" -- industry convention.
/// </summary>
public sealed class PolarizedCapacitorUnit : Unit
{
    public PolarizedCapacitorUnit(Device device, UnitSpec spec) : base(device, spec)
    {
        Size = new Size(4, 2);
        BuildPins(spec);
    }

    protected override void BuildPins(UnitSpec spec)
    {
        // Pin 1 = "+", the positive terminal. Convention matches DiodeUnit
        // (pin 1 = polarised pin) and EasyEDA's library symbol (the "+"
        // glyph sits at pin 1's end). Same physical orientation rule for
        // the user as a diode: pin 1 connects to higher potential.
        AddPin(new Pin("+", 1, new Point(0, 1), PinDirection.Left));
        AddPin(new Pin("-", 2, new Point(Size.Width, 1), PinDirection.Right));
    }

    public override Rectangle RoutingBounds
    {
        get
        {
            // Same inflate as CapacitorUnit -- the body sits centred at
            // y=1 and the symbol's tallest features (plates, "+" glyph)
            // extend ~1 cell above and below the lead line.
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

        // Leads from pin to plate. Symmetric: each lead runs from its
        // pin to the inner edge of its plate at midX ± plateGap. The
        // arc's vertex sits at midX + plateGap (the right lead's inner
        // edge), so the visible gap between the positive plate and the
        // arc vertex is 2*plateGap = one full grid cell, matching the
        // EDA reference where plates sit ~4 symbol units apart.
        using var leadPen = new Pen(Selected ? ctx.SelectedColor : ctx.ForegroundColor, 1.2f);
        int p1x = (Position.X + Pins[0].LocalPosition.X) * p;
        int p1y = (Position.Y + Pins[0].LocalPosition.Y) * p;
        int p2x = (Position.X + Pins[1].LocalPosition.X) * p;
        int p2y = (Position.Y + Pins[1].LocalPosition.Y) * p;
        g.DrawLine(leadPen, p1x, p1y, midX - plateGap, midY);
        g.DrawLine(leadPen, midX + plateGap, midY, p2x, p2y);

        using var pen = new Pen(Selected ? ctx.SelectedColor : ctx.ForegroundColor, 1.4f);

        // Positive plate: straight vertical line, left of the gap.
        g.DrawLine(pen, midX - plateGap, midY - plateHeight,
                        midX - plateGap, midY + plateHeight);

        // Negative plate: arc with concave side facing the positive
        // plate, like a ")" shape next to a "|". The EDA source's ARC
        // has endpoints at (~4.5, ±7) and a midpoint at (~2, 0) -- the
        // midpoint sits closer to the positive plate than the endpoints,
        // so the arc bulges AWAY from the positive plate at top/bottom
        // and bows back toward it in the middle. This is the standard
        // polarised-cap symbol convention.
        //
        // Implementation: draw the LEFT half of an ellipse so the curve
        // opens leftward. The rect's left edge sits at midX + plateGap
        // (the right lead's inner edge -- so the lead meets the arc
        // vertex cleanly with no gap), and the rect's right edge sits
        // arcDepth*2 further right. arcDepth controls how visibly the
        // curve bows; large arcDepth = deep scoop, small arcDepth = a
        // nearly-flat line.
        //
        // arcDepth is clamped to a minimum of 1 pixel because at very
        // low zoom (small p) the float-to-int truncation can give 0,
        // and Rectangle(...,0,...) makes GDI+ throw "Parameter is not
        // valid".
        int arcDepth = Math.Max(1, (int)(p * 0.5f));
        int arcHeight = Math.Max(1, plateHeight * 2);
        var arcRect = new Rectangle(
            midX + plateGap, midY - plateHeight,
            arcDepth * 2, arcHeight);
        if (arcRect.Width > 0 && arcRect.Height > 0)
        {
            g.DrawArc(pen, arcRect, 90f, 180f);
        }
        else
        {
            // Degenerate: draw a straight line where the arc would have
            // been so the symbol still reads as a polarised cap. Drawn
            // at midX + plateGap -- the arc vertex position -- so the
            // negative-pin lead still meets the plate cleanly.
            g.DrawLine(pen, midX + plateGap, midY - plateHeight,
                            midX + plateGap, midY + plateHeight);
        }

        // "+" glyph to the UPPER-LEFT of the body, beside the positive
        // plate (not above the body). The EDA source places it at symbol
        // coords (-8, +3) -- to the left of the positive plate (which is
        // at X=-2), level with the upper portion of the body. Same
        // relative positioning here: left of the positive plate by about
        // half a cell, vertically just above midY by about a third of the
        // plate height. Generously sized so it's clearly visible.
        int plusSize = Math.Max(2, (int)(p * 0.35f));
        int plusX = midX - plateGap - (int)(p * 0.7f);
        int plusY = midY - (int)(plateHeight * 0.55f);
        using var plusPen = new Pen(Selected ? ctx.SelectedColor : ctx.ForegroundColor, 1.4f);
        g.DrawLine(plusPen, plusX - plusSize, plusY, plusX + plusSize, plusY);
        g.DrawLine(plusPen, plusX, plusY - plusSize, plusX, plusY + plusSize);

        // Pin dots for both terminals (matches every other passive).
        using var pinBrush = new SolidBrush(ctx.PinColor);
        g.FillEllipse(pinBrush, p1x - 2, p1y - 2, 4, 4);
        g.FillEllipse(pinBrush, p2x - 2, p2y - 2, 4, 4);
    }

    protected override void DrawLabels(Graphics g, RenderContext ctx)
    {
        // Identical to CapacitorUnit: the plate-and-arc body extends past
        // the bounding box by ~0.6p above and below, so labels need to
        // clear that overhang.
        int p = ctx.GridPitch;
        var b = Bounds;
        float midX = (b.X + b.Width / 2f) * p;
        float midY = (b.Y + b.Height / 2f) * p;

        float plateOverhang = p * 0.6f;
        float bodyTopY = b.Y * p - plateOverhang;
        float bodyBottomY = (b.Y + b.Height) * p + plateOverhang;
        float bodyLeftX = b.X * p - plateOverhang;
        DrawPassiveLabels(g, ctx, midX, midY, bodyTopY, bodyBottomY, bodyLeftX);
    }
}