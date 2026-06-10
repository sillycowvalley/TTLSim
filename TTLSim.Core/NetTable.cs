namespace TTLSim.Core;

/// <summary>
/// Builds Nets from a flat list of pin-to-pin connections using union-find.
/// </summary>
public sealed class NetTable
{
    private readonly Dictionary<PinRef, Net> netByPin = new();
    private readonly List<Net> nets = new();

    public IReadOnlyList<Net> Nets => nets;

    /// <summary>Returns the Net containing this pin, or null if the pin is on no net.</summary>
    public Net? FindNet(PinRef pin) =>
        netByPin.TryGetValue(pin, out Net? n) ? n : null;

    /// <summary>
    /// Build nets from a sequence of (a, b) pin pairs. Each pair declares those two pins
    /// belong to the same net; transitive closure produces the final net set.
    /// </summary>
    public static NetTable Build(IEnumerable<(PinRef A, PinRef B)> connections)
    {
        // Union-find over pins.
        Dictionary<PinRef, PinRef> parent = new();

        PinRef Find(PinRef p)
        {
            if (!parent.TryGetValue(p, out PinRef pp))
            {
                parent[p] = p;
                return p;
            }
            if (pp.Equals(p))
                return p;

            // Path compression.
            PinRef root = Find(pp);
            parent[p] = root;
            return root;
        }

        void Union(PinRef a, PinRef b)
        {
            PinRef ra = Find(a);
            PinRef rb = Find(b);
            if (!ra.Equals(rb))
                parent[ra] = rb;
        }

        foreach ((PinRef a, PinRef b) in connections)
        {
            Find(a);
            Find(b);
            Union(a, b);
        }

        // Group pins by root, create a Net per group.
        NetTable table = new();
        Dictionary<PinRef, Net> rootToNet = new();
        int nextId = 1;

        foreach (PinRef pin in parent.Keys)
        {
            PinRef root = Find(pin);

            if (!rootToNet.TryGetValue(root, out Net? net))
            {
                net = new Net(nextId++);
                rootToNet[root] = net;
                table.nets.Add(net);
            }

            table.netByPin[pin] = net;
            net.AddPin(pin);
        }

        return table;
    }
}