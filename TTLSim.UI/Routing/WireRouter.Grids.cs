using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using TTLSim.UI.Components;
using TTLSim.UI.Model;

namespace TTLSim.UI.Routing;

public sealed partial class WireRouter
{
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

    private static string ShortId(string? id) =>
        string.IsNullOrEmpty(id) ? "?" : id.Length <= 4 ? id : id[..4];
}
