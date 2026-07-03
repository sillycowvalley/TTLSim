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

    // Per-direction step deltas indexed by (int)PinDirection. Built from
    // DirToDelta at type initialisation so the mapping can never drift from
    // the switch; the hot search loop indexes these arrays instead of
    // calling DirToDelta (which profiling showed was not being inlined).
    private static readonly int[] s_dx = new int[4];
    private static readonly int[] s_dy = new int[4];

    static WireRouter()
    {
        foreach (var dir in s_directions)
        {
            var (dx, dy) = DirToDelta(dir);
            s_dx[(int)dir] = dx;
            s_dy[(int)dir] = dy;
        }
    }

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

    private static void CarveCorridor(bool[] blocked, Rectangle bounds, Pin pin)
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

    private static void Clear(bool[] blocked, Rectangle bounds, Point cell)
    {
        if (TryIndex(bounds, cell.X, cell.Y, out int ix, out int iy))
            blocked[(iy * bounds.Width) + ix] = false;
    }

    // A* search to a single pin. The heuristic is Manhattan distance to the
    // end cell times StepCost. It is ADMISSIBLE (never overestimates)
    // because every real step costs at least StepCost -- all penalties
    // (bend, foreign-wire, parallel, vertex-transit) are additions on top
    // of it -- and it is CONSISTENT because one step changes the Manhattan
    // distance by at most 1 while costing at least StepCost. So the search
    // finds exactly the same minimal-cost routes Dijkstra did; among
    // equal-cost routes the tie-break order differs, so geometry may vary,
    // but the result is still deterministic and cost-optimal.
    //
    // The open set is the packed-long heap in SearchScratch (see
    // SearchQueue): priority is f = g + h. g is not carried in the queue;
    // it is recovered from the scratch best-g on dequeue, and an entry is
    // stale iff its f exceeds the current best g plus h for its state --
    // i.e. the state was improved after this entry was pushed.
    private static List<Point>? Search(
        Rectangle bounds, SearchScratch scratch, bool[] blocked,
        int[] foreignWirePenalty, int[] ownNetPenalty,
        byte[] foreignWireDir,
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

        int startIdx = ((startIy * width) + startIx) * 4 + (int)startDir;
        scratch.Visit(startIdx, 1, 0);   // g = 0, no predecessor

        int Heuristic(int worldX, int worldY)
            => (Math.Abs(worldX - end.X) + Math.Abs(worldY - end.Y)) * StepCost;

        var queue = scratch.Queue;
        queue.Enqueue(startIdx, Heuristic(start.X, start.Y));

        int goal = -1;

        while (queue.TryDequeue(out int stateIdx, out int f))
        {
            int dirInt = stateIdx & 3;
            int cellIdx = stateIdx >> 2;
            int cx = cellIdx % width;
            int cy = cellIdx / width;
            int worldX = cx + bounds.X;
            int worldY = cy + bounds.Y;

            // Every enqueued state has been Visit()ed, so best-g exists.
            int g = scratch.GetBestGPlus1(stateIdx) - 1;
            if (f > g + Heuristic(worldX, worldY)) continue;   // stale entry

            if (worldX == end.X && worldY == end.Y && dirInt == (int)requiredArrival)
            {
                goal = stateIdx;
                break;
            }

            byte hereMask = foreignWireDir[cellIdx];
            for (int d = 0; d < 4; d++)
            {
                int nx = worldX + s_dx[d];
                int ny = worldY + s_dy[d];
                if (!TryIndex(bounds, nx, ny, out int ix, out int iy)) continue;
                int nCell = (iy * width) + ix;
                if (blocked[nCell]) continue;

                bool bending = d != dirInt;

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

                byte enteredMask = foreignWireDir[nCell];
                int parallelCost = MovingParallelToForeignMask(enteredMask, d)
                    ? ForeignParallelPenalty : 0;
                int vertexTransitCost = (enteredMask & ForeignVertex) != 0
                    ? ForeignVertexTransitPenalty : 0;

                int stepCost = StepCost
                             + foreignWirePenalty[nCell]
                             + ownNetPenalty[nCell]
                             + parallelCost
                             + vertexTransitCost
                             + bendCost;
                int ng = g + stepCost;

                int nextIdx = nCell * 4 + d;
                int existingPlus1 = scratch.GetBestGPlus1(nextIdx);
                if (existingPlus1 != 0 && ng + 1 >= existingPlus1) continue;

                scratch.Visit(nextIdx, ng + 1, stateIdx + 1);
                queue.Enqueue(nextIdx, ng + Heuristic(nx, ny));
            }
        }

        return goal >= 0
            ? BacktraceCells(scratch, bounds, startIdx, goal)
            : null;
    }

    // A* search to any cell of an already-routed net. The heuristic is
    // Manhattan distance to the BOUNDING RECTANGLE of the target cells
    // (zero inside it), times StepCost -- admissible because no target
    // cell is closer than its bounding box. Weaker than the single-target
    // heuristic when the net sprawls, but still never worse than the old
    // Dijkstra behaviour (h = 0 degenerates to exactly that).
    private static List<Point>? SearchToAnyCell(
        Rectangle bounds, SearchScratch scratch, bool[] blocked,
        int[] foreignWirePenalty, int[] ownNetPenalty,
        byte[] foreignWireDir,
        Point start, PinDirection startDir, HashSet<Point> targetCells,
        HashSet<Point>? hardBlockBendCells)
    {
        if (!TryIndex(bounds, start.X, start.Y, out int startIx, out int startIy))
            return null;

        scratch.Reset();
        int width = bounds.Width;

        int startIdx = ((startIy * width) + startIx) * 4 + (int)startDir;
        scratch.Visit(startIdx, 1, 0);   // g = 0, no predecessor

        // Bounding box of the target cells, in world coordinates.
        int tMinX = int.MaxValue, tMinY = int.MaxValue;
        int tMaxX = int.MinValue, tMaxY = int.MinValue;
        foreach (var t in targetCells)
        {
            if (t.X < tMinX) tMinX = t.X;
            if (t.Y < tMinY) tMinY = t.Y;
            if (t.X > tMaxX) tMaxX = t.X;
            if (t.Y > tMaxY) tMaxY = t.Y;
        }
        bool haveTargets = tMinX != int.MaxValue;

        int Heuristic(int worldX, int worldY)
        {
            if (!haveTargets) return 0;
            int hx = worldX < tMinX ? tMinX - worldX
                   : worldX > tMaxX ? worldX - tMaxX : 0;
            int hy = worldY < tMinY ? tMinY - worldY
                   : worldY > tMaxY ? worldY - tMaxY : 0;
            return (hx + hy) * StepCost;
        }

        var queue = scratch.Queue;
        queue.Enqueue(startIdx, Heuristic(start.X, start.Y));

        int goal = -1;

        while (queue.TryDequeue(out int stateIdx, out int f))
        {
            int dirInt = stateIdx & 3;
            int cellIdx = stateIdx >> 2;
            int cx = cellIdx % width;
            int cy = cellIdx / width;
            int worldX = cx + bounds.X;
            int worldY = cy + bounds.Y;

            // Every enqueued state has been Visit()ed, so best-g exists.
            int g = scratch.GetBestGPlus1(stateIdx) - 1;
            if (f > g + Heuristic(worldX, worldY)) continue;   // stale entry

            if (stateIdx != startIdx
                && targetCells.Contains(new Point(worldX, worldY)))
            {
                goal = stateIdx;
                break;
            }

            byte hereMask = foreignWireDir[cellIdx];
            for (int d = 0; d < 4; d++)
            {
                int nx = worldX + s_dx[d];
                int ny = worldY + s_dy[d];
                if (!TryIndex(bounds, nx, ny, out int ix, out int iy)) continue;
                int nCell = (iy * width) + ix;
                if (blocked[nCell]) continue;

                bool bending = d != dirInt;

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

                byte enteredMask = foreignWireDir[nCell];
                int parallelCost = MovingParallelToForeignMask(enteredMask, d)
                    ? ForeignParallelPenalty : 0;
                int vertexTransitCost = (enteredMask & ForeignVertex) != 0
                    ? ForeignVertexTransitPenalty : 0;

                int stepCost = StepCost
                             + foreignWirePenalty[nCell]
                             + ownNetPenalty[nCell]
                             + parallelCost
                             + vertexTransitCost
                             + bendCost;
                int ng = g + stepCost;

                int nextIdx = nCell * 4 + d;
                int existingPlus1 = scratch.GetBestGPlus1(nextIdx);
                if (existingPlus1 != 0 && ng + 1 >= existingPlus1) continue;

                scratch.Visit(nextIdx, ng + 1, stateIdx + 1);
                queue.Enqueue(nextIdx, ng + Heuristic(nx, ny));
            }
        }

        return goal >= 0
            ? BacktraceCells(scratch, bounds, startIdx, goal)
            : null;
    }

    private static bool MovingParallelToForeignMask(byte mask, int dirInt)
    {
        if (mask == 0) return false;
        bool horizontal = s_dx[dirInt] != 0;
        return horizontal
            ? (mask & ForeignH) != 0
            : (mask & ForeignV) != 0;
    }

    // Runs immediately after the same leg's search, so every state on the
    // predecessor chain was Visit()ed in the current generation — the raw
    // Predecessor reads below are valid without a generation check.
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
    /// Flat-array storage for the per-leg A* search, replacing the
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
    ///   - BestG stores g + 1 (0 = unvisited).
    ///   - Predecessor stores the predecessor's state index + 1 (0 = none).
    ///
    /// GENERATION STAMPING: BestG and Predecessor are only valid for a
    /// state whose Generation entry equals the current generation. Reset
    /// is therefore a counter increment, not a full-grid memset -- once
    /// the A* heuristic made each leg touch only a narrow cone of cells,
    /// the per-leg Array.Clear of the whole grid became a measurable cost
    /// in its own right (Buffer._ZeroMemory in profiles). Callers must go
    /// through GetBestGPlus1 / Visit rather than the arrays directly.
    ///
    /// One instance is allocated per RouteAll and shared by every leg's
    /// search; Reset() runs at the top of each search.
    /// </summary>
    private sealed class SearchScratch
    {
        private readonly int[] BestG;
        public readonly int[] Predecessor;
        private readonly int[] generation;
        private int currentGeneration;

        /// <summary>Open-set heap, shared across legs; cleared by Reset.</summary>
        public readonly SearchQueue Queue = new();

        public SearchScratch(Rectangle bounds)
        {
            int states = bounds.Width * bounds.Height * 4;
            BestG = new int[states];
            Predecessor = new int[states];
            generation = new int[states];
            currentGeneration = 0;
        }

        public void Reset()
        {
            if (currentGeneration == int.MaxValue)
            {
                // Practically unreachable, but keep the wraparound sound.
                Array.Clear(generation);
                currentGeneration = 0;
            }
            currentGeneration++;
            Queue.Clear();
        }

        /// <summary>g + 1 for the state, or 0 if unvisited this leg.</summary>
        public int GetBestGPlus1(int stateIdx)
            => generation[stateIdx] == currentGeneration ? BestG[stateIdx] : 0;

        /// <summary>Record g + 1 and predecessor (+1) for the state.</summary>
        public void Visit(int stateIdx, int gPlus1, int predecessorPlus1)
        {
            generation[stateIdx] = currentGeneration;
            BestG[stateIdx] = gPlus1;
            Predecessor[stateIdx] = predecessorPlus1;
        }
    }

    /// <summary>
    /// Bucket-queue (Dial's algorithm) open set for the A* searches,
    /// replacing the binary heap whose sift-down was ~33% of routing time
    /// (memory latency walking a large heap). Priorities are small
    /// non-negative integers, and with the consistent Manhattan heuristic
    /// the dequeued f never decreases — so states live in a circular array
    /// of buckets indexed by (f &amp; mask): O(1) enqueue, O(1) dequeue plus a
    /// bounded scan past empty buckets, no sifting at all.
    ///
    /// Step costs here are NOT statically bounded (wire penalties stack
    /// per cell), so no fixed span is assumed: the live f-span
    /// (maxF − minF) is tracked, and the ring doubles and redistributes on
    /// the rare enqueue that would exceed it. While the span invariant
    /// holds, each bucket holds exactly one f value, which is what lets
    /// redistribution reconstruct each bucket's f.
    ///
    /// Within one bucket (equal f) states pop LIFO — deterministic, but a
    /// different tie-break than the heap's, so equal-cost routes may land
    /// in different (equally good) places than the heap version produced.
    /// </summary>
    private sealed class SearchQueue
    {
        private int[]?[] buckets = new int[]?[256];
        private int[] counts = new int[256];
        private int mask = 255;
        private int minF;
        private int maxF;
        private int total;

        public void Clear()
        {
            // counts can only be non-zero when entries remain (a search
            // that broke at its goal leaves the rest of the open set
            // behind). All-zero counts stay all-zero, so skip the wipe
            // when the queue drained naturally.
            if (total > 0)
                Array.Clear(counts);
            total = 0;
        }

        public void Enqueue(int stateIdx, int f)
        {
            if (total == 0)
            {
                minF = f;
                maxF = f;
            }
            else
            {
                // Existing entries are positioned relative to the CURRENT
                // minF; capture it before updating so Grow can reconstruct
                // their f values correctly even on the defensive f < minF
                // path (which a consistent heuristic never takes).
                int anchor = minF;
                if (f < minF) minF = f;
                if (f > maxF) maxF = f;
                if (maxF - minF > mask)
                    Grow(anchor);
            }

            int idx = f & mask;
            var bucket = buckets[idx];
            if (bucket is null)
                buckets[idx] = bucket = new int[8];
            else if (counts[idx] == bucket.Length)
            {
                Array.Resize(ref bucket, bucket.Length * 2);
                buckets[idx] = bucket;
            }
            bucket[counts[idx]++] = stateIdx;
            total++;
        }

        public bool TryDequeue(out int stateIdx, out int f)
        {
            if (total == 0)
            {
                stateIdx = 0;
                f = 0;
                return false;
            }

            int idx = minF & mask;
            while (counts[idx] == 0)
            {
                minF++;
                idx = minF & mask;
            }

            stateIdx = buckets[idx]![--counts[idx]];
            f = minF;
            total--;
            return true;
        }

        /// <summary>
        /// Double the ring until the current span fits, redistributing the
        /// existing entries. Rare: only fires when one leg's f-span exceeds
        /// every span seen before. While the old invariant held, bucket b's
        /// single f value is reconstructible as <paramref name="anchor"/>
        /// (the minF the entries were positioned against) plus b's offset
        /// from the anchor's old slot, modulo the old size.
        /// </summary>
        private void Grow(int anchor)
        {
            int oldSize = mask + 1;
            int oldMask = mask;
            var oldBuckets = buckets;
            var oldCounts = counts;

            int newSize = oldSize;
            while (maxF - minF > newSize - 1)
                newSize *= 2;

            buckets = new int[]?[newSize];
            counts = new int[newSize];
            mask = newSize - 1;

            for (int b = 0; b < oldSize; b++)
            {
                int n = oldCounts[b];
                if (n == 0) continue;

                int f = anchor + (((b - (anchor & oldMask)) + oldSize) & oldMask);
                int idx = f & mask;

                var src = oldBuckets[b]!;
                var dst = buckets[idx];
                if (dst is null || dst.Length < n)
                    buckets[idx] = dst = new int[Math.Max(8, n)];
                Array.Copy(src, dst, n);
                counts[idx] = n;
            }
        }
    }
}