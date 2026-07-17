using Microsoft.Extensions.Logging;

namespace TTLSim.Core;

public sealed class Simulator : IScheduler
{
    private readonly NetTable nets;
    private readonly EventQueue queue = new();
    private readonly List<IChip> chips;
    private readonly Microsoft.Extensions.Logging.ILogger logger;

    private readonly Dictionary<int, List<(IChip Chip, int PinIndex)>> listeners = new();

    private readonly HashSet<int> faultedNetIds = new();
    private bool stopRequested;

    // Contention observed but not yet escalated: net id -> onset detail and
    // the deadline at which it stops being a forgivable handover blink.
    // Populated by UpdateFault's rising edge (when the tolerance is on),
    // emptied by either the falling edge (blink -- forgiven) or
    // EscalateDueFaults (persisted -- reported). Lives per-Simulator, so a
    // rebuild starts clean by construction.
    private readonly Dictionary<int, PendingFault> pendingFaults = new();

    public Simulator(NetTable nets, IEnumerable<IChip> chips,
        Microsoft.Extensions.Logging.ILogger? logger = null)
    {
        this.nets = nets;
        this.chips = chips.ToList();
        this.logger = logger ?? Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance;
        BuildListenerMap();
        this.logger.LogInformation("Simulator built: {NetCount} nets, {ChipCount} chips",
            nets.Nets.Count, this.chips.Count);
    }

    public long CurrentTick { get; private set; }

    public NetTable Nets => nets;

    public IReadOnlyList<IChip> Chips => chips;

    /// <summary>
    /// How long (in picoseconds) a net may sit in contention before it is
    /// escalated as an electrical fault. Default 100 ns.
    ///
    /// <para>Rationale: on a tri-state bus handover, the outgoing driver's
    /// output-disable time (HC tPHZ/tPLZ, up to ~40-45 ns) overlaps the
    /// incoming driver's turn-on, so a few dozen nanoseconds of opposed
    /// strong drive is a routine, self-clearing "handover blink" -- thermally
    /// and electrically nothing on real silicon, and explicitly permitted by
    /// module contracts that forbid edge-sensitive consumers on the bus
    /// instead. Escalating at the first contested tick therefore halts
    /// correct designs. Duration is the discriminator: real faults are
    /// steady-state, blinks clear within one disable time. 100 ns covers any
    /// HC disable/enable skew with margin while staying far below functional
    /// sampling intervals.</para>
    ///
    /// <para>During the deferral the net resolves to
    /// <see cref="Signal.Unknown"/> exactly as before -- only the reporting
    /// waits. A contention that clears within the tolerance logs a debug
    /// "handover blink" line and raises nothing; one that persists raises
    /// <see cref="ElectricalFault"/> at onset + tolerance, with the event's
    /// Tick still naming the ONSET so the log points at the moment the fight
    /// began. Set to 0 to restore immediate escalation.</para>
    /// </summary>
    public long ContentionTolerancePs { get; set; } = 100_000;

    /// <summary>
    /// Nets currently in contention. A net enters this set the instant two
    /// same-tier drivers on it assert opposite levels, and leaves it when they
    /// stop. Consult it after any Run* call to see whether the circuit is
    /// electrically sound right now. Membership is live truth and is NOT
    /// deferred by <see cref="ContentionTolerancePs"/> -- a handover blink
    /// passes through this set for its duration.
    /// </summary>
    public IReadOnlyCollection<int> FaultedNetIds => faultedNetIds;

    /// <summary>
    /// Raised when a net's contention has persisted past
    /// <see cref="ContentionTolerancePs"/> (or immediately on the rising edge
    /// when the tolerance is 0) -- once per episode, not once per event while
    /// it persists. A net that clears and re-contends starts a fresh episode.
    ///
    /// Handlers run inside the event loop, on whatever thread called Run*.
    /// A handler that wants to halt (the "break on electrical fault"
    /// behaviour) calls <see cref="RequestStop"/>; the current Run* call then
    /// returns without draining the rest of its window, with
    /// <see cref="CurrentTick"/> at the escalation point (onset + tolerance).
    /// </summary>
    public event EventHandler<NetFaultEventArgs>? ElectricalFault;

    /// <summary>
    /// Ask the in-flight Run* call to return at the current tick. One-shot:
    /// the flag is consumed by the loop that observes it, so the next Run*
    /// continues normally. Safe to call from an ElectricalFault handler.
    /// </summary>
    public void RequestStop() => stopRequested = true;

    public void Start()
    {
        CurrentTick = 0;
        foreach (IChip chip in chips)
            chip.Initialize(this);
        logger.LogInformation("Simulator started");
    }

    public void RunUntil(long targetTick, int maxEvents = 5_000_000)
    {
        int n = 0;
        stopRequested = false;
        while (queue.NextTick is long t and >= 0 && t <= targetTick)
        {
            // Deferred contention whose deadline falls STRICTLY before the
            // next event escalates first, at its own deadline tick. Strict:
            // an event AT the deadline tick gets to clear the fault first, so
            // a blink lasting exactly the tolerance is still forgiven.
            if (EscalateDueFaults(t - 1))
            {
                stopRequested = false;
                return;
            }

            queue.TryDequeue(out SimEvent ev);
            CurrentTick = ev.Tick;
            ApplyEvent(ev);

            // An ElectricalFault handler asked us to halt here. Leave
            // CurrentTick at the faulting event so the UI can show exactly
            // when the contention appeared; the remaining events stay queued.
            if (stopRequested)
            {
                stopRequested = false;
                return;
            }

            if (++n >= maxEvents)
            {
                logger.LogWarning(
                    "RunUntil hit the {Cap}-event cap at tick {Tick}; possible same-tick loop.",
                    maxEvents, CurrentTick);
                return;
            }
        }

        // Deadlines between the last event and the window's end. A steady
        // short generates its conflicting events and then goes quiet, so
        // without this the deferral would never be revisited.
        if (EscalateDueFaults(targetTick))
        {
            stopRequested = false;
            return;
        }

        if (queue.Count == 0 || queue.NextTick > targetTick)
            CurrentTick = targetTick;
    }

    public bool RunUntilQuiescent(int maxEvents = 1_000_000)
    {
        int n = 0;
        stopRequested = false;
        while (queue.NextTick is long t and >= 0)
        {
            // Same strictly-before rule as RunUntil: the event at the
            // deadline tick may be the falling edge that forgives the blink.
            if (EscalateDueFaults(t - 1))
            {
                stopRequested = false;
                return false;   // halted, not quiescent
            }

            queue.TryDequeue(out SimEvent ev);
            CurrentTick = ev.Tick;
            ApplyEvent(ev);
            if (stopRequested)
            {
                stopRequested = false;
                return false;   // halted, not quiescent
            }
            if (++n >= maxEvents) return false;
        }

        // Queue empty: no future event can ever clear a pending contention,
        // so anything still deferred is steady-state by definition.
        if (EscalateDueFaults(long.MaxValue))
        {
            stopRequested = false;
            return false;
        }
        return true;
    }

    void IScheduler.Schedule(long delay, Driver driver, Signal newOutput)
    {
        if (delay < 0) throw new ArgumentOutOfRangeException(nameof(delay));
        queue.Schedule(CurrentTick + delay, driver, newOutput);
    }

    private void ApplyEvent(SimEvent ev)
    {
        Driver driver = ev.Driver;
        Net net = driver.Net;

        if (driver.Output == ev.NewOutput)
            return;
        driver.Output = ev.NewOutput;

        // Contention check FIRST, before the no-change early-out below.
        // A permanently-contended net resolves to Unknown, which is also its
        // initial value -- so it never "changes", never logs, and would slip
        // through the early-out completely silent. That silence is exactly how
        // a shorted latch hides. Check the drivers, not the resolved value.
        UpdateFault(net, ev.Tick);

        Signal resolved = net.Resolve();
        if (resolved == net.Value)
            return;

        logger.LogDebug("t={Tick} net {Net} [{Pins}] {Old} -> {New}",
            ev.Tick, net.Name, DescribePins(net), net.Value, resolved);

        net.Value = resolved;
        net.LastChangeTick = ev.Tick;

        if (!listeners.TryGetValue(net.Id, out var list))
            return;

        foreach ((IChip chip, int pinIndex) in list)
            chip.OnInputChanged(pinIndex, this);
    }

    /// <summary>
    /// Recompute this net's fault state. Rising edge: when the handover
    /// tolerance is on, record the onset and a deadline instead of raising --
    /// tri-state handover routinely produces a few dozen nanoseconds of
    /// opposed drive that clears itself (the "handover blink"). Falling edge
    /// within the tolerance forgives the episode with a debug line; a
    /// deadline reached first is escalated by <see cref="EscalateDueFaults"/>.
    /// Edge-triggered on purpose: a stuck short would otherwise fire on every
    /// event for the rest of the run and bury the log.
    /// </summary>
    private void UpdateFault(Net net, long tick)
    {
        NetFault? fault = net.DetectFault();
        bool wasFaulted = net.Fault is not null;
        net.Fault = fault;

        if (fault is not null && !wasFaulted)
        {
            faultedNetIds.Add(net.Id);

            if (ContentionTolerancePs <= 0)
            {
                logger.LogError(
                    "t={Tick} ELECTRICAL FAULT net {Net} [{Pins}] -- {High} {Strength} driver(s) "
                    + "asserting High against {Low} asserting Low. Two outputs are fighting.",
                    tick, net.Name, DescribePins(net),
                    fault.HighDrivers, fault.Strength, fault.LowDrivers);

                ElectricalFault?.Invoke(this, new NetFaultEventArgs(net, fault, tick));
                return;
            }

            pendingFaults[net.Id] = new PendingFault(
                net, fault, OnsetTick: tick, Deadline: tick + ContentionTolerancePs);
            logger.LogDebug(
                "t={Tick} net {Net} contention began; treating as tri-state handover "
                + "unless it outlives {Tol} ps",
                tick, net.Name, ContentionTolerancePs);
        }
        else if (fault is null && wasFaulted)
        {
            faultedNetIds.Remove(net.Id);

            if (pendingFaults.Remove(net.Id, out PendingFault? blink))
            {
                logger.LogDebug(
                    "t={Tick} net {Net} handover blink: contention cleared after {Dur} ps "
                    + "(tolerance {Tol} ps)",
                    tick, net.Name, tick - blink!.OnsetTick, ContentionTolerancePs);
            }
            else
            {
                logger.LogDebug("t={Tick} net {Net} contention cleared", tick, net.Name);
            }
        }
    }

    /// <summary>
    /// Escalate every deferred contention whose deadline is at or before
    /// <paramref name="upToTick"/>, earliest first, advancing
    /// <see cref="CurrentTick"/> to each deadline so a halt lands at
    /// onset + tolerance. The raised event carries the ONSET tick -- that is
    /// when the fight began; the deadline is merely when we stopped hoping.
    /// Returns true if any handler requested a stop (caller consumes the
    /// flag and returns).
    /// </summary>
    private bool EscalateDueFaults(long upToTick)
    {
        if (pendingFaults.Count == 0) return false;

        while (true)
        {
            PendingFault? due = null;
            foreach (PendingFault pf in pendingFaults.Values)
            {
                if (pf.Deadline <= upToTick && (due is null || pf.Deadline < due.Deadline))
                    due = pf;
            }
            if (due is null) return false;

            pendingFaults.Remove(due.Net.Id);
            if (due.Deadline > CurrentTick)
                CurrentTick = due.Deadline;

            // The entry only survives to its deadline if no falling edge
            // occurred, so Net.Fault is the live contention; fall back to the
            // onset snapshot purely for safety.
            NetFault fault = due.Net.Fault ?? due.Fault;

            logger.LogError(
                "t={Tick} ELECTRICAL FAULT net {Net} [{Pins}] -- contention since t={Onset} "
                + "outlived the {Tol} ps handover tolerance: {High} {Strength} driver(s) "
                + "asserting High against {Low} asserting Low. Two outputs are fighting.",
                CurrentTick, due.Net.Name, DescribePins(due.Net), due.OnsetTick,
                ContentionTolerancePs, fault.HighDrivers, fault.Strength, fault.LowDrivers);

            ElectricalFault?.Invoke(this, new NetFaultEventArgs(due.Net, fault, due.OnsetTick));

            if (stopRequested) return true;
        }
    }

    /// <summary>A contention that has begun but not yet outlived the
    /// handover tolerance: the net, the fault as first detected, when it
    /// began, and when it stops being forgivable.</summary>
    private sealed record PendingFault(
        Net Net, NetFault Fault, long OnsetTick, long Deadline);

    /// <summary>
    /// Compact one-line description of a net's pins for the log. Lists up
    /// to four pins; if there are more, appends a count of the rest. This
    /// makes net-change log lines self-explanatory without needing to
    /// cross-reference a netlist.
    /// </summary>
    private static string DescribePins(Net net)
    {
        IReadOnlyList<PinRef> pins = net.Pins;
        if (pins.Count == 0) return "no-pins";

        const int max = 4;
        int n = Math.Min(pins.Count, max);
        System.Text.StringBuilder sb = new();
        for (int i = 0; i < n; i++)
        {
            if (i > 0) sb.Append(',');
            sb.Append(pins[i].ItemId);
            sb.Append('.');
            sb.Append(pins[i].PinNumber);
        }
        if (pins.Count > max)
        {
            sb.Append(",+");
            sb.Append(pins.Count - max);
        }
        return sb.ToString();
    }

    private void BuildListenerMap()
    {
        foreach (IChip chip in chips)
        {
            for (int i = 0; i < chip.Nets.Count; i++)
            {
                Net? net = chip.Nets[i];
                if (net is null) continue;

                if (!listeners.TryGetValue(net.Id, out var list))
                {
                    list = new List<(IChip, int)>();
                    listeners[net.Id] = list;
                }
                list.Add((chip, i));
            }
        }
    }
}

/// <summary>
/// Payload for <see cref="Simulator.ElectricalFault"/>: which net went into
/// contention, the fault detail, and the simulated tick it happened at.
/// The net's Pins list names every pin sitting on the shorted node, which is
/// what a user needs to find the offending wire.
/// </summary>
public sealed class NetFaultEventArgs : EventArgs
{
    public NetFaultEventArgs(Net net, NetFault fault, long tick)
    {
        Net = net;
        Fault = fault;
        Tick = tick;
    }

    public Net Net { get; }

    public NetFault Fault { get; }

    /// <summary>Simulated tick (picoseconds) at which the contention appeared
    /// (the onset -- not the escalation deadline).</summary>
    public long Tick { get; }
}
