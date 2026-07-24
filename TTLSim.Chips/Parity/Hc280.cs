using Microsoft.Extensions.Logging;
using TTLSim.Core;

namespace TTLSim.Chips.Parity;

/// <summary>
/// 74HC280 — 9-bit odd/even parity generator/checker, 14-pin DIP. Fully
/// combinational, no enable, no high-Z state.
///
/// PE (pin 5) is HIGH when an EVEN number of the nine data inputs I0..I8 is
/// HIGH; PO (pin 6) is HIGH when an ODD number is HIGH. The two outputs are
/// always exact complements — the part is a nine-input XOR (PO) and its
/// inverse (PE), which is how the datasheet's logic diagram is built.
///
/// Pin map (from ChipPartDefinition.Ic74280, Nexperia Rev. 4 Table 2):
///   I0=8  I1=9  I2=10  I3=11  I4=12  I5=13      data inputs
///   I6=1  I7=2  I8=4                            data inputs
///   PE=5                                        even parity output
///   PO=6                                        odd parity output
///   NC=3                                        no bond wire; never reaches this model
///   VCC=14  GND=7                               power (consumed by the build pipeline)
///
/// Cascading: tie the PE output of a lower stage into a spare data input of
/// the next to extend word length (datasheet Fig. 7 chains two packages into
/// a 17-bit checker). Unused inputs tie LOW, which leaves the parity of the
/// used bits unchanged.
///
/// Inputs map Unknown/HighZ to Low, matching the catalogue convention ("treat
/// weird inputs as Low and let TTL011 surface the floating pin at design
/// time") — see Hc688. Note the consequence is sharper on this part than on
/// most: parity depends on EVERY input, so one floating pin silently shifts
/// both outputs rather than affecting one bit of a wider result. TTL011 is
/// what catches that, not the model.
///
/// Both outputs always drive, so each gets its own Driver and both are
/// scheduled on every recompute.
/// </summary>
public sealed class Hc280 : IChip
{
    /// <summary>Fallback delay when the model is constructed directly (tests).
    /// The factory passes TtlTiming.ResolvePs instead, which carries the
    /// per-family rows. 60 ns is the HC datasheet maximum at VCC 4.5 V over
    /// -40..+125 °C, for both the In→PE and In→PO paths.</summary>
    public const long PropagationDelayPs = 60_000;

    // Indices into nets[] -- the order PinNumbers is declared in below.
    private const int IndexI0 = 0;   // I0..I8 at indices 0..8
    private const int IndexPe = 9;   // PE (pin 5) output
    private const int IndexPo = 10;  // PO (pin 6) output

    private const int DataInputCount = 9;

    private readonly Net[] nets;
    private readonly Driver peDriver;
    private readonly Driver poDriver;
    private readonly long delayPs;

    private readonly ILogger? logger;
    private readonly string label;

    public Hc280(
        Net i0, Net i1, Net i2, Net i3, Net i4, Net i5, Net i6, Net i7, Net i8,
        Net pe,
        Net po,
        string label = "280",
        ILogger? logger = null,
        long delayPs = PropagationDelayPs)
    {
        // Order MUST match PinNumbers below.
        nets = new[]
        {
            i0, i1, i2, i3, i4, i5, i6, i7, i8,
            pe,
            po
        };

        peDriver = new Driver(nets[IndexPe], DriveStrength.Strong);
        poDriver = new Driver(nets[IndexPo], DriveStrength.Strong);
        this.delayPs = delayPs;
        this.label = label;
        this.logger = logger;
    }

    // Pin numbers in nets[] order: I0..I8, PE, PO.
    public IReadOnlyList<int> PinNumbers { get; } = new[]
    {
        8, 9, 10, 11, 12, 13, 1, 2, 4,
        5,
        6
    };

    public IReadOnlyList<Net> Nets => nets;

    public void Initialize(IScheduler scheduler) => Recompute(scheduler);

    public void OnInputChanged(int pinIndex, IScheduler scheduler)
    {
        // The outputs' own transitions never trigger a recompute.
        if (pinIndex == IndexPe || pinIndex == IndexPo) return;
        Recompute(scheduler);
    }

    /// <summary>
    /// Count the HIGH data inputs and schedule both output drivers. Called
    /// from Initialize and from any data-input transition.
    /// </summary>
    private void Recompute(IScheduler scheduler)
    {
        // A pin contributes to the count only when solidly High; Low, Unknown
        // and HighZ all read as Low, so an unwired input behaves as one tied
        // to GND and leaves the parity of the wired bits alone.
        int highCount = 0;
        for (int bit = 0; bit < DataInputCount; bit++)
            if (nets[IndexI0 + bit].Value == Signal.High)
                highCount++;

        bool even = (highCount & 1) == 0;

        scheduler.Schedule(delayPs, peDriver, even ? Signal.High : Signal.Low);
        scheduler.Schedule(delayPs, poDriver, even ? Signal.Low : Signal.High);
    }
}
