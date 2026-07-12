using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
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

    /// <summary>
    /// Set by <see cref="NetLabelIndex.Build"/>'s alias pass: describes a
    /// NAME COLLISION with another group. "D0" and ("D", bit 0) are DIFFERENT
    /// electrical nets under the (name, bit) tie key, yet both can render as
    /// "D0" on the canvas -- two visually identical labels that silently do
    /// not connect. Null when no such collision exists.
    /// </summary>
    public string? AliasNote { get; internal set; }

    /// <summary>True when this group has any diagnostic at all -- shown red in
    /// the Net Labels panel.</summary>
    public bool HasError => TiesNothing || HasLoneBits || AliasNote is not null;

    /// <summary>
    /// Full diagnostic text for the status bar, or null when the group is
    /// healthy. One line per problem, joined with " | " so the single-line
    /// status label carries all of it.
    /// </summary>
    public string? Diagnostic
    {
        get
        {
            var parts = new List<string>();

            if (IsUnnamed)
            {
                parts.Add("Unnamed labels tie nothing — name each one or delete it");
            }
            else if (TiesNothing)
            {
                parts.Add(Count == 1
                    ? $"Only label named \"{Name}\" — ties nothing"
                    : $"No bit of \"{Name}\" is tapped twice — ties nothing");
            }
            else if (HasLoneBits)
            {
                var lone = LoneBits.ToList();
                parts.Add($"Bit{(lone.Count == 1 ? "" : "s")} {string.Join(", ", lone)} "
                        + $"of \"{Name}\" tapped only once — tie{(lone.Count == 1 ? "s" : "")} nothing");
            }

            if (AliasNote is not null)
                parts.Add(AliasNote);

            return parts.Count == 0 ? null : string.Join("  |  ", parts);
        }
    }

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
/// panel (which lists it) and the status bar (which reports the count and
/// diagnostics for the selected label), so both agree on what "occurs N times"
/// means and on what counts as an error.
/// </summary>
public static class NetLabelIndex
{
    /// <summary>
    /// Every distinct net-label name in the schematic, ordered with the unnamed
    /// group (if any) first -- it is always a defect -- and then by name,
    /// ordinal (the same comparison the tie keys use). Includes the alias pass
    /// (see <see cref="NetLabelGroup.AliasNote"/>).
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

        var groups = byName
            .Select(kv => new NetLabelGroup(kv.Key, kv.Value, pinsPerBit[kv.Key]))
            .OrderBy(g => g.IsUnnamed ? 0 : 1)
            // Sort ignoring the active-low '/' prefix so "/RESET" files under R
            // next to "RESET", not in a slash-block at the top. The full name is
            // the tiebreak, so "/CS" and "CS" keep a stable relative order.
            .ThenBy(g => g.Name.TrimStart('/'), StringComparer.Ordinal)
            .ThenBy(g => g.Name, StringComparer.Ordinal)
            .ToList();

        DetectAliases(groups);
        return groups;
    }

    /// <summary>
    /// Flag name collisions between groups: a group literally named "D0" and a
    /// group named "D" that carries bit 0 are DIFFERENT nets under the
    /// (name, bit) tie key, yet the "D" port's bit-0 pin renders "D0" -- the two
    /// look identical on the canvas and silently do not connect. Both groups
    /// get an <see cref="NetLabelGroup.AliasNote"/>.
    ///
    /// <para>The trailing digits must be in canonical form ("D0", not "D00")
    /// for the renderings to actually coincide -- <see cref="NetLabelItem.BitName"/>
    /// never emits leading zeros -- so non-canonical digit tails are skipped.</para>
    /// </summary>
    private static void DetectAliases(List<NetLabelGroup> groups)
    {
        var byName = new Dictionary<string, NetLabelGroup>(StringComparer.Ordinal);
        foreach (var g in groups)
            if (!g.IsUnnamed)
                byName[g.Name] = g;

        foreach (var g in groups)
        {
            if (g.IsUnnamed) continue;

            // Split "D12" into base "D" + digits "12". Skip names with no
            // trailing digits, or that are all digits (no base to collide with).
            string name = g.Name;
            int split = name.Length;
            while (split > 0 && char.IsDigit(name[split - 1])) split--;
            if (split == name.Length || split == 0) continue;

            string baseName = name.Substring(0, split);
            string digits = name.Substring(split);
            if (!int.TryParse(digits, out int bit)) continue;
            if (digits != bit.ToString()) continue;   // "D00" never renders from a "D" port

            if (!byName.TryGetValue(baseName, out var baseGroup)) continue;
            if (!baseGroup.PinsPerBit.ContainsKey(bit)) continue;

            g.AliasNote =
                $"NAME COLLISION: \"{name}\" is a different net from \"{baseName}\" bit {bit}, "
              + $"but both render as \"{name}\" — they do NOT connect";
            baseGroup.AliasNote = Append(baseGroup.AliasNote,
                $"NAME COLLISION: bit {bit} renders as \"{name}\", identical to the separate "
              + $"net named \"{name}\" — they do NOT connect");
        }
    }

    private static string Append(string? existing, string more) =>
        existing is null ? more : existing + "  |  " + more;
}