namespace TTLSim.Core;

///   Strong - chip outputs, VCC/GND rails, closed contacts relaying a strong source.
///   Medium - diode forward conduction. Beats a pull resistor (a few hundred
///            microamps of diode current swamps a 47k pull-up/down in the real
///            world), but loses to any transistor output actively driving the net.
///   Weak   - pull-up/pull-down resistors.
/// </summary>
public enum DriveStrength
{
    Strong,
    Medium,
    Weak
}

/// <summary>
/// One contribution to a net's value: a single output pin's current drive.
/// A chip owns one Driver per output pin and updates it via the scheduler.
/// </summary>
public sealed class Driver
{
    public Driver(Net net, DriveStrength strength)
    {
        Net = net;
        Strength = strength;
        Output = Signal.HighZ;
        net.AddDriver(this);
    }

    public Net Net { get; }

    public DriveStrength Strength { get; set; }

    /// <summary>What this pin is currently driving onto the net.</summary>
    public Signal Output { get; set; }
}