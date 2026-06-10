using Microsoft.Extensions.Logging;

namespace TTLSim.Core;

public sealed class Simulator : IScheduler
{
    private readonly NetTable nets;
    private readonly EventQueue queue = new();
    private readonly List<IChip> chips;
    private readonly Microsoft.Extensions.Logging.ILogger logger;

    private readonly Dictionary<int, List<(IChip Chip, int PinIndex)>> listeners = new();

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
        while (queue.NextTick is long t and >= 0 && t <= targetTick)
        {
            queue.TryDequeue(out SimEvent ev);
            CurrentTick = ev.Tick;
            ApplyEvent(ev);

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
        while (queue.TryDequeue(out SimEvent ev))
        {
            CurrentTick = ev.Tick;
            ApplyEvent(ev);
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