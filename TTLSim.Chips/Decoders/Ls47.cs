using TTLSim.Core;

namespace TTLSim.Chips.Decoders;

/// <summary>
/// 74HC47 BCD-to-7-segment decoder/driver. Active-low outputs. tPD ~25 ns.
/// Inputs (active-high BCD):  A=pin7 (bit0), B=pin1 (bit1), C=pin2 (bit2), D=pin6 (bit3)
/// Controls (active-low):      LT=pin3, RBI=pin5, BI=pin4
/// Outputs (active-low):       a=pin13, b=pin12, c=pin11, d=pin10, e=pin9, f=pin15, g=pin14
/// </summary>
public sealed class Ls47 : IChip
{
    public const long PropagationDelayPs = 25_000;

    private const int IndexA = 0, IndexB = 1, IndexC = 2, IndexD = 3;
    private const int IndexLT = 4, IndexRBI = 5, IndexBI = 6;
    private const int IndexSegA = 7;

    private readonly Net[] nets;
    private readonly Driver[] segDrivers = new Driver[7];

    public Ls47(
        Net a, Net b, Net c, Net d,
        Net lt, Net rbi, Net bi,
        Net segA, Net segB, Net segC, Net segD, Net segE, Net segF, Net segG)
    {
        nets = new[] { a, b, c, d, lt, rbi, bi, segA, segB, segC, segD, segE, segF, segG };
        for (int i = 0; i < 7; i++)
            segDrivers[i] = new Driver(nets[IndexSegA + i], DriveStrength.Strong);
    }

    public IReadOnlyList<int> PinNumbers { get; } =
        new[] { 7, 1, 2, 6, 3, 5, 4, 13, 12, 11, 10, 9, 15, 14 };

    public IReadOnlyList<Net> Nets => nets;

    public void Initialize(IScheduler scheduler) => RecomputeAndSchedule(scheduler);

    public void OnInputChanged(int pinIndex, IScheduler scheduler)
    {
        if (pinIndex >= IndexSegA) return;
        RecomputeAndSchedule(scheduler);
    }

    private void RecomputeAndSchedule(IScheduler scheduler)
    {
        bool[] segs = ComputeSegments();
        for (int i = 0; i < 7; i++)
        {
            Signal s = segs[i] ? Signal.Low : Signal.High;
            scheduler.Schedule(PropagationDelayPs, segDrivers[i], s);
        }
    }

    private bool[] ComputeSegments()
    {
        Signal lt = nets[IndexLT].Value;
        Signal rbi = nets[IndexRBI].Value;
        Signal bi = nets[IndexBI].Value;

        if (bi == Signal.Low) return new bool[7];
        if (lt == Signal.Low) return new[] { true, true, true, true, true, true, true };

        int? bcd = ReadBcd();
        if (bcd is null) return new bool[7];

        if (rbi == Signal.Low && bcd == 0) return new bool[7];

        return DigitSegments[bcd.Value];
    }

    private int? ReadBcd()
    {
        int value = 0;
        for (int i = 0; i < 4; i++)
        {
            Signal s = nets[i].Value;
            if (s == Signal.High) value |= 1 << i;
            else if (s != Signal.Low) return null;
        }
        return value;
    }

    private static readonly bool[][] DigitSegments = new[]
    {
        new[] { true,  true,  true,  true,  true,  true,  false }, // 0
        new[] { false, true,  true,  false, false, false, false }, // 1
        new[] { true,  true,  false, true,  true,  false, true  }, // 2
        new[] { true,  true,  true,  true,  false, false, true  }, // 3
        new[] { false, true,  true,  false, false, true,  true  }, // 4
        new[] { true,  false, true,  true,  false, true,  true  }, // 5
        new[] { false, false, true,  true,  true,  true,  true  }, // 6
        new[] { true,  true,  true,  false, false, false, false }, // 7
        new[] { true,  true,  true,  true,  true,  true,  true  }, // 8
        new[] { true,  true,  true,  true,  false, true,  true  }, // 9
        new[] { false, false, false, true,  true,  false, true  }, // 10
        new[] { false, false, true,  true,  false, false, true  }, // 11
        new[] { false, true,  false, false, false, true,  true  }, // 12
        new[] { true,  false, false, true,  false, true,  true  }, // 13
        new[] { false, false, false, true,  true,  true,  true  }, // 14
        new[] { false, false, false, false, false, false, false }, // 15
    };
}