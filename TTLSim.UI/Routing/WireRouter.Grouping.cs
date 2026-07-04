using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using TTLSim.UI.Components;
using TTLSim.UI.Model;

namespace TTLSim.UI.Routing;

public sealed partial class WireRouter
{
    // Both methods below build the same union-find over shared pins, extended
    // by the net-label tie pairs: each tie participates exactly like a
    // zero-length pseudo-connection (indices connections.Count .. +ties.Count),
    // so two wire clusters joined only by same-named labels become one net id
    // / one routing group. Chained ties close transitively through unwired
    // middle labels because the pseudo-connections themselves union through
    // the shared label pins.

    private static Dictionary<Connection, int> BuildNetIdMap(
        IReadOnlyList<Connection> connections,
        IReadOnlyList<(Pin A, Pin B)> ties)
    {
        int total = connections.Count + ties.Count;
        var parent = new int[total];
        for (int i = 0; i < total; i++) parent[i] = i;
        int Find(int x)
        {
            while (parent[x] != x) { parent[x] = parent[parent[x]]; x = parent[x]; }
            return x;
        }
        void Union(int a, int b) { int ra = Find(a), rb = Find(b); if (ra != rb) parent[ra] = rb; }

        var pinToConn = new Dictionary<Pin, int>();
        void Touch(Pin pin, int index)
        {
            if (pinToConn.TryGetValue(pin, out int j)) Union(index, j);
            else pinToConn[pin] = index;
        }

        for (int i = 0; i < connections.Count; i++)
        {
            var c = connections[i];
            Touch(c.A, i);
            Touch(c.B, i);
        }
        for (int t = 0; t < ties.Count; t++)
        {
            Touch(ties[t].A, connections.Count + t);
            Touch(ties[t].B, connections.Count + t);
        }

        var map = new Dictionary<Connection, int>(connections.Count);
        for (int i = 0; i < connections.Count; i++)
            map[connections[i]] = Find(i);
        return map;
    }

    private static List<List<Connection>> GroupByPin(
        IReadOnlyList<Connection> connections,
        IReadOnlyList<(Pin A, Pin B)> ties)
    {
        int total = connections.Count + ties.Count;
        var parent = new int[total];
        for (int i = 0; i < total; i++) parent[i] = i;

        int Find(int x)
        {
            while (parent[x] != x) { parent[x] = parent[parent[x]]; x = parent[x]; }
            return x;
        }
        void Union(int a, int b) { int ra = Find(a), rb = Find(b); if (ra != rb) parent[ra] = rb; }

        var pinToConn = new Dictionary<Pin, int>();
        void Touch(Pin pin, int index)
        {
            if (pinToConn.TryGetValue(pin, out int j)) Union(index, j);
            else pinToConn[pin] = index;
        }

        for (int i = 0; i < connections.Count; i++)
        {
            var c = connections[i];
            Touch(c.A, i);
            Touch(c.B, i);
        }
        for (int t = 0; t < ties.Count; t++)
        {
            Touch(ties[t].A, connections.Count + t);
            Touch(ties[t].B, connections.Count + t);
        }

        // Groups contain only real connections; the tie pseudo-connections
        // exist solely to merge roots.
        var byRoot = new Dictionary<int, List<Connection>>();
        for (int i = 0; i < connections.Count; i++)
        {
            int root = Find(i);
            if (!byRoot.TryGetValue(root, out var list))
                byRoot[root] = list = new List<Connection>();
            list.Add(connections[i]);
        }
        return byRoot.Values.ToList();
    }
}