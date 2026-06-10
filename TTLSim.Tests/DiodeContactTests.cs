using TTLSim.Chips.Passives;
using TTLSim.Chips.Sources;
using TTLSim.Core;
using Xunit;

namespace TTLSim.Tests;

public class DiodeContactTests
{
    [Fact]
    public void Anode_high_pulls_cathode_high()
    {
        Net anode = new(1), cathode = new(2);
        VccDriver vcc = new(anode);
        DiodeContact diode = new(anode, cathode);

        Simulator sim = new(
            NetTable.Build(System.Array.Empty<(PinRef, PinRef)>()),
            new IChip[] { vcc, diode });
        sim.Start();
        sim.RunUntilQuiescent();

        Assert.Equal(Signal.High, cathode.Value);
    }

    [Fact]
    public void Anode_low_leaves_cathode_floating()
    {
        Net anode = new(1), cathode = new(2);
        GndDriver gnd = new(anode);
        DiodeContact diode = new(anode, cathode);

        Simulator sim = new(
            NetTable.Build(System.Array.Empty<(PinRef, PinRef)>()),
            new IChip[] { gnd, diode });
        sim.Start();
        sim.RunUntilQuiescent();

        // Nothing else drives the cathode; the diode is off, contributing
        // HighZ. The engine's ApplyEvent skips Resolve when a driver doesn't
        // actually change value (HighZ -> HighZ is a no-op), so the cathode
        // net keeps its initial Unknown rather than getting resolved to HighZ.
        // Both are "nothing useful is happening here" in practice; Unknown
        // is just the more honest of the two.
        Assert.Equal(Signal.Unknown, cathode.Value);
    }

    [Fact]
    public void Strong_low_on_cathode_beats_diode()
    {
        // Schottky drop is 0.3 V in reality; in our four-state world the
        // diode loses to any strong driver on the cathode.
        Net anode = new(1), cathode = new(2);
        VccDriver vcc = new(anode);
        DiodeContact diode = new(anode, cathode);
        GndDriver gnd = new(cathode);

        Simulator sim = new(
            NetTable.Build(System.Array.Empty<(PinRef, PinRef)>()),
            new IChip[] { vcc, diode, gnd });
        sim.Start();
        sim.RunUntilQuiescent();

        Assert.Equal(Signal.Low, cathode.Value);
    }

    [Fact]
    public void Cathode_high_does_not_drive_anode()
    {
        // Reverse-biased: cathode HIGH, anode otherwise undriven. The diode
        // blocks; anode stays floating. (The back-conduction rule only fires
        // for cathode LOW -- see Cathode_low_pulls_anode_low below.)
        Net anode = new(1), cathode = new(2);
        DiodeContact diode = new(anode, cathode);
        VccDriver vcc = new(cathode);

        Simulator sim = new(
            NetTable.Build(System.Array.Empty<(PinRef, PinRef)>()),
            new IChip[] { diode, vcc });
        sim.Start();
        sim.RunUntilQuiescent();

        Assert.Equal(Signal.High, cathode.Value);
        // anode is floating (reverse-biased diode contributes nothing).
        // Engine optimisation: HighZ->HighZ drivers don't trigger Resolve,
        // so the net keeps its initial Unknown value rather than HighZ.
        Assert.Equal(Signal.Unknown, anode.Value);
    }

    [Fact]
    public void Cathode_low_pulls_anode_low()
    {
        // Forward-biased viewed from the cathode end: a strong low on the
        // cathode pulls the anode net low through the conducting diode.
        // This is what makes "VCC -> R -> LED -> D -> CLK_LOW" work in the
        // sim -- the cathode side of D sinks current to CLK, and the LED's
        // cathode (= the diode's anode) gets pulled low so the LED lights.
        Net anode = new(1), cathode = new(2);
        DiodeContact diode = new(anode, cathode);
        GndDriver gnd = new(cathode);

        Simulator sim = new(
            NetTable.Build(System.Array.Empty<(PinRef, PinRef)>()),
            new IChip[] { diode, gnd });
        sim.Start();
        sim.RunUntilQuiescent();

        Assert.Equal(Signal.Low, cathode.Value);
        Assert.Equal(Signal.Low, anode.Value);
    }

    [Fact]
    public void Diode_loses_to_strong_driver_on_anode()
    {
        // Symmetric to Strong_low_on_cathode_beats_diode: a strong driver on
        // the anode side should beat the Medium back-conduction drive.
        Net anode = new(1), cathode = new(2);
        DiodeContact diode = new(anode, cathode);
        VccDriver vccAnode = new(anode);     // strong HIGH on anode
        GndDriver gndCathode = new(cathode); // strong LOW on cathode

        Simulator sim = new(
            NetTable.Build(System.Array.Empty<(PinRef, PinRef)>()),
            new IChip[] { diode, vccAnode, gndCathode });
        sim.Start();
        sim.RunUntilQuiescent();

        // Both sides are strongly driven; the diode contributes Medium on
        // both, both lose to Strong. Externally-set values stand.
        Assert.Equal(Signal.High, anode.Value);
        Assert.Equal(Signal.Low, cathode.Value);
    }

    [Fact]
    public void Diode_beats_weak_pull_via_back_conduction()
    {
        // Forward-biased back-conduction (cathode strong LOW) at Medium drive
        // must beat a Weak pull-up on the anode side.
        Net anode = new(1), cathode = new(2);
        DiodeContact diode = new(anode, cathode);
        GndDriver gnd = new(cathode);
        PullDriver pullup = new(anode, Signal.High);

        Simulator sim = new(
            NetTable.Build(System.Array.Empty<(PinRef, PinRef)>()),
            new IChip[] { diode, gnd, pullup });
        sim.Start();
        sim.RunUntilQuiescent();

        // Medium LOW from the diode beats Weak HIGH from the pull-up.
        Assert.Equal(Signal.Low, anode.Value);
    }

    [Fact]
    public void Wired_or_two_diodes_either_anode_high_pulls_cathode_high()
    {
        // Classic diode-OR: two diodes, anodes driven independently, cathodes
        // tied together with a pull-down. Output goes high if either input
        // is high.
        Net a1 = new(1), a2 = new(2), shared = new(3);

        DiodeContact d1 = new(a1, shared);
        DiodeContact d2 = new(a2, shared);
        PullDriver pulldown = new(shared, Signal.Low);

        // a1 high, a2 low.
        VccDriver vcc = new(a1);
        GndDriver gnd = new(a2);

        Simulator sim = new(
            NetTable.Build(System.Array.Empty<(PinRef, PinRef)>()),
            new IChip[] { d1, d2, pulldown, vcc, gnd });
        sim.Start();
        sim.RunUntilQuiescent();

        // a1=H, a2=L -> shared should be H (d1 pulls it up, d2 off).
        Assert.Equal(Signal.High, shared.Value);
    }

    [Fact]
    public void Wired_or_both_anodes_low_leaves_cathode_at_pulldown()
    {
        Net a1 = new(1), a2 = new(2), shared = new(3);

        DiodeContact d1 = new(a1, shared);
        DiodeContact d2 = new(a2, shared);
        PullDriver pulldown = new(shared, Signal.Low);
        GndDriver g1 = new(a1);
        GndDriver g2 = new(a2);

        Simulator sim = new(
            NetTable.Build(System.Array.Empty<(PinRef, PinRef)>()),
            new IChip[] { d1, d2, pulldown, g1, g2 });
        sim.Start();
        sim.RunUntilQuiescent();

        Assert.Equal(Signal.Low, shared.Value);
    }

    [Fact]
    public void Anode_change_propagates_to_cathode()
    {
        // Start with anode low, then drive it high via a switch closing,
        // and confirm the cathode follows.
        Net anode = new(1), cathode = new(2), src = new(3);

        VccDriver vcc = new(src);
        SwitchInput sw = new(src, anode, initiallyClosed: false);
        DiodeContact diode = new(anode, cathode);
        PullDriver pulldown = new(anode, Signal.Low);

        Simulator sim = new(
            NetTable.Build(System.Array.Empty<(PinRef, PinRef)>()),
            new IChip[] { vcc, sw, diode, pulldown });
        sim.Start();
        sim.RunUntilQuiescent();

        Assert.Equal(Signal.Low, anode.Value);
        // Cathode floats: diode is off (anode LOW), pulldown is on the anode
        // side. The cathode net has no driver that ever leaves HighZ, so it
        // stays at the initial Unknown rather than getting resolved to HighZ.
        Assert.Equal(Signal.Unknown, cathode.Value);

        sw.SetClosed(true, sim);
        sim.RunUntilQuiescent();

        Assert.Equal(Signal.High, anode.Value);
        Assert.Equal(Signal.High, cathode.Value);
    }

    // Additional test to add to DiodeContactTests.cs after the existing tests.
    // This pins down the behaviour that fixes the spurious-startup-tick bug
    // in the Timer.ttlproj diode-AND reset detectors.

    [Fact]
    public void Pullup_on_anode_alone_does_not_drive_cathode()
    {
        // A diode with only a Weak pull-up on the anode side and nothing on the
        // cathode side must NOT propagate the pull-up's HIGH onto the cathode.
        // Reason: a real diode is a passive valve, not a buffer. With a high-
        // impedance anode source (a pull-up resistor), the cathode side can't
        // be charged HIGH without first discharging the pull-up -- so in steady
        // state with no load on the cathode the cathode stays floating.
        //
        // The simulator consequence: if this rule didn't hold, then at t=0
        // (before any strong driver has propagated) a diode-AND reset detector
        // would push HIGH onto its cathode-side Q-output nets. A propagation
        // delay later when the counter chip asserts its real LOW, the Q-output
        // nets would transition HIGH->LOW, which a downstream falling-edge
        // detector (the next counter's CKA) would treat as a real clock edge.
        // That's exactly the bug that made the minutes counter start at 1
        // instead of 0 in the Timer.ttlproj clock circuit.
        Net anode = new(1), cathode = new(2);
        PullDriver pullup = new(anode, Signal.High);
        DiodeContact diode = new(anode, cathode);

        Simulator sim = new(
            NetTable.Build(System.Array.Empty<(PinRef, PinRef)>()),
            new IChip[] { pullup, diode });
        sim.Start();
        sim.RunUntilQuiescent();

        // Anode resolves to Weak HIGH (the pull-up alone, since the diode itself
        // contributes HighZ on the anode side).
        Assert.Equal(Signal.High, anode.Value);
        // Cathode must remain at its initial Unknown -- the diode's Weak-source
        // anode is not Strong enough to forward-conduct.
        Assert.Equal(Signal.Unknown, cathode.Value);
    }

    [Fact]
    public void Diode_and_reset_detector_settles_cleanly_at_startup()
    {
        // Reproduce the exact topology that misbehaved in Timer.ttlproj's
        // mod-6 reset detector:
        //   * Two diodes, cathodes on the "Q output" side, anodes joined at
        //     a junction.
        //   * R 47k pull-up from the junction to VCC.
        //   * Both Q outputs strongly driven LOW (as a counter chip would do
        //     after it has propagated its Initialize).
        // At quiescence the junction should be LOW (because each diode passes
        // its Strong-LOW back to the junction at Medium), and the Q-output
        // nets must NOT have transiently risen during settling.
        Net q1 = new(1), q2 = new(2), junction = new(3), vcc = new(4);

        VccDriver vccDriver = new(vcc);
        GndDriver gndQ1 = new(q1);              // strong LOW on Q1
        GndDriver gndQ2 = new(q2);              // strong LOW on Q2

        // Diodes: cathodes on Q outputs, anodes on the junction.
        // (anode = pin1 = first arg, cathode = pin2 = second arg.)
        DiodeContact d1 = new(junction, q1);
        DiodeContact d2 = new(junction, q2);

        // Pull-up: PullDriver with one end on VCC. The PullDriver in the project
        // takes a Net + a target Signal and drives that net to that signal weakly.
        PullDriver pullup = new(junction, Signal.High);

        Simulator sim = new(
            NetTable.Build(System.Array.Empty<(PinRef, PinRef)>()),
            new IChip[] { vccDriver, gndQ1, gndQ2, d1, d2, pullup });
        sim.Start();
        sim.RunUntilQuiescent();

        // Junction: pulled up Weakly to HIGH, pulled down at Medium by both
        // diodes (each cathode is Strong LOW, so back-conduction fires).
        // Medium beats Weak. Junction should be LOW.
        Assert.Equal(Signal.Low, junction.Value);

        // Q outputs must remain at their strongly-driven LOW. The diode-AND
        // pull-up must NOT have transiently lifted them HIGH.
        Assert.Equal(Signal.Low, q1.Value);
        Assert.Equal(Signal.Low, q2.Value);
    }

    [Fact]
    public void Diode_and_asserts_when_both_q_outputs_go_high()
    {
        // Same diode-AND topology as the previous test, but now both Q outputs
        // are strongly driven HIGH. Expectation: junction goes HIGH (no diode
        // conducts; pull-up wins). This is the "reset assertion" condition in
        // the Timer circuit's mod-6 detector.
        Net q1 = new(1), q2 = new(2), junction = new(3);

        VccDriver vccQ1 = new(q1);              // strong HIGH on Q1
        VccDriver vccQ2 = new(q2);              // strong HIGH on Q2

        DiodeContact d1 = new(junction, q1);
        DiodeContact d2 = new(junction, q2);
        PullDriver pullup = new(junction, Signal.High);

        Simulator sim = new(
            NetTable.Build(System.Array.Empty<(PinRef, PinRef)>()),
            new IChip[] { vccQ1, vccQ2, d1, d2, pullup });
        sim.Start();
        sim.RunUntilQuiescent();

        // Both diodes see Strong HIGH on their cathode side -> back-conduction
        // condition (cathode==LOW) is NOT met -> anode-target = HighZ.
        // Both diodes see anode = Weak HIGH (pull-up) -> forward conduction
        // condition (anode == Strong HIGH) is NOT met -> cathode-target = HighZ.
        // Junction's only contribution is the Weak HIGH pull-up.
        Assert.Equal(Signal.High, junction.Value);
    }

    [Fact]
    public void Diode_and_one_q_high_one_q_low_leaves_junction_low()
    {
        // The middle case of a diode-AND: one Q HIGH, one Q LOW. The diode
        // whose cathode is on the LOW Q output back-conducts and pulls the
        // junction Medium LOW. Junction ends up LOW.
        Net q1 = new(1), q2 = new(2), junction = new(3);

        VccDriver vccQ1 = new(q1);              // Q1 HIGH
        GndDriver gndQ2 = new(q2);              // Q2 LOW

        DiodeContact d1 = new(junction, q1);
        DiodeContact d2 = new(junction, q2);
        PullDriver pullup = new(junction, Signal.High);

        Simulator sim = new(
            NetTable.Build(System.Array.Empty<(PinRef, PinRef)>()),
            new IChip[] { vccQ1, gndQ2, d1, d2, pullup });
        sim.Start();
        sim.RunUntilQuiescent();

        Assert.Equal(Signal.Low, junction.Value);
        Assert.Equal(Signal.High, q1.Value);
        Assert.Equal(Signal.Low, q2.Value);
    }
}