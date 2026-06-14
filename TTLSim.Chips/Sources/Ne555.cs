using TTLSim.Core;

namespace TTLSim.Chips.Sources;

/// <summary>
/// Digital stand-in for one NE555 timer core. The simulator has no analog
/// model, so the external RC network is invisible to it; the timer's role is
/// supplied explicitly (which constructor is used) rather than inferred from
/// the surrounding wiring, which cannot tell a Schmitt debouncer from an
/// output-feedback astable -- both tie THR to TRG and leave DISCH open.
///
/// Schmitt: OUT = NOT(THR/TRG), reactive, after one propagation delay. This is
///   the comparator-plus-latch reduced to an inverter because THR and TRG
///   share a net. Covers the debounce / Schmitt-trigger wiring.
/// Astable: OUT free-runs as a square wave at the configured period, ignoring
///   its inputs -- the digital equivalent of the RC oscillator, since the
///   engine has no capacitance to pace one with. Mirrors ClockSource: it
///   self-clocks off changes to its own OUT net.
///
/// Only OUT (pin 3) feeds the rest of the circuit; DISCH and CTRL are not
/// driven. An NE556 is two of these cores sharing VCC and GND.
/// </summary>
public sealed class Ne555 : IChip
{
    private enum Mode { Schmitt, Astable }

    private readonly Mode mode;
    private readonly Net outNet;
    private readonly Net? inNet;            // THR/TRG node; Schmitt mode only
    private readonly Driver outDriver;
    private readonly long propagationPs;    // Schmitt mode
    private readonly long halfPeriodPs;     // Astable mode
    private readonly int[] pins;
    private readonly Net[] nets;
    private Signal level = Signal.Low;      // Astable: last driven OUT level

    /// <summary>Schmitt-trigger / debounce core: OUT = NOT(in) after delay.</summary>
    public Ne555(Net inNet, Net outNet, long propagationPicoseconds)
    {
        mode = Mode.Schmitt;
        this.inNet = inNet;
        this.outNet = outNet;
        propagationPs = propagationPicoseconds;
        outDriver = new Driver(outNet, DriveStrength.Strong);
        nets = new[] { inNet, outNet };     // index 0 = THR/TRG in, index 1 = OUT
        pins = new[] { 6, 3 };
    }

    /// <summary>Astable core: OUT free-runs at the given period.</summary>
    public Ne555(Net outNet, long periodPicoseconds)
    {
        mode = Mode.Astable;
        this.outNet = outNet;
        halfPeriodPs = periodPicoseconds / 2;
        outDriver = new Driver(outNet, DriveStrength.Strong);
        nets = new[] { outNet };
        pins = new[] { 3 };
    }

    public IReadOnlyList<int> PinNumbers => pins;
    public IReadOnlyList<Net> Nets => nets;

    public void Initialize(IScheduler scheduler)
    {
        if (mode == Mode.Astable)
        {
            scheduler.Schedule(0, outDriver, Signal.Low);
            level = Signal.Low;
            ScheduleNextEdge(scheduler);
        }
        else
        {
            EvaluateSchmitt(scheduler);
        }
    }

    public void OnInputChanged(int pinIndex, IScheduler scheduler)
    {
        if (mode == Mode.Astable)
        {
            // Self-clocking: the only net we watch is our own OUT, whose
            // change schedules the next opposite edge a half-period later.
            level = outNet.Value;
            ScheduleNextEdge(scheduler);
            return;
        }

        // Schmitt: react to the THR/TRG input (index 0); ignore OUT feedback (index 1).
        if (pinIndex == 0) EvaluateSchmitt(scheduler);
    }

    private void EvaluateSchmitt(IScheduler scheduler)
    {
        Signal next = inNet!.Value switch
        {
            Signal.High => Signal.Low,
            Signal.Low => Signal.High,
            _ => Signal.Unknown
        };
        scheduler.Schedule(propagationPs, outDriver, next);
    }

    private void ScheduleNextEdge(IScheduler scheduler)
    {
        Signal next = level == Signal.High ? Signal.Low : Signal.High;
        scheduler.Schedule(halfPeriodPs, outDriver, next);
    }
}