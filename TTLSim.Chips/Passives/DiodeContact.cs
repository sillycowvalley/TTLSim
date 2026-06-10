using TTLSim.Core;

namespace TTLSim.Chips.Passives;

/// <summary>
/// Idealised diode for digital simulation. Two terminals, forward-conducting
/// in either *direction of state propagation* whenever the diode is forward-
/// biased AND there is a real (chip-driven) source committing the signal;
/// blocking otherwise.
///
/// State-propagation rules (driver outputs):
///   * Anode net resolves Strong HIGH (excluding our contribution)
///         -> drive cathode Medium HIGH.
///   * Cathode net resolves Strong LOW (excluding our contribution)
///         -> drive anode Medium LOW.
///   * Otherwise both contributions are HighZ.
///
/// The "Strong source required" condition is what makes the diode behave
/// like a passive valve rather than an amplifier. A real diode can pass a
/// chip-driven signal through, but it cannot propagate a weakly-pulled
/// signal (from a pull-up resistor through another diode) onward to the
/// next stage -- the accumulating forward voltage drops would collapse
/// the signal. Without this rule, two diodes in series with a pull-up on
/// the junction between them would create a startup glitch: at t=0 the
/// pull-up's Weak HIGH would leak through the diodes onto whatever they
/// connect to, and when the real chip output asserts its LOW value a
/// propagation delay later, the resulting HIGH->LOW transition would
/// register as a spurious clock edge on anything listening.
///
/// Both rules describe the same physical condition -- forward conduction --
/// just observed from different ends of the diode. A real Schottky in a
/// "VCC -> R -> diode -> CLK_LOW" chain conducts current down through
/// the diode to the clock; the diode's anode side is pulled to ~0.3V
/// because the cathode is at 0V and the diode is on. That's what the
/// second rule captures: a strong low on the cathode propagates back to
/// pull the anode low too.
///
/// Drive strength is Medium so the diode beats pull-up / pull-down resistors
/// (per Driver.cs's tier comments) but always loses to a chip output or rail
/// actively driving the same net. That maps "a few hundred microamps of
/// diode current swamps a 47k pull, but loses to a transistor" into four-
/// state logic without dragging in voltages.
///
/// Schottky-specific traits (lower forward drop, no minority-carrier storage)
/// don't matter at the digital level; the part name is just a value string
/// on the device.
///
/// What this still does NOT capture: negative-spike clamping to ground,
/// reverse breakdown, exact forward voltage. Those need an analogue solver.
/// </summary>
public sealed class DiodeContact : IChip
{
    private const int IndexAnode = 0;
    private const int IndexCathode = 1;

    private readonly Net[] nets;
    private readonly Driver anodeDriver;
    private readonly Driver cathodeDriver;

    public DiodeContact(Net anode, Net cathode)
    {
        nets = new[] { anode, cathode };
        anodeDriver = new Driver(anode, DriveStrength.Medium);
        cathodeDriver = new Driver(cathode, DriveStrength.Medium);
    }

    public IReadOnlyList<int> PinNumbers { get; } = new[] { 1, 2 };

    public IReadOnlyList<Net> Nets => nets;

    public void Initialize(IScheduler scheduler) => Apply(scheduler);

    public void OnInputChanged(int pinIndex, IScheduler scheduler) => Apply(scheduler);

    private void Apply(IScheduler scheduler)
    {
        // Resolve each net excluding our own contribution so we never feed
        // back into the value we just published. The reported tier matters:
        // forward conduction requires a Strong source, not just a Weak pull
        // or a Medium contribution from another diode in the same wired-AND
        // / wired-OR network.
        Signal anode = nets[IndexAnode].Resolve(
            out DriveStrength anodeStrength, exclude: anodeDriver);
        Signal cathode = nets[IndexCathode].Resolve(
            out DriveStrength cathodeStrength, exclude: cathodeDriver);

        Signal cathodeTarget =
            (anode == Signal.High && anodeStrength == DriveStrength.Strong)
                ? Signal.High : Signal.HighZ;
        Signal anodeTarget =
            (cathode == Signal.Low && cathodeStrength == DriveStrength.Strong)
                ? Signal.Low : Signal.HighZ;

        scheduler.Schedule(0, cathodeDriver, cathodeTarget);
        scheduler.Schedule(0, anodeDriver, anodeTarget);
    }
}