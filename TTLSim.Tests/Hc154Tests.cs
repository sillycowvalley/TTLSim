using TTLSim.Chips.Decoders;
using TTLSim.Chips.Sources;
using TTLSim.Core;

public class Hc154Tests
{
    // Exhaustive: 16 select values x all 4 enable combinations = 64 cases.
    public static IEnumerable<object[]> AllInputs()
    {
        for (int v = 0; v < 64; v++)
            yield return new object[]
            {
                v & 15,          // sel (A3:A0)
                (v & 16) != 0,   // /E0 high
                (v & 32) != 0,   // /E1 high
            };
    }

    [Theory]
    [MemberData(nameof(AllInputs))]
    public void Decodes_exactly_one_output_when_enabled(
        int sel, bool e0High, bool e1High)
    {
        Signal[] outputs = Evaluate(sel, e0High, e1High);

        // Decoding requires both /E0 and /E1 LOW.
        bool enabled = !e0High && !e1High;

        for (int i = 0; i < 16; i++)
        {
            Signal expected = enabled && i == sel ? Signal.Low : Signal.High;
            Assert.Equal(expected, outputs[i]);
        }
    }

    // Build the '154 as it sits in the circuit, drive the six inputs, run to
    // quiescence, and read the sixteen outputs (index i = /Yi).
    private static Signal[] Evaluate(int sel, bool e0High, bool e1High)
    {
        Net a0 = new(1), a1 = new(2), a2 = new(3), a3 = new(4);
        Net e0N = new(5), e1N = new(6);
        Net[] y = new Net[16];
        for (int i = 0; i < 16; i++)
            y[i] = new Net(10 + i);

        Hc154 decoder = new(
            y0N: y[0], y1N: y[1], y2N: y[2], y3N: y[3],
            y4N: y[4], y5N: y[5], y6N: y[6], y7N: y[7],
            y8N: y[8], y9N: y[9], y10N: y[10], y11N: y[11],
            y12N: y[12], y13N: y[13], y14N: y[14], y15N: y[15],
            e0N: e0N, e1N: e1N,
            a3: a3, a2: a2, a1: a1, a0: a0);

        var chips = new List<IChip>
        {
            Drive(a0, (sel & 1) != 0),
            Drive(a1, (sel & 2) != 0),
            Drive(a2, (sel & 4) != 0),
            Drive(a3, (sel & 8) != 0),
            Drive(e0N, e0High),
            Drive(e1N, e1High),
            decoder,
        };

        Simulator sim = new(
            NetTable.Build(System.Array.Empty<(PinRef, PinRef)>()),
            chips.ToArray());
        sim.Start();
        sim.RunUntilQuiescent();

        Signal[] outputs = new Signal[16];
        for (int i = 0; i < 16; i++)
            outputs[i] = y[i].Value;
        return outputs;
    }

    private static IChip Drive(Net net, bool high) =>
        high ? new VccDriver(net) : new GndDriver(net);
}