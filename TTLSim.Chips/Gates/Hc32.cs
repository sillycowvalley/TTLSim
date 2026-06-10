using TTLSim.Core;

namespace TTLSim.Chips.Gates;

/// <summary>One 2-input OR gate from a 74HC32. tPD ~10 ns.</summary>
public sealed class Hc32 : IChip
{
    public const long PropagationDelayPs = 10_000;

    private readonly Net inputA;
    private readonly Net inputB;
    private readonly Net output;
    private readonly Driver driver;
    private readonly long delayPs;

    public Hc32(Net inputA, Net inputB, Net output, long delayPs = PropagationDelayPs)
    {
        this.inputA = inputA;
        this.inputB = inputB;
        this.output = output;
        driver = new Driver(output, DriveStrength.Strong);
        this.delayPs = delayPs;
    }

    public IReadOnlyList<int> PinNumbers { get; } = new[] { 0, 1, 2 };

    public IReadOnlyList<Net> Nets => new[] { inputA, inputB, output };

    public void Initialize(IScheduler scheduler)
    {
        scheduler.Schedule(delayPs, driver, ComputeOutput());
    }

    public void OnInputChanged(int pinIndex, IScheduler scheduler)
    {
        if (pinIndex == 2) return;
        scheduler.Schedule(delayPs, driver, ComputeOutput());
    }

    private Signal ComputeOutput()
    {
        Signal a = SampleInput(inputA.Value);
        Signal b = SampleInput(inputB.Value);

        if (a == Signal.High || b == Signal.High) return Signal.High;
        if (a == Signal.Low && b == Signal.Low) return Signal.Low;
        return Signal.Unknown;
    }

    private static Signal SampleInput(Signal s) =>
        s == Signal.HighZ ? Signal.Unknown : s;
}
