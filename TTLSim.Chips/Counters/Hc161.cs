using Microsoft.Extensions.Logging;
using TTLSim.Core;

namespace TTLSim.Chips.Counters;

/// <summary>
/// 74HC161 — presettable synchronous 4-bit binary counter with ASYNCHRONOUS
/// clear. Identical to the '163 except /CLR: on the '161, /CLR low zeroes
/// the count immediately, overriding the clock; on the '163 it waits for
/// the rising edge.
///
/// Rising-edge priority (when /CLR is high): /LD low loads D0..D3; else
/// CEP and CET both high counts up; else holds. RCO is high whenever the
/// count is 15 and CET is high.
/// </summary>
public sealed class Hc161 : IChip
{
    public const long PropagationDelayPs = 12_000;

    // Indices into nets[] -- the order PinNumbers is declared in.
    private const int IndexClr = 0;   // /CLR  (pin 1)
    private const int IndexClk = 1;   // CLK   (pin 2)
    private const int IndexD0 = 2;    // D0..D3 (pins 3..6)
    private const int IndexCep = 6;   // CEP   (pin 7)
    private const int IndexLd = 7;    // /LD   (pin 9)
    private const int IndexCet = 8;   // CET   (pin 10)
    private const int IndexQ0 = 9;    // Q0..Q3 (pins 14,13,12,11)
    private const int IndexRco = 13;  // RCO   (pin 15)

    private readonly Net[] nets;
    private readonly Driver[] qDrivers = new Driver[4];
    private readonly Driver rcoDriver;
    private readonly long delayPs;

    private int count;
    private Signal prevClk = Signal.Unknown;
    private bool clearAsserted;

    private readonly Microsoft.Extensions.Logging.ILogger logger;
    private readonly string label;

    public Hc161(
        Net clrN, Net clkN,
        Net d0, Net d1, Net d2, Net d3,
        Net cepN, Net ldN, Net cetN,
        Net q0, Net q1, Net q2, Net q3,
        Net rcoN,
        string label = "161",
        Microsoft.Extensions.Logging.ILogger? logger = null,
        long delayPs = PropagationDelayPs)
    {
        nets = new[]
        {
            clrN, clkN,
            d0, d1, d2, d3,
            cepN, ldN, cetN,
            q0, q1, q2, q3,
            rcoN
        };
        for (int i = 0; i < 4; i++)
            qDrivers[i] = new Driver(nets[IndexQ0 + i], DriveStrength.Strong);
        rcoDriver = new Driver(nets[IndexRco], DriveStrength.Strong);

        this.label = label;
        this.logger = logger ?? Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance;
        this.delayPs = delayPs;
    }

    // Pin numbers in nets[] order: /CLR, CLK, D0..D3, CEP, /LD, CET, Q0..Q3, RCO.
    public IReadOnlyList<int> PinNumbers { get; }
        = new[] { 1, 2, 3, 4, 5, 6, 7, 9, 10, 14, 13, 12, 11, 15 };

    public IReadOnlyList<Net> Nets => nets;

    public void Initialize(IScheduler scheduler)
    {
        count = 0;
        for (int i = 0; i < 4; i++)
            scheduler.Schedule(delayPs, qDrivers[i], Signal.Low);
        scheduler.Schedule(delayPs, rcoDriver, Signal.Low);
        prevClk = nets[IndexClk].Value;
        clearAsserted = nets[IndexClr].Value == Signal.Low;
    }

    public void OnInputChanged(int pinIndex, IScheduler scheduler)
    {
        // Unlike the '163, /CLR is asynchronous here -- a change on it acts
        // immediately rather than waiting for the clock edge.
        if (pinIndex == IndexClr)
            HandleClearChange(scheduler);
        else if (pinIndex == IndexClk)
            HandleClockEdge(scheduler);
        else if (pinIndex == IndexCet)
            UpdateRco(scheduler);
    }

    private void HandleClearChange(IScheduler scheduler)
    {
        Signal clr = nets[IndexClr].Value;
        if (clr == Signal.Low && !clearAsserted)
        {
            clearAsserted = true;
            logger.LogDebug("{Label} /CLR asserted -- async clear to 0", label);
            SetCount(0, scheduler);
        }
        else if (clr != Signal.Low)
        {
            clearAsserted = false;
        }
    }

    private void HandleClockEdge(IScheduler scheduler)
    {
        Signal newClk = nets[IndexClk].Value;
        bool rising = prevClk == Signal.Low && newClk == Signal.High;
        prevClk = newClk;
        if (!rising) return;

        // While /CLR is held low, the async clear pins the count at 0 --
        // the clock can't load or count past it.
        if (clearAsserted) return;

        int next;
        if (nets[IndexLd].Value == Signal.Low)
        {
            next = ReadDataInputs();                    // synchronous load
        }
        else if (nets[IndexCep].Value == Signal.High &&
                 nets[IndexCet].Value == Signal.High)
        {
            next = (count + 1) & 0xF;                   // count up
        }
        else
        {
            next = count;                               // hold
        }

        logger.LogDebug("{Label} CLK rising: {Old} -> {New}", label, count, next);
        SetCount(next, scheduler);
    }

    private int ReadDataInputs()
    {
        int v = 0;
        for (int i = 0; i < 4; i++)
            if (nets[IndexD0 + i].Value == Signal.High)
                v |= 1 << i;
        return v;
    }

    private void SetCount(int next, IScheduler scheduler)
    {
        if (next != count)
        {
            count = next;
            for (int i = 0; i < 4; i++)
            {
                Signal bit = ((count >> i) & 1) != 0 ? Signal.High : Signal.Low;
                scheduler.Schedule(delayPs, qDrivers[i], bit);
            }
        }
        UpdateRco(scheduler);
    }

    private void UpdateRco(IScheduler scheduler)
    {
        // RCO = (count == 15) AND CET high. Combinational off the current
        // count and CET -- not edge-triggered.
        bool rco = count == 0xF && nets[IndexCet].Value == Signal.High;
        scheduler.Schedule(delayPs, rcoDriver,
            rco ? Signal.High : Signal.Low);
    }
}