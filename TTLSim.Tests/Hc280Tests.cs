using TTLSim.Chips.Parity;
using TTLSim.Chips.Sources;
using TTLSim.Core;

public class Hc280Tests
{
    // Exhaustive: all 2^9 = 512 input patterns.
    public static IEnumerable<object[]> AllInputs()
    {
        for (int v = 0; v < 512; v++)
            yield return new object[] { v };
    }

    [Theory]
    [MemberData(nameof(AllInputs))]
    public void Pe_is_high_on_even_parity_and_po_is_its_complement(int pattern)
    {
        (Signal pe, Signal po) = Evaluate(pattern);

        int highCount = System.Numerics.BitOperations.PopCount((uint)pattern);
        bool even = (highCount & 1) == 0;

        Assert.Equal(even ? Signal.High : Signal.Low, pe);
        Assert.Equal(even ? Signal.Low : Signal.High, po);
    }

    [Fact]
    public void All_inputs_low_reads_as_even()
    {
        // Zero HIGH inputs is an even count, so PE asserts.
        (Signal pe, Signal po) = Evaluate(0);

        Assert.Equal(Signal.High, pe);
        Assert.Equal(Signal.Low, po);
    }

    [Fact]
    public void Floating_input_reads_as_low()
    {
        // Catalogue convention: Unknown / HighZ are treated as Low, so an
        // unwired input behaves exactly like one tied to GND -- it does not
        // shift the parity of the wired bits. Here I0 and I1 are driven HIGH
        // and everything else is left undriven; two HIGH inputs is even.
        Net[] inputs = new Net[9];
        for (int i = 0; i < 9; i++)
            inputs[i] = new Net(i + 1);
        Net pe = new(20), po = new(21);

        Hc280 parity = Build(inputs, pe, po);

        var chips = new List<IChip>
        {
            Drive(inputs[0], true),
            Drive(inputs[1], true),
            parity,
        };

        Run(chips);

        Assert.Equal(Signal.High, pe.Value);
        Assert.Equal(Signal.Low, po.Value);
    }

    [Fact]
    public void Unwired_output_does_not_block_the_other()
    {
        // The factory hands an open output a stand-in net that drives nothing.
        // The model must still compute and drive the output that IS wired.
        Net[] inputs = new Net[9];
        for (int i = 0; i < 9; i++)
            inputs[i] = new Net(i + 1);
        Net po = new(21);
        Net peStandIn = new(-1, "pe-nc");

        Hc280 parity = Build(inputs, peStandIn, po);

        var chips = new List<IChip> { parity };
        for (int i = 0; i < 9; i++)
            chips.Add(Drive(inputs[i], i == 0));   // exactly one HIGH -> odd

        Run(chips);

        Assert.Equal(Signal.High, po.Value);
    }

    [Fact]
    public void Cascade_of_two_packages_gives_seventeen_bit_parity()
    {
        // Word length extends by feeding one package's output into a spare
        // data input of the next. Wiring PO -> I8 chains the XOR directly:
        // PO(B) = parity(b) XOR parity(a) = odd parity of all 17 bits.
        //
        // (The datasheet cascades with PE instead. Since the function table
        // makes PE the exact complement of PO, a PE-fed chain inverts the
        // sense at each stage -- with one feeding stage the final PE carries
        // the total odd parity and PO carries the even. Same silicon, just a
        // label swap to keep track of; PO-feeding is used here because the
        // expected value is self-evident.)
        Net[] aIn = new Net[9];
        for (int i = 0; i < 9; i++) aIn[i] = new Net(i + 1);
        Net aPe = new(20), aPo = new(21);

        Net[] bIn = new Net[9];
        for (int i = 0; i < 8; i++) bIn[i] = new Net(i + 30);
        bIn[8] = aPo;                       // stage A's odd output into stage B's I8
        Net bPe = new(50), bPo = new(51);

        // 17 bits: nine on A, eight on B. Pattern chosen to have an odd total
        // (three HIGH), so the cascade's odd output must assert.
        bool[] aBits = { true, false, false, true, false, false, false, false, false };
        bool[] bBits = { false, true, false, false, false, false, false, false };

        var chips = new List<IChip>
        {
            Build(aIn, aPe, aPo),
            Build(bIn, bPe, bPo),
        };
        for (int i = 0; i < 9; i++) chips.Add(Drive(aIn[i], aBits[i]));
        for (int i = 0; i < 8; i++) chips.Add(Drive(bIn[i], bBits[i]));

        Run(chips);

        Assert.Equal(Signal.High, bPo.Value);
        Assert.Equal(Signal.Low, bPe.Value);
    }

    // Build the '280 as it sits in the circuit, drive the nine inputs from the
    // bit pattern, run to quiescence, and read both outputs.
    private static (Signal pe, Signal po) Evaluate(int pattern)
    {
        Net[] inputs = new Net[9];
        for (int i = 0; i < 9; i++)
            inputs[i] = new Net(i + 1);
        Net pe = new(20), po = new(21);

        var chips = new List<IChip> { Build(inputs, pe, po) };
        for (int i = 0; i < 9; i++)
            chips.Add(Drive(inputs[i], (pattern & (1 << i)) != 0));

        Run(chips);

        return (pe.Value, po.Value);
    }

    private static Hc280 Build(Net[] inputs, Net pe, Net po) =>
        new(
            i0: inputs[0], i1: inputs[1], i2: inputs[2],
            i3: inputs[3], i4: inputs[4], i5: inputs[5],
            i6: inputs[6], i7: inputs[7], i8: inputs[8],
            pe: pe, po: po);

    private static void Run(List<IChip> chips)
    {
        Simulator sim = new(
            NetTable.Build(System.Array.Empty<(PinRef, PinRef)>()),
            chips.ToArray());
        sim.Start();
        sim.RunUntilQuiescent();
    }

    private static IChip Drive(Net net, bool high) =>
        high ? new VccDriver(net) : new GndDriver(net);
}
