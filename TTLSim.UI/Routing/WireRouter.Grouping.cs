using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using TTLSim.UI.Components;
using TTLSim.UI.Model;

namespace TTLSim.UI.Routing;

public sealed partial class WireRouter
{
    private static Dictionary<Connection, int> BuildNetIdMap(
        IReadOnlyList<Connection> connections)
    {
        var parent = new int[connections.Count];
        for (int i = 0; i < connections.Count; i++) parent[i] = i;
        int Find(int x)
        {
            while (parent[x] != x) { parent[x] = parent[parent[x]]; x = parent[x]; }
            return x;
        }
        void Union(int a, int b) { int ra = Find(a), rb = Find(b); if (ra != rb) parent[ra] = rb; }

        var pinToConn = new Dictionary<Pin, int>();
        for (int i = 0; i < connections.Count; i++)
        {
            var c = connections[i];
            if (pinToConn.TryGetValue(c.A, out int j1)) Union(i, j1);
            else pinToConn[c.A] = i;
            if (pinToConn.TryGetValue(c.B, out int j2)) Union(i, j2);
            else pinToConn[c.B] = i;
        }

        var map = new Dictionary<Connection, int>(connections.Count);
        for (int i = 0; i < connections.Count; i++)
            map[connections[i]] = Find(i);
        return map;
    }

    private static List<List<Connection>> GroupByPin(IReadOnlyList<Connection> connections)
    {
        var parent = new int[connections.Count];
        for (int i = 0; i < connections.Count; i++) parent[i] = i;

        int Find(int x)
        {
            while (parent[x] != x) { parent[x] = parent[parent[x]]; x = parent[x]; }
            return x;
        }
        void Union(int a, int b) { int ra = Find(a), rb = Find(b); if (ra != rb) parent[ra] = rb; }

        var pinToConn = new Dictionary<Pin, int>();
        for (int i = 0; i < connections.Count; i++)
        {
            var c = connections[i];
            if (pinToConn.TryGetValue(c.A, out int j1)) Union(i, j1);
            else pinToConn[c.A] = i;
            if (pinToConn.TryGetValue(c.B, out int j2)) Union(i, j2);
            else pinToConn[c.B] = i;
        }

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
