using TTLSim.Core;

namespace TTLSim.Chips.Gates;

/// <summary>
/// One inverter from a 74HC14 hex Schmitt-trigger inverter. tPD ~15 ns.
/// In a digital event simulation the Schmitt hysteresis is irrelevant;
/// behaves as a plain logic inverter.
/// </summary>
public sealed class Hc14 : IChip
{
    public const long PropagationDelayPs = 15_000;

    private readonly Net input;
    private readonly Net output;
    private readonly Driver driver;
    private readonly long delayPs;

    public Hc14(Net input, Net output, long delayPs = PropagationDelayPs)
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
