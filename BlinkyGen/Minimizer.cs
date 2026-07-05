namespace BlinkyMGen;

// ---------------------------------------------------------------------------
// A small two-level SOP minimizer (greedy cube expansion, Espresso-lite).
// Given a boolean function over N inputs as care/value arrays, it returns a
// cover of product cubes. Two variable orders are tried and the smaller kept;
// the result is an upper bound on the exact minimum — good enough to emit CUPL
// equations and rank term counts. The real fitter (BlinkyJED/WinCUPL) meets or
// beats these.
// ---------------------------------------------------------------------------

public sealed class Minimizer
{
    public readonly record struct Cube(int Value, int Mask); // Mask bit set = literal present

    readonly int inputBits;
    readonly int space;

    public Minimizer(int inputBits)
    {
        this.inputBits = inputBits;
        space = 1 << inputBits;
    }

    /// <summary>Minimize one function. <paramref name="value"/>[m] is the
    /// desired output at minterm m; <paramref name="care"/>[m] false means
    /// don't-care. Returns the cheaper of the two variable orders.</summary>
    public IReadOnlyList<Cube> Cover(bool[] value, bool[] care, bool complement)
    {
        var a = CoverPass(value, care, complement, ascending: true);
        var b = CoverPass(value, care, complement, ascending: false);
        return b.Count < a.Count ? b : a;
    }

    /// <summary>Minimize both polarities, return the cheaper with its sense.</summary>
    public (IReadOnlyList<Cube> cubes, bool inverted) Best(bool[] value, bool[] care)
    {
        var p = Cover(value, care, complement: false);
        var n = Cover(value, care, complement: true);
        return n.Count < p.Count ? (n, true) : (p, false);
    }

    List<Cube> CoverPass(bool[] value, bool[] care, bool complement, bool ascending)
    {
        var covered = new bool[space];
        var cubes = new List<Cube>();

        for (int m = 0; m < space; m++)
        {
            if (!care[m] || covered[m]) continue;
            if ((value[m] ? !complement : complement)) continue;  // on-set point?
            // start with the singleton minterm, expand by freeing variables
            int mask = space - 1, val = m;
            bool grew = true;
            while (grew)
            {
                grew = false;
                for (int i = 0; i < inputBits; i++)
                {
                    int v = ascending ? i : inputBits - 1 - i;
                    int bit = 1 << v;
                    if ((mask & bit) == 0) continue;
                    int nm = mask & ~bit, nv = val & ~bit;
                    if (CubeInside(value, care, complement, nv, nm))
                    {
                        mask = nm; val = nv; grew = true;
                    }
                }
            }
            cubes.Add(new Cube(val, mask));
            MarkCovered(covered, val, mask);
        }
        return cubes;
    }

    bool CubeInside(bool[] value, bool[] care, bool complement, int val, int mask)
    {
        int free = ~mask & (space - 1);
        int s = free;
        while (true)
        {
            int p = val | s;
            bool onSet = value[p] ? !complement : complement;
            if (care[p] && !onSet) return false;    // a care-point violates
            if (s == 0) return true;
            s = (s - 1) & free;
        }
    }

    void MarkCovered(bool[] covered, int val, int mask)
    {
        int free = ~mask & (space - 1);
        int s = free;
        while (true)
        {
            covered[val | s] = true;
            if (s == 0) return;
            s = (s - 1) & free;
        }
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
