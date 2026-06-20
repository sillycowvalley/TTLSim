using Microsoft.Extensions.Logging;
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Linq;
using System.Windows.Forms;
using TTLSim.Chips.Displays;
using TTLSim.Core;
using TTLSim.UI.Commands;
using TTLSim.UI.Components;
using TTLSim.UI.Logging;
using TTLSim.UI.Model;
using TTLSim.UI.Persistence;

namespace TTLSim.UI.View;

public sealed partial class SchematicCanvas
{
    // ---------------------------------------------------------------- painting

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.InterpolationMode = InterpolationMode.HighQualityBicubic;
        g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.ClearTypeGridFit;

        // Sim mode: subtle warm-cream background tint to distinguish from edit mode.
        if (SignalProvider is not null)
        {
            using var bg = new SolidBrush(Color.FromArgb(0xFA, 0xFA, 0xF5));
            g.FillRectangle(bg, 0, 0, ClientSize.Width, ClientSize.Height);
        }

        DrawGrid(g);

        g.TranslateTransform(PanOffset.X, PanOffset.Y);
        g.ScaleTransform(Zoom, Zoom);

        var ctx = new RenderContext
        {
            GridPitch = GridPitch,
            Zoom = Zoom,
            SegmentProvider = DisplayBindings is null ? null : (item =>
            {
                if (DisplayBindings.TryGetValue(item, out var disp))
                    return (disp.Segments.ToArray(), disp.Dp);
                return null;
            }),
            LedStateProvider = PinSignalProvider is null ? null : (item =>
            {
                // Lit when anode (pin 1) is driven high and cathode (pin 2) low.
                return PinSignalProvider(item, 1) == Signal.High
                    && PinSignalProvider(item, 2) == Signal.Low;
            }),
            SignalStateProvider = PinSignalProvider
        };

        // Cosmetic background items (rectangles, text labels) render behind
        // everything else: before wires, links, junctions, and components.
        // They are skipped in the main item loop below so each paints exactly
        // once. They sit on top of the grid (drawn above) but behind all
        // schematic content.
        foreach (var item in Schematic.ActiveItems)
            if (item is IBackgroundItem)
                item.Draw(g, ctx);

#if DEBUG
        // Debug: visualise routing bounds in pale pink.
        using (var routingBrush = new SolidBrush(Color.FromArgb(60, 255, 180, 200)))
        {
            int p = GridPitch;
            foreach (var item in Schematic.ActiveItems)
            {
                var rb = item.RoutingBounds;
                g.FillRectangle(routingBrush,
                    rb.X * p, rb.Y * p, rb.Width * p, rb.Height * p);
            }
        }

        foreach (var connection in Schematic.ActiveConnections)
            DrawConnector(g, connection);
#endif

        foreach (var connection in Schematic.ActiveConnections)
            DrawWire(g, connection);

        foreach (var link in Schematic.ActiveLinks)
            DrawHeaderLink(g, link);

        DrawJunctions(g);

        DrawCoincidentCornerWarnings(g);

        foreach (var item in Schematic.ActiveItems)
        {
            if (item is IBackgroundItem) continue;   // drawn in the background pass above
            item.Draw(g, ctx);
        }

        if (wireStartPin != null)
        {
            int p = GridPitch;
            var from = wireStartPin.WorldPosition;
            using var dashPen = new Pen(Color.FromArgb(180, 80, 80, 200), 1.2f)
            { DashStyle = DashStyle.Dash };
            g.DrawLine(dashPen,
                from.X * p, from.Y * p,
                wirePreviewEnd.X * p, wirePreviewEnd.Y * p);
        }

        if (headerLinkStart != null)
        {
            int p = GridPitch;
            // Anchor the preview at the start header's body centre.
            var bnd = headerLinkStart.Bounds;
            float ax = (bnd.X + bnd.Width / 2f) * p;
            float ay = (bnd.Y + bnd.Height / 2f) * p;
            using var dashPen = new Pen(Color.FromArgb(180, 120, 90, 170), 1.2f)
            { DashStyle = DashStyle.Dash };
            g.DrawLine(dashPen, ax, ay,
                headerLinkPreviewEnd.X * p, headerLinkPreviewEnd.Y * p);
        }

        if (marqueeing)
        {
            int p = GridPitch;
            int minX = Math.Min(marqueeStart.X, marqueeEnd.X) * p;
            int maxX = Math.Max(marqueeStart.X, marqueeEnd.X) * p;
            int minY = Math.Min(marqueeStart.Y, marqueeEnd.Y) * p;
            int maxY = Math.Max(marqueeStart.Y, marqueeEnd.Y) * p;
            int w = maxX - minX;
            int h = maxY - minY;

            bool overlapMode = marqueeEnd.X < marqueeStart.X;
            // Containment (L->R): solid stroke, faint blue fill.
            // Overlap     (R->L): dashed stroke, faint green fill.
            Color stroke = overlapMode
                ? Color.FromArgb(220, 60, 140, 60)
                : Color.FromArgb(220, 60, 100, 200);
            Color fill = overlapMode
                ? Color.FromArgb(40, 60, 140, 60)
                : Color.FromArgb(40, 60, 100, 200);

            using var fillBrush = new SolidBrush(fill);
            using var pen = new Pen(stroke, 1f);
            if (overlapMode) pen.DashStyle = DashStyle.Dash;
            if (w > 0 || h > 0)
            {
                g.FillRectangle(fillBrush, minX, minY, w, h);
                g.DrawRectangle(pen, minX, minY, w, h);
            }
        }
    }

    /// <summary>
    /// Debug overlay: dotted light-blue straight line directly between the
    /// two pins of a connection. Draws regardless of the router; useful for
    /// confirming what the model thinks is connected vs. what the router
    /// produced.
    /// </summary>
#if DEBUG
    private void DrawConnector(Graphics g, Connection connection)
    {
        int p = GridPitch;
        var a = connection.A.WorldPosition;
        var b = connection.B.WorldPosition;

        Color color = connection.Selected
            ? Color.FromArgb(220, 40, 90, 200)
            : Color.FromArgb(200, 130, 170, 230);

        using var pen = new Pen(color, 1.0f) { DashStyle = DashStyle.Dot };
        g.DrawLine(pen, a.X * p, a.Y * p, b.X * p, b.Y * p);
    }
#endif
    /// <summary>
    /// Draw the routed polyline for a single connection in the wire colour
    /// chosen on the connection itself. Selected wires render in a fixed
    /// blue regardless of their assigned colour so the selection stays
    /// visible against any palette choice.
    /// </summary>
    private void DrawWire(Graphics g, Connection connection)
    {
        if (!Routes.Polylines.TryGetValue(connection, out var pts) || pts.Count < 2)
            return;

        int p = GridPitch;
        var screen = new PointF[pts.Count];
        for (int i = 0; i < pts.Count; i++)
            screen[i] = new PointF(pts[i].X * p, pts[i].Y * p);

        Color color;
        float thickness = 1.4f;

        if (SignalProvider is { } provider)
        {
            Signal? state = provider(connection);
            color = state.HasValue ? SignalColors.For(state.Value) : SignalColors.Unknown;
        }
        else
        {
            color = connection.Color.ToColor();
        }

        if (connection.Selected)
            color = RenderContext.DefaultSelected;

        using var pen = new Pen(color, thickness);
        g.DrawLines(pen, screen);
    }

    /// <summary>
    /// Yield the strand endpoints (in grid units) for a header link: pin i of
    /// A to pin i of B, for every pin both headers share. Strands always
    /// terminate on the true pin endpoints, so the drawing is honestly 1-to-1
    /// (it may cross when the headers face each other). The link's Reversed
    /// flag does not affect these endpoints.
    /// </summary>
    private static IEnumerable<(Point A, Point B)> HeaderLinkStrands(HeaderLink link)
    {
        int n = link.PinCount;
        for (int i = 1; i <= n; i++)
        {
            var pa = link.A.Pins.FirstOrDefault(p => p.Number == i);
            var pb = link.B.Pins.FirstOrDefault(p => p.Number == i);
            if (pa is null || pb is null) continue;
            yield return (pa.WorldPosition, pb.WorldPosition);
        }
    }

    /// <summary>
    /// Draw a header link as a bundle of straight strands, one per pin pair.
    /// Not routed -- a link is a fixed bundle, so it bypasses the wire router.
    /// A selected link renders in the selection colour.
    /// </summary>
    private void DrawHeaderLink(Graphics g, HeaderLink link)
    {
        int p = GridPitch;
        Color color = link.Selected
            ? RenderContext.DefaultSelected
            : Color.FromArgb(210, 120, 90, 170);   // muted violet ribbon

        using var pen = new Pen(color, 1.4f);
        foreach (var (a, b) in HeaderLinkStrands(link))
            g.DrawLine(pen, a.X * p, a.Y * p, b.X * p, b.Y * p);
    }

    /// <summary>
    /// Render junction blobs (T-junctions and crossings of same-net wires).
    /// Empty for 2-pin connections; will populate when multi-pin nets are
    /// introduced.
    /// </summary>
    private void DrawJunctions(Graphics g)
    {
        if (Routes.Junctions.Count == 0) return;

        int p = GridPitch;
        using var brush = new SolidBrush(Color.Black);
        float r = 2.0f;
        foreach (var j in Routes.Junctions)
            g.FillEllipse(brush, j.X * p - r, j.Y * p - r, r * 2, r * 2);
    }

    /// <summary>
    /// Render red warning dots at cells where a wire's vertex coincides
    /// with a different-net wire. These are export-blocking — EasyEDA
    /// would infer a junction at the cell and merge the two nets.
    /// </summary>
    private void DrawCoincidentCornerWarnings(Graphics g)
    {
        var corners = CoincidentCorners;
        if (corners.Count == 0) return;

        int p = GridPitch;
        using var fill = new SolidBrush(Color.FromArgb(220, 220, 30, 30));
        using var ring = new Pen(Color.FromArgb(255, 120, 0, 0), 1.0f);
        float r = 3.5f;
        foreach (var corner in corners)
        {
            float cx = corner.Cell.X * p;
            float cy = corner.Cell.Y * p;
            g.FillEllipse(fill, cx - r, cy - r, r * 2, r * 2);
            g.DrawEllipse(ring, cx - r, cy - r, r * 2, r * 2);
        }
    }

    private void DrawGrid(Graphics g)
    {
        float pitchScreen = Zoom * GridPitch;
        if (pitchScreen < 3f) return;

        float startX = PanOffset.X % pitchScreen;
        float startY = PanOffset.Y % pitchScreen;

        using var minorPen = new Pen(Color.FromArgb(40, Color.Gray));
        using var majorPen = new Pen(Color.FromArgb(90, Color.Gray));

        float majorPitch = pitchScreen * 10f;
        float majorStartX = PanOffset.X % majorPitch;
        float majorStartY = PanOffset.Y % majorPitch;

        using var dotBrush = new SolidBrush(Color.FromArgb(70, Color.Gray));
        if (pitchScreen >= 6f)
        {
            if (pitchScreen >= 12f)
            {
                for (float x = startX; x < Width; x += pitchScreen)
                    for (float y = startY; y < Height; y += pitchScreen)
                        g.FillEllipse(dotBrush, x - 1.5f, y - 1.5f, 3, 3);
            }
            else
            {
                for (float x = startX; x < Width; x += pitchScreen)
                    for (float y = startY; y < Height; y += pitchScreen)
                        g.FillRectangle(dotBrush, x - 0.5f, y - 0.5f, 1, 1);
            }
        }

        for (float x = majorStartX; x < Width; x += majorPitch)
            g.DrawLine(majorPen, x, 0, x, Height);
        for (float y = majorStartY; y < Height; y += majorPitch)
            g.DrawLine(majorPen, 0, y, Width, y);
    }
}
