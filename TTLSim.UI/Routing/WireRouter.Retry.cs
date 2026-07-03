using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using TTLSim.UI.Components;
using TTLSim.UI.Model;

namespace TTLSim.UI.Routing;

public sealed partial class WireRouter
{
    /// <summary>
    /// After the main route, scan for cross-net coincident corners and
    /// retry the vertex-owning connection of each, hard-blocking the
    /// offending cell from being a bend in the retry's search. Each
    /// connection is retried at most once per call to bound work.
    /// </summary>
    private static void TryFixCollisions(
        Schematic schematic,
        IReadOnlyList<Connection> connections,
        Dictionary<Connection, IReadOnlyList<Point>> polylines,
        Rectangle bounds, SearchScratch scratch, bool[] bodyBlocked,
        HashSet<Point> junctionsSet)
    {
        // Net id per connection via the same union-find used at routing
        // time, so "same net" matches the routing groups. Built over the
        // ACTIVE connections only: an inactive (hidden) wire is electrically
        // absent and must not bridge two visible nets into one id, which would
        // mask a real cross-net corner between them.
        var netIdOf = BuildNetIdMap(connections);

        var collisions = CoincidentCornerDetector.Detect(
            connections, new RouteResult(polylines, junctionsSet.ToList()),
            c => netIdOf[c]);

        if (collisions.Count == 0) return;

        // For each collision, find which connections have a vertex AT
        // the cell (not just transit). Those are candidates for retry.
        // A connection retried once is not retried again — bounded work.
        var retried = new HashSet<Connection>();

        foreach (var corner in collisions)
        {
            var cell = corner.Cell;

            foreach (var conn in corner.Connections)
            {
                if (retried.Contains(conn)) continue;
                if (!polylines.TryGetValue(conn, out var poly)) continue;

                // Vertex-owner check: is this connection's polyline
                // vertex (endpoint or bend) at this cell?
                bool vertexHere = false;
                foreach (var v in poly)
                    if (v == cell) { vertexHere = true; break; }
                if (!vertexHere) continue;

                // Endpoint-on-foreign-wire is not router-solvable: we
                // can't block the wire's own pin. Skip retry in that
                // case and accept the original route.
                if (poly[0] == cell || poly[^1] == cell) continue;

                retried.Add(conn);

                var candidate = RetryRouteWithBendBlock(
                    schematic, conn, cell, polylines, bounds, scratch,
                    bodyBlocked, netIdOf);

                if (candidate is null) continue;

                // Accept only if total collision count strictly improves.
                var tentative = new Dictionary<Connection, IReadOnlyList<Point>>(polylines)
                {
                    [conn] = candidate
                };
                var newCollisions = CoincidentCornerDetector.Detect(
                    connections,
                    new RouteResult(tentative, junctionsSet.ToList()),
                    c => netIdOf[c]);

                if (newCollisions.Count < collisions.Count)
                {
                    polylines[conn] = candidate;
                    collisions = newCollisions;
                    if (collisions.Count == 0) return;
                }
            }
        }
    }

    /// <summary>
    /// Re-route <paramref name="conn"/> with <paramref name="blockedBend"/>
    /// forbidden as a bend cell. Builds a fresh foreignWireDir from all
    /// the OTHER polylines so this connection isn't penalised by its own
    /// (now-replaced) prior route. Returns null if the retry fails (no
    /// path found) or produces the same polyline as before.
    /// </summary>
    private static IReadOnlyList<Point>? RetryRouteWithBendBlock(
        Schematic schematic,
        Connection conn, Point blockedBend,
        Dictionary<Connection, IReadOnlyList<Point>> polylines,
        Rectangle bounds, SearchScratch scratch, bool[] bodyBlocked,
        Dictionary<Connection, int> netIdOf)
    {
        // Build cost grids reflecting every committed polyline EXCEPT
        // this one. This lets the retry place a vertex anywhere this
        // wire's old polyline used to occupy.
        int cellCount = bounds.Width * bounds.Height;
        var foreignWirePenalty = new int[cellCount];
        var foreignWireDir = new byte[cellCount];
        var ownNetPenalty = new int[cellCount];

        // Pre-seed pin cells (same as the main pass: active items only).
        foreach (var item in schematic.ActiveItems)
            foreach (var pin in item.Pins)
                SetBit(foreignWireDir, bounds, pin.WorldPosition, ForeignPinSeed);

        int connNetId = netIdOf[conn];
        foreach (var kv in polylines)
        {
            if (kv.Key == conn) continue;
            bool sameNet = netIdOf[kv.Key] == connNetId;
            if (sameNet)
            {
                // Same-net wires go in ownNetPenalty so they share with
                // this wire (branches can merge to trunk, etc).
                StampPolyline(ownNetPenalty, bounds, kv.Value, WirePenalty);
            }
            else
            {
                StampPolyline(foreignWirePenalty, bounds, kv.Value, WirePenalty);
                MarkPolylineDirections(foreignWireDir, bounds, kv.Value);
                MarkPolylineVertices(foreignWireDir, bounds, kv.Value);
            }
        }

        var hardBlock = new HashSet<Point> { blockedBend };

        var blocked = (bool[])bodyBlocked.Clone();
        CarveCorridor(blocked, bounds, conn.A);
        CarveCorridor(blocked, bounds, conn.B);

        var path = Search(bounds, scratch, blocked,
            foreignWirePenalty, ownNetPenalty, foreignWireDir,
            conn.A.WorldPosition, conn.B.WorldPosition,
            conn.A.Direction, conn.B.Direction,
            hardBlock);

        if (path == null) return null;

        var simplified = Simplify(path);

        // Reject no-op: the search may have found the same path again
        // (if the bend block isn't a vertex of any found path). We only
        // care to swap if the polyline actually changed.
        if (polylines.TryGetValue(conn, out var oldPoly)
            && PolylinesEqual(simplified, oldPoly))
            return null;

        return simplified;
    }

    private static bool PolylinesEqual(IReadOnlyList<Point> a, IReadOnlyList<Point> b)
    {
        if (a.Count != b.Count) return false;
        for (int i = 0; i < a.Count; i++)
            if (a[i] != b[i]) return false;
        return true;
    }
}