using System;
using System.ComponentModel;
using System.Linq;
using TTLSim.UI.Components;

namespace TTLSim.UI.Model;

/// <summary>
/// A ribbon-cable-style link between two equal-pin-count header units. It ties
/// pin i of <see cref="A"/> to pin i of <see cref="B"/> for every pin, always
/// 1-&gt;1, 2-&gt;2, ... -- the electrical mapping is unconditional and is never
/// affected by the cosmetic <see cref="Reversed"/> flag.
///
/// <para>
/// Like a <see cref="Connection"/> a HeaderLink carries no geometry of its own:
/// the rendered strands are derived from the two headers' pin world positions
/// at draw time, so nothing about a link becomes invalid when its headers move
/// or rotate. A link is NOT routed -- it is a fixed bundle drawn directly
/// between pin pairs, so it bypasses the wire router entirely.
/// </para>
///
/// <para>
/// A HeaderLink has no layer of its own; it follows its endpoints. It is active
/// (simulated) only when both header units are active, exactly as a Connection
/// is active only when both endpoint items are. HeaderLinks are never exported
/// to EasyEDA -- the two headers export as independent connectors.
/// </para>
/// </summary>
public sealed class HeaderLink
{
    [Browsable(false)]
    public string Id { get; set; } = Guid.NewGuid().ToString("N");

    [Browsable(false)]
    public HeaderOutputUnit A { get; }

    [Browsable(false)]
    public HeaderOutputUnit B { get; }

    [Browsable(false)]
    public bool Selected { get; set; }

    /// <summary>
    /// Cosmetic only. When true the strands are drawn fanned for a face-to-face
    /// (mirrored) header pairing; it NEVER changes the electrical mapping, which
    /// is always A.i &lt;-&gt; B.i. Changes go through the undo stack via the
    /// PropertyGrid's generic SetPropertyCommand path, the same way a wire's
    /// Color does.
    /// </summary>
    [DefaultValue(false)]
    [Description("Cosmetic: fan the strands for a face-to-face header pairing. Does not change the 1-to-1 electrical mapping.")]
    public bool Reversed { get; set; }

    public HeaderLink(HeaderOutputUnit a, HeaderOutputUnit b)
    {
        A = a ?? throw new ArgumentNullException(nameof(a));
        B = b ?? throw new ArgumentNullException(nameof(b));
    }

    /// <summary>
    /// Number of pin pairs this link ties together. Derived from the headers
    /// (both have equal pin count by construction at creation time) so it can
    /// never drift from a stored copy. If the two ever disagree, the smaller
    /// count is used so the extra pins of the larger header are simply left
    /// unlinked rather than referencing a non-existent pin.
    /// </summary>
    [Browsable(false)]
    public int PinCount => Math.Min(A.Pins.Count(), B.Pins.Count());
}