using TTLSim.Chips.Pld;
using TTLSim.Chips.Sources;
using TTLSim.Core;

public class GalTests
{
    // A synthetic 1-OLMC device: 2 inputs (lines 0,1 on pins 1,2), one product
    // term, output on pin 3, XOR polarity fuse at index 4. This exercises the
    // AND-OR-XOR evaluator without depending on the real GAL16V8 geometry.
    private static GalDevice Synth() => new(
        PartNumber: "SYNTH",
        FuseCount: 5,            // 4 array fuses (1 row x 4 cols) + 1 XOR fuse
        Rows: 1,
        Cols: 4,
        OlmcCount: 1,
        OlmcOutputPins: new[] { 3 },
        XorFuseBase: 4,
        SynFuse: 99,             // out of range -> false -> fallback to mode 1 map
        Ac0Fuse: 99,
        ColumnMapMode1: new[] { 1, 2 },
        ColumnMapMode2: new[] { 1, 2 },
        ColumnMapMode3: new[] { 1, 2 });

    private static void Run(IChip chip, params IChip[] drivers)
    {
        IChip[] all = new IChip[drivers.Length + 1];
        System.Array.Copy(drivers, all, drivers.Length);
        all[drivers.Length] = chip;
        Simulator sim = new(NetTable.Build(System.Array.Empty<(PinRef, PinRef)>()), all);
        sim.Start();
        sim.RunUntilQuiescent();
    }

    // Fuses for "out = A & B" (active-high): connect the TRUE literal of both
    // lines (intact = false), blow the complements (true), XOR fuse = 1 (high).
    private static bool[] AndHigh() => new[] { false, true, false, true, true };

    [Theory]
    [InlineData(Signal.Low, Signal.Low, Signal.Low)]
    [InlineData(Signal.Low, Signal.High, Signal.Low)]
    [InlineData(Signal.High, Signal.Low, Signal.Low)]
    [InlineData(Signal.High, Signal.High, Signal.High)]
    public void And_of_two_inputs(Signal a, Signal b, Signal expected)
    {
        Net n1 = new(1), n2 = new(2), n3 = new(3);
        var map = new Dictionary<int, Net> { [1] = n1, [2] = n2, [3] = n3 };
        Gal gal = new(Synth(), AndHigh(), map);

        IChip da = a == Signal.High ? new VccDriver(n1) : new GndDriver(n1);
        IChip db = b == Signal.High ? new VccDriver(n2) : new GndDriver(n2);
        Run(gal, da, db);

        Assert.Equal(expected, n3.Value);
    }

    [Fact]
    public void Xor_fuse_zero_inverts_to_nand()
    {
        // Same array as AND, but XOR fuse = 0 (active low) -> output = !(A&B).
        bool[] nand = new[] { false, true, false, true, false };
        Net n1 = new(1), n2 = new(2), n3 = new(3);
        var map = new Dictionary<int, Net> { [1] = n1, [2] = n2, [3] = n3 };
        Gal gal = new(Synth(), nand, map);

        Run(gal, new VccDriver(n1), new VccDriver(n2));   // A=B=1 -> NAND = 0
        Assert.Equal(Signal.Low, n3.Value);
    }

    [Fact]
    public void All_intact_block_releases_output()
    {
        // Erased/input OLMC: every array fuse intact (0) -> pin not driven.
        bool[] erased = new[] { false, false, false, false, false };
        Net n1 = new(1), n2 = new(2), n3 = new(3);
        var map = new Dictionary<int, Net> { [1] = n1, [2] = n2, [3] = n3 };
        Gal gal = new(Synth(), erased, map);

        Run(gal, new VccDriver(n1), new VccDriver(n2),
            new TTLSim.Chips.Passives.PullDriver(n3, Signal.Low));
        Assert.Equal(Signal.Low, n3.Value);
    }

    [Theory]
    [InlineData(0b0111, Signal.High)]   // OP0,OP1,OP2 = 1, OP3 = 0 -> branch
    [InlineData(0b1111, Signal.Low)]    // OP3 = 1 -> no
    [InlineData(0b0011, Signal.Low)]    // OP2 = 0 -> no
    [InlineData(0b0000, Signal.Low)]
    [InlineData(0b0110, Signal.Low)]
    public void Decodes_branch_opcode_on_real_gal16v8(int code, Signal expected)
    {
        // LD_PC = OP0 & OP1 & OP2 & !OP3, output pin 19, inputs pins 2/3/4/5.
        // Built straight from the galasm forward map (PinToFuse16Mode1):
        //   pin2->base col 0, pin3->4, pin4->8, pin5->12 (true=base, comp=base+1).
        // The evaluator uses the inverted ColumnMapMode1, so a wrong inversion
        // would mis-sample and fail this truth table.
        GalDevice dev = GalDevice.Gal16V8;
        bool[] f = new bool[dev.FuseCount];        // all intact (erased)

        for (int c = 0; c < dev.Cols; c++) f[c] = true;   // blow row 0 (pin19, rows 0..7)
        f[0] = false;     // OP0 true  (pin2)
        f[4] = false;     // OP1 true  (pin3)
        f[8] = false;     // OP2 true  (pin4)
        f[13] = false;    // OP3 comp  (pin5)

        f[dev.XorFuseBase + 0] = true;   // pin19 active high
        f[dev.SynFuse] = true;           // SYN=1, AC0=0 -> simple mode

        Net n2 = new(2), n3 = new(3), n4 = new(4), n5 = new(5), n19 = new(19);
        var map = new Dictionary<int, Net> { [2] = n2, [3] = n3, [4] = n4, [5] = n5, [19] = n19 };
        Gal gal = new(dev, f, map);

        IChip Bit(Net net, int bit) =>
            (code & bit) != 0 ? new VccDriver(net) : (IChip)new GndDriver(net);
        Run(gal, Bit(n2, 1), Bit(n3, 2), Bit(n4, 4), Bit(n5, 8));

        Assert.Equal(expected, n19.Value);
    }
}