using TTLSim.Core;

namespace TTLSim.Chips.Passives;

/// <summary>
/// A resistor used as a pull-up or pull-down: one end tied to a power rail,
/// the other weakly driven to that rail's value. Loses to any strong driver
/// on the same net (e.g. a chip output or a closed switch).
/// </summary>
public sealed class PullDriver : IChip
{
    private readonly Net net;
    private readonly Driver driver;
    private readonly Signal pullValue;

    public PullDriver(Net net, Signal pullValue)
    {
        this.net = net;
        this.pullValue = pullValue;
        driver = new Driver(net, DriveStrength.Weak);
    }

    public IReadOnlyList<int> PinNumbers { get; } = new[] { 0 };

    public IReadOnlyList<Net> Nets => new[] { net };

    public void Initialize(IScheduler scheduler)
    {
        scheduler.Schedule(0, driver, pullValue);
    }

    public void OnInputChanged(int pinIndex, IScheduler scheduler) { }
}