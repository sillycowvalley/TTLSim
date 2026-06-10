using Microsoft.Extensions.Logging;
using TTLSim.Core;

namespace TTLSim.Chips.Counters;

/// <summary>
/// One half of a 74HC393 — a 4-bit binary ripple counter. Counts on the
/// falling edge of CLK; asynchronously cleared while CLR is high.
/// </summary>
public sealed class Hc393Half : IChip
{
    public const long PropagationDelayPs = 10_000;

    private const int IndexClk = 0;
    private const int IndexClr = 1;
    private const int IndexQ0 = 2;

    private readonly Net[] nets;
    private readonly Driver[] qDrivers = new Driver[4];
    private readonly long delayPs;

    private readonly bool[] ffState = new bool[4];

    private Signal prevClk = Signal.Unknown;
    private readonly Signal[] prevQ = new Signal[4]
        { Signal.Unknown, Signal.Unknown, Signal.Unknown, Signal.Unknown };

    private bool clearAsserted;

    private readonly Microsoft.Extensions.Logging.ILogger logger;
    private readonly string label;

    public Hc393Half(Net clk, Net clr, Net q0, Net q1, Net q2, Net q3,
        string label = "393",
        Microsoft.Extensions.Logging.ILogger? logger = null,
        long delayPs = PropagationDelayPs)
    {
        nets = new[] { clk, clr, q0, q1, q2, q3 };
        for (int i = 0; i < 4; i++)
            qDrivers[i] = new Driver(nets[IndexQ0 + i], DriveStrength.Strong);
        this.label = label;
        this.logger = logger ?? Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance;
        this.delayPs = delayPs;
    }

    public IReadOnlyList<int> PinNumbers { get; } = new[] { 0, 1, 2, 3, 4, 5 };

    public IReadOnlyList<Net> Nets => nets;

    public void Initialize(IScheduler scheduler)
    {
        for (int i = 0; i < 4; i++)
        {
            ffState[i] = false;
            scheduler.Schedule(delayPs, qDrivers[i], Signal.Low);
        }
        prevClk = nets[IndexClk].Value;
        for (int i = 0; i < 4; i++)
            prevQ[i] = nets[IndexQ0 + i].Value;
        clearAsserted = nets[IndexClr].Value == Signal.High;
    }

    public void OnInputChanged(int pinIndex, IScheduler scheduler)
    {
        switch (pinIndex)
        {
            case IndexClr: HandleClearChange(scheduler); break;
            case IndexClk: HandleClockEdge(scheduler); break;
            default:
                if (pinIndex >= IndexQ0 && pinIndex <= IndexQ0 + 3)
                    HandleRipple(pinIndex - IndexQ0, scheduler);
                break;
        }
    }

    private void HandleClearChange(IScheduler scheduler)
    {
        Signal clr = nets[IndexClr].Value;
        if (clr == Signal.High && !clearAsserted)
        {
            clearAsserted = true;
            logger.LogDebug("{Label} CLR asserted on net {Net}, clearing all bits",
                label, nets[IndexClr].Name);
            for (int i = 0; i < 4; i++)
            {
                if (ffState[i])
                {
                    ffState[i] = false;
                    scheduler.Schedule(delayPs, qDrivers[i], Signal.Low);
                }
            }
        }
        else if (clr != Signal.High)
        {
            clearAsserted = false;
            prevClk = nets[IndexClk].Value;
        }
    }

    private void HandleClockEdge(IScheduler scheduler)
    {
        Signal newClk = nets[IndexClk].Value;
        bool falling = prevClk == Signal.High && newClk == Signal.Low;
        prevClk = newClk;

        logger.LogDebug("{Label} CLK {Prev}->{New} (falling={Falling}, clear={Clear})",
            label, prevClk, newClk, falling, clearAsserted);

        if (!falling || clearAsserted) return;
        ToggleStage(0, scheduler);
    }

    private void HandleRipple(int stage, IScheduler scheduler)
    {
        if (stage >= 3) { prevQ[stage] = nets[IndexQ0 + stage].Value; return; }

        Signal newVal = nets[IndexQ0 + stage].Value;
        bool falling = prevQ[stage] == Signal.High && newVal == Signal.Low;
        prevQ[stage] = newVal;

        if (!falling || clearAsserted) return;
        ToggleStage(stage + 1, scheduler);
    }

    private void ToggleStage(int stage, IScheduler scheduler)
    {
        ffState[stage] = !ffState[stage];
        Signal next = ffState[stage] ? Signal.High : Signal.Low;
        logger.LogDebug("{Label} stage {Stage} toggle -> {Value}", label, stage, next);
        scheduler.Schedule(delayPs, qDrivers[stage], next);
    }
}