using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using TTLSim.UI.Components;
using TTLSim.UI.Model;

namespace TTLSim.UI.Routing;

public sealed partial class WireRouter
{
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

    private static PinDirection Opposite(PinDirection d) => d switch
    {
        PinDirection.Left => PinDirection.Right,
        PinDirection.Right => PinDirection.Left,
        PinDirection.Up => PinDirection.Down,
        PinDirection.Down => PinDirection.Up,
        _ => d
    };

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
