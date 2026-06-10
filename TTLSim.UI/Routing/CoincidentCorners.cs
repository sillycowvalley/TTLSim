using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using TTLSim.UI.Components;
using TTLSim.UI.Model;

namespace TTLSim.UI.Routing;

/// <summary>
/// One detected cross-net coincident corner. <see cref="Cell"/> is the
/// offending grid cell; <see cref="Connections"/> are every connection
/// whose polyline touches that cell (vertex or transit); <see cref="NetIds"/>
/// are the distinct net ids touching it.
/// </summary>
public sealed record CoincidentCorner(
    Point Cell,
    IReadOnlyList<Connection> Connections,
    IReadOnlyList<int> NetIds);

/// <summary>
/// Detects grid cells where a wire's vertex (polyline endpoint or interior
/// bend) coincides with another *different-net* wire — at a vertex OR on
/// the interior of a segment. EasyEDA infers a junction at such cells and
/// silently merges the two nets, so these are export-blocking errors.
///
/// Used both by the EasyEDA exporter (to emit EDA003 diagnostics) and by
/// the canvas (to draw red warning dots before the user exports).
/// </summary>
public static class CoincidentCornerDetector
{
    /// <summary>
    /// Scan every polyline in <paramref name="routes"/> and report cells
    /// where two different nets touch and at least one of them has a
    /// vertex there. Results are ordered by (Y, X) for determinism.
    /// </summary>
    public static IReadOnlyList<CoincidentCorner> Detect(
        IReadOnlyList<Connection> connections,
        RouteResult routes,
        Func<Connection, int> netIdOf)
    {
        var vertexNetsAt = new Dictionary<Point, HashSet<int>>();
        var transitNetsAt = new Dictionary<Point, HashSet<int>>();
        var connsAt = new Dictionary<Point, HashSet<Connection>>();

        void RecordVertex(Point p, int netId, Connection c)
        {
            if (!vertexNetsAt.TryGetValue(p, out var set))
                vertexNetsAt[p] = set = new HashSet<int>();
            set.Add(netId);
            if (!connsAt.TryGetValue(p, out var cs))
                connsAt[p] = cs = new HashSet<Connection>();
            cs.Add(c);
        }
        void RecordTransit(Point p, int netId, Connection c)
        {
            if (!transitNetsAt.TryGetValue(p, out var set))
                transitNetsAt[p] = set = new HashSet<int>();
            set.Add(netId);
            if (!connsAt.TryGetValue(p, out var cs))
                connsAt[p] = cs = new HashSet<Connection>();
            cs.Add(c);
        }

        foreach (var conn in connections)
        {
            if (!routes.Polylines.TryGetValue(conn, out var poly) || poly.Count < 2)
                continue;
            int netId = netIdOf(conn);

            for (int i = 0; i < poly.Count; i++)
                RecordVertex(poly[i], netId, conn);

            for (int i = 0; i + 1 < poly.Count; i++)
            {
                var a = poly[i];
                var b = poly[i + 1];
                if (a.X == b.X)
                {
                    int y0 = Math.Min(a.Y, b.Y), y1 = Math.Max(a.Y, b.Y);
                    for (int y = y0 + 1; y < y1; y++)
                        RecordTransit(new Point(a.X, y), netId, conn);
                }
                else if (a.Y == b.Y)
                {
                    int x0 = Math.Min(a.X, b.X), x1 = Math.Max(a.X, b.X);
                    for (int x = x0 + 1; x < x1; x++)
                        RecordTransit(new Point(x, a.Y), netId, conn);
                }
            }
        }

        var results = new List<CoincidentCorner>();
        foreach (var kv in vertexNetsAt.OrderBy(k => k.Key.Y).ThenBy(k => k.Key.X))
        {
            var cell = kv.Key;
            transitNetsAt.TryGetValue(cell, out var transitNets);

            var allNets = new HashSet<int>(kv.Value);
            if (transitNets is not null) allNets.UnionWith(transitNets);
            if (allNets.Count < 2) continue;

            var conns = connsAt[cell]
                .OrderBy(c => c.Id, StringComparer.Ordinal)
                .ToList();
            var nets = allNets.OrderBy(n => n).ToList();
            results.Add(new CoincidentCorner(cell, conns, nets));
        }
        return results;
    }

    /// <summary>
    /// Produce one human-readable line per coincident corner, naming the
    /// colliding pins grouped by net id. Intended for verbose logging so a
    /// red dot on the canvas can be tied back to the exact pins and nets
    /// that merge at its cell. Pure formatting — no logging dependency; the
    /// caller decides where the lines go.
    /// </summary>
    public static IEnumerable<string> Describe(
        IReadOnlyList<CoincidentCorner> corners,
        Func<Connection, int> netIdOf)
    {
        foreach (var corner in corners)
        {
            var nets = corner.Connections
                .GroupBy(netIdOf)
                .OrderBy(grp => grp.Key)
                .Select(grp =>
                {
                    var legs = grp
                        .Select(c => $"{PinLabel(c.A)}<->{PinLabel(c.B)}")
                        .OrderBy(s => s, StringComparer.Ordinal);
                    return $"net {grp.Key} [{string.Join(", ", legs)}]";
                });

            yield return
                $"Coincident corner at ({corner.Cell.X},{corner.Cell.Y}): "
                + string.Join("; ", nets);
        }
    }

    /// <summary>
    /// Name a pin as "Designator.PinName" (e.g. "U2.I/O3"). Falls back to the
    /// owner's label or id for non-Unit owners (power flags, raw items), and
    /// to the pin number when the pin has no name.
    /// </summary>
    private static string PinLabel(Pin pin)
    {
        string owner = pin.Owner switch
        {
            Unit u => u.DisplayDesignator,
            { Label.Length: > 0 } item => item.Label,
            { } item => item.Id,
            _ => "?"
        };

        string name = !string.IsNullOrEmpty(pin.Name) ? pin.Name
                    : pin.Number > 0 ? pin.Number.ToString()
                    : "?";

        return $"{owner}.{name}";
    }
}