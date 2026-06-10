using TTLSim.Core;
using Xunit;

namespace TTLSim.Tests;

public class SimulatorTests
{
    [Fact]
    public void Initialize_runs_on_every_chip()
    {
        Net n = new(1);
        TestSource src = new(n, Signal.High, delay: 0);

        Simulator sim = new(EmptyNetTable(), new[] { (IChip)src });
        sim.Start();
        sim.RunUntilQuiescent();

        Assert.Equal(Signal.High, n.Value);
        Assert.Equal(1, src.InitializeCalls);
    }

    [Fact]
    public void Input_change_propagates_to_listener()
    {
        Net input = new(1);
        Net output = new(2);

        TestSource src = new(input, Signal.High, delay: 100);
        Inverter inv = new(input, output, delay: 50);

        Simulator sim = new(EmptyNetTable(), new IChip[] { src, inv });
        sim.Start();
        sim.RunUntil(200);

        Assert.Equal(Signal.High, input.Value);
        Assert.Equal(Signal.Low, output.Value);
    }

    [Fact]
    public void Idempotent_events_do_not_refire_listeners()
    {
        Net n = new(1);
        TestSource src = new(n, Signal.High, delay: 0);
        ListenerCounter listener = new(n);

        Simulator sim = new(EmptyNetTable(), new IChip[] { src, listener });
        sim.Start();
        sim.RunUntilQuiescent();

        // Drive the same value again via the source's own driver -- coalesced.
        src.ScheduleAgain(sim, 10);
        sim.RunUntil(100);

        Assert.Equal(1, listener.ChangeNotifications);
    }

    private static NetTable EmptyNetTable() =>
        NetTable.Build(System.Array.Empty<(PinRef, PinRef)>());
}

/// <summary>Drives a single net to a single value at tick = delay, then stops.</summary>
internal sealed class TestSource : IChip
{
    private readonly Net net;
    private readonly Driver driver;
    private readonly Signal value;
    private readonly long delay;

    public TestSource(Net net, Signal value, long delay)
    {
        this.net = net;
        this.value = value;
        this.delay = delay;
        driver = new Driver(net, DriveStrength.Strong);
    }

    public IReadOnlyList<int> PinNumbers => new[] { 1 };
    public IReadOnlyList<Net> Nets => new[] { net };
    public int InitializeCalls { get; private set; }

    public void Initialize(IScheduler scheduler)
    {
        InitializeCalls++;
        scheduler.Schedule(delay, driver, value);
    }

    public void OnInputChanged(int pinIndex, IScheduler scheduler) { }

    /// <summary>Re-schedule the same value -- used to test event coalescing.</summary>
    public void ScheduleAgain(IScheduler scheduler, long delay) =>
        scheduler.Schedule(delay, driver, value);
}

/// <summary>Inverts its input onto its output with the given propagation delay.</summary>
internal sealed class Inverter : IChip
{
    private readonly Net input;
    private readonly Net output;
    private readonly Driver driver;
    private readonly long delay;

    public Inverter(Net input, Net output, long delay)
    {
        this.input = input;
        this.output = output;
        this.delay = delay;
        driver = new Driver(output, DriveStrength.Strong);
    }

    public IReadOnlyList<int> PinNumbers => new[] { 1, 2 };
    public IReadOnlyList<Net> Nets => new[] { input, output };

    public void Initialize(IScheduler scheduler) { }

    public void OnInputChanged(int pinIndex, IScheduler scheduler)
    {
        if (pinIndex != 0) return;

        Signal next = input.Value switch
        {
            Signal.High => Signal.Low,
            Signal.Low => Signal.High,
            _ => Signal.Unknown
        };
        scheduler.Schedule(delay, driver, next);
    }
}

/// <summary>Just counts how many times its input net changed.</summary>
internal sealed class ListenerCounter : IChip
{
    private readonly Net net;
    public ListenerCounter(Net net) { this.net = net; }

    public IReadOnlyList<int> PinNumbers => new[] { 1 };
    public IReadOnlyList<Net> Nets => new[] { net };
    public int ChangeNotifications { get; private set; }

    public void Initialize(IScheduler scheduler) { }
    public void OnInputChanged(int pinIndex, IScheduler scheduler) => ChangeNotifications++;
}