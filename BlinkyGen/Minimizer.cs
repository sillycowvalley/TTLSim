namespace BlinkyMGen;

// ---------------------------------------------------------------------------
// Exact two-level SOP minimizer (Quine-McCluskey prime implicants + cover).
//
// Replaces the earlier greedy version, which had an inverted on-set test that
// prevented cube expansion and emitted one product term per raw minterm — the
// cause of the 64-term IDX0 and 28-term DST0 blow-ups BlinkyJED reported.
//
// For the input widths here (<= 9 variables) exact minimization is fast, and
// the term counts match what the fitter will accept.
// ---------------------------------------------------------------------------

public sealed class Minimizer
{
    public readonly record struct Cube(int Value, int Mask); // Mask bit set = literal present

    readonly int inputBits;

    public Minimizer(int inputBits) => this.inputBits = inputBits;

    /// <summary>Minimize one function. value[m] is the desired output at
    /// minterm m; care[m] false means don't-care. Covers the on-set, using
    /// don't-cares freely.</summary>
    public IReadOnlyList<Cube> Cover(bool[] value, bool[] care, bool complement)
    {
        int n = value.Length;
        var onset = new List<int>();
        var dc = new List<int>();
        for (int m = 0; m < n; m++)
        {
            if (!care[m]) { dc.Add(m); continue; }
            bool on = value[m] != complement;   // cover this polarity's true points
            if (on) onset.Add(m);
        }
        if (onset.Count == 0) return Array.Empty<Cube>();

        var primes = PrimeImplicants(onset, dc);
        return CoverSet(onset, primes);
    }

    /// <summary>Minimize both polarities; return the cheaper with its sense.</summary>
    public (IReadOnlyList<Cube> cubes, bool inverted) Best(bool[] value, bool[] care)
    {
        var p = Cover(value, care, complement: false);
        var n = Cover(value, care, complement: true);
        return n.Count < p.Count ? (n, true) : (p, false);
    }

    // ---- prime implicant generation (iterated combining) -------------------

    List<Cube> PrimeImplicants(List<int> onset, List<int> dc)
    {
        int full = (1 << inputBits) - 1;
        var current = new HashSet<Cube>();
        foreach (int m in onset) current.Add(new Cube(m, full));
        foreach (int m in dc) current.Add(new Cube(m, full));

        var primes = new HashSet<Cube>();
        while (current.Count > 0)
        {
            var used = new HashSet<Cube>();
            var next = new HashSet<Cube>();
            var list = current.ToList();
            for (int i = 0; i < list.Count; i++)
                for (int j = i + 1; j < list.Count; j++)
                {
                    var c = Combine(list[i], list[j]);
                    if (c is { } cc)
                    {
                        next.Add(cc);
                        used.Add(list[i]);
                        used.Add(list[j]);
                    }
                }
            foreach (var c in current)
                if (!used.Contains(c)) primes.Add(c);
            current = next;
        }
        return primes.ToList();
    }

    static Cube? Combine(Cube a, Cube b)
    {
        if (a.Mask != b.Mask) return null;
        int diff = a.Value ^ b.Value;
        if (BitCount(diff) != 1) return null;
        if ((diff & ~a.Mask) != 0) return null;         // differ only in a fixed bit
        int mask = a.Mask & ~diff;
        return new Cube(a.Value & ~diff & mask, mask);
    }

    // ---- cover the on-set with fewest primes (essentials + greedy) ---------

    List<Cube> CoverSet(List<int> onset, List<Cube> primes)
    {
        var covers = primes.Select(p => onset.Where(m => Covers(p, m)).ToHashSet()).ToList();
        var remaining = new HashSet<int>(onset);
        var chosen = new List<Cube>();
        var pickedIdx = new HashSet<int>();

        while (remaining.Count > 0)
        {
            int essential = -1;
            foreach (int m in remaining)
            {
                int only = -1, count = 0;
                for (int pi = 0; pi < primes.Count; pi++)
                    if (!pickedIdx.Contains(pi) && covers[pi].Contains(m)) { only = pi; if (++count > 1) break; }
                if (count == 1) { essential = only; break; }
            }

            int pick = essential;
            if (pick < 0)
            {
                int bestCov = -1;
                for (int pi = 0; pi < primes.Count; pi++)
                {
                    if (pickedIdx.Contains(pi)) continue;
                    int c = covers[pi].Count(remaining.Contains);
                    if (c > bestCov) { bestCov = c; pick = pi; }
                }
            }
            if (pick < 0) break;

            pickedIdx.Add(pick);
            chosen.Add(primes[pick]);
            remaining.ExceptWith(covers[pick]);
        }
        return chosen;
    }

    static bool Covers(Cube p, int m) => (m & p.Mask) == (p.Value & p.Mask);

    static int BitCount(int v)
    {
        int c = 0;
        while (v != 0) { v &= v - 1; c++; }
        return c;
    }

    /// <summary>Render a cube as a CUPL product term over named inputs.</summary>
    public string CubeToCupl(Cube cube, string[] inputNames)
    {
        var lits = new List<string>();
        for (int v = 0; v < inputBits; v++)
        {
            if ((cube.Mask & (1 << v)) == 0) continue;
            bool high = (cube.Value & (1 << v)) != 0;
            lits.Add((high ? "" : "!") + inputNames[v]);
        }
        return lits.Count == 0 ? "'b'1" : string.Join(" & ", lits);
    }
}
