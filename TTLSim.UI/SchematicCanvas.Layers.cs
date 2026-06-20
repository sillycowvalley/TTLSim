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
    // ---------------------------------------------------------------- layers

    /// <summary>
    /// Move every selected item onto <paramref name="layerId"/> as one undoable
    /// step. Connections and links are not moved -- they carry no layer and
    /// follow their endpoints. No-op if nothing is selected or the layer index
    /// is out of range.
    /// </summary>
    public void MoveSelectionToLayer(int layerId)
    {
        if (layerId < 0 || layerId >= Schematic.Layers.Count) return;

        var items = Schematic.Selected.ToList();
        if (items.Count == 0) return;

        string desc = items.Count == 1
            ? $"Move {items[0].GetType().Name} to layer"
            : $"Move {items.Count} items to layer";

        UndoStack.DoComposite(desc, () =>
        {
            foreach (var item in items)
            {
                if (item.LayerId == layerId) continue;
                UndoStack.Do(new SetLayerCommand(item, item.LayerId, layerId));
            }
        });

        Invalidate();
        OnSelectionChanged();
    }

    /// <summary>
    /// Set a layer's visibility (view state -- not an undo step). Hiding a layer
    /// deselects anything that just became inactive, so the property grid and
    /// Delete cannot act on items the user can no longer see, then drops the
    /// route cache (wires activate/deactivate with their endpoints) and repaints.
    /// </summary>
    public void SetLayerVisible(int index, bool visible)
    {
        Schematic.SetLayerVisible(index, visible);

        if (!visible)
        {
            foreach (var item in Schematic.Items)
                if (!Schematic.IsItemActive(item)) item.Selected = false;
            foreach (var c in Schematic.Connections)
                if (!Schematic.IsConnectionActive(c)) c.Selected = false;
            foreach (var l in Schematic.Links)
                if (!Schematic.IsLinkActive(l)) l.Selected = false;
        }

        routeCache = null;
        coincidentCornersCache = null;
        Invalidate();
        OnSelectionChanged();
    }
}
