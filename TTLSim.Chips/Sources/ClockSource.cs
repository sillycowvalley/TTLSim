using TTLSim.Core;

namespace TTLSim.Chips.Sources;

/// <summary>
/// A free-running square-wave source. Starts Low at tick 0, toggles every
/// halfPeriod ticks. Period is in picoseconds.
/// </summary>
public sealed class ClockSource : IChip
{
    private readonly Net net;
    private readonly Driver driver;
    private readonly long halfPeriod;
    private Signal current = Signal.Low;

    public ClockSource(Net net, long periodPicoseconds)
    {
        this.net = net;
        driver = new Driver(net, DriveStrength.Strong);
        halfPeriod = periodPicoseconds / 2;
    }

    public IReadOnlyList<int> PinNumbers { get; } = new[] { 0 };

    public IReadOnlyList<Net> Nets => new[] { net };

    public void Initialize(IScheduler scheduler)
    {
        scheduler.Schedule(0, driver, Signal.Low);
        ScheduleNextEdge(scheduler);
    }

    public void OnInputChanged(int pinIndex, IScheduler scheduler)
    {
        current = net.Value;
        ScheduleNextEdge(scheduler);
    }

    private void ScheduleNextEdge(IScheduler scheduler)
    {
        Signal next = current == Signal.High ? Signal.Low : Signal.High;
        scheduler.Schedule(halfPeriod, driver, next);
    }
}