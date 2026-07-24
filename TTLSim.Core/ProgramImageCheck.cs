namespace TTLSim.Core;

/// <summary>
/// Program-image presence and validity check for the build pipeline
/// (diagnostics TTL040 and TTL041).
///
/// Two part families carry a program that is loaded out of band rather than
/// wired on the canvas: the parallel EEPROMs (an Intel HEX image) and the GALs
/// (a JEDEC fuse map). Both live in <see cref="BuildDevice.Program"/>.
///
/// <para>
/// The chip factory deliberately tolerates a missing or unreadable program: an
/// EEPROM with no image builds as a blank part, and a GAL with no fuse map
/// builds with an empty array. That is the right runtime behaviour -- a part
/// you have not burnt yet should not stop the rest of the board simulating --
/// but it is silent. A blank ROM reads as a stream of constant bytes and a
/// blank GAL drives nothing useful, so the symptom is a CPU that fetches
/// garbage or a decoder whose strobes never fire, with nothing on screen to
/// say why. The malformed case is worse still: the factory logs a warning and
/// carries on, so the only trace is a line in the log file that nobody reads.
/// These diagnostics put both cases in the Output pane where the rest of the
/// build's findings are.
/// </para>
///
/// <para>
/// Both are <b>Warnings</b>, not Errors, so they match what the factory
/// actually does -- the schematic still builds and still runs. Change
/// <see cref="BlankSeverity"/> or <see cref="MalformedSeverity"/> to
/// <see cref="DiagnosticSeverity.Error"/> if you would rather a board that
/// cannot possibly work refuse to build at all.
/// </para>
///
/// <para>
/// PARALLEL-LIST WARNING: the two identifier tables below mirror
/// <c>Device.Identifiers.Eeprom</c> and <c>Device.Identifiers.Gal</c> in the UI
/// layer. They are duplicated rather than shared because Core cannot reference
/// the WinForms project. Adding a new EEPROM or PLD part means adding it in
/// both places; omitting it here costs a diagnostic, not correctness.
/// Identifiers match <see cref="BuildDevice.PartIdentifier"/>, which for these
/// box parts is the bare part number ("28C256", "GAL16V8").
/// </para>
/// </summary>
public static class ProgramImageCheck
{
    /// <summary>No program image loaded at all.</summary>
    public const string BlankCode = "TTL040";

    /// <summary>A program image is present but will not parse.</summary>
    public const string MalformedCode = "TTL041";

    private const DiagnosticSeverity BlankSeverity = DiagnosticSeverity.Warning;
    private const DiagnosticSeverity MalformedSeverity = DiagnosticSeverity.Warning;

    private static readonly HashSet<string> EepromIds = new(StringComparer.OrdinalIgnoreCase)
    {
        "28C256", "28C128", "28C64", "28C16",
    };

    private static readonly HashSet<string> GalIds = new(StringComparer.OrdinalIgnoreCase)
    {
        "GAL16V8", "GAL20V8", "GAL22V10",
    };

    /// <summary>
    /// What kind of image a part expects. <see cref="ImageKind.None"/> means the
    /// part carries no program and is skipped entirely -- SRAM powers up blank
    /// by design, and everything else has no image at all.
    /// </summary>
    private enum ImageKind
    {
        None,
        IntelHex,
        Jedec
    }

    /// <summary>
    /// One diagnostic per program-bearing device whose image is missing or
    /// unreadable. Empty when every EEPROM and GAL on the active schematic
    /// holds a program that parses.
    /// </summary>
    public static IEnumerable<Diagnostic> Check(IBuildInput input)
    {
        List<Diagnostic> result = new();

        foreach (BuildDevice dev in input.Devices)
        {
            ImageKind kind = KindOf(dev.PartIdentifier);
            if (kind == ImageKind.None) continue;

            // Locator points at the device's first unit, not the device id:
            // the OutputPanel resolves ids against the schematic's item list,
            // not its device list. Same reasoning as TTL020/TTL021. These are
            // all single-unit box parts, so the first unit IS the chip.
            string? locatorId = dev.Units.Count > 0 ? dev.Units[0].UnitId : null;

            string imageName = kind == ImageKind.IntelHex
                ? "Intel HEX image"
                : "JEDEC fuse map";

            if (string.IsNullOrWhiteSpace(dev.Program))
            {
                result.Add(new Diagnostic(
                    BlankSeverity,
                    BlankCode,
                    $"{dev.Designator} ({dev.PartIdentifier}) has no {imageName} loaded. "
                    + BlankConsequence(kind),
                    ItemId: locatorId));
                continue;
            }

            string? problem = ParseProblem(kind, dev.Program);
            if (problem is not null)
            {
                result.Add(new Diagnostic(
                    MalformedSeverity,
                    MalformedCode,
                    $"{dev.Designator} ({dev.PartIdentifier}) has a {imageName} that will "
                    + $"not parse ({problem}). The part is built blank. "
                    + BlankConsequence(kind),
                    ItemId: locatorId));
            }
        }

        return result;
    }

    /// <summary>
    /// Which family a part identifier belongs to. SRAM is deliberately absent:
    /// it carries no program and powering up blank is correct behaviour, not a
    /// finding.
    /// </summary>
    private static ImageKind KindOf(string? partIdentifier)
    {
        if (partIdentifier is null) return ImageKind.None;
        if (EepromIds.Contains(partIdentifier)) return ImageKind.IntelHex;
        if (GalIds.Contains(partIdentifier)) return ImageKind.Jedec;
        return ImageKind.None;
    }

    /// <summary>
    /// Null when the text parses; otherwise the parser's own complaint, for the
    /// diagnostic message. Only <see cref="FormatException"/> is caught -- that
    /// is what both parsers raise for bad input, and it is what the chip factory
    /// catches on the same two paths. Anything else is a real defect and should
    /// surface rather than be swallowed here.
    /// </summary>
    private static string? ParseProblem(ImageKind kind, string program)
    {
        try
        {
            if (kind == ImageKind.IntelHex)
            {
                byte[] bytes = IntelHex.Parse(program);

                // A syntactically valid image that yields no bytes (records
                // present but nothing but an EOF record, say) is a blank part
                // wearing a program's clothes. Report it the same way.
                if (bytes.Length == 0)
                    return "it contains no data records";
            }
            else
            {
                JedecFuseMap.Parse(program);
            }
            return null;
        }
        catch (FormatException ex)
        {
            return ex.Message;
        }
    }

    /// <summary>What an unprogrammed part of this kind will actually do at
    /// run time, so the message says why it matters rather than only what is
    /// missing.</summary>
    private static string BlankConsequence(ImageKind kind) =>
        kind == ImageKind.IntelHex
            ? "The part is simulated blank -- every read returns the same value, "
              + "so anything fetching from it will see constant data."
            : "The part is simulated with an empty array -- its outputs follow no "
              + "equations, so downstream logic will never be driven correctly.";
}
