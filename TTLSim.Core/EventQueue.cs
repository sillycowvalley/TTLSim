namespace TTLSim.Core;

/// <summary>One scheduled change to a driver's output.</summary>
public readonly record struct SimEvent(long Tick, Driver Driver, Signal NewOutput, long Sequence);

/// <summary>
/// Priority queue of pending SimEvents, ordered by (tick, insertion sequence).
/// </summary>
public sealed class EventQueue
{
    private readonly PriorityQueue<SimEvent, (long Tick, long Sequence)> queue = new();
    private long nextSequence;

    public int Count => queue.Count;

    public long NextTick => queue.TryPeek(out _, out (long Tick, long Sequence) prio) ? prio.Tick : -1;

    public void Schedule(long tick, Driver driver, Signal newOutput)
    {
        long seq = nextSequence++;
        SimEvent ev = new(tick, driver, newOutput, seq);
        queue.Enqueue(ev, (tick, seq));
    }

    public bool TryDequeue(out SimEvent ev) => queue.TryDequeue(out ev, out _);

    public void Clear()
    {
        queue.Clear();
        nextSequence = 0;
    }
}