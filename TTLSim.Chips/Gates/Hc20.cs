using TTLSim.Core;

namespace TTLSim.Chips.Gates;

/// <summary>One 4-input NAND gate from a 74HC20. tPD ~10 ns.</summary>
public sealed class Hc20 : IChip
{
    public const long PropagationDelayPs = 10_000;

    private readonly Net inputA;
    private readonly Net inputB;
    private readonly Net inputC;
    private readonly Net inputD;
    private readonly Net output;
    private readonly Driver driver;
    private readonly long delayPs;

    public Hc20(Net inputA, Net inputB, Net inputC, Net inputD, Net output, long delayPs = PropagationDelayPs)
    {
        this.inputA = inputA;
        this.inputB = inputB;
        this.inputC = inputC;
        this.inputD = inputD;
        this.output = output;
        driver = new Driver(output, DriveStrength.Strong);
        this.delayPs = delayPs;
    }

    public IReadOnlyList<int> PinNumbers { get; } = new[] { 0, 1, 2, 3, 4 };

    public IReadOnlyList<Net> Nets => new[] { inputA, inputB, inputC, inputD, output };

    public void Initialize(IScheduler scheduler)
    {
        scheduler.Schedule(delayPs, driver, ComputeOutput());
    }

    public void OnInputChanged(int pinIndex, IScheduler scheduler)
    {
        if (pinIndex == 4) return;
        scheduler.Schedule(delayPs, driver, ComputeOutput());
    }

    private Signal ComputeOutput()
    {
        Signal a = SampleInput(inputA.Value);
        Signal b = SampleInput(inputB.Value);
        Signal c = SampleInput(inputC.Value);
        Signal d = SampleInput(inputD.Value);

        // NAND: any Low forces output High; all High forces output Low; else Unknown.
        if (a == Signal.Low || b == Signal.Low || c == Signal.Low || d == Signal.Low)
            return Signal.High;
        if (a == Signal.High && b == Signal.High && c == Signal.High && d == Signal.High)
            return Signal.Low;
        return Signal.Unknown;
    }

    private static Signal SampleInput(Signal s) =>
        s == Signal.HighZ ? Signal.Unknown : s;
}
