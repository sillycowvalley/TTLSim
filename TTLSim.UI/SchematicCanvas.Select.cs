using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using TTLSim.UI.Model;

namespace TTLSim.UI.View;

public sealed partial class SchematicCanvas
{
    // ---------------------------------------------------------------- programmatic selection

    /// <summary>
    /// Make <paramref name="items"/> the selection, optionally centring the view
    /// on them. Items on a hidden layer are skipped -- the same rule the canvas
    /// applies everywhere else, so a panel can never select something the user
    /// cannot see (and cannot then delete it by accident).
    ///
    /// <para>Raises <see cref="SelectionChanged"/> exactly once, through the same
    /// funnel every other selection path uses, so the property grid, status bar,
    /// and side panels all refresh. Not an undo step -- selection never is.</para>
    /// </summary>
    public void SelectItems(IEnumerable<SchematicItem> items, bool center = false)
    {
        var visible = items.Where(Schematic.IsItemActive).ToList();

        Schematic.ClearSelection();
        foreach (var item in visible)
            item.Selected = true;

        if (center && visible.Count > 0)
            CenterOn(UnionCentre(visible));

        Invalidate();
        OnSelectionChanged();
    }

    /// <summary>Centre (in grid units) of the union of the items' bounds.</summary>
    private static Point UnionCentre(IReadOnlyList<SchematicItem> items)
    {
        int minX = int.MaxValue, minY = int.MaxValue;
        int maxX = int.MinValue, maxY = int.MinValue;
        foreach (var item in items)
        {
            var b = item.Bounds;
            if (b.Left < minX) minX = b.Left;
            if (b.Top < minY) minY = b.Top;
            if (b.Right > maxX) maxX = b.Right;
            if (b.Bottom > maxY) maxY = b.Bottom;
        }
        return new Point((minX + maxX) / 2, (minY + maxY) / 2);
    }
}
