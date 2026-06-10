using TTLSim.Core;

namespace TTLSim.Chips.Passives;

/// <summary>
/// SPST momentary pushbutton, modelled as a symmetric two-terminal contact.
/// When pressed, the contact is closed: each pin is driven to the value its
/// opposite net resolves to (excluding this device's own contribution), so
/// the button passes a signal in either direction and tracks live changes.
/// When released, both pins drive nothing.
///
/// The "exclude own driver" resolve is what breaks the same-tick feedback
/// loop: driverA's value depends on pin 2's net minus driverB, and vice
/// versa, so the pair settles to a fixed point in one round.
/// </summary>
public sealed class ButtonInput : IChip
{
    private readonly Net[] nets;
    private readonly Driver driverA;   // drives pin 1's net
    private readonly Driver driverB;   // drives pin 2's net

    private bool pressed;

    public ButtonInput(Net pin1, Net pin2)
    {
        nets = new[] { pin1, pin2 };
        driverA = new Driver(pin1, DriveStrength.Strong);
        driverB = new Driver(pin2, DriveStrength.Strong);
    }

    public IReadOnlyList<int> PinNumbers { get; } = new[] { 1, 2 };

    public IReadOnlyList<Net> Nets => nets;

    /// <summary>Press or release the button.</summary>
    public void SetPressed(bool value, IScheduler scheduler)
    {
        if (pressed == value) return;
        pressed = value;
        Apply(scheduler);
    }

    public void Initialize(IScheduler scheduler)
    {
        pressed = false;            // released at power-on
        Apply(scheduler);
    }

    public void OnInputChanged(int pinIndex, IScheduler scheduler)
    {
        // Closed contact tracks live net values; the exclude-own-driver
        // resolve in Apply() keeps this from looping on itself.
        if (pressed) Apply(scheduler);
    }

    private void Apply(IScheduler scheduler)
    {
        if (pressed)
        {
            // Each pin mirrors the opposite net -- both its value AND its
            // strength, ignoring our own drive on that net. Mirroring the
            // strength is what stops the contact from promoting a weak
            // pull-up to a strong drive and then wrongly beating a strong
            // driver (e.g. GND) on the near side.
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