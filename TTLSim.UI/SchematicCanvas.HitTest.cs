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
    /// <summary>
    /// Hit-test against the router's polylines. Iterates from topmost
    /// connection down so overlapping wires resolve consistently.
    /// </summary>
    private Connection? HitTestConnection(Point gridPoint)
    {
        float tol = ConnectionHitTolerance;
        var polylines = Routes.Polylines;

        for (int i = Schematic.Connections.Count - 1; i >= 0; i--)
        {
            var c = Schematic.Connections[i];
            if (!Schematic.IsConnectionActive(c)) continue;
            if (!polylines.TryGetValue(c, out var pts) || pts.Count < 2) continue;

            for (int j = 0; j < pts.Count - 1; j++)
            {
                if (DistancePointToSegment(gridPoint, pts[j], pts[j + 1]) <= tol)
                    return c;
            }
        }
        return null;
    }

    private static float DistancePointToSegment(Point p, Point a, Point b)
    {
        float vx = b.X - a.X;
        float vy = b.Y - a.Y;
        float wx = p.X - a.X;
        float wy = p.Y - a.Y;

        float lenSq = vx * vx + vy * vy;
        if (lenSq <= 0f)
            return MathF.Sqrt(wx * wx + wy * wy);

        float t = Math.Clamp((wx * vx + wy * vy) / lenSq, 0f, 1f);
        float dx = wx - t * vx;
        float dy = wy - t * vy;
        return MathF.Sqrt(dx * dx + dy * dy);
    }

    /// <summary>
    /// Hit-test against header-link strands, topmost link first. Uses the same
    /// tolerance as wire hit-testing.
    /// </summary>
    private HeaderLink? HitTestHeaderLink(Point gridPoint)
    {
        float tol = ConnectionHitTolerance;
        for (int i = Schematic.Links.Count - 1; i >= 0; i--)
        {
            var link = Schematic.Links[i];
            if (!Schematic.IsLinkActive(link)) continue;
            foreach (var (a, b) in HeaderLinkStrands(link))
                if (DistancePointToSegment(gridPoint, a, b) <= tol)
                    return link;
        }
        return null;
    }

    private SwitchUnit? HitTestSwitch(Point screenPoint)
    {
        float gx = (screenPoint.X - PanOffset.X) / (Zoom * GridPitch);
        float gy = (screenPoint.Y - PanOffset.Y) / (Zoom * GridPitch);

        foreach (var item in Schematic.ActiveItems)
        {
            if (item is not SwitchUnit sw) continue;
            var b = sw.InteractiveBounds;    // in HitTestSwitch
            if (gx >= b.Left && gx <= b.Right && gy >= b.Top && gy <= b.Bottom)
                return sw;
        }
        return null;
    }

    private SpdtSwitchUnit? HitTestSpdt(Point screenPoint)
    {
        float gx = (screenPoint.X - PanOffset.X) / (Zoom * GridPitch);
        float gy = (screenPoint.Y - PanOffset.Y) / (Zoom * GridPitch);

        foreach (var item in Schematic.ActiveItems)
        {
            if (item is not SpdtSwitchUnit sp) continue;
            var b = sp.InteractiveBounds;
            if (gx >= b.Left && gx <= b.Right && gy >= b.Top && gy <= b.Bottom)
                return sp;
        }
        return null;
    }

    private ButtonUnit? HitTestButton(Point screenPoint)
    {
        // Convert screen point to grid coordinates.
        float gx = (screenPoint.X - PanOffset.X) / (Zoom * GridPitch);
        float gy = (screenPoint.Y - PanOffset.Y) / (Zoom * GridPitch);

        foreach (var item in Schematic.ActiveItems)
        {
            if (item is not ButtonUnit btn) continue;
            var b = btn.InteractiveBounds;   // in HitTestButton
            if (gx >= b.Left && gx <= b.Right && gy >= b.Top && gy <= b.Bottom)
                return btn;
        }
        return null;
    }
}
