using System.Collections.Generic;
using TTLSim.UI.Components;

namespace TTLSim.UI.Model;

/// <summary>
/// Computes the default <see cref="TTLColor"/> for a NEWLY PLACED wire, so
/// interactive wiring picks up the schematic's colour conventions without a
/// property-grid visit. Called once at wire-creation time (the canvas's
/// second wire click), BEFORE the AddConnectionCommand executes -- the colour
/// therefore rides inside the add and needs no undo command of its own.
///
/// <para><b>Rules.</b> Candidate colours are collected from BOTH endpoints:</para>
/// <list type="bullet">
///   <item>A pin on a <see cref="VccSymbol"/> contributes Red; on a
///   <see cref="GndSymbol"/>, Navy; on a <see cref="ClockSource"/>, Orange.
///   A <see cref="CanOscillator"/> (either DIP variant -- the DIP-8 derives
///   from it) contributes Orange for its OUTPUT pin only; its +5V/GND/NC
///   pins contribute nothing, so wiring the can's supply pins still takes
///   the rail colour from the rail symbol at the other end.</item>
///   <item>A pin on a <see cref="NetLabelItem"/> whose Color is not Black
///   contributes that label's colour (the bus colour).</item>
///   <item>The existing net each endpoint already belongs to is walked
///   (transitive pin closure over ACTIVE connections, plus the schematic's
///   net-label ties, matching every other net-identity consumer). Every
///   non-Black wire colour found in the net is a candidate, and the
///   source/label rules above apply to every pin reached -- so joining a
///   net that touches VCC yields Red even if that net's existing wires
///   were drawn Black before this feature existed.</item>
/// </list>
///
/// <para><b>Decision.</b> Exactly one distinct candidate: use it. Zero, or
/// two-plus that disagree (a conflict -- e.g. VCC wired into a Green net):
/// Black, the wire default. The result is a CREATION-TIME default only;
/// recolouring wires or labels later never re-propagates to existing wires,
/// and the property grid can override the default freely.</para>
/// </summary>
public static class WireColorDefault
{
    /// <summary>
    /// Default colour for a wire about to be created between
    /// <paramref name="a"/> and <paramref name="b"/>. Call BEFORE the
    /// connection is added to <paramref name="schematic"/> -- the walk covers
    /// the two nets as they exist without the new wire.
    /// </summary>
    public static TTLColor For(Schematic schematic, Pin a, Pin b)
    {
        var candidates = new HashSet<TTLColor>();

        // One adjacency build shared by both endpoint walks. Nodes are Pin
        // objects (reference identity -- connections that meet share the
        // same Pin instance); edges are active connections plus the
        // schematic's net-label tie pairs, exactly the closure the router,
        // simulator, and coincident-corner check use.
        var adjacency = BuildAdjacency(schematic);

        var visited = new HashSet<Pin>();
        Walk(a, adjacency, visited, candidates);
        Walk(b, adjacency, visited, candidates);

        return candidates.Count == 1 ? Single(candidates) : TTLColor.Black;
    }

    /// <summary>The one element of a single-element set, without LINQ.</summary>
    private static TTLColor Single(HashSet<TTLColor> set)
    {
        foreach (var c in set) return c;
        return TTLColor.Black;   // unreachable; keeps the compiler satisfied
    }

    private static Dictionary<Pin, List<(Pin Other, Connection? Wire)>> BuildAdjacency(
        Schematic schematic)
    {
        var adjacency = new Dictionary<Pin, List<(Pin Other, Connection? Wire)>>();

        void AddEdge(Pin p, Pin q, Connection? wire)
        {
            if (!adjacency.TryGetValue(p, out var listP))
                adjacency[p] = listP = new List<(Pin, Connection?)>();
            listP.Add((q, wire));

            if (!adjacency.TryGetValue(q, out var listQ))
                adjacency[q] = listQ = new List<(Pin, Connection?)>();
            listQ.Add((p, wire));
        }

        foreach (var c in schematic.ActiveConnections)
            AddEdge(c.A, c.B, c);

        // Net-label ties: same-(name, bit) label pins are one net with no
        // drawn wire. They carry no colour themselves (Wire is null); the
        // label's own colour is contributed by CollectPin when the walk
        // reaches the label pin.
        foreach (var (tieA, tieB) in schematic.NetLabelTiePairs())
            AddEdge(tieA, tieB, null);

        return adjacency;
    }

    /// <summary>
    /// Breadth-first walk of the net containing <paramref name="start"/>,
    /// collecting candidate colours from every pin reached and every
    /// non-Black wire traversed. A shared <paramref name="visited"/> set
    /// across both endpoint walks makes a wire whose endpoints already share
    /// a net walk that net once.
    /// </summary>
    private static void Walk(
        Pin start,
        Dictionary<Pin, List<(Pin Other, Connection? Wire)>> adjacency,
        HashSet<Pin> visited,
        HashSet<TTLColor> candidates)
    {
        if (!visited.Add(start)) return;

        var queue = new Queue<Pin>();
        queue.Enqueue(start);

        while (queue.Count > 0)
        {
            var pin = queue.Dequeue();
            CollectPin(pin, candidates);

            if (!adjacency.TryGetValue(pin, out var edges)) continue;
            foreach (var (other, wire) in edges)
            {
                if (wire is not null && wire.Color != TTLColor.Black)
                    candidates.Add(wire.Color);
                if (visited.Add(other))
                    queue.Enqueue(other);
            }
        }
    }

    /// <summary>
    /// Colour contribution of a single pin, by its owner's type. Order
    /// matters only for the oscillator: CanOscillatorDip8 derives from
    /// CanOscillator, so the single CanOscillator case covers both packages.
    /// </summary>
    private static void CollectPin(Pin pin, HashSet<TTLColor> candidates)
    {
        switch (pin.Owner)
        {
            case VccSymbol:
                candidates.Add(TTLColor.Red);
                break;

            case GndSymbol:
                candidates.Add(TTLColor.Navy);
                break;

            case ClockSource:
                candidates.Add(TTLColor.Orange);
                break;

            case CanOscillator:
                // Only the driven output counts as a clock source; the can's
                // +5V/GND/NC pins are ordinary package pins. "OUT" is the
                // name both CanGeometry variants give the output pin.
                if (pin.Name == "OUT")
                    candidates.Add(TTLColor.Orange);
                break;

            case NetLabelItem label:
                if (label.Color != TTLColor.Black)
                    candidates.Add(label.Color);
                break;
        }
    }
}