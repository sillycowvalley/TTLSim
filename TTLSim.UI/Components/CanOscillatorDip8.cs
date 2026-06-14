namespace TTLSim.UI.Components;

/// <summary>
/// A half-size canned oscillator in a DIP-8 footprint. Behaves and draws
/// exactly like the full-size <see cref="CanOscillator"/> -- a single driven
/// output pin emitting a square wave at FrequencyHz -- differing only in the
/// package geometry. Four pins per side instead of seven, with the populated
/// corners at the DIP-8 positions of a real half-can:
///
///   Pin 1  (top-left)     N/C    (an output-enable / tri-state on some parts)
///   Pin 4  (bottom-left)  Ground
///   Pin 5  (bottom-right) Output  &lt;- the driven pin
///   Pin 8  (top-right)    +5 VDC
///
/// The corner positions are identical to the DIP-14 (output bottom-right,
/// N/C top-left, ground bottom-left, +5V top-right) -- only the pin numbers
/// and the pins-per-side differ -- so layout, rotation, the frequency label
/// and the property grid are all inherited unchanged. Because this derives
/// from CanOscillator, the simulation build, probe labelling and timing all
/// treat it as a clock source automatically; only save/load needs its own
/// discriminator (see SchematicSerializer / SchematicDtoMapper).
/// </summary>
public sealed class CanOscillatorDip8 : CanOscillator
{
    // Half-size can: an 8-pin DIP footprint.
    private static readonly CanGeometry Dip8 = new(
        PinsPerSide: 4,          // 8-pin package
        BodyWidthCells: 8,       // same body width as the full-size can
        OutputPin: 5,
        GroundPin: 4,
        PowerPin: 8,
        NcPin: 1);

    public CanOscillatorDip8() : base(Dip8) { }
}