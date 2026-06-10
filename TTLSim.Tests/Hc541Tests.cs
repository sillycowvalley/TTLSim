using TTLSim.Chips.Buffers;
using TTLSim.Chips.Passives;
using TTLSim.Chips.Sources;
using TTLSim.Core;

public class Hc541Tests
{
    // Nets by physical pin number; mapping matches ChipFactory.TryCreateHc541.
    private static (Net[] n, IChip chip) Build()
    {
        Net[] n = new Net[20];
        for (int i = 1; i <= 19; i++)
            if (i != 10) n[i] = new Net(i);

        Hc541 chip = new(
            oe1N: n[1],
            a1: n[2], a2: n[3], a3: n[4], a4: n[5],
            a5: n[6], a6: n[7], a7: n[8], a8: n[9],
            y1: n[18], y2: n[17], y3: n[16], y4: n[15],
            y5: n[14], y6: n[13], y7: n[12], y8: n[11],
            oe2N: n[19]);

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

    [Theory]
    [InlineData(Signal.High)]
    [InlineData(Signal.Low)]
    public void Passes_input_when_both_enables_low(Signal a)
    {
        var (n, chip) = Build();
        IChip oe1 = new GndDriver(n[1]);    // /OE1 = Low
        IChip oe2 = new GndDriver(n[19]);   // /OE2 = Low
        IChip da = a == Signal.High ? new VccDriver(n[2]) : new GndDriver(n[2]);
        Run(chip, oe1, oe2, da);
        Assert.Equal(a, n[18].Value);       // Y1 follows A1
    }

    // Unless BOTH enables are low the outputs release. A weak pull-down on Y1
    // wins; were the buffer still driving, Y1 would read High from A1.
    [Theory]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public void Releases_bus_unless_both_enables_low(bool oe1High, bool oe2High)
    {
        var (n, chip) = Build();
        IChip oe1 = oe1High ? new VccDriver(n[1]) : new GndDriver(n[1]);
        IChip oe2 = oe2High ? new VccDriver(n[19]) : new GndDriver(n[19]);
        IChip da = new VccDriver(n[2]);                  // A1 high regardless
        IChip pull = new PullDriver(n[18], Signal.Low);  // weak pull-down on Y1
        Run(chip, oe1, oe2, da, pull);
        Assert.Equal(Signal.Low, n[18].Value);           // pull wins -> buffer released
    }
}