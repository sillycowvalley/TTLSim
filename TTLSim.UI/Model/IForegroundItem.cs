namespace TTLSim.UI.Model;

/// <summary>
/// Marks a cosmetic <see cref="SchematicItem"/> -- a text label -- that renders
/// IN FRONT OF the schematic.
///
/// <para>Two behaviours hang off this marker, layered on the non-electrical
/// contract it inherits from <see cref="ICosmeticItem"/> (excluded from the
/// router and from EasyEDA export, framed by Bounds in View-&gt;Fit):</para>
/// <list type="bullet">
///   <item>The canvas draws these in a post-pass, after wires, links,
///   junctions, and components -- so they sit visually in front of all
///   schematic content regardless of list order. (Transient cursor overlays --
///   wire/link placement previews and the marquee -- still draw on top.)</item>
///   <item>Hit-testing prioritises them: a click anywhere over a label's box
///   selects the label ahead of any component, wire, link, or background item
///   beneath it -- the selection mirror of the front paint order.</item>
/// </list>
///
/// For the behind-rendering counterpart (rectangles), see
/// <see cref="IBackgroundItem"/>.
/// </summary>
public interface IForegroundItem : ICosmeticItem
{
}