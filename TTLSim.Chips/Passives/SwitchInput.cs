using TTLSim.Core;

namespace TTLSim.Chips.Passives;

/// <summary>
/// SPST latching switch, modelled as a symmetric two-terminal contact.
/// When closed, each pin is driven to the value its opposite net resolves
/// to (excluding this device's own contribution), so the switch passes a
/// signal in either direction and tracks live changes. When open, both
/// pins drive nothing.
///
/// Unlike the momentary ButtonInput, the switch latches: it powers on in
/// whatever position was saved with the schematic, and stays there until
/// toggled.
/// </summary>
public sealed class SwitchInput : IChip
{
    private readonly Net[] nets;
    private readonly Driver driverA;   // drives pin 1's net
    private readonly Driver driverB;   // drives pin 2's net

    private bool closed;
    private readonly bool initialClosed;

    public SwitchInput(Net pin1, Net pin2, bool initiallyClosed)
    {
        nets = new[] { pin1, pin2 };
        driverA = new Driver(pin1, DriveStrength.Strong);
        driverB = new Driver(pin2, DriveStrength.Strong);
        initialClosed = initiallyClosed;
    }

    public IReadOnlyList<int> PinNumbers { get; } = new[] { 1, 2 };

    public IReadOnlyList<Net> Nets => nets;

    /// <summary>Open or close the switch.</summary>
    public void SetClosed(bool value, IScheduler scheduler)
    {
        if (closed == value) return;
        closed = value;
        Apply(scheduler);
    }

    public void Initialize(IScheduler scheduler)
    {
        closed = initialClosed;
        Apply(scheduler);
    }

    public void OnInputChanged(int pinIndex, IScheduler scheduler)
    {
        // Closed contact tracks live net values; the exclude-own-driver
        // resolve in Apply() keeps this from looping on itself.
        if (closed) Apply(scheduler);
    }

    private void Apply(IScheduler scheduler)
    {
        if (closed)
        {
            // Mirror both value and strength of the opposite net -- see the
            // matching comment in ButtonInput.Apply for why strength matters.
            Signal aVal = nets[1].Resolve(out DriveStrength aStr, driverB);
            Signal bVal = nets[0].Resolve(out DriveStrength bStr, driverA);

            driverA.Strength = aStr;
            driverB.Strength = bStr;
            scheduler.Schedule(0, driverA, aVal);
            scheduler.Schedule(0, driverB, bVal);
        }
        else
        {
            scheduler.Schedule(0, driverA, Signal.HighZ);
            scheduler.Schedule(0, driverB, Signal.HighZ);
        }
    }
}