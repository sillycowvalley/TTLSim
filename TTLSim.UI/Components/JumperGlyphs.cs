using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using TTLSim.UI.Model;

namespace TTLSim.UI.Components;

/// <summary>
/// Shared drawing primitives for the jumper renderings of <see cref="SwitchUnit"/>
/// (2-pin) and <see cref="SpdtSwitchUnit"/> (3-pin). A jumper reads like a
/// pin-header: a rounded shell with square posts at each pin, plus a fat
/// "shunt" conductor bridging the posts that are electrically joined -- the
/// closed (SPST) / selected-throw (SPDT) indicator. Kept here so both units
/// compose the same look from one set of helpers instead of duplicating it.
///
/// All coordinates are screen pixels in the unit's UNROTATED frame; the base
/// Unit.Draw has already applied any rotation transform before DrawShape runs.
/// </summary>
internal static class JumperGlyphs
{
    /// <summary>Rounded body outline enclosing the posts -- the header-style shell.</summary>
    public static void DrawBody(Graphics g, RenderContext ctx, bool selected,
        int left, int top, int right, int bottom)
    {
        float r = ctx.GridPitch * 0.6f;
        using var pen = new Pen(selected ? ctx.SelectedColor : ctx.ForegroundColor, 1.2f);
        using var path = Rounded(left, top, right - left, bottom - top, r);
        g.DrawPath(pen, path);
    }

    /// <summary>Square header-style post centred at (cx, cy).</summary>
    public static void DrawPost(Graphics g, RenderContext ctx, int cx, int cy, bool selected)
    {
        int half = Math.Max(2, (int)(ctx.GridPitch * 0.5f));
        var rect = new Rectangle(cx - half, cy - half, half * 2, half * 2);
        using var fill = new SolidBrush(ctx.FillColor);
        using var pen = new Pen(selected ? ctx.SelectedColor : ctx.ForegroundColor, 1.2f);
        g.FillRectangle(fill, rect);
        g.DrawRectangle(pen, rect.X, rect.Y, rect.Width, rect.Height);
    }

    /// <summary>The shunt: a fat rounded conductor bridging two joined posts.</summary>
    public static void DrawShunt(Graphics g, RenderContext ctx, int ax, int ay, int bx, int by)
    {
        using var pen = new Pen(ctx.SelectedColor, Math.Max(3f, ctx.GridPitch * 0.6f))
        {
            StartCap = LineCap.Round,
            EndCap = LineCap.Round
        };
        g.DrawLine(pen, ax, ay, bx, by);
    }

    /// <summary>Lead from a pin endpoint to its post.</summary>
    public static void DrawLead(Graphics g, RenderContext ctx, bool selected,
        int x0, int y0, int x1, int y1)
    {
        using var pen = new Pen(selected ? ctx.SelectedColor : ctx.ForegroundColor, 1.2f);
        g.DrawLine(pen, x0, y0, x1, y1);
    }

    /// <summary>Pin terminal dot at a pin endpoint.</summary>
    public static void DrawTerminal(Graphics g, RenderContext ctx, int x, int y)
    {
        using var brush = new SolidBrush(ctx.PinColor);
        g.FillEllipse(brush, x - 2, y - 2, 4, 4);
    }

    private static GraphicsPath Rounded(float x, float y, float w, float h, float radius)
    {
        float r = Math.Min(radius, Math.Min(w, h) / 2f);
        float d = r * 2f;
        var path = new GraphicsPath();
        path.AddArc(x, y, d, d, 180, 90);
        path.AddArc(x + w - d, y, d, d, 270, 90);
        path.AddArc(x + w - d, y + h - d, d, d, 0, 90);
        path.AddArc(x, y + h - d, d, d, 90, 90);
        path.CloseFigure();
        return path;
    }
}