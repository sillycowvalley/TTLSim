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
///   <item>Tri-state: '125 '126 '173 '244 '245 '257 '299 '373 '374 '541 '573
///         '574 '590 '595
///         '670, all GALs, all EEPROM/SRAM parts. Sharing a net is the whole
///         point of them.</item>
///   <item>Open-drain / open-collector: '47 (segment drivers), DS1813 (/RST),
///         NE555 / NE556 (DISCHARGE). Wired-AND on these is legitimate.</item>
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

        // Arithmetic.
        "181", "182", "283", "688",
    };

    /// <summary>
    /// True when this part's outputs are known to be permanently driving.
    /// False for tri-state parts, open-drain parts, and -- importantly --
    /// anything not in the table, whose capability we simply don't know.
    /// </summary>
    public static bool IsTotemPole(string? partIdentifier) =>
        partIdentifier is not null && Ids.Contains(partIdentifier);
}