using TTLSim.Chips.Passives;
using TTLSim.Chips.Sources;
using TTLSim.Core;
using Xunit;

namespace TTLSim.Tests;

public class ButtonInputTests
{
    [Fact]
    public void Released_button_lets_pullup_hold_node_high()
    {
        Net node = new(1);
        Net gnd = new(2);

        PullDriver pull = new(node, Signal.High);   // pull-up on the node
        GndDriver gd = new(gnd);                  // pin 2 tied to ground
        ButtonInput btn = new(node, gnd);

        Simulator sim = new(
            NetTable.Build(System.Array.Empty<(PinRef, PinRef)>()),
            new IChip[] { pull, gd, btn });
        sim.Start();
        sim.RunUntilQuiescent();

        // Button released: node sits High via the pull-up.
        Assert.Equal(Signal.High, node.Value);
    }

    [Fact]
    public void Pressed_button_pulls_node_low()
    {
        Net node = new(1);
        Net gnd = new(2);

        PullDriver pull = new(node, Signal.High);
        GndDriver gd = new(gnd);
        ButtonInput btn = new(node, gnd);

        Simulator sim = new(
            NetTable.Build(System.Array.Empty<(PinRef, PinRef)>()),
            new IChip[] { pull, gd, btn });
        sim.Start();
        sim.RunUntilQuiescent();

        btn.SetPressed(true, (IScheduler)sim);
        sim.RunUntilQuiescent();

        // Pressed: strong Low from ground (through the button) beats the weak pull-up.
        Assert.Equal(Signal.Low, node.Value);
    }

    [Fact]
    public void Releasing_button_restores_high()
    {
        Net node = new(1);
        Net gnd = new(2);

        PullDriver pull = new(node, Signal.High);
        GndDriver gd = new(gnd);
        ButtonInput btn = new(node, gnd);

        Simulator sim = new(
            NetTable.Build(System.Array.Empty<(PinRef, PinRef)>()),
            new IChip[] { pull, gd, btn });
        sim.Start();
        sim.RunUntilQuiescent();

        btn.SetPressed(true, (IScheduler)sim);
        sim.RunUntilQuiescent();
        Assert.Equal(Signal.Low, node.Value);

        btn.SetPressed(false, (IScheduler)sim);
        sim.RunUntilQuiescent();
        Assert.Equal(Signal.High, node.Value);
    }
}