using TTLSim.Core;

namespace TTLSim.Chips.Gates;

/// <summary>The 8-input NAND gate of a 74HC30. tPD ~10 ns.</summary>
public sealed class Hc30 : IChip
{
    public const long PropagationDelayPs = 10_000;

    private readonly Net[] inputs;   // 8 inputs
    private readonly Net output;
    private readonly Driver driver;
    private readonly long delayPs;

    public Hc30(
        Net inputA, Net inputB, Net inputC, Net inputD,
        Net inputE, Net inputF, Net inputG, Net inputH,
        Net output, long delayPs = PropagationDelayPs)
    {
        inputs = new[] { inputA, inputB, inputC, inputD, inputE, inputF, inputG, inputH };
        this.output = output;
        driver = new Driver(output, DriveStrength.Strong);
        this.delayPs = delayPs;
    }

    public IReadOnlyList<int> PinNumbers { get; } = new[] { 0, 1, 2, 3, 4, 5, 6, 7, 8 };

    public IReadOnlyList<Net> Nets =>
        new[] { inputs[0], inputs[1], inputs[2], inputs[3],
                inputs[4], inputs[5], inputs[6], inputs[7], output };

    public void Initialize(IScheduler scheduler)
    {
        scheduler.Schedule(delayPs, driver, ComputeOutput());
    }

    public void OnInputChanged(int pinIndex, IScheduler scheduler)
    {
        if (pinIndex == 8) return;   // output pin's own change -- ignore
        scheduler.Schedule(delayPs, driver, ComputeOutput());
    }

    private Signal ComputeOutput()
    {
        // NAND: any Low forces output High; all High forces output Low; else Unknown.
        bool allHigh = true;
        for (int i = 0; i < inputs.Length; i++)
        {
            Signal s = SampleInput(inputs[i].Value);
            if (s == Signal.Low) return Signal.High;
            if (s != Signal.High) allHigh = false;
        }
        return allHigh ? Signal.Low : Signal.Unknown;
    }

    private static Signal SampleInput(Signal s) =>
        s == Signal.HighZ ? Signal.Unknown : s;
}
