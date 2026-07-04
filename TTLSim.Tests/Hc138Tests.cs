using TTLSim.Chips.Decoders;
using TTLSim.Chips.Sources;
using TTLSim.Core;

public class Hc138Tests
{
    // Exhaustive: 8 select values x all 8 enable combinations = 64 cases.
    public static IEnumerable<object[]> AllInputs()
    {
        for (int v = 0; v < 64; v++)
            yield return new object[]
            {
                v & 7,           // sel (A2:A0)
                (v & 8) != 0,    // /E1 high
                (v & 16) != 0,   // /E2 high
                (v & 32) != 0,   // E3 high
            };
    }

    [Theory]
    [MemberData(nameof(AllInputs))]
    public void Decodes_exactly_one_output_when_enabled(
        int sel, bool e1High, bool e2High, bool e3High)
    {
        Signal[] outputs = Evaluate(sel, e1High, e2High, e3High);

        // Decoding requires /E1 LOW, /E2 LOW, E3 HIGH.
        bool enabled = !e1High && !e2High && e3High;

        for (int i = 0; i < 8; i++)
        {
            Signal expected = enabled && i == sel ? Signal.Low : Signal.High;
            Assert.Equal(expected, outputs[i]);
        }
    }

    // Build the '138 as it sits in the circuit, drive the six inputs, run to
    // quiescence, and read the eight outputs (index i = /Yi).
    private static Signal[] Evaluate(int sel, bool e1High, bool e2High, bool e3High)
    {
        Net a0 = new(1), a1 = new(2), a2 = new(3);
        Net e1N = new(4), e2N = new(5), e3 = new(6);
        Net[] y = new Net[8];
        for (int i = 0; i < 8; i++)
            y[i] = new Net(10 + i);

        Hc138 decoder = new(
            a0, a1, a2, e1N, e2N, e3,
            y7N: y[7], y6N: y[6], y5N: y[5], y4N: y[4],
            y3N: y[3], y2N: y[2], y1N: y[1], y0N: y[0]);

        var chips = new List<IChip>
        {
            Drive(a0, (sel & 1) != 0),
            Drive(a1, (sel & 2) != 0),
            Drive(a2, (sel & 4) != 0),
            Drive(e1N, e1High),
            Drive(e2N, e2High),
            Drive(e3, e3High),
            decoder,
        };

        Simulator sim = new(
            NetTable.Build(System.Array.Empty<(PinRef, PinRef)>()),
            chips.ToArray());
        sim.Start();
        sim.RunUntilQuiescent();

        Signal[] outputs = new Signal[8];
        for (int i = 0; i < 8; i++)
            outputs[i] = y[i].Value;
        return outputs;
    }

    private static IChip Drive(Net net, bool high) =>
        high ? new VccDriver(net) : new GndDriver(net);
}