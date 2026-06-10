using TTLSim.Core;

namespace TTLSim.Chips.Gates;

/// <summary>One inverter from a 74HC04 hex inverter. tPD ~10 ns.</summary>
public sealed class Hc04 : IChip
{
    public const long PropagationDelayPs = 10_000;

    private readonly Net input;
    private readonly Net output;
    private readonly Driver driver;
    private readonly long delayPs;

    public Hc04(Net input, Net output, long delayPs = PropagationDelayPs)
    {
        this.input = input;
        this.output = output;
        driver = new Driver(output, DriveStrength.Strong);
        this.delayPs = delayPs;
    }

    public IReadOnlyList<int> PinNumbers { get; } = new[] { 0, 1 };

    public IReadOnlyList<Net> Nets => new[] { input, output };

    public void Initialize(IScheduler scheduler)
    {
        scheduler.Schedule(delayPs, driver, ComputeOutput());
    }

    public void OnInputChanged(int pinIndex, IScheduler scheduler)
    {
        if (pinIndex == 1) return;
        scheduler.Schedule(delayPs, driver, ComputeOutput());
    }

    private Signal ComputeOutput() => input.Value switch
    {
        Signal.High => Signal.Low,
        Signal.Low => Signal.High,
        _ => Signal.Unknown,
    };
}
