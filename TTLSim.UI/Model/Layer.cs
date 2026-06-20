namespace TTLSim.UI.Model;

/// <summary>
/// A schematic layer. A layer is either <b>visible</b> or <b>invisible</b>.
/// Invisible means <b>fully inactive</b>: an item on an invisible layer is not
/// drawn, not hit-tested, not simulated, not routed, and not exported.
///
/// <para>
/// Layer membership lives on the item (<see cref="SchematicItem.LayerId"/>),
/// referencing a layer by its index in <see cref="Schematic.Layers"/>.
/// Visibility lives here, on the layer -- never on the item. Wires and header
/// links carry no layer of their own; they follow their endpoints.
/// </para>
///
/// <para>
/// Index 0 is always the "Default" layer and is pinned visible (see
/// <see cref="Schematic"/>), so the whole design can never be hidden at once.
/// </para>
/// </summary>
public sealed class Layer
{
    /// <summary>Display name shown in the Layers panel.</summary>
    public string Name { get; set; } = "";

    /// <summary>
    /// When false the layer is invisible, which means fully inactive for every
    /// consumer of the schematic. Default true.
    /// </summary>
    public bool Visible { get; set; } = true;

    public Layer() { }

    public Layer(string name, bool visible = true)
    {
        Name = name;
        Visible = visible;
    }
}