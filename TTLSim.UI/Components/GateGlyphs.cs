using System;
using System.Drawing;
using System.Drawing.Drawing2D;

namespace TTLSim.UI.Components;

/// <summary>
/// Draws standard logic-gate body outlines into an arbitrary target rectangle.
/// Shared by the standalone gate Units (e.g. <c>NandGateUnit</c>) and by the
/// cosmetic chip decorators that draw miniature gate symbols inside a DIP box
/// (e.g. the 7400's <c>Decorate</c> helper). Centralising the path geometry
/// here keeps the D-shape / arc maths in one place instead of duplicated
/// between the full-size gate Unit and the in-box glyph.
///
/// The <paramref name="body"/> rectangle passed to each method is the gate
/// BODY only (the D-shape), excluding any output bubble and excluding the pin
/// stubs. The output bubble (for inverting gates) is drawn just to the right
/// of the body and its diameter is derived from the body height, so callers
/// should leave a little clearance to the right of <paramref name="body"/> for
/// it. <see cref="BubbleDiameter"/> exposes that size so callers can lay out
/// connecting traces to the true output point.
/// </summary>
public static class GateGlyphs
{
    /// <summary>
    /// Output-bubble diameter for a gate of the given body height. The bubble
    /// is a small circle; one quarter of the body height reads well at both
    /// full size and miniature in-box size.
    /// </summary>
    public static float BubbleDiameter(float bodyHeight) =>
        Math.Max(2f, bodyHeight * 0.25f);

    /// <summary>
    /// The x of the true output point (right edge of the bubble for inverting
    /// gates, right edge of the body for non-inverting gates). Lets a caller
    /// run a connecting trace from the gate output to the chip's output pin.
    /// </summary>
    public static float OutputTipX(RectangleF body, bool inverting) =>
        inverting ? body.Right + BubbleDiameter(body.Height) : body.Right;

    // ---------------------------------------------------------------- NAND / AND

    /// <summary>Draw an AND-family body: flat left edge, semicircular right.
    /// With <paramref name="inverting"/> true, append the output bubble (NAND).</summary>
    public static void DrawAnd(Graphics g, Pen outline, Brush fill, RectangleF body, bool inverting)
    {
        using var path = AndPath(body);
        g.FillPath(fill, path);
        g.DrawPath(outline, path);
        if (inverting) DrawBubble(g, outline, fill, body);
    }

    /// <summary>NAND = AND body + output bubble.</summary>
    public static void DrawNand(Graphics g, Pen outline, Brush fill, RectangleF body) =>
        DrawAnd(g, outline, fill, body, inverting: true);

    // ---------------------------------------------------------------- OR / NOR

    /// <summary>Draw an OR-family body: concave left edge, pointed right.
    /// With <paramref name="inverting"/> true, append the output bubble (NOR).</summary>
    public static void DrawOr(Graphics g, Pen outline, Brush fill, RectangleF body, bool inverting)
    {
        using var path = OrPath(body, shieldGap: 0f);
        g.FillPath(fill, path);
        g.DrawPath(outline, path);
        if (inverting) DrawBubble(g, outline, fill, body);
    }

    /// <summary>NOR = OR body + output bubble.</summary>
    public static void DrawNor(Graphics g, Pen outline, Brush fill, RectangleF body) =>
        DrawOr(g, outline, fill, body, inverting: true);

    // ---------------------------------------------------------------- XOR / XNOR

    /// <summary>XOR = OR body + extra concave shield arc to the left of it.
    /// With <paramref name="inverting"/> true, append the output bubble (XNOR).</summary>
    public static void DrawXor(Graphics g, Pen outline, Brush fill, RectangleF body, bool inverting)
    {
        float gap = body.Width * 0.12f;
        using var path = OrPath(body, shieldGap: gap);
        g.FillPath(fill, path);
        g.DrawPath(outline, path);

        // The extra shield arc sits a small gap to the left of the body's
        // concave left edge, mirroring its curvature.
        using var shield = new GraphicsPath();
        AddConcaveLeftArc(shield, new RectangleF(body.X - gap, body.Y, body.Width, body.Height));
        g.DrawPath(outline, shield);

        if (inverting) DrawBubble(g, outline, fill, body);
    }

    // ---------------------------------------------------------------- NOT

    /// <summary>Inverter: triangle pointing right with an output bubble.</summary>
    public static void DrawNot(Graphics g, Pen outline, Brush fill, RectangleF body)
    {
        using var path = new GraphicsPath();
        path.AddPolygon(new[]
        {
            new PointF(body.Left,  body.Top),
            new PointF(body.Left,  body.Bottom),
            new PointF(body.Right, body.Y + body.Height / 2f),
        });
        g.FillPath(fill, path);
        g.DrawPath(outline, path);
        DrawBubble(g, outline, fill, body);
    }

    /// <summary>
    /// Non-inverting buffer: the same right-pointing triangle as
    /// <see cref="DrawNot"/> but WITHOUT the output bubble. Used by the octal
    /// bus buffers ('244 / '245 / '541) and any other non-inverting line
    /// driver. The output point is the triangle apex (body.Right), so callers
    /// should treat this as non-inverting when laying out the output trace.
    /// </summary>
    public static void DrawBuffer(Graphics g, Pen outline, Brush fill, RectangleF body)
    {
        using var path = new GraphicsPath();
        path.AddPolygon(new[]
        {
            new PointF(body.Left,  body.Top),
            new PointF(body.Left,  body.Bottom),
            new PointF(body.Right, body.Y + body.Height / 2f),
        });
        g.FillPath(fill, path);
        g.DrawPath(outline, path);
    }

    /// <summary>
    /// Bidirectional transceiver glyph: two triangles back-to-back forming a
    /// horizontal diamond. The left apex sits at <c>body.Left</c> (mid-height)
    /// and the right apex at <c>body.Right</c>; they share a vertical base down
    /// the centre, which is drawn as a divider so the symbol reads as two
    /// triangles rather than a plain diamond. Used for the 74x245-style bus
    /// transceiver where data flows both ways (A &lt;-&gt; B).
    /// </summary>
    public static void DrawBidirectional(Graphics g, Pen outline, Brush fill, RectangleF body)
    {
        float midY = body.Y + body.Height / 2f;
        float cx = body.X + body.Width / 2f;
        using var path = new GraphicsPath();
        path.AddPolygon(new[]
        {
            new PointF(body.Left,  midY),         // left apex
            new PointF(cx,         body.Top),     // top of shared base
            new PointF(body.Right, midY),         // right apex
            new PointF(cx,         body.Bottom),  // bottom of shared base
        });
        g.FillPath(fill, path);
        g.DrawPath(outline, path);
        g.DrawLine(outline, cx, body.Top, cx, body.Bottom);  // shared base divider
    }
    /// with a hysteresis glyph inside the triangle. The glyph is asymmetric
    /// (top arm extends one way, bottom the other), so when the caller has
    /// applied a horizontal flip to the Graphics frame the glyph must be
    /// counter-flipped here to read the same way on screen. The
    /// <paramref name="xFlipped"/> flag carries that information from the
    /// caller, since inspecting the Graphics transform doesn't work when the
    /// chip itself is rotated (the X-scale element mixes with rotation).
    /// </summary>
    public static void DrawSchmittNot(Graphics g, Pen outline, Brush fill, RectangleF body, bool xFlipped)
    {
        DrawNot(g, outline, fill, body);

        float midY = body.Y + body.Height / 2f;
        float armV = body.Height * 0.15f;
        float riserH = body.Width * 0.15f;
        float xRiser = body.X + body.Width * 0.40f - riserH / 2f;
        float yTop = midY - armV;
        float yBot = midY + armV;

        GraphicsState? state = null;
        if (xFlipped)
        {
            float glyphCx = xRiser + riserH / 2f;
            state = g.Save();
            g.TranslateTransform(glyphCx, 0f);
            g.ScaleTransform(-1f, 1f);
            g.TranslateTransform(-glyphCx, 0f);
        }

        g.DrawLines(outline, new[]
        {
            new PointF(xRiser,           yBot),
            new PointF(xRiser,           midY),
            new PointF(xRiser + riserH,  midY),
            new PointF(xRiser + riserH,  yTop),
        });

        if (state is not null) g.Restore(state);
    }

    // ---------------------------------------------------------------- path builders

    /// <summary>
    /// AND body path: straight left/top/bottom, semicircular right cap. When
    /// the body is wider than half its height the arc caps only the right
    /// portion and straight top/bottom segments fill the rest (matches the
    /// existing NandGateUnit behaviour for many-input gates).
    /// </summary>
    private static GraphicsPath AndPath(RectangleF body)
    {
        float midY = body.Y + body.Height / 2f;
        float arcDiameter = Math.Min(body.Height, body.Width * 2f);
        float arcLeft = body.Right - arcDiameter;
        float arcTop = midY - arcDiameter / 2f;

        var path = new GraphicsPath();
        path.AddLine(body.Left, body.Top, body.Left, body.Bottom);                 // left edge
        path.AddLine(body.Left, body.Bottom, arcLeft + arcDiameter / 2f, body.Bottom); // bottom flat
        path.AddArc(arcLeft, arcTop, arcDiameter, arcDiameter, 90, -180);          // right cap
        path.AddLine(arcLeft + arcDiameter / 2f, body.Top, body.Left, body.Top);   // top flat
        path.CloseFigure();
        return path;
    }

    /// <summary>
    /// OR body path: concave left edge, two convex curves meeting at a point
    /// on the right. <paramref name="shieldGap"/> is unused for the body shape
    /// itself (it only affects where the XOR shield arc is drawn) and is
    /// accepted so XOR and OR share this builder.
    /// </summary>
    private static GraphicsPath OrPath(RectangleF body, float shieldGap)
    {
        float midY = body.Y + body.Height / 2f;
        var path = new GraphicsPath();

        // Top curve: from the top-left, bow out to the right point.
        path.AddBezier(
            new PointF(body.Left, body.Top),
            new PointF(body.Left + body.Width * 0.5f, body.Top),
            new PointF(body.Right - body.Width * 0.15f, body.Top + body.Height * 0.15f),
            new PointF(body.Right, midY));
        // Bottom curve: from the right point back to the bottom-left.
        path.AddBezier(
            new PointF(body.Right, midY),
            new PointF(body.Right - body.Width * 0.15f, body.Bottom - body.Height * 0.15f),
            new PointF(body.Left + body.Width * 0.5f, body.Bottom),
            new PointF(body.Left, body.Bottom));
        // Concave left edge closing the figure (bottom-left up to top-left).
        AddConcaveLeftArc(path, body);
        path.CloseFigure();
        return path;
    }

    /// <summary>
    /// Append the OR/XOR concave left edge: a shallow arc bowing rightward,
    /// drawn from the bottom-left corner up to the top-left corner.
    /// </summary>
    private static void AddConcaveLeftArc(GraphicsPath path, RectangleF body)
    {
        float bow = body.Width * 0.18f;
        path.AddBezier(
            new PointF(body.Left, body.Bottom),
            new PointF(body.Left + bow, body.Y + body.Height * 0.65f),
            new PointF(body.Left + bow, body.Y + body.Height * 0.35f),
            new PointF(body.Left, body.Top));
    }

    private static void DrawBubble(Graphics g, Pen outline, Brush fill, RectangleF body)
    {
        float d = BubbleDiameter(body.Height);
        float midY = body.Y + body.Height / 2f;
        var rect = new RectangleF(body.Right, midY - d / 2f, d, d);
        g.FillEllipse(fill, rect);
        g.DrawEllipse(outline, rect);
    }
}