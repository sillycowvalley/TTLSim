using TTLSim.Core;

namespace TTLSim.Chips.Gates;

/// <summary>One 2-input XOR gate from a 74HC86. tPD ~12 ns.</summary>
public sealed class Hc86 : IChip
{
    public const long PropagationDelayPs = 12_000;

    private readonly Net inputA;
    private readonly Net inputB;
    private readonly Net output;
    private readonly Driver driver;
    private readonly long delayPs;

    public Hc86(Net inputA, Net inputB, Net output, long delayPs = PropagationDelayPs)
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

        // XOR: needs both inputs known. Output High when they differ, Low when they match.
        if (a == Signal.Unknown || b == Signal.Unknown) return Signal.Unknown;
        return a == b ? Signal.Low : Signal.High;
    }

    private static Signal SampleInput(Signal s) =>
        s == Signal.HighZ ? Signal.Unknown : s;
}
