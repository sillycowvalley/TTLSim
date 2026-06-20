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
///
/// This class is split across several files (WireRouter.*.cs); they are
/// all the same type. Grouping/net-identity, group-and-star routing, the
/// Dijkstra search core, the collision-retry pass, and the cost-grid /
/// cell plumbing each live in their own partial.
/// </summary>
public sealed partial class WireRouter : IConnectionRouter
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
        // Only active (visible-layer) connections are routed; an inactive
        // connection -- one with an endpoint on an invisible layer -- is
        // skipped entirely, exactly as the simulator drops it. Inactive items
        // are likewise not obstacles and don't seed pin cells. This is the one
        // place the router consults layer activity; the export writer's own
        // RouteAll call inherits it for free.
        var activeConnections =
            schematic.Connections.Where(schematic.IsConnectionActive).ToList();

        var polylines = new Dictionary<Connection, IReadOnlyList<Point>>(
            activeConnections.Count);
        var junctionsSet = new HashSet<Point>();

        if (activeConnections.Count == 0)
            return new RouteResult(polylines, new List<Point>());

        var bounds = ComputeGridBounds(schematic);

        var scratch = new SearchScratch(bounds);

        var bodyBlocked = new bool[bounds.Width, bounds.Height];
        foreach (var item in schematic.ActiveItems)
            StampRect(bodyBlocked, bounds, item.RoutingBounds, true);

        var foreignWirePenalty = new int[bounds.Width, bounds.Height];
        var ownNetPenalty = new int[bounds.Width, bounds.Height];
        var foreignWireDir = new byte[bounds.Width, bounds.Height];

        // Pre-seed pin cells.
        foreach (var item in schematic.ActiveItems)
            foreach (var pin in item.Pins)
                SetBit(foreignWireDir, bounds, pin.WorldPosition, ForeignPinSeed);

        var groups = GroupByPin(activeConnections);

        // Restored declaration-order routing — the constraint-based
        // ordering experiment helped some schematics and hurt others.
        // Net wins come from the cost-model and retry pass, not ordering.
        foreach (var group in groups.OrderBy(g => activeConnections.IndexOf(g[0])))
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
        TryFixCollisions(schematic, activeConnections, polylines,
            bounds, scratch, bodyBlocked,
            junctionsSet);

        return new RouteResult(polylines, junctionsSet.ToList());
    }
}
