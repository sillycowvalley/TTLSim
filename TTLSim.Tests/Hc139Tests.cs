using TTLSim.Chips.Decoders;
using TTLSim.Chips.Sources;
using TTLSim.Core;
using Xunit;

namespace TTLSim.Tests;

/// <summary>
/// Exhaustive truth-table tests for the 74HC139 dual 2-to-4 decoder model.
/// The chip is purely combinational, so the rig has no clock: every input is
/// wired to a VccDriver or GndDriver, the sim runs to quiescence, and the
/// output nets are read back. Both halves are driven independently across
/// the full input space (8 x 8 enable/select combinations).
/// </summary>
public class Hc139Tests
{
    // Build a '139 with every input driven from a static source. Returns the
    // output nets for each half, index i = /xYi.
    private static (Net[] aY, Net[] bY, Simulator sim) Build(
        bool aEnableN, int aSel, bool bEnableN, int bSel)
    {
        int id = 0;
        Net N() => new(id++);

        Net aeN = N(), aa0 = N(), aa1 = N();
        Net ay0 = N(), ay1 = N(), ay2 = N(), ay3 = N();
        Net by3 = N(), by2 = N(), by1 = N(), by0 = N();
        Net ba1 = N(), ba0 = N(), beN = N();

        List<IChip> chips = new()
        {
            Drive(aeN, aEnableN),
            Drive(aa0, (aSel & 1) != 0),
            Drive(aa1, (aSel & 2) != 0),
            Drive(beN, bEnableN),
            Drive(ba0, (bSel & 1) != 0),
            Drive(ba1, (bSel & 2) != 0),
            new Hc139(
                aeN: aeN, aa0: aa0, aa1: aa1,
                ay0N: ay0, ay1N: ay1, ay2N: ay2, ay3N: ay3,
                by3N: by3, by2N: by2, by1N: by1, by0N: by0,
                ba1: ba1, ba0: ba0, beN: beN)
        };

        Simulator sim = new(
            NetTable.Build(Array.Empty<(PinRef, PinRef)>()),
            chips);
        sim.Start();
        sim.RunUntilQuiescent();

        return (new[] { ay0, ay1, ay2, ay3 }, new[] { by0, by1, by2, by3 }, sim);
    }

    private static IChip Drive(Net net, bool high) =>
        high ? new VccDriver(net) : new GndDriver(net);

    [Fact]
    public void Both_halves_decode_exhaustively()
    {
        // 8 states per half (enable x 4 selects), crossed: 64 rigs. Proves
        // the halves are independent as well as individually correct.
        for (int a = 0; a < 8; a++)
        {
            for (int b = 0; b < 8; b++)
            {
                bool aEnN = (a & 4) != 0;   // /AE HIGH = half A disabled
                int aSel = a & 3;
                bool bEnN = (b & 4) != 0;   // /BE HIGH = half B disabled
                int bSel = b & 3;

                var (aY, bY, _) = Build(aEnN, aSel, bEnN, bSel);

                for (int i = 0; i < 4; i++)
                {
                    Signal expA = !aEnN && i == aSel ? Signal.Low : Signal.High;
                    Signal expB = !bEnN && i == bSel ? Signal.Low : Signal.High;
                    Assert.Equal(expA, aY[i].Value);
                    Assert.Equal(expB, bY[i].Value);
                }
            }
        }
    }

    [Fact]
    public void Disabled_half_holds_all_outputs_high()
    {
        var (aY, bY, _) = Build(aEnableN: true, aSel: 2, bEnableN: false, bSel: 0);

        foreach (Net y in aY)
            Assert.Equal(Signal.High, y.Value);

        // The other half still decodes: /BY0 low, the rest high.
        Assert.Equal(Signal.Low, bY[0].Value);
        Assert.Equal(Signal.High, bY[1].Value);
        Assert.Equal(Signal.High, bY[2].Value);
        Assert.Equal(Signal.High, bY[3].Value);
    }
}