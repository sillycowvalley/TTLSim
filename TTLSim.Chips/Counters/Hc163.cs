using Microsoft.Extensions.Logging;
using TTLSim.Core;

namespace TTLSim.Chips.Counters;

/// <summary>
/// 74HC163 — presettable synchronous 4-bit binary counter with synchronous
/// clear. Everything happens on the rising edge of CLK; there are no
/// asynchronous side effects (this is the difference from the '161, whose
/// /CLR overrides the clock).
///
/// Rising-edge priority: /CLR low loads zero; else /LD low loads D0..D3;
/// else CEP and CET both high counts up; else holds. RCO is high whenever
/// the count is 15 and CET is high -- the ripple-carry used to chain a
/// second counter's CEP/CET.
/// </summary>
public sealed class Hc163 : IChip
{
    public const long PropagationDelayPs = 12_000;

    // Indices into nets[] -- the order PinNumbers is declared in.
    private const int IndexClr = 0;   // /CLR  (pin 1)
    private const int IndexClk = 1;   // CLK   (pin 2)
    private const int IndexD0 = 2;   // D0..D3 (pins 3..6)
    private const int IndexCep = 6;   // CEP   (pin 7)
    private const int IndexLd = 7;   // /LD   (pin 9)
    private const int IndexCet = 8;   // CET   (pin 10)
    private const int IndexQ0 = 9;   // Q0..Q3 (pins 14,13,12,11)
    private const int IndexRco = 13;  // RCO   (pin 15)

    private readonly Net[] nets;
    private readonly Driver[] qDrivers = new Driver[4];
    private readonly Driver rcoDriver;
    private readonly long delayPs;

    private int count;
    private Signal prevClk = Signal.Unknown;

    private readonly Microsoft.Extensions.Logging.ILogger logger;
    private readonly string label;

    public Hc163(
        Net clrN, Net clkN,
        Net d0, Net d1, Net d2, Net d3,
        Net cepN, Net ldN, Net cetN,
        Net q0, Net q1, Net q2, Net q3,
        Net rcoN,
        string label = "163",
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
    }

    public void OnInputChanged(int pinIndex, IScheduler scheduler)
    {
        // Only the clock edge does anything. CEP/CET/D/LD/CLR are all
        // sampled at the edge -- a change on any of them between edges is
        // remembered by the net itself and read next edge. RCO can change
        // when CET changes, so refresh it on a CET change too.
        if (pinIndex == IndexClk)
            HandleClockEdge(scheduler);
        else if (pinIndex == IndexCet)
            UpdateRco(scheduler);
    }

    private void HandleClockEdge(IScheduler scheduler)
    {
        Signal newClk = nets[IndexClk].Value;
        bool rising = prevClk == Signal.Low && newClk == Signal.High;
        prevClk = newClk;
        if (!rising) return;

        int next;
        if (nets[IndexClr].Value == Signal.Low)
        {
            next = 0;                                   // synchronous clear
        }
        else if (nets[IndexLd].Value == Signal.Low)
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