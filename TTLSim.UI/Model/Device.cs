using System.Collections.Generic;
using System.ComponentModel;
using TTLSim.UI.Components;

namespace TTLSim.UI.Model;

/// <summary>
/// The logical chip or part: carries the designator (U1, R3), part number,
/// and family. Owns a list of Units that are individually placed on the
/// canvas. A Device is NOT a SchematicItem -- it doesn't draw and has no
/// position. Units do.
///
/// Property-grid visibility is per-instance: each part shows only the
/// properties that mean something for it. A 74-series part shows Family; a
/// memory/PLD part shows Propagation Delay; an EEPROM additionally shows its
/// Program and Program Contents; a passive shows Value. This is driven by
/// <see cref="DevicePropertyFilter"/>, attached via the type-converter below,
/// because these properties are mutually exclusive per part and static
/// [Browsable] attributes can't express that.
/// </summary>
[TypeConverter(typeof(DevicePropertyFilter))]
public sealed class Device
{
    [Browsable(false)]
    public string Id { get; set; } = System.Guid.NewGuid().ToString("N");

    /// <summary>e.g. "U1", "R3", "C7". Auto-assigned on placement; editable in the property grid.</summary>
    [Category("Identity")]
    public string Designator { get; set; } = "";

    [Browsable(false)]
    public PartDefinition Definition { get; }

    /// <summary>
    /// Optional value string for passives ("10k", "100nF", "red"). Null for ICs.
    /// Displayed beneath the designator on the canvas when set. The property
    /// grid shows this only for passive parts (see DevicePropertyFilter).
    /// </summary>
    [Category("Identity")]
    public string? Value { get; set; }

    /// <summary>EEPROM/ROM program image as Intel HEX text, or a GAL JEDEC fuse
    /// map. Null for non-program parts and for SRAM (which powers up blank).
    /// Imported/exported via the File menu. The property grid shows this only
    /// for program-bearing parts (EEPROM and GAL; see DevicePropertyFilter).</summary>
    [Category("Identity")]
    [RefreshProperties(RefreshProperties.Repaint)]
    public string? Program { get; set; }

    /// <summary>Read-only feedback for the property grid: a short summary of the
    /// loaded program -- byte count plus a preview of the first bytes, blank when
    /// nothing is loaded, or "(invalid Intel HEX)" if <see cref="Program"/> will
    /// not parse. Refreshes whenever the grid re-reads (e.g. after Import HEX).
    /// Intel-HEX-specific, so the property grid shows this only for EEPROM parts
    /// (see DevicePropertyFilter).</summary>
    [Category("Identity")]
    [DisplayName("Program Contents")]
    [Description("Summary of the EEPROM/ROM image currently loaded (set via File > Import HEX).")]
    [ReadOnly(true)]
    public string ProgramContents
    {
        get
        {
            if (string.IsNullOrWhiteSpace(Program)) return "";
            byte[] bytes;
            try { bytes = TTLSim.Core.IntelHex.Parse(Program); }
            catch (System.FormatException) { return "(invalid Intel HEX)"; }
            if (bytes.Length == 0) return "";

            var sb = new System.Text.StringBuilder();
            int preview = System.Math.Min(bytes.Length, 8);
            for (int i = 0; i < preview; i++)
            {
                if (i > 0) sb.Append(' ');
                sb.Append(bytes[i].ToString("X2"));
            }
            if (bytes.Length > preview) sb.Append(" ...");
            return $"{bytes.Length} bytes - {sb}";
        }
    }

    /// <summary>74xx logic family. Only meaningful for 74-series IC parts; the
    /// property grid hides it for memory / PLD parts (see DevicePropertyFilter).</summary>
    [Category("Identity")]
    public TtlFamily? Family { get; set; }

    /// <summary>
    /// Propagation / access delay in nanoseconds, for parts whose speed cannot
    /// be deduced from a 74-series family -- the parallel memories (EEPROM,
    /// SRAM) and the PLDs (GAL). Null means "use the part's built-in default
    /// speed grade". The property grid shows this only for those parts (see
    /// DevicePropertyFilter); it is hidden for 74-series and passive parts.
    /// </summary>
    [Category("Identity")]
    [DisplayName("Propagation Delay (ns)")]
    [Description("Address/enable-valid to data-valid delay in nanoseconds. " +
                 "Leave blank to use the part's default speed grade.")]
    public int? PropagationDelayNs { get; set; }

    [Browsable(false)]
    public List<Unit> Units { get; } = new();

    /// <summary>Explicitly placed power unit, if any. Null when power is implicit.</summary>
    [Browsable(false)]
    public PowerUnit? PowerUnit { get; set; }

    /// <summary>
    /// Display string for the part. For 74-series ICs and chips this is
    /// family-prefixed ("74HC00", "74LS393"); for non-74 chips (NE555, 28C256)
    /// it's the literal part identifier; for passives it's the part identifier.
    /// </summary>
    [Category("Identity")]
    [ReadOnly(true)]
    public string FullPartNumber => Definition switch
    {
        IcPartDefinition ic when Family is { } f =>
            f == TtlFamily.Standard ? $"74{ic.PartNumber}" : $"74{f}{ic.PartNumber}",
        IcPartDefinition ic => $"74{ic.PartNumber}",
        ChipPartDefinition cp when cp.IsSeries74 && Family is { } f =>
            f == TtlFamily.Standard ? $"74{cp.PartNumber}" : $"74{f}{cp.PartNumber}",
        ChipPartDefinition cp when cp.IsSeries74 => $"74{cp.PartNumber}",
        _ => Definition.Identifier
    };

    // ---------------------------------------------------------- grid predicates
    // These drive DevicePropertyFilter: each property shows only when its
    // predicate is true for this specific part.

    /// <summary>True for true 74-series parts (the only parts that have a TTL
    /// family). Shows <see cref="Family"/>.</summary>
    [Browsable(false)]
    public bool IsFamilyBearer =>
        Definition is IcPartDefinition
        || (Definition is ChipPartDefinition cp && cp.IsSeries74);

    /// <summary>True for parts whose delay is carried explicitly on the device
    /// rather than deduced from a 74-series family: the parallel memories and
    /// the GAL/PLD parts. Shows <see cref="PropagationDelayNs"/>.</summary>
    [Browsable(false)]
    public bool UsesExplicitDelay =>
        Definition is ChipPartDefinition cp && !cp.IsSeries74
        && Identifiers.DelayBearing.Contains(cp.PartNumber);

    /// <summary>True for passive parts (resistor, capacitor, LED, ...). Shows
    /// <see cref="Value"/>.</summary>
    [Browsable(false)]
    public bool IsPassive => Definition is PassivePartDefinition;

    /// <summary>True for parts that load a program image: EEPROM (Intel HEX) and
    /// GAL (JEDEC fuse map). Shows <see cref="Program"/>. SRAM powers up blank
    /// and is excluded.</summary>
    [Browsable(false)]
    public bool CarriesProgram =>
        Definition is ChipPartDefinition cp
        && (Identifiers.Eeprom.Contains(cp.PartNumber)
            || Identifiers.Gal.Contains(cp.PartNumber));

    /// <summary>True for EEPROM parts only. Shows <see cref="ProgramContents"/>,
    /// which formats the image as Intel HEX bytes -- meaningful for an EEPROM
    /// but not for a GAL (whose program is a JEDEC fuse map).</summary>
    [Browsable(false)]
    public bool ShowsProgramContents =>
        Definition is ChipPartDefinition cp && Identifiers.Eeprom.Contains(cp.PartNumber);

    public Device(PartDefinition definition)
    {
        Definition = definition;
        if (definition is IcPartDefinition) Family = TtlFamily.HC;
        else if (definition is ChipPartDefinition cp && cp.IsSeries74) Family = cp.DefaultFamily;
    }

    public override string ToString() => Designator;

    /// <summary>
    /// Part-identifier sets that classify a chip for property-grid visibility
    /// and (for delay) for the simulator. Kept here so the rules live in one
    /// place.
    /// </summary>
    public static class Identifiers
    {
        /// <summary>Parallel EEPROM parts (carry an Intel HEX image).</summary>
        public static readonly HashSet<string> Eeprom = new(System.StringComparer.Ordinal)
        {
            "28C256", "28C128", "28C64", "28C16",
        };

        /// <summary>Parallel SRAM parts (power up blank; carry no program).</summary>
        public static readonly HashSet<string> Sram = new(System.StringComparer.Ordinal)
        {
            "62256", "6116", "2114",
        };

        /// <summary>GAL / PLD parts (carry a JEDEC fuse map).</summary>
        public static readonly HashSet<string> Gal = new(System.StringComparer.Ordinal)
        {
            "GAL16V8", "GAL20V8",
        };

        /// <summary>Every part whose delay is carried on the device rather than
        /// deduced from a 74-series family: all memory plus the GALs.</summary>
        public static readonly HashSet<string> DelayBearing =
            new(System.StringComparer.Ordinal);

        static Identifiers()
        {
            DelayBearing.UnionWith(Eeprom);
            DelayBearing.UnionWith(Sram);
            DelayBearing.UnionWith(Gal);
        }
    }
}

/// <summary>
/// Per-instance property filter for <see cref="Device"/>. Removes the
/// properties that don't apply to the specific part, so the property grid
/// shows only what's meaningful for it:
///   Family            -- 74-series parts only
///   Propagation Delay -- memory / PLD parts only
///   Program           -- EEPROM and GAL only
///   Program Contents  -- EEPROM only
///   Value             -- passive parts only
/// Designator and FullPartNumber are always shown.
/// </summary>
public sealed class DevicePropertyFilter : ExpandableObjectConverter
{
    public override bool GetPropertiesSupported(ITypeDescriptorContext? context) => true;

    public override PropertyDescriptorCollection GetProperties(
        ITypeDescriptorContext? context, object value, System.Attribute[]? attributes)
    {
        PropertyDescriptorCollection all =
            TypeDescriptor.GetProperties(value, attributes, noCustomTypeDesc: true);

        if (value is not Device device) return all;

        var kept = new List<PropertyDescriptor>(all.Count);
        foreach (PropertyDescriptor pd in all)
        {
            if (pd.Name == nameof(Device.Family) && !device.IsFamilyBearer) continue;
            if (pd.Name == nameof(Device.PropagationDelayNs) && !device.UsesExplicitDelay) continue;
            if (pd.Name == nameof(Device.Value) && !device.IsPassive) continue;
            if (pd.Name == nameof(Device.Program) && !device.CarriesProgram) continue;
            if (pd.Name == nameof(Device.ProgramContents) && !device.ShowsProgramContents) continue;
            kept.Add(pd);
        }

        return new PropertyDescriptorCollection(kept.ToArray());
    }
}

/// <summary>
/// Placeholder for a power unit. The concrete implementation comes with the
/// "show power for this device" action in a later phase. Kept here so Device
/// can reference it now without breaking compilation later.
/// </summary>
public abstract class PowerUnit : Unit
{
    protected PowerUnit(Device device, UnitSpec spec) : base(device, spec) { }
}