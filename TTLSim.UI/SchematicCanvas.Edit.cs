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
    // ---------------------------------------------------------------- rotation

    /// <summary>True iff at least one item is selected. Selected connections
    /// don't block rotation -- they have no rotation of their own and follow
    /// their endpoint pins, the same policy NudgeSelection applies to moves.
    /// (Matters for Select All: Ctrl+A selects wires too.)</summary>
    public bool CanRotateSelection => Schematic.Selected.Any();

    /// <summary>
    /// Rotate the selection 90 degrees clockwise (or counter-clockwise if
    /// <paramref name="clockwise"/> is false).
    ///
    /// <para>
    /// A single item rotates in place about its own pivot, exactly as before.
    /// Multiple items rotate as a rigid group: each item steps its own
    /// rotation AND its pivot orbits the centre of the selection's total
    /// bounds, so the arrangement turns as one. Wires follow their pins.
    /// The whole group rotation is one composite undo step.
    /// </para>
    /// </summary>
    public void RotateSelection(bool clockwise)
    {
        var items = Schematic.Selected.ToList();
        if (items.Count == 0) return;

        if (items.Count == 1)
        {
            var item = items[0];
            var from = item.Rotation;
            var to = clockwise
                ? RotateCw(from)
                : RotateCcw(from);
            if (from == to) return;

            UndoStack.DoComposite($"Rotate {item.GetType().Name}", () =>
            {
                UndoStack.Do(new RotateItemCommand(item, from, to));
            });
            return;
        }

        // Group rotation. The turn's centre is the centre of the union of the
        // selected items' visual bounds. All coordinates are integer grid
        // units, so a 90-degree turn of integer offsets about an integer
        // centre stays exactly on-grid.
        var unionBounds = items[0].Bounds;
        foreach (var item in items.Skip(1))
            unionBounds = Rectangle.Union(unionBounds, item.Bounds);
        int cx = unionBounds.X + unionBounds.Width / 2;
        int cy = unionBounds.Y + unionBounds.Height / 2;

        UndoStack.DoComposite($"Rotate {items.Count} items", () =>
        {
            foreach (var item in items)
            {
                var fromRot = item.Rotation;
                var toRot = clockwise ? RotateCw(fromRot) : RotateCcw(fromRot);
                if (fromRot != toRot)
                    UndoStack.Do(new RotateItemCommand(item, fromRot, toRot));

                // Orbit the item's pivot (its rotation-invariant visual
                // centre) about the group centre. Grid Y grows downward, so
                // clockwise is (dx, dy) -> (-dy, dx).
                var pivot = item.Pivot;
                int dx = pivot.X - cx;
                int dy = pivot.Y - cy;
                var newPivot = clockwise
                    ? new Point(cx - dy, cy + dx)
                    : new Point(cx + dy, cy - dx);

                // Pivot == Position + (Width/2, Height/2) with truncating
                // division, so this inversion is exact and round-trips.
                var fromPos = item.Position;
                var toPos = new Point(newPivot.X - item.Size.Width / 2,
                                      newPivot.Y - item.Size.Height / 2);
                if (fromPos != toPos)
                    UndoStack.Do(new MoveItemCommand(item, fromPos, toPos));
            }
        });

        Invalidate();
    }

    private static Rotation RotateCw(Rotation r) => r switch
    {
        Rotation.R0 => Rotation.R90,
        Rotation.R90 => Rotation.R180,
        Rotation.R180 => Rotation.R270,
        Rotation.R270 => Rotation.R0,
        _ => r
    };

    private static Rotation RotateCcw(Rotation r) => r switch
    {
        Rotation.R0 => Rotation.R270,
        Rotation.R90 => Rotation.R0,
        Rotation.R180 => Rotation.R90,
        Rotation.R270 => Rotation.R180,
        _ => r
    };


    /// <summary>
    /// Delete-key handler. Selecting any Unit promotes to deleting the entire
    /// Device (all its units, its power unit if any, and the Device record).
    /// Selected non-Unit items (VccSymbol, GndSymbol) and selected connections
    /// are deleted directly. Connections touching anything being deleted are
    /// deleted implicitly. Everything goes in one composite.
    /// </summary>
    private void HandleDelete()
    {
        var selectedItems = Schematic.Selected.ToList();
        var selectedConnections = Schematic.SelectedConnections.ToList();

        // Devices selected via any of their units. Each device contributes
        // ALL its units (and its power unit, if any) to the deletion set.
        var devicesToDelete = new HashSet<Device>(
            selectedItems.OfType<Unit>().Select(u => u.Device));

        // All units that go because their device is going.
        var unitsImplicit = new HashSet<SchematicItem>();
        foreach (var device in devicesToDelete)
        {
            foreach (var unit in device.Units)
                unitsImplicit.Add(unit);
            if (device.PowerUnit != null)
                unitsImplicit.Add(device.PowerUnit);
        }

        // Non-Unit items the user explicitly selected (VCC, GND).
        var nonUnitItemsSelected = selectedItems
            .Where(i => i is not Unit)
            .ToList();

        // Items going away in total: implicit-via-device-closure + explicit-non-unit.
        var allItemsToRemove = new HashSet<SchematicItem>(unitsImplicit);
        foreach (var i in nonUnitItemsSelected) allItemsToRemove.Add(i);

        // Connections implicitly removed because an attached item is going.
        var implicitConnections = new HashSet<Connection>();
        foreach (var item in allItemsToRemove)
            foreach (var c in Schematic.ConnectionsOn(item))
                implicitConnections.Add(c);

        foreach (var c in selectedConnections)
            implicitConnections.Remove(c);

        // Header links implicitly removed because an attached header is going.
        var implicitLinks = new HashSet<HeaderLink>();
        foreach (var item in allItemsToRemove)
            foreach (var l in Schematic.LinksOn(item))
                implicitLinks.Add(l);

        var selectedLinks = Schematic.SelectedLinks.ToList();
        foreach (var l in selectedLinks)
            implicitLinks.Remove(l);

        int total = allItemsToRemove.Count + selectedConnections.Count
                  + implicitConnections.Count + devicesToDelete.Count
                  + selectedLinks.Count + implicitLinks.Count;
        if (total == 0) return;

        string description;
        if (devicesToDelete.Count == 1 && allItemsToRemove.Count == devicesToDelete.First().Units.Count
            && nonUnitItemsSelected.Count == 0 && selectedConnections.Count == 0 && selectedLinks.Count == 0)
        {
            description = $"Delete {devicesToDelete.First().Designator}";
        }
        else if (devicesToDelete.Count == 0 && nonUnitItemsSelected.Count == 1
                 && selectedConnections.Count == 0 && selectedLinks.Count == 0)
        {
            description = $"Delete {nonUnitItemsSelected[0].GetType().Name}";
        }
        else if (devicesToDelete.Count == 0 && nonUnitItemsSelected.Count == 0
                 && selectedConnections.Count == 1 && selectedLinks.Count == 0)
        {
            description = "Delete Connection";
        }
        else if (devicesToDelete.Count == 0 && nonUnitItemsSelected.Count == 0
                 && selectedConnections.Count == 0 && selectedLinks.Count == 1)
        {
            description = "Delete Header Link";
        }
        else
        {
            int visibleTotal = devicesToDelete.Count + nonUnitItemsSelected.Count
                             + selectedConnections.Count + selectedLinks.Count;
            description = $"Delete {visibleTotal} items";
        }

        UndoStack.DoComposite(description, () =>
        {
            // Order matters for clean undo: connections first, then items, then devices.
            // Undo replays in reverse so devices come back first, then items, then connections.
            foreach (var c in implicitConnections)
                UndoStack.Do(new RemoveConnectionCommand(c));
            foreach (var c in selectedConnections)
                UndoStack.Do(new RemoveConnectionCommand(c));
            foreach (var l in implicitLinks)
                UndoStack.Do(new RemoveHeaderLinkCommand(l));
            foreach (var l in selectedLinks)
                UndoStack.Do(new RemoveHeaderLinkCommand(l));
            foreach (var item in allItemsToRemove)
                UndoStack.Do(new RemoveItemCommand(item));
            foreach (var device in devicesToDelete)
                UndoStack.Do(new RemoveDeviceCommand(device));
        });

        Invalidate();
        OnSelectionChanged();
    }
}
