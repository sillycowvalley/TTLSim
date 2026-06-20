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

    /// <summary>
    /// Ribbon-cable links between equal-pin-count header units. Each link ties
    /// pin i of one header to pin i of the other for every pin. Like a
    /// Connection a link has no geometry of its own; unlike a Connection it is
    /// not routed -- the strands are drawn directly between the pin pairs.
    /// </summary>
    public List<HeaderLink> Links { get; } = new();

    /// <summary>
    /// Layers. Index 0 is "Default" and is pinned visible -- it always exists
    /// and cannot be hidden, so the whole design can never be hidden at once.
    /// An item's <see cref="SchematicItem.LayerId"/> indexes into this list;
    /// an out-of-range id is treated as Default by the active rule. The list
    /// is reset to a single Default by <see cref="Clear"/> and replaced from
    /// the source by <see cref="CopyFrom"/>.
    /// </summary>
    public List<Layer> Layers { get; } = new() { new Layer("Default", visible: true) };

    public void Add(SchematicItem item) => Items.Add(item);
    public void Remove(SchematicItem item) => Items.Remove(item);

    public void Add(Connection connection) => Connections.Add(connection);
    public void Remove(Connection connection) => Connections.Remove(connection);

    public void Add(HeaderLink link) => Links.Add(link);
    public void Remove(HeaderLink link) => Links.Remove(link);

    /// <summary>
    /// Remove everything: devices, items, connections, and links. This is the
    /// ONE place that knows the full set of collections, so callers that reset
    /// the model (File-New) never have to re-list them and silently miss one.
    /// When a new collection is added to this class, clear it here and every
    /// reset path inherits it.
    ///
    /// <para>Layers are reset to a single visible "Default" rather than emptied,
    /// so the Default-always-exists invariant holds after a File-New.</para>
    /// </summary>
    public void Clear()
    {
        Devices.Clear();
        Items.Clear();
        Connections.Clear();
        Links.Clear();
        Layers.Clear();
        Layers.Add(new Layer("Default", visible: true));
    }

    /// <summary>
    /// Replace this schematic's contents in place with another's: clear, then
    /// take every device, item, connection, and link from <paramref name="other"/>.
    /// Used by the load path so a freshly loaded schematic moves into the live
    /// one without swapping the Schematic reference. Like <see cref="Clear"/>,
    /// this is the single place that enumerates the collections -- a new
    /// collection added to this class is copied here and every load path
    /// inherits it.
    ///
    /// <para>Layers replace the post-Clear Default from <paramref name="other"/>.
    /// If the source carries no layers (a model built before the persistence
    /// increment), the Default left by Clear is kept so the invariant holds.</para>
    /// </summary>
    public void CopyFrom(Schematic other)
    {
        if (ReferenceEquals(other, this)) return;
        Clear();
        Devices.AddRange(other.Devices);
        Items.AddRange(other.Items);
        Connections.AddRange(other.Connections);
        Links.AddRange(other.Links);
        if (other.Layers.Count > 0)
        {
            Layers.Clear();
            Layers.AddRange(other.Layers);
        }
    }

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
        // Designated standalone items (the canned oscillator's X1) share the
        // numbering pool, so X-numbers stay unique against them too.
        foreach (var item in Items)
        {
            if (item is IDesignatedItem di &&
                di.Designator.StartsWith(prefix) &&
                int.TryParse(di.Designator.AsSpan(prefix.Length), out int n))
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
        // First pass: real items, topmost first. A component sitting on top of
        // a cosmetic background item (rectangle, text label) must take the
        // click even though that background item is drawn behind it.
        for (int i = Items.Count - 1; i >= 0; i--)
        {
            if (Items[i] is IBackgroundItem) continue;
            if (Items[i].Bounds.Contains(gridPoint))
                return Items[i];
        }
        // Second pass: cosmetic background items, so a click on the bare
        // interior of a rectangle (or on a text label) still selects it.
        for (int i = Items.Count - 1; i >= 0; i--)
        {
            if (Items[i] is not IBackgroundItem) continue;
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
    /// All header links with an endpoint on the given item. Used by composite
    /// delete: when a header (or the device it belongs to) goes, the links
    /// attached to it go with it -- the same closure rule ConnectionsOn gives
    /// for wires.
    /// </summary>
    public IEnumerable<HeaderLink> LinksOn(SchematicItem item)
    {
        foreach (var link in Links)
        {
            if (link.A == item || link.B == item)
                yield return link;
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
    public IEnumerable<HeaderLink> SelectedLinks => Links.Where(l => l.Selected);

    public void ClearSelection()
    {
        foreach (var item in Items) item.Selected = false;
        foreach (var c in Connections) c.Selected = false;
        foreach (var l in Links) l.Selected = false;
    }

    // ====================================================================
    //  Layer activity -- the SINGLE home for "visible = active".
    //
    //  Every consumer (simulation, router, EasyEDA export, canvas paint,
    //  hit-test/marquee, View->Fit) decides activity through the predicates
    //  and enumerators below. No consumer re-derives the visibility check on
    //  its own. Adding the rule here once is what keeps it from drifting
    //  across the independent enumeration sites.
    // ====================================================================

    /// <summary>
    /// True if the layer at <paramref name="layerId"/> is visible. An
    /// out-of-range id (including the 0 default on items and on old files) is
    /// treated as visible, so nothing silently disappears for want of a layer
    /// entry.
    /// </summary>
    public bool IsLayerVisible(int layerId) =>
        layerId < 0 || layerId >= Layers.Count || Layers[layerId].Visible;

    /// <summary>An item is active iff its layer is visible.</summary>
    public bool IsItemActive(SchematicItem item) => IsLayerVisible(item.LayerId);

    /// <summary>
    /// A connection is active iff both endpoint items exist and are active.
    /// Connections carry no layer of their own; hiding a layer deactivates the
    /// items on it, and any wire touching one of those items deactivates here
    /// because one endpoint went inactive.
    /// </summary>
    public bool IsConnectionActive(Connection connection) =>
        connection.A.Owner is { } a && connection.B.Owner is { } b
        && IsItemActive(a) && IsItemActive(b);

    /// <summary>
    /// A header link is active iff both endpoint headers are active -- the same
    /// rule as a connection. Hiding a module's layer hides its header, which
    /// deactivates every link attached to that header.
    /// </summary>
    public bool IsLinkActive(HeaderLink link) =>
        IsItemActive(link.A) && IsItemActive(link.B);

    /// <summary>Items on a visible layer. The active view of <see cref="Items"/>.</summary>
    public IEnumerable<SchematicItem> ActiveItems => Items.Where(IsItemActive);

    /// <summary>Connections with both endpoints active. The active view of <see cref="Connections"/>.</summary>
    public IEnumerable<Connection> ActiveConnections => Connections.Where(IsConnectionActive);

    /// <summary>Header links with both endpoint headers active. The active view of <see cref="Links"/>.</summary>
    public IEnumerable<HeaderLink> ActiveLinks => Links.Where(IsLinkActive);
}