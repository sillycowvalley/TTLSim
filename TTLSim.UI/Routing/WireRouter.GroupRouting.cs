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
}
