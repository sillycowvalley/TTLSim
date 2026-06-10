using Microsoft.Extensions.Logging;
using TTLSim.Core;

namespace TTLSim.Chips.Counters;

/// <summary>
/// One half of a 74LS390 — a 4-bit decade counter split into two
/// independently-clocked sections that share a common async master reset:
///
///   * a ÷2 stage:  CKA (CP0) falling edge toggles QA (Q0).
///   * a ÷5 stage:  CKB (CP1) falling edge advances a mod-5 counter whose
///                  outputs are QB,QC,QD (Q1=bit0, Q2=bit1, Q3=bit2),
///                  cycling 0,1,2,3,4,0,1,... .
///
/// MR (active HIGH, async) forces all four outputs LOW for this half.
///
/// To get BCD ÷10 the user wires QA → CKB externally; for a 50%-duty ÷10
/// they wire CKB as the input, QD → CKA, and take QA as the output. We
/// model the two sections as fully independent — exactly how the chip
/// actually behaves — so any of those external arrangements work without
/// special-casing here.
///
/// The ÷5 section is modelled as a synchronous mod-5 state machine rather
/// than as the datasheet's ripple-with-feedback flip-flop network. The
/// observable Q1/Q2/Q3 behaviour is identical from the simulator's point of
/// view, and we sidestep the decoding-spike concern (which the '393 model
/// doesn't attempt either).
/// </summary>
public sealed class Hc390Half : IChip
{
    public const long PropagationDelayPs = 10_000;

    private const int IndexCka = 0;
    private const int IndexCkb = 1;
    private const int IndexMr = 2;
    private const int IndexQa = 3;   // ÷2 output
    private const int IndexQb = 4;   // ÷5 bit 0
    private const int IndexQc = 5;   // ÷5 bit 1
    private const int IndexQd = 6;   // ÷5 bit 2

    private readonly Net[] nets;
    private readonly Driver[] qDrivers = new Driver[4];   // 0=QA, 1=QB, 2=QC, 3=QD
    private readonly long delayPs;

    private bool qaState;          // ÷2 flip-flop
    private int div5State;         // 0..4, ÷5 counter

    private Signal prevCka = Signal.Unknown;
    private Signal prevCkb = Signal.Unknown;

    private bool clearAsserted;

    private readonly Microsoft.Extensions.Logging.ILogger logger;
    private readonly string label;

    public Hc390Half(Net cka, Net ckb, Net mr,
        Net qa, Net qb, Net qc, Net qd,
        string label = "390",
        Microsoft.Extensions.Logging.ILogger? logger = null,
        long delayPs = PropagationDelayPs)
    {
        nets = new[] { cka, ckb, mr, qa, qb, qc, qd };
        for (int i = 0; i < 4; i++)
            qDrivers[i] = new Driver(nets[IndexQa + i], DriveStrength.Strong);
        this.label = label;
        this.logger = logger ?? Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance;
        this.delayPs = delayPs;
    }

    public IReadOnlyList<int> PinNumbers { get; } = new[] { 0, 1, 2, 3, 4, 5, 6 };

    public IReadOnlyList<Net> Nets => nets;

    public void Initialize(IScheduler scheduler)
    {
        qaState = false;
        div5State = 0;
        for (int i = 0; i < 4; i++)
            scheduler.Schedule(delayPs, qDrivers[i], Signal.Low);

        prevCka = nets[IndexCka].Value;
        prevCkb = nets[IndexCkb].Value;
        clearAsserted = nets[IndexMr].Value == Signal.High;
    }

    public void OnInputChanged(int pinIndex, IScheduler scheduler)
    {
        switch (pinIndex)
        {
            case IndexMr: HandleClearChange(scheduler); break;
            case IndexCka: HandleCkaEdge(scheduler); break;
            case IndexCkb: HandleCkbEdge(scheduler); break;
                // Q outputs are driven by this chip; nothing to do on changes.
        }
    }

    private void HandleClearChange(IScheduler scheduler)
    {
        Signal mr = nets[IndexMr].Value;
        if (mr == Signal.High && !clearAsserted)
        {
            clearAsserted = true;
            logger.LogDebug("{Label} MR asserted on net {Net}, clearing both sections",
                label, nets[IndexMr].Name);

            if (qaState)
            {
                qaState = false;
                scheduler.Schedule(delayPs, qDrivers[0], Signal.Low);
            }
            if (div5State != 0)
            {
                div5State = 0;
                scheduler.Schedule(delayPs, qDrivers[1], Signal.Low);
                scheduler.Schedule(delayPs, qDrivers[2], Signal.Low);
                scheduler.Schedule(delayPs, qDrivers[3], Signal.Low);
            }
        }
        else if (mr != Signal.High)
        {
            clearAsserted = false;
            // Resync edge detectors so an edge that occurred while clear was
            // asserted doesn't fire spuriously when MR releases.
            prevCka = nets[IndexCka].Value;
            prevCkb = nets[IndexCkb].Value;
        }
    }

    private void HandleCkaEdge(IScheduler scheduler)
    {
        Signal newCka = nets[IndexCka].Value;
        bool falling = prevCka == Signal.High && newCka == Signal.Low;
        prevCka = newCka;

        if (!falling || clearAsserted) return;

        qaState = !qaState;
        Signal next = qaState ? Signal.High : Signal.Low;
        logger.LogDebug("{Label} CKA falling -> QA={Value}", label, next);
        scheduler.Schedule(delayPs, qDrivers[0], next);
    }

    private void HandleCkbEdge(IScheduler scheduler)
    {
        Signal newCkb = nets[IndexCkb].Value;
        bool falling = prevCkb == Signal.High && newCkb == Signal.Low;
        prevCkb = newCkb;

        if (!falling || clearAsserted) return;

        int prev = div5State;
        div5State = (div5State + 1) % 5;
        logger.LogDebug("{Label} CKB falling -> div5 {Prev}->{Next}",
            label, prev, div5State);

        // Drive each ÷5 output whose bit changed.
        for (int bit = 0; bit < 3; bit++)
        {
            int mask = 1 << bit;
            if (((prev ^ div5State) & mask) != 0)
            {
                Signal v = (div5State & mask) != 0 ? Signal.High : Signal.Low;
                // qDrivers[1]=QB(bit0), [2]=QC(bit1), [3]=QD(bit2).
                scheduler.Schedule(delayPs, qDrivers[1 + bit], v);
            }
        }
    }
}