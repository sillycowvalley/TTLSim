using System;
using System.Collections.Generic;
using System.Linq;
using TTLSim.UI.Components;

namespace TTLSim.UI.Model;

/// <summary>
/// One net-label NAME and every <see cref="NetLabelItem"/> in the schematic that
/// carries it. The electrical tie key is (name, absolute bit) -- see
/// <see cref="Schematic.NetLabelTiePairs"/> -- so a name is only a name: a
/// width-8 port "D" and eight width-1 "D" taps all belong to the one group here,
/// and it is the per-BIT pin counts that say what actually ties to what.
///
/// <para>The scan is schematic-wide and deliberately IGNORES layer visibility,
/// matching <see cref="NetLabelItem.HigherBitsProbe"/>: naming intent does not
/// change when a layer is hidden. (The electrical ties themselves stay
/// activity-filtered in NetLabelTiePairs; this is a naming index, not a net
/// table.)</para>
/// </summary>
public sealed class NetLabelGroup
{
    public NetLabelGroup(string name, List<NetLabelItem> labels, Dictionary<int, int> pinsPerBit)
    {
        Name = name;
        Labels = labels;
        PinsPerBit = pinsPerBit;
    }

    /// <summary>The label text. Empty for unnamed labels (which tie nothing).</summary>
    public string Name { get; }

    /// <summary>True when the name is blank -- these tie nothing and draw as "?".</summary>
    public bool IsUnnamed => string.IsNullOrWhiteSpace(Name);

    /// <summary>Every label item carrying this name, in schematic order.</summary>
    public IReadOnlyList<NetLabelItem> Labels { get; }

    /// <summary>Absolute bit -&gt; how many label PINS in the schematic carry it.
    /// A bit with a count of 1 is tapped in only one place and therefore ties
    /// to nothing.</summary>
    public IReadOnlyDictionary<int, int> PinsPerBit { get; }

    /// <summary>Number of label items carrying this name -- the "occurs N times"
    /// figure shown in the status bar.</summary>
    public int Count => Labels.Count;

    public int MinBit => PinsPerBit.Count == 0 ? 0 : PinsPerBit.Keys.Min();
    public int MaxBit => PinsPerBit.Count == 0 ? 0 : PinsPerBit.Keys.Max();

    /// <summary>True when any label carrying this name is wider than one bit.</summary>
    public bool IsBus => Labels.Any(l => l.Width > 1);

    /// <summary>Bits carried by exactly one pin anywhere in the schematic: a
    /// label that names a net nothing else names. Almost always a typo or an
    /// unfinished connection.</summary>
    public IEnumerable<int> LoneBits =>
        PinsPerBit.Where(kv => kv.Value < 2).Select(kv => kv.Key).OrderBy(b => b);

    /// <summary>True when NO bit of this name is tapped twice -- the name ties
    /// nothing at all. Always true for an unnamed label.</summary>
    public bool TiesNothing => IsUnnamed || PinsPerBit.All(kv => kv.Value < 2);

    /// <summary>True when at least one bit -- but not all -- is a lone tap.</summary>
    public bool HasLoneBits => !TiesNothing && PinsPerBit.Any(kv => kv.Value < 2);

    /// <summary>"D[0..7]", "CLK", or "(unnamed)". The bit range is shown only
    /// when the name is used as a bus (any label wider than one bit) or when
    /// its taps span more than bit 0.</summary>
    public string DisplayName
    {
        get
        {
            if (IsUnnamed) return "(unnamed)";
            if (PinsPerBit.Count == 0) return Name;
            int lo = MinBit, hi = MaxBit;
            if (!IsBus && lo == 0 && hi == 0) return Name;
            return lo == hi ? $"{Name}{lo}" : $"{Name}[{lo}..{hi}]";
        }
    }
}

/// <summary>
/// Builds the by-name view of a schematic's net labels. Shared by the Net Labels
/// panel (which lists it) and the status bar (which reports the count for the
/// selected label), so both agree on what "occurs N times" means.
/// </summary>
public static class NetLabelIndex
{
    /// <summary>
    /// Every distinct net-label name in the schematic, ordered with the unnamed
    /// group (if any) first -- it is always a defect -- and then by name,
    /// ordinal (the same comparison the tie keys use).
    /// </summary>
    public static List<NetLabelGroup> Build(Schematic schematic)
    {
        var byName = new Dictionary<string, List<NetLabelItem>>(StringComparer.Ordinal);
        var pinsPerBit = new Dictionary<string, Dictionary<int, int>>(StringComparer.Ordinal);

        foreach (var item in schematic.Items)
        {
            if (item is not NetLabelItem label) continue;

            string name = string.IsNullOrWhiteSpace(label.Label) ? "" : label.Label;

            if (!byName.TryGetValue(name, out var list))
                byName[name] = list = new List<NetLabelItem>();
            list.Add(label);

            if (!pinsPerBit.TryGetValue(name, out var bits))
                pinsPerBit[name] = bits = new Dictionary<int, int>();

            foreach (var pin in label.Pins)
            {
                int bit = label.BitOfPin(pin.Number);
                bits[bit] = bits.TryGetValue(bit, out int n) ? n + 1 : 1;
            }
        }

        return byName
            .Select(kv => new NetLabelGroup(kv.Key, kv.Value, pinsPerBit[kv.Key]))
            .OrderBy(g => g.IsUnnamed ? 0 : 1)
            .ThenBy(g => g.Name, StringComparer.Ordinal)
            .ToList();
    }

    /// <summary>
    /// How many net labels in the schematic carry the given name (including the
    /// one asking). Ordinal match, blank names counted together. Used by the
    /// status bar, which only needs the one figure and not the whole index.
    /// </summary>
    public static int CountOf(Schematic schematic, string? name)
    {
        string target = string.IsNullOrWhiteSpace(name) ? "" : name!;
        int count = 0;
        foreach (var item in schematic.Items)
        {
            if (item is not NetLabelItem label) continue;
            string other = string.IsNullOrWhiteSpace(label.Label) ? "" : label.Label;
            if (string.Equals(other, target, StringComparison.Ordinal)) count++;
        }
        return count;
    }
}
