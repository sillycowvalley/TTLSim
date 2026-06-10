using TTLSim.Core;

namespace TTLSim.Chips.Passives;

/// <summary>
/// Series resistor sitting between two ordinary nets (neither end on a power
/// rail). In a real circuit the resistor limits current; in this four-state
/// digital sim we don't model current, so the resistor is treated as an
/// always-closed bidirectional contact: each pin drives its own net with
/// the value (and strength) the opposite net resolves to, excluding our own
/// contribution.
///
/// That makes the resistor a transparent conductor for signal propagation
/// while still letting a strong driver on either end win against anything
/// upstream. This is the same trick <see cref="SwitchInput"/> uses for a
/// closed contact, minus the open/closed state.
///
/// Resistors that DO have one end on a power rail are still modelled as a
/// <see cref="PullDriver"/> -- a one-way weak pull is cheaper than a
/// pass-through and matches the dominant real-world use (pull-ups and
/// pull-downs on input pins). This class only fills the gap for the
/// "current-limiter in series with something" cases like an LED chain
/// driven by a clock through a diode.
/// </summary>
public sealed class ResistorContact : IChip
{
    private readonly Net[] nets;
    private readonly Driver driverA;
    private readonly Driver driverB;

    public ResistorContact(Net pin1, Net pin2)
    {
        nets = new[] { pin1, pin2 };
        driverA = new Driver(pin1, DriveStrength.Strong);
        driverB = new Driver(pin2, DriveStrength.Strong);
    }

    public IReadOnlyList<int> PinNumbers { get; } = new[] { 1, 2 };

    public IReadOnlyList<Net> Nets => nets;

    public void Initialize(IScheduler scheduler) => Apply(scheduler);

    public void OnInputChanged(int pinIndex, IScheduler scheduler) => Apply(scheduler);

    private void Apply(IScheduler scheduler)
    {
        // Mirror both value and strength of the opposite net, excluding our
        // own contribution to keep Resolve from feeding back into itself.
        Signal aVal = nets[1].Resolve(out DriveStrength aStr, driverB);
        Signal bVal = nets[0].Resolve(out DriveStrength bStr, driverA);

        driverA.Strength = aStr;
        driverB.Strength = bStr;
        scheduler.Schedule(0, driverA, aVal);
        scheduler.Schedule(0, driverB, bVal);
    }
}