namespace TTLSim.UI.Model;

/// <summary>
/// Marks a cosmetic <see cref="SchematicItem"/> -- a rectangle -- that renders
/// BEHIND the schematic.
///
/// <para>Two behaviours hang off this marker, layered on the non-electrical
/// contract it inherits from <see cref="ICosmeticItem"/> (excluded from the
/// router and from EasyEDA export, framed by Bounds in View-&gt;Fit):</para>
/// <list type="bullet">
///   <item>The canvas draws these in a pre-pass, before wires and components,
///   and skips them in the main item loop -- so they sit visually behind
///   everything else regardless of list order.</item>
///   <item>Hit-testing deprioritises them: a component sitting on top of a
///   cosmetic rectangle still takes the click; only a click on the bare
///   interior selects the rectangle, and only once foreground labels, real
///   components, and wires have been offered the click first.</item>
/// </list>
///
/// For the front-rendering counterpart (text labels), see
/// <see cref="IForegroundItem"/>.
/// </summary>
public interface IBackgroundItem : ICosmeticItem
{
}