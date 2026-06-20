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
    // ---------------------------------------------------------------- copy / cut / paste

    private (List<Device> Devices, List<SchematicItem> Items, List<Connection> Connections, List<HeaderLink> Links)
        GatherSelectionForClipboard()
    {
        var items = Schematic.Selected.ToList();
        var connections = Schematic.SelectedConnections.ToList();
        var links = Schematic.SelectedLinks.ToList();

        var devices = items
            .OfType<Unit>()
            .Select(u => u.Device)
            .Distinct()
            .ToList();

        return (devices, items, connections, links);
    }

    /// <summary>
    /// Copy the current selection to the clipboard. No-op (and no undo
    /// entry -- copy never mutates the schematic) if nothing is selected or
    /// the clipboard write fails. Resets the paste cascade: a new payload
    /// makes the old cascade meaningless.
    /// </summary>
    public void Copy()
    {
        var (devices, items, connections, links) = GatherSelectionForClipboard();
        if (items.Count == 0) return;

        if (ClipboardService.Copy(devices, items, connections, links, Schematic.Layers))
            pasteCascadeCount = 0;
    }

    /// <summary>
    /// Cut the current selection: copy it to the clipboard, then delete the
    /// originals. The delete reuses the exact same composite-delete path as
    /// the Delete key, so undo of a cut behaves identically to undo of a
    /// delete. If the clipboard write fails the originals are left alone --
    /// a cut that didn't copy must not destroy anything. Resets the paste
    /// cascade, same as Copy.
    /// </summary>
    public void Cut()
    {
        var (devices, items, connections, links) = GatherSelectionForClipboard();
        if (items.Count == 0) return;

        if (!ClipboardService.Cut(devices, items, connections, links, Schematic.Layers))
            return;   // clipboard write failed -- don't delete the originals

        pasteCascadeCount = 0;
        HandleDelete();
    }

    /// <summary>
    /// True when there is something on the clipboard to paste. Suitable for
    /// driving an Edit-menu item's enabled state.
    /// </summary>
    public bool CanPaste => ClipboardService.CanPaste;

    /// <summary>
    /// Paste the clipboard contents.
    ///
    /// <para>
    /// If the cursor is currently over the canvas, the paste is positioned
    /// under the cursor (via <see cref="PasteAt"/>, which also resets the
    /// cascade). Otherwise the paste lands at a cascade offset that steps
    /// further up-and-right on each successive non-mouse paste, so repeated
    /// Ctrl+V doesn't stack everything on one spot.
    /// </para>
    ///
    /// <para>
    /// Mouse-driven callers (a right-click "Paste here") should use
    /// <see cref="PasteAt"/> directly to paste at an explicit point.
    /// </para>
    /// </summary>
    public void Paste()
    {
        if (isMouseOverCanvas)
        {
            PasteAt(lastMouseGrid);
            return;
        }

        pasteCascadeCount++;
        var offset = new Point(
            CascadeStep.Width * pasteCascadeCount,
            CascadeStep.Height * pasteCascadeCount);

        var pasted = PasteFromClipboardInto(offset);
        if (pasted is not null && pasted.Items.Count > 0)
        {
            Invalidate();
            OnSelectionChanged();
        }
        else
        {
            // Nothing pasted -- don't leave the cascade counter advanced for
            // a paste that didn't happen.
            pasteCascadeCount--;
        }
    }

    /// <summary>
    /// Paste the clipboard contents positioned at an explicit grid point.
    /// The pasted group is translated so its bounding-box centre lands on
    /// <paramref name="gridPoint"/>. Resets the cascade counter -- an
    /// explicitly-positioned paste is a fresh anchor.
    /// </summary>
    public void PasteAt(Point gridPoint)
    {
        // PasteFromClipboardInto takes an OFFSET, but the caller gave us a
        // target point and we can't know the payload's bounds until it has
        // been rebuilt. So: rebuild at zero offset inside a "Paste"
        // composite, measure the result, then translate it onto gridPoint
        // with MoveItemCommands recorded into the SAME composite -- the whole
        // thing stays one undo step.
        UndoStack.BeginComposite("Paste");
        try
        {
            var pasted = PasteFromClipboardInto(Point.Empty);
            if (pasted is null || pasted.Items.Count == 0)
            {
                UndoStack.EndComposite();   // discards empty buffer
                return;
            }

            // Bounding box of the freshly pasted items, in grid units.
            int minX = int.MaxValue, minY = int.MaxValue;
            int maxX = int.MinValue, maxY = int.MinValue;
            foreach (var item in pasted.Items)
            {
                var b = item.Bounds;
                if (b.Left < minX) minX = b.Left;
                if (b.Top < minY) minY = b.Top;
                if (b.Right > maxX) maxX = b.Right;
                if (b.Bottom > maxY) maxY = b.Bottom;
            }

            int centreX = (minX + maxX) / 2;
            int centreY = (minY + maxY) / 2;
            int dx = gridPoint.X - centreX;
            int dy = gridPoint.Y - centreY;

            if (dx != 0 || dy != 0)
            {
                foreach (var item in pasted.Items)
                {
                    var from = item.Position;
                    var to = new Point(from.X + dx, from.Y + dy);
                    item.Position = from;   // MoveItemCommand.Execute applies 'to'
                    UndoStack.Do(new MoveItemCommand(item, from, to));
                }
            }
        }
        finally
        {
            UndoStack.EndComposite();
        }

        // A mouse-positioned paste is a fresh anchor: the next cascade paste
        // starts over rather than stepping off a stale count.
        pasteCascadeCount = 0;

        Invalidate();
        OnSelectionChanged();
    }

    /// <summary>
    /// Rebuild the clipboard payload with fresh ids, offset every pasted item
    /// by <paramref name="offset"/> grid units, and add the whole result --
    /// devices, items, connections -- to the schematic as ONE composite
    /// ("Paste"). The pasted items become the new selection.
    ///
    /// <para>
    /// Returns the MapResult on success, or null if there was nothing to
    /// paste or the payload could not be rebuilt. Note this method records
    /// its own composite; <see cref="BeginCopyDrag"/> and
    /// <see cref="PasteAt"/> call it while a composite is ALREADY open, which
    /// UndoStack handles via its nesting depth counter -- the inner composite
    /// folds into the outer one.
    /// </para>
    /// </summary>
    private SchematicDtoMapper.MapResult? PasteFromClipboardInto(Point offset)
    {
        var result = ClipboardService.Paste(Schematic);
        if (result is null || result.Items.Count == 0)
            return null;

        if (offset != Point.Empty)
        {
            foreach (var item in result.Items)
                item.Position = new Point(
                    item.Position.X + offset.X,
                    item.Position.Y + offset.Y);
        }

        UndoStack.DoComposite("Paste", () =>
        {
            foreach (var device in result.Devices)
                UndoStack.Do(new AddDeviceCommand(device));
            foreach (var item in result.Items)
                UndoStack.Do(new AddItemCommand(item));
            foreach (var connection in result.Connections)
                UndoStack.Do(new AddConnectionCommand(connection));
            foreach (var link in result.Links)
                UndoStack.Do(new AddHeaderLinkCommand(link));
        });

        // Make the pasted items and connections the selection, mirroring
        // DropPart / DropSymbol.
        Schematic.ClearSelection();
        foreach (var item in result.Items)
            item.Selected = true;
        foreach (var connection in result.Connections)
            connection.Selected = true;
        foreach (var link in result.Links)
            link.Selected = true;
        OnSelectionChanged();

        return result;
    }
}
