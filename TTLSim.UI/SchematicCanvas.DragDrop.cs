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
    // ---------------------------------------------------------------- drag/drop from library

    protected override void OnDragEnter(DragEventArgs e)
    {
        if (e.Data?.GetDataPresent(typeof(LibraryPartDragData)) == true
            || e.Data?.GetDataPresent(typeof(LibrarySymbolDragData)) == true)
            e.Effect = DragDropEffects.Copy;
        else
            e.Effect = DragDropEffects.None;
    }

    protected override void OnDragOver(DragEventArgs e)
    {
        if (e.Data?.GetDataPresent(typeof(LibraryPartDragData)) == true
            || e.Data?.GetDataPresent(typeof(LibrarySymbolDragData)) == true)
            e.Effect = DragDropEffects.Copy;
    }

    protected override void OnDragDrop(DragEventArgs e)
    {
        // Take focus so keyboard shortcuts (Space, W, Delete, ...) work
        // immediately after a drop without needing a separate canvas click.
        Focus();

        var clientPt = PointToClient(new Point(e.X, e.Y));
        var grid = ScreenToGrid(clientPt);

        // Two payload shapes: a PartDefinition (chip or passive) becomes a
        // Device; a standalone symbol factory just creates a SchematicItem.
        if (e.Data?.GetData(typeof(LibraryPartDragData)) is LibraryPartDragData partData)
        {
            DropPart(partData.Definition, grid);
            return;
        }

        if (e.Data?.GetData(typeof(LibrarySymbolDragData)) is LibrarySymbolDragData symbolData)
        {
            DropSymbol(symbolData.Factory, grid);
            return;
        }
    }

    private void DropPart(PartDefinition definition, Point dropPoint)
    {
        var device = DeviceFactory.Create(definition, dropPoint, Schematic);
        var unitsToAdd = device.Units.ToList();  // snapshot in case the list mutates

        // New parts land on the current layer. This is part of the item's
        // initial state, not a separate undo step -- undo/redo of the Add
        // carries the LayerId with the item.
        foreach (var unit in unitsToAdd)
            unit.LayerId = CurrentLayerId;

        UndoStack.DoComposite($"Add {device.Designator}", () =>
        {
            UndoStack.Do(new AddDeviceCommand(device));
            foreach (var unit in unitsToAdd)
                UndoStack.Do(new AddItemCommand(unit));
        });

        Schematic.ClearSelection();
        foreach (var unit in unitsToAdd) unit.Selected = true;
        Invalidate();
        OnSelectionChanged();
    }

    private void DropSymbol(Func<SchematicItem> factory, Point dropPoint)
    {
        var item = factory();
        item.Position = new Point(
            dropPoint.X - item.Size.Width / 2,
            dropPoint.Y - item.Size.Height / 2);

        // Designated standalone items (the canned oscillator) get the next free
        // designator, unique against the current schematic.
        if (item is IDesignatedItem designated)
            designated.Designator = Schematic.NextDesignator(designated.ReferencePrefix);

        item.LayerId = CurrentLayerId;   // new item lands on the current layer

        UndoStack.DoComposite($"Add {item.GetType().Name}", () =>
        {
            UndoStack.Do(new AddItemCommand(item));
        });

        Schematic.ClearSelection();
        item.Selected = true;
        Invalidate();
        OnSelectionChanged();
    }
}
