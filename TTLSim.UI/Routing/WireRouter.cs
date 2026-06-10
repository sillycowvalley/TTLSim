using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using TTLSim.UI.Components;
using TTLSim.UI.Model;

namespace TTLSim.UI.Routing;

/// <summary>
/// Single-pass bend-aware Dijkstra router with star and chain merging,
/// plus a post-route retry pass to fix cross-net coincident corners that
/// the single-pass cost model could not avoid.
///
/// Groups connections that are electrically related (share pins
/// transitively). Within each group:
///   - If every connection shares one common pin, route as a star: trunk
///     plus branches merging into it.
///   - Otherwise (a chain or more general topology) decompose into
///     sub-stars: repeatedly find the pin touched by the most remaining
///     connections, route those as a sub-star, repeat with what's left.
///     Any leftover connections route independently.
///   - After routing the group, emit a junction blob at every pin
///     touched by 2+ connections in the group.
///
/// Chip and gate bodies are hard obstacles; corridors are carved at each
/// pin involved in routing so wires can reach them.
///
/// Cost model — two penalty grids plus a direction-aware foreign-wire
/// marker:
///   - foreignWirePenalty: per-cell soft cost for previously-routed
///     groups' wires.
///   - ownNetPenalty: same shape, for legs of the current group;
///     cleared between groups.
///   - foreignWireDir: byte grid with flags per cell — H, V, Vertex.
///     Pre-seeded at every PIN CELL of every item.
///
/// Cost contributions per search step entering cell (ix,iy):
///   StepCost + foreignWirePenalty + ownNetPenalty
///     + ForeignParallelPenalty (if moving parallel to a foreign segment)
///     + ForeignVertexTransitPenalty (if entering a foreign-vertex cell)
///     + BendPenalty (if bending) + ForeignBendOnWirePenalty (if bending
///       AND the current cell has a foreign wire).
///
/// Search state storage: the Dijkstra state space is (cell × direction),
/// fully known up front from the grid bounds. Per-leg g-scores and
/// predecessors live in flat int arrays indexed by an encoded state
/// index (see SearchScratch), NOT in Dictionary&lt;State,...&gt;. Profiling
/// showed the dictionary path (hashing + equality + probing on a record
/// struct key) dominating routing time by a wide margin; the flat arrays
/// reduce every lookup/update to a single array access. One scratch
/// buffer is allocated per RouteAll and cleared (memset) between legs.
///
/// Post-route retry: after the single-pass route, the detector finds
/// cross-net coincident corners (vertex-on-wire). For each such cell,
/// the connection(s) whose vertex lies on the cell get re-routed once
/// with that cell hard-blocked from being a bend. If the retry reduces
/// the total collision count, it replaces the original route. Each
/// connection is retried at most once per RouteAll call to bound work.
///
/// Deterministic: output depends only on the schematic and the order of
/// connections within it.
/// </summary>
public sealed class WireRouter : IConnectionRouter
{
    private const int StepCost = 1;
    private const int BendPenalty = 4;
    private const int WirePenalty = 6;   // per cell, per prior wire sharing it
    private const int Margin = 20;       // cells of slack around schematic bbox

    private const int ForeignParallelPenalty = 12;
    private const int ForeignBendOnWirePenalty = 40;
    private const int ForeignVertexTransitPenalty = 40;

    private const byte ForeignH = 0x1;
    private const byte ForeignV = 0x2;
    private const byte ForeignVertex = 0x4;
    private const byte ForeignPinSeed = ForeignH | ForeignV | ForeignVertex;

    public RouteResult RouteAll(Schematic schematic)
    {
        var polylines = new Dictionary<Connection, IReadOnlyList<Point>>(
            schematic.Connections.Count);
        var junctionsSet = new HashSet<Point>();

        if (schematic.Connections.Count == 0)
            return new RouteResult(polylines, new List<Point>());

        var bounds = ComputeGridBounds(schematic);

        var scratch = new SearchScratch(bounds);

        var bodyBlocked = new bool[bounds.Width, bounds.Height];
        foreach (var item in schematic.Items)
            StampRect(bodyBlocked, bounds, item.RoutingBounds, true);

        var foreignWirePenalty = new int[bounds.Width, bounds.Height];
        var ownNetPenalty = new int[bounds.Width, bounds.Height];
        var foreignWireDir = new byte[bounds.Width, bounds.Height];

        // Pre-seed pin cells.
        foreach (var item in schematic.Items)
            foreach (var pin in item.Pins)
                SetBit(foreignWireDir, bounds, pin.WorldPosition, ForeignPinSeed);

        var groups = GroupByPin(schematic.Connections);

        // Restored declaration-order routing — the constraint-based
        // ordering experiment helped some schematics and hurt others.
        // Net wins come from the cost-model and retry pass, not ordering.
        foreach (var group in groups.OrderBy(g => schematic.Connections.IndexOf(g[0])))
        {
            RouteGroup(group, bounds, scratch, bodyBlocked,
                foreignWirePenalty, ownNetPenalty, foreignWireDir,
                polylines, junctionsSet,
                hardBlockBendCells: null);

            FoldAndClear(ownNetPenalty, foreignWirePenalty, bounds);
            foreach (var c in group)
            {
                if (!polylines.TryGetValue(c, out var poly)) continue;
                MarkPolylineDirections(foreignWireDir, bounds, poly);
                MarkPolylineVertices(foreignWireDir, bounds, poly);
            }
        }

        // --- Post-route retry ----------------------------------------
        // The single-pass route minimised each wire's local cost given
        // what was already routed at that moment. That can still leave
        // cross-net coincident corners — typically because a wire routed
        // earlier picked a vertex cell that a later wire's only viable
        // route traverses. Detect those and try to reroute the wire that
        // owns the vertex with that cell hard-blocked from being a bend.
        // Take the new route only if it doesn't introduce more collisions.
        TryFixCollisions(schematic, polylines,
            bounds, scratch, bodyBlocked,
            junctionsSet);

        return new RouteResult(polylines, junctionsSet.ToList());
    }

    /// <summary>
    /// After the main route, scan for cross-net coincident corners and
    /// retry the vertex-owning connection of each, hard-blocking the
    /// offending cell from being a bend in the retry's search. Each
    /// connection is retried at most once per call to bound work.
    /// </summary>
    private static void TryFixCollisions(
        Schematic schematic,
        Dictionary<Connection, IReadOnlyList<Point>> polylines,
        Rectangle bounds, SearchScratch scratch, bool[,] bodyBlocked,
        HashSet<Point> junctionsSet)
    {
        // Net id per connection via the same union-find used at routing
        // time, so "same net" matches the routing groups.
        var netIdOf = BuildNetIdMap(schematic.Connections);

        var collisions = CoincidentCornerDetector.Detect(
            schematic.Connections, new RouteResult(polylines, junctionsSet.ToList()),
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
                    schematic.Connections,
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
        Rectangle bounds, SearchScratch scratch, bool[,] bodyBlocked,
        Dictionary<Connection, int> netIdOf)
    {
        // Build cost grids reflecting every committed polyline EXCEPT
        // this one. This lets the retry place a vertex anywhere this
        // wire's old polyline used to occupy.
        var foreignWirePenalty = new int[bounds.Width, bounds.Height];
        var foreignWireDir = new byte[bounds.Width, bounds.Height];
        var ownNetPenalty = new int[bounds.Width, bounds.Height];

        // Pre-seed pin cells (same as the main pass).
        foreach (var item in schematic.Items)
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

        var blocked = (bool[,])bodyBlocked.Clone();
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

    private static Dictionary<Connection, int> BuildNetIdMap(
        IReadOnlyList<Connection> connections)
    {
        var parent = new int[connections.Count];
        for (int i = 0; i < connections.Count; i++) parent[i] = i;
        int Find(int x)
        {
            while (parent[x] != x) { parent[x] = parent[parent[x]]; x = parent[x]; }
            return x;
        }
        void Union(int a, int b) { int ra = Find(a), rb = Find(b); if (ra != rb) parent[ra] = rb; }

        var pinToConn = new Dictionary<Pin, int>();
        for (int i = 0; i < connections.Count; i++)
        {
            var c = connections[i];
            if (pinToConn.TryGetValue(c.A, out int j1)) Union(i, j1);
            else pinToConn[c.A] = i;
            if (pinToConn.TryGetValue(c.B, out int j2)) Union(i, j2);
            else pinToConn[c.B] = i;
        }

        var map = new Dictionary<Connection, int>(connections.Count);
        for (int i = 0; i < connections.Count; i++)
            map[connections[i]] = Find(i);
        return map;
    }

    /// <summary>
    /// Route every connection in <paramref name="group"/> and emit any
    /// junctions for shared pins. Strategy: pull off sub-stars greedily
    /// (largest first), route each as a tree; route remaining singletons
    /// as plain 2-pin connections; emit junction blobs at pins touched
    /// by 2+ connections in the group.
    /// </summary>
    private static void RouteGroup(
        List<Connection> group, Rectangle bounds,
        SearchScratch scratch, bool[,] bodyBlocked,
        int[,] foreignWirePenalty, int[,] ownNetPenalty,
        byte[,] foreignWireDir,
        Dictionary<Connection, IReadOnlyList<Point>> polylines,
        HashSet<Point> junctionsSet,
        HashSet<Point>? hardBlockBendCells)
    {

        var remaining = new List<Connection>(group);

        while (remaining.Count > 0)
        {
            var pinCounts = new Dictionary<Pin, int>();
            foreach (var c in remaining)
            {
                pinCounts.TryGetValue(c.A, out int a); pinCounts[c.A] = a + 1;
                pinCounts.TryGetValue(c.B, out int b); pinCounts[c.B] = b + 1;
            }

            Pin? hub = pinCounts
                .Where(kv => kv.Value >= 2)
                .OrderByDescending(kv => kv.Value)
                .ThenBy(kv => kv.Key.Owner?.Id ?? "")
                .ThenBy(kv => kv.Key.Number)
                .Select(kv => (Pin?)kv.Key)
                .FirstOrDefault();

            if (hub == null) break;

            var subStar = remaining.Where(c => c.A == hub || c.B == hub).ToList();
            foreach (var c in subStar) remaining.Remove(c);

            RouteStar(subStar, hub, bounds, scratch, bodyBlocked,
                foreignWirePenalty, ownNetPenalty, foreignWireDir,
                polylines, junctionsSet, hardBlockBendCells);
        }

        foreach (var c in remaining)
        {
            var polyline = RouteOne(c, bounds, scratch, bodyBlocked,
                foreignWirePenalty, ownNetPenalty, foreignWireDir,
                hardBlockBendCells);
            polylines[c] = polyline;
            StampPolyline(ownNetPenalty, bounds, polyline, WirePenalty);
        }

        // Same-group branch-point detection — see original block.
        var cellDirs = new Dictionary<Point, int>();
        foreach (var c in group)
        {
            if (!polylines.TryGetValue(c, out var poly)) continue;
            for (int i = 0; i < poly.Count - 1; i++)
            {
                var a = poly[i];
                var b = poly[i + 1];
                if (a.X == b.X)
                {
                    int y0 = Math.Min(a.Y, b.Y), y1 = Math.Max(a.Y, b.Y);
                    for (int y = y0; y <= y1; y++)
                    {
                        var cell = new Point(a.X, y);
                        cellDirs.TryGetValue(cell, out int m);
                        if (y > y0) m |= 1;
                        if (y < y1) m |= 2;
                        cellDirs[cell] = m;
                    }
                }
                else if (a.Y == b.Y)
                {
                    int x0 = Math.Min(a.X, b.X), x1 = Math.Max(a.X, b.X);
                    for (int x = x0; x <= x1; x++)
                    {
                        var cell = new Point(x, a.Y);
                        cellDirs.TryGetValue(cell, out int m);
                        if (x > x0) m |= 4;
                        if (x < x1) m |= 8;
                        cellDirs[cell] = m;
                    }
                }
            }
        }
        foreach (var kv in cellDirs)
        {
            int m = kv.Value;
            int bits = ((m & 1) != 0 ? 1 : 0)
                     + ((m & 2) != 0 ? 1 : 0)
                     + ((m & 4) != 0 ? 1 : 0)
                     + ((m & 8) != 0 ? 1 : 0);
            if (bits >= 3)
                junctionsSet.Add(kv.Key);
        }
    }

    private static List<List<Connection>> GroupByPin(IReadOnlyList<Connection> connections)
    {
        var parent = new int[connections.Count];
        for (int i = 0; i < connections.Count; i++) parent[i] = i;

        int Find(int x)
        {
            while (parent[x] != x) { parent[x] = parent[parent[x]]; x = parent[x]; }
            return x;
        }
        void Union(int a, int b) { int ra = Find(a), rb = Find(b); if (ra != rb) parent[ra] = rb; }

        var pinToConn = new Dictionary<Pin, int>();
        for (int i = 0; i < connections.Count; i++)
        {
            var c = connections[i];
            if (pinToConn.TryGetValue(c.A, out int j1)) Union(i, j1);
            else pinToConn[c.A] = i;
            if (pinToConn.TryGetValue(c.B, out int j2)) Union(i, j2);
            else pinToConn[c.B] = i;
        }

        var byRoot = new Dictionary<int, List<Connection>>();
        for (int i = 0; i < connections.Count; i++)
        {
            int root = Find(i);
            if (!byRoot.TryGetValue(root, out var list))
                byRoot[root] = list = new List<Connection>();
            list.Add(connections[i]);
        }
        return byRoot.Values.ToList();
    }

    private static void RouteStar(
        List<Connection> group, Pin commonPin,
        Rectangle bounds, SearchScratch scratch, bool[,] bodyBlocked,
        int[,] foreignWirePenalty, int[,] ownNetPenalty,
        byte[,] foreignWireDir,
        Dictionary<Connection, IReadOnlyList<Point>> polylines,
        HashSet<Point> junctionsSet,
        HashSet<Point>? hardBlockBendCells)
    {
        if (group.Count == 1)
        {
            var only = group[0];
            var polyline = RouteOne(only, bounds, scratch, bodyBlocked,
                foreignWirePenalty, ownNetPenalty, foreignWireDir,
                hardBlockBendCells);
            polylines[only] = polyline;
            StampPolyline(ownNetPenalty, bounds, polyline, WirePenalty);
            return;
        }

        var legs = group.Select(c =>
        {
            var leaf = c.A == commonPin ? c.B : c.A;
            int distance = Math.Abs(leaf.WorldPosition.X - commonPin.WorldPosition.X)
                         + Math.Abs(leaf.WorldPosition.Y - commonPin.WorldPosition.Y);
            return (Connection: c, Leaf: leaf, Distance: distance);
        }).OrderByDescending(t => t.Distance)
          .ThenBy(t => t.Leaf.Owner?.Id ?? "")
          .ThenBy(t => t.Leaf.Number)
          .ToList();

        var trunkLeg = legs[0];
        var trunkPolyline = RouteTwoPin(
            commonPin, trunkLeg.Leaf, bounds, scratch, bodyBlocked,
            foreignWirePenalty, ownNetPenalty, foreignWireDir,
            hardBlockBendCells);
        polylines[trunkLeg.Connection] = trunkPolyline;
        StampPolyline(ownNetPenalty, bounds, trunkPolyline, WirePenalty);

        var netCells = new HashSet<Point>();
        AddCellsFromPolyline(netCells, trunkPolyline);

        for (int i = 1; i < legs.Count; i++)
        {
            var leg = legs[i];
            var branchPolyline = RouteToNet(
                leg.Leaf, netCells, bounds, scratch, bodyBlocked,
                foreignWirePenalty, ownNetPenalty, foreignWireDir,
                hardBlockBendCells);

            if (branchPolyline == null)
            {
                branchPolyline = RouteTwoPin(
                    leg.Leaf, commonPin, bounds, scratch, bodyBlocked,
                    foreignWirePenalty, ownNetPenalty, foreignWireDir,
                    hardBlockBendCells);
            }
            else
            {
                junctionsSet.Add(branchPolyline[^1]);
            }

            polylines[leg.Connection] = branchPolyline;
            StampPolyline(ownNetPenalty, bounds, branchPolyline, WirePenalty);
            AddCellsFromPolyline(netCells, branchPolyline);
        }
    }

    private static void AddCellsFromPolyline(HashSet<Point> cells, IReadOnlyList<Point> poly)
    {
        for (int i = 0; i < poly.Count - 1; i++)
        {
            var a = poly[i];
            var b = poly[i + 1];
            if (a.X == b.X)
            {
                int y0 = Math.Min(a.Y, b.Y), y1 = Math.Max(a.Y, b.Y);
                for (int y = y0; y <= y1; y++) cells.Add(new Point(a.X, y));
            }
            else if (a.Y == b.Y)
            {
                int x0 = Math.Min(a.X, b.X), x1 = Math.Max(a.X, b.X);
                for (int x = x0; x <= x1; x++) cells.Add(new Point(x, a.Y));
            }
        }
    }

    private static IReadOnlyList<Point> RouteOne(
        Connection connection, Rectangle bounds,
        SearchScratch scratch, bool[,] bodyBlocked,
        int[,] foreignWirePenalty, int[,] ownNetPenalty,
        byte[,] foreignWireDir,
        HashSet<Point>? hardBlockBendCells)
        => RouteTwoPin(connection.A, connection.B, bounds, scratch, bodyBlocked,
            foreignWirePenalty, ownNetPenalty, foreignWireDir,
            hardBlockBendCells);

    private static IReadOnlyList<Point> RouteTwoPin(
        Pin pinA, Pin pinB, Rectangle bounds,
        SearchScratch scratch, bool[,] bodyBlocked,
        int[,] foreignWirePenalty, int[,] ownNetPenalty,
        byte[,] foreignWireDir,
        HashSet<Point>? hardBlockBendCells)
    {
        var blocked = (bool[,])bodyBlocked.Clone();
        CarveCorridor(blocked, bounds, pinA);
        CarveCorridor(blocked, bounds, pinB);

        var path = Search(bounds, scratch, blocked,
            foreignWirePenalty, ownNetPenalty, foreignWireDir,
            pinA.WorldPosition, pinB.WorldPosition,
            pinA.Direction, pinB.Direction,
            hardBlockBendCells);

        if (path == null)
            return new[] { pinA.WorldPosition, pinB.WorldPosition };

        return Simplify(path);
    }

    private static IReadOnlyList<Point>? RouteToNet(
        Pin leaf, HashSet<Point> netCells,
        Rectangle bounds, SearchScratch scratch, bool[,] bodyBlocked,
        int[,] foreignWirePenalty, int[,] ownNetPenalty,
        byte[,] foreignWireDir,
        HashSet<Point>? hardBlockBendCells)
    {
        var blocked = (bool[,])bodyBlocked.Clone();
        CarveCorridor(blocked, bounds, leaf);

        foreach (var cell in netCells)
            Clear(blocked, bounds, cell);

        var path = SearchToAnyCell(bounds, scratch, blocked,
            foreignWirePenalty, ownNetPenalty, foreignWireDir,
            leaf.WorldPosition, leaf.Direction, netCells,
            hardBlockBendCells);

        if (path == null) return null;
        return Simplify(path);
    }

    private static void CarveCorridor(bool[,] blocked, Rectangle bounds, Pin pin)
    {
        var p = pin.WorldPosition;
        var (dx, dy) = DirToDelta(pin.Direction);
        var rb = pin.Owner?.RoutingBounds ?? new Rectangle(p.X, p.Y, 1, 1);

        var cell = p;
        for (int i = 0; i < 100; i++)
        {
            Clear(blocked, bounds, cell);
            cell = new Point(cell.X + dx, cell.Y + dy);
            if (!rb.Contains(cell)) break;
        }
        Clear(blocked, bounds, cell);
    }

    private static void Clear(bool[,] blocked, Rectangle bounds, Point cell)
    {
        if (TryIndex(bounds, cell.X, cell.Y, out int ix, out int iy))
            blocked[ix, iy] = false;
    }

    private static List<Point>? Search(
        Rectangle bounds, SearchScratch scratch, bool[,] blocked,
        int[,] foreignWirePenalty, int[,] ownNetPenalty,
        byte[,] foreignWireDir,
        Point start, Point end,
        PinDirection startDir, PinDirection endDir,
        HashSet<Point>? hardBlockBendCells)
    {
        var requiredArrival = Opposite(endDir);

        // Start is always inside bounds (ComputeGridBounds includes every
        // connection endpoint), but guard anyway: callers treat null as
        // "no route" and fall back to a straight line.
        if (!TryIndex(bounds, start.X, start.Y, out int startIx, out int startIy))
            return null;

        scratch.Reset();
        int width = bounds.Width;
        var bestG = scratch.BestG;          // g + 1; 0 = unvisited
        var predecessor = scratch.Predecessor;   // state index + 1; 0 = none

        int startIdx = ((startIy * width) + startIx) * 4 + (int)startDir;
        bestG[startIdx] = 1;   // g = 0

        var queue = new PriorityQueue<int, int>();
        queue.Enqueue(startIdx, 0);

        int goal = -1;

        while (queue.TryDequeue(out int stateIdx, out int dequeuedG))
        {
            if (dequeuedG + 1 > bestG[stateIdx]) continue;   // stale entry

            int dirInt = stateIdx & 3;
            int cellIdx = stateIdx >> 2;
            int cx = cellIdx % width;
            int cy = cellIdx / width;
            int worldX = cx + bounds.X;
            int worldY = cy + bounds.Y;

            if (worldX == end.X && worldY == end.Y && dirInt == (int)requiredArrival)
            {
                goal = stateIdx;
                break;
            }

            int g = dequeuedG;
            byte hereMask = foreignWireDir[cx, cy];
            foreach (var dir in s_directions)
            {
                var (dx, dy) = DirToDelta(dir);
                int nx = worldX + dx;
                int ny = worldY + dy;
                if (!TryIndex(bounds, nx, ny, out int ix, out int iy)) continue;
                if (blocked[ix, iy]) continue;

                bool bending = (int)dir != dirInt;

                // Hard-block bends at specified cells. Transit through
                // the cell is still allowed — only bend-at-cell is
                // forbidden. This matches the EDA003 mechanism.
                if (bending && hardBlockBendCells is not null
                    && hardBlockBendCells.Contains(new Point(worldX, worldY)))
                {
                    continue;
                }

                int bendCost = 0;
                if (bending)
                {
                    bendCost = BendPenalty;
                    if (hereMask != 0)
                        bendCost += ForeignBendOnWirePenalty;
                }

                byte enteredMask = foreignWireDir[ix, iy];
                int parallelCost = MovingParallelToForeignMask(enteredMask, dir)
                    ? ForeignParallelPenalty : 0;
                int vertexTransitCost = (enteredMask & ForeignVertex) != 0
                    ? ForeignVertexTransitPenalty : 0;

                int stepCost = StepCost
                             + foreignWirePenalty[ix, iy]
                             + ownNetPenalty[ix, iy]
                             + parallelCost
                             + vertexTransitCost
                             + bendCost;
                int ng = g + stepCost;

                int nextIdx = ((iy * width) + ix) * 4 + (int)dir;
                int existingPlus1 = bestG[nextIdx];
                if (existingPlus1 != 0 && ng + 1 >= existingPlus1) continue;

                bestG[nextIdx] = ng + 1;
                predecessor[nextIdx] = stateIdx + 1;
                queue.Enqueue(nextIdx, ng);
            }
        }

        return goal >= 0
            ? BacktraceCells(scratch, bounds, startIdx, goal)
            : null;
    }

    private static List<Point>? SearchToAnyCell(
        Rectangle bounds, SearchScratch scratch, bool[,] blocked,
        int[,] foreignWirePenalty, int[,] ownNetPenalty,
        byte[,] foreignWireDir,
        Point start, PinDirection startDir, HashSet<Point> targetCells,
        HashSet<Point>? hardBlockBendCells)
    {
        if (!TryIndex(bounds, start.X, start.Y, out int startIx, out int startIy))
            return null;

        scratch.Reset();
        int width = bounds.Width;
        var bestG = scratch.BestG;          // g + 1; 0 = unvisited
        var predecessor = scratch.Predecessor;   // state index + 1; 0 = none

        int startIdx = ((startIy * width) + startIx) * 4 + (int)startDir;
        bestG[startIdx] = 1;   // g = 0

        var queue = new PriorityQueue<int, int>();
        queue.Enqueue(startIdx, 0);

        int goal = -1;

        while (queue.TryDequeue(out int stateIdx, out int dequeuedG))
        {
            if (dequeuedG + 1 > bestG[stateIdx]) continue;   // stale entry

            int dirInt = stateIdx & 3;
            int cellIdx = stateIdx >> 2;
            int cx = cellIdx % width;
            int cy = cellIdx / width;
            int worldX = cx + bounds.X;
            int worldY = cy + bounds.Y;

            if (stateIdx != startIdx
                && targetCells.Contains(new Point(worldX, worldY)))
            {
                goal = stateIdx;
                break;
            }

            int g = dequeuedG;
            byte hereMask = foreignWireDir[cx, cy];
            foreach (var dir in s_directions)
            {
                var (dx, dy) = DirToDelta(dir);
                int nx = worldX + dx;
                int ny = worldY + dy;
                if (!TryIndex(bounds, nx, ny, out int ix, out int iy)) continue;
                if (blocked[ix, iy]) continue;

                bool bending = (int)dir != dirInt;

                if (bending && hardBlockBendCells is not null
                    && hardBlockBendCells.Contains(new Point(worldX, worldY)))
                {
                    continue;
                }

                int bendCost = 0;
                if (bending)
                {
                    bendCost = BendPenalty;
                    if (hereMask != 0)
                        bendCost += ForeignBendOnWirePenalty;
                }

                byte enteredMask = foreignWireDir[ix, iy];
                int parallelCost = MovingParallelToForeignMask(enteredMask, dir)
                    ? ForeignParallelPenalty : 0;
                int vertexTransitCost = (enteredMask & ForeignVertex) != 0
                    ? ForeignVertexTransitPenalty : 0;

                int stepCost = StepCost
                             + foreignWirePenalty[ix, iy]
                             + ownNetPenalty[ix, iy]
                             + parallelCost
                             + vertexTransitCost
                             + bendCost;
                int ng = g + stepCost;

                int nextIdx = ((iy * width) + ix) * 4 + (int)dir;
                int existingPlus1 = bestG[nextIdx];
                if (existingPlus1 != 0 && ng + 1 >= existingPlus1) continue;

                bestG[nextIdx] = ng + 1;
                predecessor[nextIdx] = stateIdx + 1;
                queue.Enqueue(nextIdx, ng);
            }
        }

        return goal >= 0
            ? BacktraceCells(scratch, bounds, startIdx, goal)
            : null;
    }

    private static bool MovingParallelToForeignMask(byte mask, PinDirection dir)
    {
        if (mask == 0) return false;
        bool horizontal = dir == PinDirection.Left || dir == PinDirection.Right;
        return horizontal
            ? (mask & ForeignH) != 0
            : (mask & ForeignV) != 0;
    }

    private static List<Point>? BacktraceCells(
        SearchScratch scratch, Rectangle bounds, int startIdx, int endIdx)
    {
        int width = bounds.Width;
        var cells = new List<Point>();
        int cur = endIdx;
        while (true)
        {
            int cellIdx = cur >> 2;
            cells.Add(new Point(cellIdx % width + bounds.X,
                                cellIdx / width + bounds.Y));
            if (cur == startIdx) break;
            int prevPlus1 = scratch.Predecessor[cur];
            if (prevPlus1 == 0) return null;
            cur = prevPlus1 - 1;
        }
        cells.Reverse();
        return cells;
    }

    private static IReadOnlyList<Point> Simplify(List<Point> cells)
    {
        if (cells.Count < 3) return cells;

        var simplified = new List<Point> { cells[0] };
        for (int i = 1; i < cells.Count - 1; i++)
        {
            var prev = cells[i - 1];
            var here = cells[i];
            var next = cells[i + 1];
            bool collinear = (prev.X == here.X && here.X == next.X)
                          || (prev.Y == here.Y && here.Y == next.Y);
            if (!collinear) simplified.Add(here);
        }
        simplified.Add(cells[^1]);
        return simplified;
    }

    private static void StampPolyline(
        int[,] grid, Rectangle bounds,
        IReadOnlyList<Point> poly, int penalty)
    {
        for (int i = 0; i < poly.Count - 1; i++)
        {
            var a = poly[i];
            var b = poly[i + 1];
            if (a.X == b.X)
            {
                int y0 = Math.Min(a.Y, b.Y);
                int y1 = Math.Max(a.Y, b.Y);
                for (int y = y0; y <= y1; y++)
                    AddAt(grid, bounds, a.X, y, penalty);
            }
            else if (a.Y == b.Y)
            {
                int x0 = Math.Min(a.X, b.X);
                int x1 = Math.Max(a.X, b.X);
                for (int x = x0; x <= x1; x++)
                    AddAt(grid, bounds, x, a.Y, penalty);
            }
        }
    }

    private static void MarkPolylineDirections(
        byte[,] grid, Rectangle bounds, IReadOnlyList<Point> poly)
    {
        for (int i = 0; i < poly.Count - 1; i++)
        {
            var a = poly[i];
            var b = poly[i + 1];
            if (a.X == b.X)
            {
                int y0 = Math.Min(a.Y, b.Y);
                int y1 = Math.Max(a.Y, b.Y);
                for (int y = y0; y <= y1; y++)
                    SetBit(grid, bounds, new Point(a.X, y), ForeignV);
            }
            else if (a.Y == b.Y)
            {
                int x0 = Math.Min(a.X, b.X);
                int x1 = Math.Max(a.X, b.X);
                for (int x = x0; x <= x1; x++)
                    SetBit(grid, bounds, new Point(x, a.Y), ForeignH);
            }
        }
    }

    private static void MarkPolylineVertices(
        byte[,] grid, Rectangle bounds, IReadOnlyList<Point> poly)
    {
        foreach (var v in poly)
            SetBit(grid, bounds, v, ForeignVertex);
    }

    private static void FoldAndClear(int[,] ownGrid, int[,] foreignGrid, Rectangle bounds)
    {
        for (int ix = 0; ix < bounds.Width; ix++)
            for (int iy = 0; iy < bounds.Height; iy++)
            {
                if (ownGrid[ix, iy] != 0)
                {
                    foreignGrid[ix, iy] += ownGrid[ix, iy];
                    ownGrid[ix, iy] = 0;
                }
            }
    }

    private static void StampRect(bool[,] grid, Rectangle bounds, Rectangle rect, bool value)
    {
        for (int gx = rect.Left; gx < rect.Right; gx++)
            for (int gy = rect.Top; gy < rect.Bottom; gy++)
                if (TryIndex(bounds, gx, gy, out int ix, out int iy))
                    grid[ix, iy] = value;
    }

    private static void AddAt(int[,] grid, Rectangle bounds, int gx, int gy, int value)
    {
        if (TryIndex(bounds, gx, gy, out int ix, out int iy))
            grid[ix, iy] += value;
    }

    private static void SetBit(byte[,] grid, Rectangle bounds, Point cell, byte bit)
    {
        if (TryIndex(bounds, cell.X, cell.Y, out int ix, out int iy))
            grid[ix, iy] |= bit;
    }

    private static Rectangle ComputeGridBounds(Schematic schematic)
    {
        int minX = int.MaxValue, minY = int.MaxValue;
        int maxX = int.MinValue, maxY = int.MinValue;

        void Include(int x, int y)
        {
            if (x < minX) minX = x;
            if (y < minY) minY = y;
            if (x > maxX) maxX = x;
            if (y > maxY) maxY = y;
        }

        foreach (var item in schematic.Items)
        {
            var rb = item.RoutingBounds;
            Include(rb.Left, rb.Top);
            Include(rb.Right - 1, rb.Bottom - 1);
        }
        foreach (var c in schematic.Connections)
        {
            Include(c.A.WorldPosition.X, c.A.WorldPosition.Y);
            Include(c.B.WorldPosition.X, c.B.WorldPosition.Y);
        }

        if (minX == int.MaxValue)
            return new Rectangle(0, 0, 1, 1);

        return new Rectangle(
            minX - Margin, minY - Margin,
            maxX - minX + 2 * Margin + 1,
            maxY - minY + 2 * Margin + 1);
    }

    private static bool TryIndex(Rectangle bounds, int gx, int gy, out int ix, out int iy)
    {
        ix = gx - bounds.X;
        iy = gy - bounds.Y;
        return ix >= 0 && ix < bounds.Width && iy >= 0 && iy < bounds.Height;
    }

    private static PinDirection Opposite(PinDirection d) => d switch
    {
        PinDirection.Left => PinDirection.Right,
        PinDirection.Right => PinDirection.Left,
        PinDirection.Up => PinDirection.Down,
        PinDirection.Down => PinDirection.Up,
        _ => d
    };

    private static string ShortId(string? id) =>
        string.IsNullOrEmpty(id) ? "?" : id.Length <= 4 ? id : id[..4];

    private static readonly PinDirection[] s_directions =
    {
        PinDirection.Left, PinDirection.Right, PinDirection.Up, PinDirection.Down
    };

    private static (int dx, int dy) DirToDelta(PinDirection dir) => dir switch
    {
        PinDirection.Left => (-1, 0),
        PinDirection.Right => (1, 0),
        PinDirection.Up => (0, -1),
        PinDirection.Down => (0, 1),
        _ => (0, 0)
    };

    /// <summary>
    /// Flat-array storage for the per-leg Dijkstra search, replacing the
    /// previous Dictionary&lt;State,int&gt; / Dictionary&lt;State,State&gt; pair.
    /// The search state is (cell, direction); with the grid bounds known
    /// up front, every state maps to a dense index:
    ///
    ///     index = ((iy * Width) + ix) * 4 + (int)dir
    ///
    /// where (ix, iy) are bounds-relative cell coordinates and dir is one
    /// of the four PinDirection values (Left=0, Right=1, Up=2, Down=3).
    /// Decode: dir = index &amp; 3, cell = index &gt;&gt; 2, ix = cell % Width,
    /// iy = cell / Width.
    ///
    /// Both arrays use 0 as "empty" so Reset is a plain memset:
    ///   - BestG stores g + 1 (0 = unvisited).
    ///   - Predecessor stores the predecessor's state index + 1 (0 = none).
    ///
    /// One instance is allocated per RouteAll and shared by every leg's
    /// search; Reset() runs at the top of each search.
    /// </summary>
    private sealed class SearchScratch
    {
        public readonly int[] BestG;
        public readonly int[] Predecessor;

        public SearchScratch(Rectangle bounds)
        {
            int states = bounds.Width * bounds.Height * 4;
            BestG = new int[states];
            Predecessor = new int[states];
        }

        public void Reset()
        {
            Array.Clear(BestG);
            Array.Clear(Predecessor);
        }
    }
}