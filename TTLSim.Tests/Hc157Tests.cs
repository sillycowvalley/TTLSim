using TTLSim.Chips.Multiplexers;
using TTLSim.Chips.Sources;
using TTLSim.Core;

public class Hc157Tests
{
    /// <summary>
    /// With /E LOW (enabled) and S LOW, each Y output should follow the
    /// corresponding I0 input.
    /// </summary>
    [Theory]
    [InlineData(Signal.Low, Signal.Low, Signal.Low, Signal.Low, Signal.Low)]
    [InlineData(Signal.High, Signal.Low, Signal.High, Signal.Low, Signal.High)]
    [InlineData(Signal.Low, Signal.High, Signal.Low, Signal.High, Signal.High)]
    [InlineData(Signal.High, Signal.High, Signal.High, Signal.High, Signal.High)]
    public void Select_low_routes_I0(Signal i1_0, Signal i2_0, Signal i3_0, Signal i4_0, Signal anyHigh)
    {
        BuildAndAssert(
            s: Signal.Low, en: Signal.Low,
            i_0: new[] { i1_0, i2_0, i3_0, i4_0 },
            i_1: new[] { Signal.Low, Signal.Low, Signal.Low, Signal.Low },
            expectedY: new[] { i1_0, i2_0, i3_0, i4_0 });
        _ = anyHigh;  // not used; just keeps the theory rows distinct
    }

    /// <summary>
    /// With /E LOW and S HIGH, each Y should follow I1.
    /// </summary>
    [Fact]
    public void Select_high_routes_I1()
    {
        BuildAndAssert(
            s: Signal.High, en: Signal.Low,
            i_0: new[] { Signal.Low, Signal.Low, Signal.Low, Signal.Low },
            i_1: new[] { Signal.High, Signal.Low, Signal.High, Signal.Low },
            expectedY: new[] { Signal.High, Signal.Low, Signal.High, Signal.Low });
    }

    /// <summary>
    /// With /E HIGH (disabled) the outputs are forced LOW regardless of the
    /// selected input. Note: not high-Z -- the '157 actively drives LOW.
    /// </summary>
    [Fact]
    public void Enable_high_forces_outputs_low()
    {
        BuildAndAssert(
            s: Signal.Low, en: Signal.High,
            i_0: new[] { Signal.High, Signal.High, Signal.High, Signal.High },
            i_1: new[] { Signal.High, Signal.High, Signal.High, Signal.High },
            expectedY: new[] { Signal.Low, Signal.Low, Signal.Low, Signal.Low });
    }

    /// <summary>
    /// Per-channel independence: each pair (I0, I1) drives only its own Y.
    /// Set distinct values on every input to confirm the four channels are
    /// not cross-wired internally.
    /// </summary>
    [Fact]
    public void Channels_are_independent()
    {
        // S=HIGH so the I1 column gets routed through.
        BuildAndAssert(
            s: Signal.High, en: Signal.Low,
            i_0: new[] { Signal.Low, Signal.Low, Signal.Low, Signal.Low },
            i_1: new[] { Signal.Low, Signal.High, Signal.Low, Signal.High },
            expectedY: new[] { Signal.Low, Signal.High, Signal.Low, Signal.High });
    }

    // ----------------------------------------------------------------------

    private static void BuildAndAssert(
        Signal s, Signal en,
        Signal[] i_0, Signal[] i_1, Signal[] expectedY)
    {
        Net sNet = new(1);
        Net enNet = new(2);
        Net[] i0Nets = { new(10), new(11), new(12), new(13) };
        Net[] i1Nets = { new(20), new(21), new(22), new(23) };
        Net[] yNets = { new(30), new(31), new(32), new(33) };

        List<IChip> chips = new();
        chips.Add(s == Signal.High ? new VccDriver(sNet) : new GndDriver(sNet));
        chips.Add(en == Signal.High ? new VccDriver(enNet) : new GndDriver(enNet));
        for (int ch = 0; ch < 4; ch++)
        {
            chips.Add(i_0[ch] == Signal.High ? new VccDriver(i0Nets[ch]) : new GndDriver(i0Nets[ch]));
            chips.Add(i_1[ch] == Signal.High ? new VccDriver(i1Nets[ch]) : new GndDriver(i1Nets[ch]));
        }

        Hc157 mux = new(
            s: sNet, enN: enNet,
            i1_0: i0Nets[0], i1_1: i1Nets[0], y1: yNets[0],
            i2_0: i0Nets[1], i2_1: i1Nets[1], y2: yNets[1],
            i3_0: i0Nets[2], i3_1: i1Nets[2], y3: yNets[2],
            i4_0: i0Nets[3], i4_1: i1Nets[3], y4: yNets[3]);
        chips.Add(mux);

        Simulator sim = new(
            NetTable.Build(System.Array.Empty<(PinRef, PinRef)>()),
            chips);
        sim.Start();
        sim.RunUntilQuiescent();

        for (int ch = 0; ch < 4; ch++)
            Assert.Equal(expectedY[ch], yNets[ch].Value);
    }
}