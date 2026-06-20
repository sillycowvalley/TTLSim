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
    // ---------------------------------------------------------------- coordinate math

    /// <summary>Convert a screen-pixel point to a grid-unit point.</summary>
    public Point ScreenToGrid(Point screen)
    {
        float gx = (screen.X - PanOffset.X) / (Zoom * GridPitch);
        float gy = (screen.Y - PanOffset.Y) / (Zoom * GridPitch);
        return new Point((int)Math.Round(gx), (int)Math.Round(gy));
    }

    /// <summary>Set zoom and pan together, e.g. when restoring view state from a file.</summary>
    public void SetView(float zoom, PointF pan)
    {
        Zoom = Math.Clamp(zoom, 0.05f, 100f);
        PanOffset = pan;
        Invalidate();
        ViewChanged?.Invoke(this, EventArgs.Empty);
    }


    /// <summary>Convert a grid-unit point to a screen-pixel point.</summary>
    public PointF GridToScreen(Point grid) =>
        new(grid.X * Zoom * GridPitch + PanOffset.X,
            grid.Y * Zoom * GridPitch + PanOffset.Y);

    // ---------------------------------------------------------------- zoom

    protected override void OnMouseWheel(MouseEventArgs e)
    {
        base.OnMouseWheel(e);
        float factor = e.Delta > 0 ? 1.15f : 1f / 1.15f;
        ZoomAt(e.Location, factor);
    }

    public void ZoomAt(Point screenPoint, float factor)
    {
        float newZoom = Math.Clamp(Zoom * factor, 0.05f, 100f);
        if (Math.Abs(newZoom - Zoom) < 0.0001f) return;

        float gx = (screenPoint.X - PanOffset.X) / (Zoom * GridPitch);
        float gy = (screenPoint.Y - PanOffset.Y) / (Zoom * GridPitch);
        Zoom = newZoom;
        PanOffset = new PointF(
            screenPoint.X - gx * Zoom * GridPitch,
            screenPoint.Y - gy * Zoom * GridPitch);

        Invalidate();
        ViewChanged?.Invoke(this, EventArgs.Empty);
    }

    public void ResetView()
    {
        Zoom = 4f;
        PanOffset = new PointF(40, 40);
        Invalidate();
        ViewChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>
    /// Pan (no zoom change) so the given grid-space point sits at the centre
    /// of the visible canvas. Used by the output panel's click-to-locate.
    /// </summary>
    public void CenterOn(Point gridPoint)
    {
        PanOffset = new PointF(
            ClientSize.Width / 2f - gridPoint.X * Zoom * GridPitch,
            ClientSize.Height / 2f - gridPoint.Y * Zoom * GridPitch);
        Invalidate();
        ViewChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>
    /// Look up an item by its Id. Returns null if no item matches. Power
    /// symbols and clock sources live in Items; Units also live there.
    /// </summary>
    public SchematicItem? FindItemById(string id)
    {
        foreach (var item in Schematic.Items)
            if (item.Id == id) return item;
        return null;
    }

    /// <summary>
    /// Look up a connection by its Id. Returns null if no connection matches.
    /// Used by diagnostic-locate to centre/select wires named in build or
    /// export diagnostics.
    /// </summary>
    public Connection? FindConnectionById(string id)
    {
        foreach (var c in Schematic.Connections)
            if (c.Id == id) return c;
        return null;
    }

    /// <summary>
    /// True for combinational gate units (AND/OR/NAND/NOR/XOR/NOT) whose
    /// every input is connected but whose output drives nothing -- typically
    /// the spare gates of a shared package tied off to GND/VCC. Used by
    /// FitView to ignore tie-off clutter when framing the schematic.
    /// </summary>
    private bool IsUnusedGate(SchematicItem item)
    {
        if (item is not (AndGateUnit or OrGateUnit or XorGateUnit
                         or NandGateUnit or NorGateUnit or NotGateUnit))
            return false;

        // Unrotated LocalDirection: Left == input, Right == output (every
        // gate unit goes through Unit.BuildLeftInputsRightOutput).
        bool anyInput = false, anyOutput = false;
        foreach (var pin in item.Pins)
        {
            if (pin.LocalDirection == PinDirection.Left) anyInput = true;
            if (pin.LocalDirection == PinDirection.Right) anyOutput = true;
        }
        if (!anyInput || !anyOutput) return false;

        // One pass over the connection list, classifying each endpoint that
        // sits on this item. Output with any connection => gate is used.
        // After the pass, every input must have been seen at least once.
        var inputsSeen = new HashSet<Pin>();
        foreach (var c in Schematic.ConnectionsOn(item))
        {
            foreach (var ep in new[] { c.A, c.B })
            {
                if (ep.Owner != item) continue;
                if (ep.LocalDirection == PinDirection.Right) return false;
                if (ep.LocalDirection == PinDirection.Left) inputsSeen.Add(ep);
            }
        }

        foreach (var pin in item.Pins)
        {
            if (pin.LocalDirection == PinDirection.Left && !inputsSeen.Contains(pin))
                return false;
        }
        return true;
    }

    /// <summary>
    /// Zoom and pan so that the entire schematic fits in the visible
    /// canvas area, with a small margin. Falls back to ResetView when
    /// the schematic is empty.
    /// </summary>
    public void FitView()
    {
        if (!Schematic.ActiveItems.Any())
        {
            ResetView();
            return;
        }

        // Bounding box across every item AND every routed wire cell, in
        // grid units. Item.Bounds only covers the symbol body/pins -- wire
        // polylines can swing outside that on detours, so we also walk the
        // router's polylines so the fit shows the entire visible drawing.
        int minX = int.MaxValue, minY = int.MaxValue;
        int maxX = int.MinValue, maxY = int.MinValue;

        // Pre-compute the set of unused gates so we can skip them and their
        // tie-off connections when framing the view.
        var unusedGates = new HashSet<SchematicItem>();
        foreach (var item in Schematic.ActiveItems)
            if (IsUnusedGate(item)) unusedGates.Add(item);

        // Power symbols (GND/VCC) whose every connection terminates on an
        // unused gate are themselves just tie-off clutter -- exclude those
        // too. A power symbol with at least one connection to a USED item,
        // or with no connections at all, stays in the frame.
        var hiddenPower = new HashSet<SchematicItem>();
        foreach (var item in Schematic.ActiveItems)
        {
            if (item is not (GndSymbol or VccSymbol)) continue;

            bool hasAny = false;
            bool allToUnusedGates = true;
            foreach (var c in Schematic.ConnectionsOn(item))
            {
                hasAny = true;
                var other = c.A.Owner == item ? c.B.Owner : c.A.Owner;
                if (other is null || !unusedGates.Contains(other))
                {
                    allToUnusedGates = false;
                    break;
                }
            }
            if (hasAny && allToUnusedGates) hiddenPower.Add(item);
        }

        foreach (var item in Schematic.ActiveItems)
        {
            if (unusedGates.Contains(item)) continue;
            if (hiddenPower.Contains(item)) continue;
            var b = item is IBackgroundItem ? item.Bounds : item.RoutingBounds;
            if (b.Left < minX) minX = b.Left;
            if (b.Top < minY) minY = b.Top;
            if (b.Right > maxX) maxX = b.Right;
            if (b.Bottom > maxY) maxY = b.Bottom;
        }

        foreach (var kvp in Routes.Polylines)
        {
            var c = kvp.Key;
            // Skip wires on an invisible layer -- a hidden wire must not drag
            // the viewport. (Routes still routes every connection until the
            // router increment, so the filter lives here for now.)
            if (!Schematic.IsConnectionActive(c)) continue;
            // Skip connections whose only purpose is to tie off an unused gate.
            if ((c.A.Owner is { } oa && (unusedGates.Contains(oa) || hiddenPower.Contains(oa))) ||
                (c.B.Owner is { } ob && (unusedGates.Contains(ob) || hiddenPower.Contains(ob))))
                continue;

            foreach (var pt in kvp.Value)
            {
                if (pt.X < minX) minX = pt.X;
                if (pt.Y < minY) minY = pt.Y;
                if (pt.X > maxX) maxX = pt.X;
                if (pt.Y > maxY) maxY = pt.Y;
            }
        }

        int boxW = maxX - minX;
        int boxH = maxY - minY;
        if (boxW <= 0 || boxH <= 0 || ClientSize.Width <= 0 || ClientSize.Height <= 0)
        {
            ResetView();
            return;
        }

        // 5% margin on each side -> usable area is 90% of the canvas.
        const float marginFrac = 0.02f;
        float usableW = ClientSize.Width * (1f - 2f * marginFrac);
        float usableH = ClientSize.Height * (1f - 2f * marginFrac);

        // Box width/height in screen pixels at zoom 1 is (boxW * GridPitch).
        float zoomFitX = usableW / (boxW * GridPitch);
        float zoomFitY = usableH / (boxH * GridPitch);
        float fitZoom = Math.Min(zoomFitX, zoomFitY);
        // Fit is a deliberate one-shot operation -- allow any zoom level
        // the geometry demands, well outside the interactive 0.5..40 range.
        // A floor above zero just avoids degenerate cases.
        fitZoom = Math.Max(fitZoom, 0.001f);

        // Centre the schematic in the canvas at the chosen zoom.
        float boxCentreXgrid = (minX + maxX) / 2f;
        float boxCentreYgrid = (minY + maxY) / 2f;
        float panX = ClientSize.Width / 2f - boxCentreXgrid * fitZoom * GridPitch;
        float panY = ClientSize.Height / 2f - boxCentreYgrid * fitZoom * GridPitch;

        Zoom = fitZoom;
        PanOffset = new PointF(panX, panY);
        Invalidate();
        ViewChanged?.Invoke(this, EventArgs.Empty);
    }
}
