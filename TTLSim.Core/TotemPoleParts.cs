namespace TTLSim.Core;

/// <summary>
/// Parts whose logic outputs are *always* driving -- plain totem-pole /
/// push-pull stages with no high-Z state and no open-drain pin. Two of these
/// wired to the same net is an unconditional short circuit: there is no
/// operating condition under which they can share a wire, so the builder can
/// raise it as a hard error (TTL005) without ever running the simulation.
///
/// <para>
/// The table is a WHITELIST OF CERTAINTY, and the direction matters. A part
/// that is missing from this list is treated as "capability unknown", so the
/// static check stays quiet and the runtime contention detector
/// (<see cref="Net.DetectFault"/>) covers it instead. That failure mode is
/// benign -- a missed check. Inverting the table (listing the tri-state parts
/// and erroring on everything else) would fail the other way: one forgotten
/// entry and a perfectly good bus becomes a build-blocking error. So: when in
/// doubt, leave a part OUT.
/// </para>
///
/// <para>
/// Deliberately absent, and why:
/// </para>
/// <list type="bullet">
///   <item>Tri-state: '125 '126 '173 '189 '244 '245 '257 '299 '373 '374 '541
///         '573 '574 '590 '595
///         '670, all GALs, all EEPROM/SRAM parts. Sharing a net is the whole
///         point of them. ('189 is a 74-number among 74-numbers but it is a
///         RAM -- 16x4, tri-state, complementary outputs -- so it is listed
///         here explicitly rather than left to the EEPROM/SRAM catch-all.)</item>
///   <item>Open-drain / open-collector: '47 (segment drivers), DS1813 (/RST),
///         NE555 / NE556 (DISCHARGE). Wired-AND on these is legitimate.</item>
///   <item>Mixed drive: '181. F0..F3, /P, /G and Cn+4 are ordinary totem-pole
///         stages, but A=B (pin 14) is OPEN-COLLECTOR -- it exists precisely
///         to be wire-ANDed across slices on one pull-up for a multi-nibble
///         equality signal. This table is per-PART, not per-pin, so the one
///         OC pin disqualifies the whole part: listing it would raise TTL005
///         on that legal A=B tie. Cost of the exclusion: a genuine F-to-F
///         short between two '181s downgrades from a build error to the
///         runtime detector (Net.DetectFault) -- the same trade every
///         tri-state part above already makes.</item>
///   <item>Passive pull-up: '48. Pin-identical to the '47 and active-HIGH
///         rather than open-collector, which makes it look like a totem-pole
///         part -- but its segment drivers pull up through internal resistors,
///         not a push-pull stage. It is NOT a candidate for this list; the
///         apparent gap next to the '47 is deliberate.</item>
///   <item>Teensy 4.1. Its pins are configured in software, so the output
///         stage of any given pin is unknowable from the schematic --
///         precisely the "capability unknown" case this table stays out of.</item>
///   <item>Passives, headers, displays. They own no output pins, so they never
///         reach this check anyway.</item>
/// </list>
///
/// <para>
/// PARALLEL-LIST WARNING: adding a new push-pull 74-series chip means adding
/// its identifier here as well as to the chip factory, the DTO mapper, the
/// library panel and the part definition. Omitting it here costs a diagnostic,
/// not correctness -- but the check is only as good as the list.
/// </para>
///
/// Identifiers match <see cref="BuildDevice.PartIdentifier"/>, which carries
/// the bare part number ("00", "273") with the logic family held separately.
/// One entry therefore covers the HC, HCT, LS and AC variants alike -- their
/// output stages are all push-pull.
/// </summary>
public static class TotemPoleParts
{
    private static readonly HashSet<string> Ids = new(StringComparer.OrdinalIgnoreCase)
    {
        // Gates -- every output a push-pull stage.
        "00", "02", "04", "08", "10", "14", "20", "30", "32", "86", "132",

        // Flip-flops.
        "73", "74", "107", "175",

        // Registers without an output enable. ('273 has /CLR only; '377 has
        // /CE, which gates the *clock*, not the output stage -- the Qs drive
        // regardless. That is precisely the trap: an engineer reaching for a
        // register on a bus wants a '374, and a '273 in the same socket shorts
        // whatever else is on that bus.)
        "273", "377",

        // Shift registers without an output enable. ('164 Q0..Q7, '165
        // Q7 and /Q7, '194 Q0..Q3 -- all always driving. The '595 and
        // '299 are the tri-state shift parts and stay OUT.)
        "164", "165", "194",

        // Counters.
        // ('590 is NOT here: its register outputs are tri-state.)
        "161", "163", "191", "193", "390", "393", "4040", "4060",

        // Decoders.
        "138", "139", "154",

        // Multiplexers WITHOUT tri-state outputs. Note '157 belongs here and
        // its near-twin '257 does not -- the '257 is the tri-state part.
        "151", "153", "157",

        // Arithmetic. (The '181 is deliberately absent -- see the
        // mixed-drive bullet above: its A=B pin is open-collector.)
        "182", "283", "688",

        // Parity. Both PE and PO are plain push-pull outputs -- the '280
        // has no enable and no high-Z state, so two of them on one net is
        // an unconditional short.
        "280",
    };

    /// <summary>
    /// True when this part's outputs are known to be permanently driving.
    /// False for tri-state parts, open-drain parts, and -- importantly --
    /// anything not in the table, whose capability we simply don't know.
    /// </summary>
    public static bool IsTotemPole(string? partIdentifier) =>
        partIdentifier is not null && Ids.Contains(partIdentifier);
}