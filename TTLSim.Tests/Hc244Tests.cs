using TTLSim.Chips.Buffers;
using TTLSim.Chips.Passives;
using TTLSim.Chips.Sources;
using TTLSim.Core;

public class Hc244Tests
{
    // Nets indexed by physical pin number (1..19, 10 = GND unused). Mapping
    // matches ChipFactory.TryCreateHc244.
    private static (Net[] n, IChip chip) Build()
    {
        Net[] n = new Net[20];
        for (int i = 1; i <= 19; i++)
            if (i != 10) n[i] = new Net(i);

        Hc244 chip = new(
            oe1N: n[1],
            a1: n[2], a2: n[4], a3: n[6], a4: n[8],
            y1: n[18], y2: n[16], y3: n[14], y4: n[12],
            oe2N: n[19],
            a5: n[11], a6: n[13], a7: n[15], a8: n[17],
            y5: n[9], y6: n[7], y7: n[5], y8: n[3]);

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
    public void Bank1_passes_input_when_enabled(Signal a)
    {
        var (n, chip) = Build();
        IChip oe = new GndDriver(n[1]);   // /1OE = Low -> enabled
        IChip da = a == Signal.High ? new VccDriver(n[2]) : new GndDriver(n[2]);
        Run(chip, oe, da);
        Assert.Equal(a, n[18].Value);     // 1Y1 follows 1A1
    }

    // A disabled output must let go of the bus. A weak pull-down on 1Y1 then
    // wins; if the buffer were still driving, 1Y1 would read High from 1A1.
    [Fact]
    public void Bank1_releases_bus_when_disabled()
    {
        var (n, chip) = Build();
        IChip oe = new VccDriver(n[1]);                  // /1OE = High -> disabled
        IChip da = new VccDriver(n[2]);                  // 1A1 high regardless
        IChip pull = new PullDriver(n[18], Signal.Low);  // weak pull-down on 1Y1
        Run(chip, oe, da, pull);
        Assert.Equal(Signal.Low, n[18].Value);           // pull wins -> buffer released
    }

    [Fact]
    public void Banks_are_independent()
    {
        var (n, chip) = Build();
        IChip oe1 = new GndDriver(n[1]);                 // bank 1 enabled
        IChip oe2 = new VccDriver(n[19]);                // bank 2 disabled
        IChip a1 = new VccDriver(n[2]);                  // 1A1 high
        IChip a5 = new VccDriver(n[11]);                 // 2A1 high
        IChip pull = new PullDriver(n[9], Signal.Low);   // weak pull-down on 2Y1
        Run(chip, oe1, oe2, a1, a5, pull);
        Assert.Equal(Signal.High, n[18].Value);          // 1Y1 follows (enabled)
        Assert.Equal(Signal.Low, n[9].Value);            // 2Y1 released despite 2A1 high
    }
}