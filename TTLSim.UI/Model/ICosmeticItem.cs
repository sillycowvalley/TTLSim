namespace TTLSim.UI.Model;

/// <summary>
/// Marks a purely cosmetic <see cref="SchematicItem"/> -- a rectangle, a text
/// label -- that carries no electrical meaning: no pins, an empty
/// <see cref="SchematicItem.RoutingBounds"/>, invisible to the wire router and
/// to EasyEDA export, and framed by its <see cref="SchematicItem.Bounds"/>
/// (not its empty routing bounds) by View-&gt;Fit.
///
/// <para>This base marker says nothing about paint order. Two refinements fix
/// that: <see cref="IBackgroundItem"/> renders behind the schematic, and
/// <see cref="IForegroundItem"/> renders in front of it. Every consumer that
/// only cares "is this electrical?" -- the router, EasyEDA export, View-&gt;Fit
/// -- tests for <see cref="ICosmeticItem"/> and so catches both kinds with one
/// check, which is what keeps the non-electrical rule from drifting as new
/// cosmetic items are added.</para>
///
/// The interface is intentionally empty: it is a classification, not a
/// contract. Everything a cosmetic item needs is already on
/// <see cref="SchematicItem"/>, so they remain ordinary items for selection,
/// move, rotate, copy/paste, undo, and serialisation.
/// </summary>
public interface ICosmeticItem
{
}