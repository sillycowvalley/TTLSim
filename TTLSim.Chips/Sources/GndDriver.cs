using TTLSim.Core;

namespace TTLSim.Chips.Sources;

/// <summary>A GND symbol. Strongly drives its pin's net Low at tick 0.</summary>
public sealed class GndDriver : IChip
{
    private readonly Net net;
    private readonly Driver driver;

    public GndDriver(Net net)
    {
        this.net = net;
        driver = new Driver(net, DriveStrength.Strong);
    }

    public IReadOnlyList<int> PinNumbers { get; } = new[] { 0 };

    public IReadOnlyList<Net> Nets => new[] { net };

    public void Initialize(IScheduler scheduler)
    {
        scheduler.Schedule(0, driver, Signal.Low);
    }

    public void OnInputChanged(int pinIndex, IScheduler scheduler) { }
}