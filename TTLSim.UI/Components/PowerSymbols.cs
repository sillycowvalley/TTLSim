using System.Drawing;
using TTLSim.UI.Model;

namespace TTLSim.UI.Components;

/// <summary>VCC symbol -- T-bar above a downward stub, pin at the bottom.</summary>
public sealed class VccSymbol : SchematicItem
{
    public VccSymbol()
    {
        Size = new Size(4, 4);
        Label = "VCC";
        AddPin(new Pin("VCC", 0, new Point(2, 4), PinDirection.Down));
    }

    public override void Draw(Graphics g, RenderContext ctx)
    {
        var state = g.Save();
        ApplyRotationTransform(g, ctx);
        DrawShape(g, ctx);
        DrawLabel(g, ctx);
        g.Restore(state);
    }

    private void DrawShape(Graphics g, RenderContext ctx)
    {
        int p = ctx.GridPitch;
        int cx = (Position.X + 2) * p;
        int topY = Position.Y * p;
        int pinY = (Position.Y + 4) * p;

        using var pen = new Pen(Selected ? ctx.SelectedColor : ctx.ForegroundColor, 1.2f);
        using var brush = new SolidBrush(ctx.ForegroundColor);

        g.DrawLine(pen, cx - p, topY + p, cx + p, topY + p);
        g.DrawLine(pen, cx, topY + p, cx, pinY);
        g.FillEllipse(brush, cx - 2, pinY - 2, 4, 4);
    }

    private void DrawLabel(Graphics g, RenderContext ctx)
    {
        if (string.IsNullOrEmpty(Label)) return;
        int p = ctx.GridPitch;
        SizeF size = g.MeasureString(Label, ctx.LabelFont);
        float margin = p / 4f;

        // Drawn inside the body rotation transform. We want the text
        // to stay readable (never upside-down or right-to-left) in
        // screen space, so apply an additional text-relative rotation
        // that, combined with the body rotation, yields either 0 or
        // 270 net rotation:
        //   body  0  -> text +0       net 0   (horizontal)
        //   body 90  -> text +180     net 270 (reads bottom-up in screen)
        //   body 180 -> text +180     net 0   (horizontal, below bar)
        //   body 270 -> text +0       net 270 (reads bottom-up)
        int bodyRot = (int)Rotation;
        bool textFlip = bodyRot == 90 || bodyRot == 180;

        // Bar centre in local coordinates: (cx, topY + p).
        float cx = (Position.X + 2) * p;
        float barY = (Position.Y + 1) * p;

        using var brush = new SolidBrush(ctx.ForegroundColor);
        var savedState = g.Save();
        try
        {
            if (!textFlip)
            {
                // Normal orientation: text top-left such that its
                // horizontal centre lands on cx, its bottom edge sits
                // `margin` above the bar.
                float tx = cx - size.Width / 2f;
                float ty = barY - size.Height - margin;
                g.DrawString(Label, ctx.LabelFont, brush, tx, ty);
            }
            else
            {
                // Flipped 180°. Translate to the bar centre, rotate
                // 180°, then draw the text at the position that
                // (after the 180° rotation) lands the visible text
                // above the bar in screen space. Under a 180°
                // rotation around (cx, barY), the local point
                // (cx + dx, barY + dy) maps to (cx - dx, barY - dy).
                // So drawing at local (cx - size.Width/2, barY + margin)
                // lands the rotated text at screen
                // (cx + size.Width/2, barY - margin) — i.e. its visual
                // bottom-right ends up margin above the bar, and the
                // text reads correctly after the body+text rotations
                // compose.
                g.TranslateTransform(cx, barY);
                g.RotateTransform(180f);
                g.TranslateTransform(-cx, -barY);
                float tx = cx - size.Width / 2f;
                float ty = barY + margin;
                g.DrawString(Label, ctx.LabelFont, brush, tx, ty);
            }
        }
        finally
        {
            g.Restore(savedState);
        }
    }

    private void ApplyRotationTransform(Graphics g, RenderContext ctx)
    {
        if (Rotation == Rotation.R0) return;
        int p = ctx.GridPitch;
        float pivotX = Pivot.X * p;
        float pivotY = Pivot.Y * p;
        g.TranslateTransform(pivotX, pivotY);
        g.RotateTransform((float)(int)Rotation);
        g.TranslateTransform(-pivotX, -pivotY);
    }
}

/// <summary>GND symbol -- three horizontal bars decreasing in width, pin at the top.</summary>
public sealed class GndSymbol : SchematicItem
{
    public GndSymbol()
    {
        Size = new Size(4, 4);
        Label = "";
        AddPin(new Pin("GND", 0, new Point(2, 0), PinDirection.Up));
    }

    public override void Draw(Graphics g, RenderContext ctx)
    {
        var state = g.Save();
        ApplyRotationTransform(g, ctx);
        DrawShape(g, ctx);
        g.Restore(state);

        DrawLabel(g, ctx);
    }

    private void DrawShape(Graphics g, RenderContext ctx)
    {
        int p = ctx.GridPitch;
        int cx = (Position.X + 2) * p;
        int pinY = Position.Y * p;
        int barY = (Position.Y + 2) * p;

        using var pen = new Pen(Selected ? ctx.SelectedColor : ctx.ForegroundColor, 1.2f);
        using var brush = new SolidBrush(ctx.ForegroundColor);

        g.DrawLine(pen, cx, pinY, cx, barY);
        g.DrawLine(pen, cx - p * 3 / 2, barY, cx + p * 3 / 2, barY);
        g.DrawLine(pen, cx - p, barY + 3, cx + p, barY + 3);
        g.DrawLine(pen, cx - p / 2, barY + 6, cx + p / 2, barY + 6);
        g.FillEllipse(brush, cx - 2, pinY - 2, 4, 4);
    }

    private void DrawLabel(Graphics g, RenderContext ctx)
    {
        if (string.IsNullOrEmpty(Label)) return;
        int p = ctx.GridPitch;
        var b = Bounds;
        using var brush = new SolidBrush(ctx.ForegroundColor);
        g.DrawString(Label, ctx.LabelFont, brush,
            (b.X + b.Width) * p, (b.Y + b.Height / 2) * p);
    }

    private void ApplyRotationTransform(Graphics g, RenderContext ctx)
    {
        if (Rotation == Rotation.R0) return;
        int p = ctx.GridPitch;
        float pivotX = Pivot.X * p;
        float pivotY = Pivot.Y * p;
        g.TranslateTransform(pivotX, pivotY);
        g.RotateTransform((float)(int)Rotation);
        g.TranslateTransform(-pivotX, -pivotY);
    }
}