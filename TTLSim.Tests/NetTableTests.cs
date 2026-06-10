using TTLSim.Core;
using Xunit;

namespace TTLSim.Tests;

public class NetTableTests
{
    [Fact]
    public void Single_connection_creates_one_net_with_two_pins()
    {
        PinRef a = new("U1", 1);
        PinRef b = new("U2", 2);

        NetTable table = NetTable.Build(new[] { (a, b) });

        Assert.Single(table.Nets);
        Assert.Same(table.FindNet(a), table.FindNet(b));
    }

    [Fact]
    public void Transitive_connections_merge_into_one_net()
    {
        PinRef a = new("U1", 1);
        PinRef b = new("U2", 2);
        PinRef c = new("U3", 3);

        // a-b and b-c should give one net of {a,b,c}.
        NetTable table = NetTable.Build(new[] { (a, b), (b, c) });

        Assert.Single(table.Nets);
        Assert.Same(table.FindNet(a), table.FindNet(c));
    }

    [Fact]
    public void Disjoint_connections_create_separate_nets()
    {
        PinRef a = new("U1", 1);
        PinRef b = new("U2", 2);
        PinRef c = new("U3", 3);
        PinRef d = new("U4", 4);

        NetTable table = NetTable.Build(new[] { (a, b), (c, d) });

        Assert.Equal(2, table.Nets.Count);
        Assert.NotSame(table.FindNet(a), table.FindNet(c));
    }

    [Fact]
    public void Unconnected_pin_lookup_returns_null()
    {
        NetTable table = NetTable.Build(Array.Empty<(PinRef, PinRef)>());
        Assert.Null(table.FindNet(new PinRef("UX", 1)));
    }
}

public class NetResolveTests
{
    private static Driver Strong(Net n, Signal s) =>
        new(n, DriveStrength.Strong) { Output = s };

    private static Driver Weak(Net n, Signal s) =>
        new(n, DriveStrength.Weak) { Output = s };

    [Fact]
    public void No_drivers_resolves_to_highz()
    {
        Net n = new(1);
        Assert.Equal(Signal.HighZ, n.Resolve());
    }

    [Fact]
    public void Single_strong_driver_wins()
    {
        Net n = new(1);
        Strong(n, Signal.High);
        Assert.Equal(Signal.High, n.Resolve());
    }

    [Fact]
    public void Strong_beats_weak()
    {
        Net n = new(1);
        Weak(n, Signal.High);
        Strong(n, Signal.Low);
        Assert.Equal(Signal.Low, n.Resolve());
    }

    [Fact]
    public void Weak_alone_defines_the_net()
    {
        Net n = new(1);
        Weak(n, Signal.High);
        Assert.Equal(Signal.High, n.Resolve());
    }

    [Fact]
    public void Conflicting_strong_drivers_resolve_to_unknown()
    {
        Net n = new(1);
        Strong(n, Signal.High);
        Strong(n, Signal.Low);
        Assert.Equal(Signal.Unknown, n.Resolve());
    }

    [Fact]
    public void Highz_drivers_are_ignored()
    {
        Net n = new(1);
        Strong(n, Signal.HighZ);
        Strong(n, Signal.High);
        Assert.Equal(Signal.High, n.Resolve());
    }

    [Fact]
    public void Conflicting_weak_drivers_resolve_to_unknown()
    {
        Net n = new(1);
        Weak(n, Signal.High);
        Weak(n, Signal.Low);
        Assert.Equal(Signal.Unknown, n.Resolve());
    }
}

public class EventQueueTests
{
    [Fact]
    public void Events_dequeue_in_tick_order()
    {
        EventQueue q = new();
        Net na = new(1, "A");
        Net nb = new(2, "B");
        Driver da = new(na, DriveStrength.Strong);
        Driver db = new(nb, DriveStrength.Strong);

        q.Schedule(100, da, Signal.High);
        q.Schedule(50, db, Signal.Low);

        q.TryDequeue(out SimEvent e1);
        q.TryDequeue(out SimEvent e2);

        Assert.Equal(50, e1.Tick);
        Assert.Equal(100, e2.Tick);
    }

    [Fact]
    public void Same_tick_events_dequeue_in_insertion_order()
    {
        EventQueue q = new();
        Net na = new(1, "A");
        Net nb = new(2, "B");
        Driver da = new(na, DriveStrength.Strong);
        Driver db = new(nb, DriveStrength.Strong);

        q.Schedule(100, da, Signal.High);
        q.Schedule(100, db, Signal.Low);

        q.TryDequeue(out SimEvent e1);
        q.TryDequeue(out SimEvent e2);

        Assert.Same(da, e1.Driver);
        Assert.Same(db, e2.Driver);
    }
}