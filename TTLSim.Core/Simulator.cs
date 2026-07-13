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
    /// Nets currently in contention. A net enters this set the instant two
    /// same-tier drivers on it assert opposite levels, and leaves it when they
    /// stop. Consult it after any Run* call to see whether the circuit is
    /// electrically sound right now.
    /// </summary>
    public IReadOnlyCollection<int> FaultedNetIds => faultedNetIds;

    /// <summary>
    /// Raised on the rising edge of a net's faulted state -- once when the
    /// contention appears, not once per event while it persists. A net that
    /// clears and re-contends raises again.
    ///
    /// Handlers run inside the event loop, on whatever thread called Run*.
    /// A handler that wants to halt at the faulting tick (the "break on
    /// electrical fault" behaviour) calls <see cref="RequestStop"/>; the
    /// current Run* call then returns without draining the rest of its window.
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
        if (queue.Count == 0 || queue.NextTick > targetTick)
            CurrentTick = targetTick;
    }

    public bool RunUntilQuiescent(int maxEvents = 1_000_000)
    {
        int n = 0;
        stopRequested = false;
        while (queue.TryDequeue(out SimEvent ev))
        {
            CurrentTick = ev.Tick;
            ApplyEvent(ev);
            if (stopRequested)
            {
                stopRequested = false;
                return false;   // halted, not quiescent
            }
            if (++n >= maxEvents) return false;
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
    /// Recompute this net's fault state and raise ElectricalFault on the
    /// rising edge. Edge-triggered on purpose: a stuck short would otherwise
    /// fire on every event for the rest of the run and bury the log.
    /// </summary>
    private void UpdateFault(Net net, long tick)
    {
        NetFault? fault = net.DetectFault();
        bool wasFaulted = net.Fault is not null;
        net.Fault = fault;

        if (fault is not null && !wasFaulted)
        {
            faultedNetIds.Add(net.Id);
            logger.LogError(
                "t={Tick} ELECTRICAL FAULT net {Net} [{Pins}] -- {High} {Strength} driver(s) "
                + "asserting High against {Low} asserting Low. Two outputs are fighting.",
                tick, net.Name, DescribePins(net),
                fault.HighDrivers, fault.Strength, fault.LowDrivers);

            ElectricalFault?.Invoke(this, new NetFaultEventArgs(net, fault, tick));
        }
        else if (fault is null && wasFaulted)
        {
            faultedNetIds.Remove(net.Id);
            logger.LogDebug("t={Tick} net {Net} contention cleared", tick, net.Name);
        }
    }

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

    /// <summary>Simulated tick (picoseconds) at which the contention appeared.</summary>
    public long Tick { get; }
}