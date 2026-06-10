using TTLSim.Chips.Buffers;
using TTLSim.Chips.Passives;
using TTLSim.Chips.Sources;
using TTLSim.Core;

public class Hc245Tests
{
    // Nets by physical pin number; mapping matches ChipFactory.TryCreateHc245.
    private static (Net[] n, IChip chip) Build()
    {
        Net[] n = new Net[20];
        for (int i = 1; i <= 19; i++)
            if (i != 10) n[i] = new Net(i);

        Hc245 chip = new(
            dir: n[1],
            a1: n[2], a2: n[3], a3: n[4], a4: n[5],
            a5: n[6], a6: n[7], a7: n[8], a8: n[9],
            b1: n[18], b2: n[17], b3: n[16], b4: n[15],
            b5: n[14], b6: n[13], b7: n[12], b8: n[11],
            oeN: n[19]);

        return (n, chip);
    }

    private static void Run(IChip chip, params IChip[] drivers)
    {
        IChip[] all = new IChip[drivers.Length + 1];
        System.Array.Copy(drivers, all, drivers.Length);
        all[drivers.Length] = chip;

        Simulator sim = new(
            NetTable.Build(System.Array.Empty<(PinRef, PinRef)>()), all);
        sim.Start();
        sim.RunUntilQuiescent();
    }

    [Fact]
    public void A_drives_B_when_dir_high()
    {
        var (n, chip) = Build();
        IChip dir = new VccDriver(n[1]);   // DIR = High -> A to B
        IChip oe = new GndDriver(n[19]);   // /OE = Low -> enabled
        IChip a1 = new VccDriver(n[2]);    // A1 = High
        Run(chip, dir, oe, a1);
        Assert.Equal(Signal.High, n[18].Value);   // B1 follows A1
    }

    [Fact]
    public void B_drives_A_when_dir_low()
    {
        var (n, chip) = Build();
        IChip dir = new GndDriver(n[1]);   // DIR = Low -> B to A
        IChip oe = new GndDriver(n[19]);   // /OE = Low -> enabled
        IChip b1 = new VccDriver(n[18]);   // B1 = High
        Run(chip, dir, oe, b1);
        Assert.Equal(Signal.High, n[2].Value);    // A1 follows B1
    }

    // /OE high isolates both sides. A weak pull-down on B1 wins; were the
    // transceiver still driving A->B, B1 would read High from A1.
    [Fact]
    public void Both_sides_release_bus_when_disabled()
    {
        var (n, chip) = Build();
        IChip dir = new VccDriver(n[1]);                 // would be A -> B...
        IChip oe = new VccDriver(n[19]);                 // ...but /OE high -> isolated
        IChip a1 = new VccDriver(n[2]);                  // A1 high regardless
        IChip pullB = new PullDriver(n[18], Signal.Low); // weak pull-down on B1
        Run(chip, dir, oe, a1, pullB);
        Assert.Equal(Signal.Low, n[18].Value);           // pull wins -> B side released
    }
}