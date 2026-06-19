namespace TTLSim.UI.Model;

/// <summary>
/// Marks a purely cosmetic <see cref="SchematicItem"/> -- a rectangle, a text
/// label -- that renders BEHIND the schematic and carries no electrical
/// meaning.
///
/// <para>Three behaviours hang off this marker:</para>
/// <list type="bullet">
///   <item>The canvas draws these in a pre-pass, before wires and components,
///   and skips them in the main item loop -- so they sit visually behind
///   everything else regardless of list order.</item>
///   <item>Hit-testing deprioritises them: a component sitting on top of a
///   cosmetic rectangle still takes the click; only a click on bare interior
///   selects the rectangle.</item>
///   <item>They are invisible to the wire router and to electrical export.
///   Concrete cosmetic items back this up by carrying no pins and returning
///   <see cref="System.Drawing.Rectangle.Empty"/> from
///   <see cref="SchematicItem.RoutingBounds"/>.</item>
/// </list>
///
/// The interface is intentionally empty: it is a classification, not a
/// contract. Everything an <see cref="IBackgroundItem"/> needs is already on
/// <see cref="SchematicItem"/>, so they remain ordinary items for selection,
/// move, rotate, copy/paste, undo, and serialisation.
/// </summary>
public interface IBackgroundItem
{
}