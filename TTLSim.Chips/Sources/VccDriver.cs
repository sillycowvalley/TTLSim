using TTLSim.Core;

namespace TTLSim.Chips.Sources;

/// <summary>A VCC symbol. Strongly drives its pin's net High at tick 0.</summary>
public sealed class VccDriver : IChip
{
    private readonly Net net;
    private readonly Driver driver;

    public VccDriver(Net net)
    {
        this.net = net;
        driver = new Driver(net, DriveStrength.Strong);
    }

    public IReadOnlyList<int> PinNumbers { get; } = new[] { 0 };

    public IReadOnlyList<Net> Nets => new[] { net };

    public void Initialize(IScheduler scheduler)
    {
        scheduler.Schedule(0, driver, Signal.High);
    }

    public void OnInputChanged(int pinIndex, IScheduler scheduler) { }
}