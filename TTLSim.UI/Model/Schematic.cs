using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using TTLSim.UI.Components;

namespace TTLSim.UI.Model;

/// <summary>The model behind the canvas: every item, device, and connection in the schematic.</summary>
public sealed class Schematic
{
    /// <summary>
    /// Logical parts (chips and passives). Each Device owns a list of Units
    /// that also live in Items. VccSymbol and GndSymbol are SchematicItems
    /// but not Units -- they don't belong to any Device.
    /// </summary>
    public List<Device> Devices { get; } = new();

    public List<SchematicItem> Items { get; } = new();

    /// <summary>
    /// Logical connections between pins. Each Connection has no geometry of
    /// its own; the rendered wire is produced by an IConnectionRouter at
    /// paint time, cached by the canvas.
    /// </summary>
    public List<Connection> Connections { get; } = new();

    public void Add(SchematicItem item) => Items.Add(item);
    public void Remove(SchematicItem item) => Items.Remove(item);

    public void Add(Connection connection) => Connections.Add(connection);
    public void Remove(Connection connection) => Connections.Remove(connection);

    /// <summary>
    /// Lowest unused integer N such that no existing device has designator
    /// equal to prefix + N. Used by DeviceFactory to assign U1, U2, ..., or
    /// R1, R2, ..., depending on the part's reference prefix.
    /// </summary>
    public string NextDesignator(string prefix)
    {
        var used = new HashSet<int>();
        foreach (var d in Devices)
        {
            if (d.Designator.StartsWith(prefix) &&
                int.TryParse(d.Designator.AsSpan(prefix.Length), out int n))
            {
                used.Add(n);
            }
        }
        for (int i = 1; ; i++)
        {
            if (!used.Contains(i)) return $"{prefix}{i}";
        }
    }

    /// <summary>Hit test: topmost item whose bounds contain the given grid point.</summary>
    public SchematicItem? HitTest(Point gridPoint)
    {
        for (int i = Items.Count - 1; i >= 0; i--)
        {
            if (Items[i].Bounds.Contains(gridPoint))
                return Items[i];
        }
        return null;
    }

    /// <summary>
    /// All connections with an endpoint on the given item. Used by composite
    /// delete: when an item goes, its connections go with it.
    /// </summary>
    public IEnumerable<Connection> ConnectionsOn(SchematicItem item)
    {
        foreach (var c in Connections)
        {
            if (c.A.Owner == item || c.B.Owner == item)
                yield return c;
        }
    }

    /// <summary>
    /// Find a pin whose world position equals the given grid point. Searches
    /// from topmost item down so that overlapping items resolve consistently
    /// with HitTest. Returns null if no pin sits exactly on that grid point.
    /// </summary>
    public Pin? PinAt(Point gridPoint)
    {
        for (int i = Items.Count - 1; i >= 0; i--)
        {
            foreach (var pin in Items[i].Pins)
            {
                if (pin.WorldPosition == gridPoint)
                    return pin;
            }
        }
        return null;
    }

    public IEnumerable<SchematicItem> Selected => Items.Where(i => i.Selected);
    public IEnumerable<Connection> SelectedConnections => Connections.Where(c => c.Selected);

    public void ClearSelection()
    {
        foreach (var item in Items) item.Selected = false;
        foreach (var c in Connections) c.Selected = false;
    }
}