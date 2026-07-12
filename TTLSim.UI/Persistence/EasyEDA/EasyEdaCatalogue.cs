using System;
using System.Collections.Generic;
using System.Drawing;
using System.Security.Cryptography;
using System.Text;
using TTLSim.UI.Components;
using TTLSim.UI.Model;

namespace TTLSim.UI.Persistence.EasyEDA;

/// <summary>
/// One entry in the EasyEDA catalogue: a TTLSim part type → a triple of
/// (symbol, footprint, device) UUIDs plus the embedded-resource filenames.
///
/// Pin geometry is described in EasyEDA's local coordinate frame: each pin
/// number known to TTLSim maps to an (x, y) offset from the EasyEDA
/// COMPONENT placement point. The exporter uses this to position the
/// EasyEDA component so its pins line up with TTLSim's pin world positions.
///
/// For parts where one EasyEDA library entry covers many TTLSim instances
/// (LED, VCC, GND), the static fragments in device_fragments.json suffice.
/// For parts where each TTLSim value maps to a distinct EasyEDA library
/// part (resistors with 71 values), the per-value device and symbol
/// fragments are synthesised at lookup time via <see cref="InlineDeviceJson"/>
/// and <see cref="InlineSymbolJson"/>, plus a template .esym instantiated
/// at zip-build time via <see cref="SymbolTemplateTokens"/>.
/// </summary>
/// <summary>
/// Per-rotation EasyEDA-pixel offset of a single label kind (Designator,
/// Name, or Value) from the COMPONENT anchor. Y is in EasyEDA Y-up space
/// (positive = visually above on screen).
/// </summary>
public readonly record struct LabelOffset(int X, int Y);

/// <summary>
/// All three label offsets for a single rotation, plus the rotation of
/// the label TEXT itself. TextRotationDeg is the EasyEDA text-rotation
/// applied to the Designator and Name ATTRs at this placement rotation
/// (0 = horizontal; 90 = reading bottom-to-top). Vertical-body chips
/// (DIP at R90/R270) set this to 90 so the labels sit beside the body
/// and read along it; every other part leaves it 0 (default), preserving
/// the existing horizontal-label behaviour.
/// </summary>
public readonly record struct LabelOffsetSet(
    LabelOffset Designator,
    LabelOffset Name,
    LabelOffset Value,
    int TextRotationDeg = 0);

/// <summary>
/// Per-part, per-rotation label placement. Each catalogue entry derives
/// these by hand-tuning the labels in EasyEDA and reading the offsets
/// back from the saved .epro. Parts with different body shapes (resistor
/// vs LED vs capacitor) need different tables because the labels are
/// positioned relative to the body, not to the anchor.
/// </summary>
public sealed record LabelOffsetsByRotation(
    LabelOffsetSet Rot0,
    LabelOffsetSet Rot90,
    LabelOffsetSet Rot180,
    LabelOffsetSet Rot270);

public sealed record CataloguePart(
    string DeviceUuid,
    string SymbolUuid,
    string SymbolResourceName,
    string? FootprintUuid,
    string? FootprintResourceName,
    string PartTitle,
    System.Collections.Generic.Dictionary<int, Point> PinLocalPositions,
    bool EmitValueLabel = false,
    bool IsNetFlag = false,
    string? NetName = null,
    bool EmitNameOverride = false,
    string? InlineDeviceJson = null,
    string? InlineSymbolJson = null,
    System.Collections.Generic.IReadOnlyDictionary<string, string>? SymbolTemplateTokens = null,
    LabelOffsetsByRotation? LabelOffsets = null,
    // When true, the part's pin world positions already use the
    // EasyEDA rotation sense (R90/R270 swapped relative to TTL Sim's
    // default) -- so the exporter should NOT apply its
    // `(360 - r) % 360` rotation-sense shim. Used by header units,
    // which carry SwapR90R270 = true on their Pins so the canvas
    // visually matches EasyEDA.
    bool MatchesEasyEdaRotationSense = false,
    // Extra rotation (0/90/180/270 degrees) added on top of the
    // rotation-sense shim. Set to 180 when a part's PinLocalPositions
    // are X-mirrored relative to the .esym's native pin numbering --
    // the mirror is equivalent to a 180° rotation for a 2-pin part
    // symmetric about Y, so without compensation the rendered symbol
    // flips relative to the wire endpoints. Currently only DiodePart
    // needs this.
    int EasyEdaRotationOffsetDeg = 0,
    // When true (combined with EmitNameOverride = true), the visible
    // Name ATTR is written with the designator's font style (size 8)
    // instead of the smaller Value style (size 6). Used by headers so
    // their Name reads at the same size as the designator alongside it,
    // matching the hand-edited reference. The LED uses the default
    // (false) so its Name stays size 6 below the designator.

    bool NameLabelUsesDesignatorStyle = false,
    // When EmitValueLabel is true, ValueOverride decides what text the
    // sheet writer writes into the COMPONENT's Value ATTR:
    //   - null  -> emit "value=null" so EasyEDA falls back to the
    //              device template's Value field. Original behaviour
    //              from when each resistor value had its own device.
    //   - non-null -> emit "value=<ValueOverride>" so the placed
    //              instance carries its own display text. Used by the
    //              Frankenstein resistor (one device, many instances
    //              showing different resistances).
    // Has no effect when EmitValueLabel is false.
    string? ValueOverride = null,
    // When true, the Name ATTR on the placed COMPONENT is emitted as
    // value=null, valVisible=1, default style -- telling EasyEDA to
    // render the device template's templated Name (e.g. the device's
    // "={Manufacturer Part}") at this position. Used by DIP ICs, where
    // the displayed part name ("NE555N") lives in the device template
    // rather than being a user-typed label or a per-instance override.
    // Distinct from EmitNameOverride (which writes the user's Label as
    // the value) and from the resistor's blanked "" Name ATTR. Has no
    // effect when EmitNameOverride is also set.
    bool EmitTemplatedName = false);

/// <summary>
/// Maps TTLSim parts to EasyEDA library entries. LED, VCC, GND, Switch, Button,
/// and Resistor are shipped as single library entries (one .esym, one .efoo each).
/// The Resistor is a Frankenstein part: one shared library device whose Value ATTR
/// is overridden per instance to display the user's typed resistance text. Anything
/// else throws NotImplementedException naming the missing part.
/// </summary>
public static class EasyEDACatalogue
{
    // ---------------------------------------------------- UUIDs (verbatim from sample)

    // LED (5mm through-hole, red). Symbol, footprint and 3D model
    // sourced from the May 2026 Models.epro -- TONYU DY-324SVRC. The
    // 3D Model is referenced by UUID in the device entry and resolved
    // by EasyEDA's cloud library at render time, so no embedded binary
    // asset is needed.
    private const string LedDeviceUuid = "856406c93c6149ea8e8e6d8b2f9ca939";
    private const string LedSymbolUuid = "b81cb2d96d1a4f56acc9ecf6c73ef881";
    private const string LedFootprintUuid = "4a99776da31e4715b4080fd142f55a57";
    private const string Led3dModelUuid = "ebee32c7611e4683ad93cab18ddbd1f8";

    // Power-VCC -- Net Flag symbol, no footprint
    private const string VccDeviceUuid = "1e0a4fb10c0d469abe117d2e6303547a";
    private const string VccSymbolUuid = "ed6167b3a4844aec980dab702aa58d0c";

    // Ground-GND -- Net Flag symbol, no footprint
    private const string GndDeviceUuid = "e81f0dd534964512910e0f8ea58466cc";
    private const string GndSymbolUuid = "38bf0daecbc14b51831e971507e9b906";

    // Can oscillator (full-can, DIP-14 / 0.6" footprint). Footprint + 3D model
    // lifted from the Can_Oscillator_DIP-14 reference (MXO45-2C, OSC-TH_4P).
    // The schematic symbol is authored to TTL Sim's tall DIP-14 can outline
    // (4 corner pins 1/7/8/14) so exported wires meet the pins cleanly; the
    // compact vendor symbol would force large wire extenders. Device/symbol
    // UUIDs are freshly minted; footprint + 3D are the reference's.
    private const string CanOscDip14DeviceUuid = "d52508430df1c21355c50516bc1e2c17";
    private const string CanOscDip14SymbolUuid = "fc8be532ccd30dafc1a7c1cf14997e39";
    private const string CanOscDip14FootprintUuid = "96a74e205c3b533c";
    private const string CanOscDip143dModelUuid = "b28ea8a6f4d94dddb31ac04d823ca8bd";

    // Half-can oscillator (DIP-8 / 0.3" footprint). Same corner layout as the
    // full can but pins 1/4/5/8 and a shorter body. Footprint + 3D model lifted
    // from the Can_Oscillator_DIP-8 reference (MXO45HST-2C, OSC-TH_4P).
    private const string CanOscDip8DeviceUuid = "206403cff8b74d2993df59ed14fe0dc5";
    private const string CanOscDip8SymbolUuid = "b06a45bf13be758ce0c349dee733eba7";
    private const string CanOscDip8FootprintUuid = "5e3a271f66fc3588";
    private const string CanOscDip83dModelUuid = "0d8056454aa5403aab4efe452ce6339f";

    // 2.54 mm 1xN MALE pin headers -- swapped from female sockets to
    // male pin headers (May 2026), now sourced from the HX PH254 series
    // in Models.epro. The HDR-TH_NP-P2.54-V-M footprints come paired
    // with matching cloud-library 3D models so the EasyEDA Pro 3D
    // viewer shows actual pin headers rather than empty pads.
    //
    // Device UUIDs are preserved from the female-header era so existing
    // saved .ttlproj files still resolve to the same library entry on
    // open -- but every other field (symbol UUID, footprint UUID, the
    // .esym + .efoo resources, MPN, LCSC code, 3D model) is replaced.
    //
    // Note: this is DIFFERENT from Hdr2MaleFootprintUuid below, which
    // is the BOOMELE male 2P footprint that the Switch and Button
    // Frankenstein parts use for their off-board pluggable wiring.
    // Switch/Button were unaffected by the female->male header swap.
    private const string Header2DeviceUuid = "c321050abd3a4cf59555c4cd1f2d358d";
    private const string Header2SymbolUuid = "a30e8e4b43f24cd2acafd4910c0b8235";
    private const string Header2FootprintUuid = "d419dee46db64bc89e7c7d9c6bd005ef";
    private const string Header23dModelUuid = "b9a02bffb1e248eabf92e8e2aa859dc0";

    // 1x3 male header (HX PH254-01-03). UUIDs lifted verbatim from the
    // 3-Pin_Header reference .epro -- same HX PH254 family as the others,
    // so the symbol/footprint/3D-model carry across cleanly.
    private const string Header3DeviceUuid = "1dee39b11efc7523";
    private const string Header3SymbolUuid = "3d13e4135b2a7fd3";
    private const string Header3FootprintUuid = "ea2eed413840d7de";
    private const string Header33dModelUuid = "3b8cf4ff435f4f2b912293f2932d0598";

    private const string Header4DeviceUuid = "61d307bf2bdb4663b1f70c9c59055ecc";
    private const string Header4SymbolUuid = "e4b5734c1a83476b8ba4c83689cfc5be";
    private const string Header4FootprintUuid = "5709415b839a4c77b5e71aab5f9c71ae";
    private const string Header43dModelUuid = "8a04245d30e54eb999146b73785c6ab0";

    private const string Header6DeviceUuid = "e6b16eb806964a6db55aaa7e40dbfc67";
    private const string Header6SymbolUuid = "542a95afd23746f7b03754cab139a8c3";
    private const string Header6FootprintUuid = "4dfdd5d709bb4d2a8a23feaa147af49e";
    private const string Header63dModelUuid = "6ebb80f5681945ec9fb368fbc217c3f7";

    private const string Header8DeviceUuid = "93f69b3d536b4f728684324d9aa3050f";
    private const string Header8SymbolUuid = "53476ce9627d47b5bce643baf49f8d80";
    private const string Header8FootprintUuid = "610264fc014347b5a38553a4c4dd06cb";
    private const string Header83dModelUuid = "926dcf5c05be473f8295f20cc8ccf72f";

    // Resistor shared resources. All resistor instances point at a single
    // EasyEDA library device, regardless of resistance value. The displayed
    // value text ("470Ω", "2.2kΩ", ...) comes from each placed COMPONENT's
    // Value ATTR override -- see EasyEdaSheetWriter.EmitComponent and
    // BuildResistorPart below.
    //
    // The placeholder LCSC part data on the device entry (CR1/4W-1K, VO) is
    // intentional: EasyEDA's PCB router refuses to route components that
    // don't carry an LCSC stock number, so a generic 1kΩ part is baked in.
    // The BOM lists every resistor as that 1kΩ -- which is wrong, but we
    // don't use the BOM for production, so it doesn't matter. Anyone who
    // DOES want a real BOM needs to manually substitute the right values
    // in EasyEDA after import.
    //
    // Footprint and 3D model came from the May 2026 Res.epro -- footprint
    // is RES-TH_BD2.7-L6.2-P10.20-D0.4 (2.7mm body, 10.2mm pitch); the 3D
    // model is referenced by UUID and resolved by EasyEDA's cloud library
    // at render time, so no embedded binary asset is needed.
    internal const string ResistorFootprintUuid = "72c61d64364a43e890807f3622aaf0ce";
    private const string Resistor3dModelUuid = "f91fbe0e2e6f409498f09f811e4616fe";
    private const string ResistorDeviceUuid = "6d850419aea044bb805429ccd89d98eb";
    private const string ResistorSymbolUuid = "2d1d4b07d6cd4afeaf2cd056f4440b61";

    // Bussed SIP-9 resistor network (Bourns 4609X-101 family): pin 1 common,
    // pins 2..9 each a resistor to the common. One shared library device --
    // the resistance comes from the per-instance Value ATTR override, exactly
    // like the discrete resistor. UUIDs lifted verbatim from Network.epro;
    // the footprint .efoo ships verbatim and its manifest block lives in
    // device_fragments.json, while device + symbol fragments are inlined below.
    internal const string ResistorNetworkFootprintUuid = "7dc4dff0ffd2ec64";
    private const string ResistorNetworkDeviceUuid = "ad9b8177d7792bfe";
    private const string ResistorNetworkSymbolUuid = "bc10366d41f207d3";

    // Switch / Pushbutton -- "Frankenstein" parts that combine a real
    // schematic symbol (SS11-RBDWQ-R20-R rocker / SKPMAPE010 tactile) with
    // a 2-pin 2.54mm male through-hole header footprint so the physical
    // build is a pluggable connector rather than the actual switch part.
    // The symbol UUIDs are taken verbatim from the EasyEDA library entries
    // (extracted from the May 2026 Button_and_Switch.epro upload). The
    // shared footprint UUID is the H1 header from the same .epro
    // (HDR-TH_2P-P2.54-V-M-1, male, vertical). Device UUIDs are freshly
    // minted -- the symbol+footprint combination is unique to TTL Sim and
    // shouldn't collide with the original EasyEDA library devices.
    private const string SwitchDeviceUuid = "7f3a1e2c9b4d4e6a8f0c1d2e3f4a5b6c";
    private const string SwitchSymbolUuid = "f515f6654dec4a1394346860cb152b4c";

    private const string ButtonDeviceUuid = "8a4b2f3d0c5e5f7b9a1d2e3f4a5b6c7d";
    private const string ButtonSymbolUuid = "06cf37fc06354cb7b5e003313b5ce108";
    private const string Button4DeviceUuid = "7877cd28c903bf81";
    private const string Button4SymbolUuid = "17541abff2a89ece";
    private const string Button4FootprintUuid = "a3b4c43d623d8b7a";

    // Shared between Switch and Button -- the H1 male 2.54 mm 1x2P
    // through-hole header. This is a DIFFERENT part from the female
    // Header2FootprintUuid above (the female is what TTL Sim's "Pin
    // Header 2" component already uses for output headers).
    private const string Hdr2MaleFootprintUuid = "2fa9edd918204c849d260ba835fd4d43";

    // SPDT switch and 2-/3-pin jumpers -- same Frankenstein pattern as the
    // SPST switch/button above: each keeps its own simple schematic symbol
    // (authored to match the part's canvas pin geometry exactly, since the
    // export anchors symbol pins at the part's canvas pin positions) but is
    // physically built as a pluggable pin header. The 2-pin jumper reuses
    // the same male 2-pin header footprint as the switch/button; the SPDT
    // switch and 3-pin jumper reuse the male 3-pin header footprint added
    // for the hdr-out-3 output header (Header3FootprintUuid). Device and
    // symbol UUIDs are freshly minted (SHA1-derived, unique to TTL Sim).
    private const string Jumper2DeviceUuid = "085483325abc7b8f7bdc8f340c3d21cd";
    private const string Jumper2SymbolUuid = "080a6645c5bb30467498799499355002";
    private const string Jumper3DeviceUuid = "a84717d96452e7fd52fb35de70d8fb22";
    private const string Jumper3SymbolUuid = "71201f3b05ff1f4582973e2ff83fe013";
    private const string SpdtSwitchDeviceUuid = "70af303575154f3d18f7ba5163296de4";
    private const string SpdtSwitchSymbolUuid = "356ce089221d0ba18de8ea69e30194dd";

    // Real SS1-0-102 slide-switch through-hole footprint, lifted from the
    // SPDT.epro reference. Used for the SPDT switch's PCB land instead of a
    // header (the part is an actual slide switch). The 3-pin jumper keeps the
    // male 3-pin header footprint.
    private const string SpdtSwitchFootprintUuid = "f6b5e63f270148f6";

    // Capacitors -- Frankenstein parts, same pattern as resistors. One
    // device per dielectric (non-polarised film/ceramic vs polarised
    // electrolytic). The displayed capacitance flows through the per-
    // instance Value ATTR override; the device entry carries placeholder
    // LCSC data so the PCB router has something to route, AND a 3D model
    // reference so the EasyEDA Pro 3D viewer renders proper cap bodies.
    //
    // Symbol templates extracted from the May 2026 Capacitor_templates.epro:
    //   - MPP103J41LC407LC (10nF film cap)             -> capacitor.esym
    //   - ERJ1VM221F12OT   (220µF radial electrolytic) -> polarized-capacitor.esym
    //
    // Footprints (and matching 3D models) extracted verbatim from the
    // May 2026 Caps.epro -- these are SMALLER than the original templates'
    // footprints, sized to match real hobbyist parts (5mm-body radial
    // electrolytic; 5.5mm ceramic disc with 5mm pitch). The 3D model is
    // referenced by UUID in the device entry; EasyEDA's cloud library
    // resolves the actual 3D geometry at render time, so we don't need
    // to embed any new binary assets:
    //   - CAP-TH_BD5.0-P2.00-D0.8-FD -> polarized-capacitor.efoo
    //       + 3D model a6753ac7... (CAP-TH_D5.0xH7.0xP2.0)
    //   - CAP-TH_L5.5-W2.5-P5.00-D1.0 -> capacitor-film.efoo
    //       + 3D model c7c082d6... (CAP-TH_BD5.5-W2.5-P5.0)
    //
    // Device and symbol UUIDs are freshly minted; footprint and 3D model
    // UUIDs are the EasyEDA library's originals from the source .epros.
    private const string CapacitorDeviceUuid = "c1a2b3c4d5e6f70819a8b7c6d5e4f312";
    private const string CapacitorSymbolUuid = "0dbe2659d61a47bd9dc4e229a16eafdf";
    private const string CapacitorFootprintUuid = "c8adb17cc38c429eb6470ae3c3c53c10";
    private const string Capacitor3dModelUuid = "c7c082d623b445348079c63a4f33d608";

    private const string PolarizedCapacitorDeviceUuid = "c2b1a09f8e7d6c5b4a3928171605040e";
    private const string PolarizedCapacitorSymbolUuid = "9177ab84b26345e39a087faeba74d4d2";
    private const string PolarizedCapacitorFootprintUuid = "d1a1cd74402d4157adb43869f0cba48d";
    private const string PolarizedCapacitor3dModelUuid = "a6753ac7f17a4fce96dd233a0f76b154";

    // Diode (1N5819 Schottky, DO-41 axial through-hole). Symbol, footprint,
    // and 3D model sourced from the May 2026 Diode.epro (luJing variant,
    // LCSC C49318088). Default user-facing label is "1N5819" via
    // DiodeUnit.DefaultPartNumber; any other Schottky/silicon diode in a
    // DO-41 body fits this footprint so the user can override the label
    // ("1N4148", "1N4007", etc.) and the placeholder MPN goes along for
    // the ride.
    //
    // Pin convention mismatch: TTL Sim's DiodeUnit has pin 1 = anode
    // (left, "A") and pin 2 = cathode (right, "K"). The EDA symbol has
    // it REVERSED: NUMBER="1" is the cathode on the left side and
    // NUMBER="2" is the anode on the right. The swap is handled in
    // DiodePart's PinLocalPositions (below), not by editing the .esym.
    private const string DiodeDeviceUuid = "02d66298da854e5d826c8864173f84aa";
    private const string DiodeSymbolUuid = "0fff7573b4cb40f0b1ca3171fa47cdbb";
    private const string DiodeFootprintUuid = "b5bb679d28f74cf3aa33ec57a9f03b89";
    private const string Diode3dModelUuid = "39d8e4572d564e62a86d2224fc908dd8";

    // ---------------------------------------------------- DIP-8 ICs
    //
    // DIP-8 chips export as a single-PART symbol drawn as the DIP box,
    // matching what ChipUnit draws on the canvas. One shared footprint
    // (dip-8.efoo, the DIP-8_L9.8-W6.6-P2.54 body) is reused by every
    // DIP-8 chip. One shared symbol template (dip-8.esym) is reused too:
    // pin coordinates, body rectangle, the pin-1 indicator dot, and the
    // 1..8 pin NUMBERs are all fixed by the package, so only the pin
    // NAMEs, the part title, and the displayed symbol name change per
    // chip. Those flow in via SymbolTemplateTokens (see BuildDip8Part).
    //
    // UUIDs are lifted verbatim from each chip's real EasyEDA library
    // entry (same approach as LED / VCC / GND / headers): the footprint
    // UUID is the shared DIP-8 body; the symbol and device UUIDs are the
    // chip-specific library entries, so EasyEDA's "Associate footprint
    // automatically" succeeds via the Supplier / Supplier Part /
    // Manufacturer Part fields carried in device_fragments.json.
    //
    // The footprint UUID is shared across all DIP-8 chips.
    internal const string Dip8FootprintUuid = "8b2a17fc29ee4e14bd39b38b48aed4a0";

    // One row per supported DIP-8 chip, keyed by ChipPartDefinition.PartNumber.
    // To add a new DIP-8 chip:
    //   1. Add a row here with the chip's library Device + Symbol UUIDs.
    //   2. Add the matching device + symbol fragments to
    //      device_fragments.json (lifted from a reference .epro export
    //      of that chip), bound to those UUIDs and to Dip8FootprintUuid.
    // The pin NAMEs come from the ChipPartDefinition itself, so they
    // don't need to be repeated here.
    private sealed record Dip8Chip(
        string PartNumber,   // matches ChipPartDefinition.PartNumber, e.g. "NE555"
        string SymbolName,   // displayed name -- the device's Manufacturer Part, e.g. "NE555N"
        string DeviceUuid,
        string SymbolUuid);

    private static readonly Dictionary<string, Dip8Chip> Dip8Chips = new()
    {
        ["NE555"] = new Dip8Chip(
            PartNumber: "NE555",
            SymbolName: "NE555N",
            DeviceUuid: "cf60d3b4e80f4567ad60bdfda7d0b00e",
            SymbolUuid: "3d01f6582f3b40059b1d912cd60530a4"),
    };

    // ---------------------------------------------------- DIP-14 ICs
    //
    // DIP-14 single-unit chips (NE556, 7474, 74107, 74393 -- the box-drawn
    // ChipPartDefinitions, NOT the gate IcPartDefinitions) export as a
    // single-PART symbol drawn as the DIP box, matching ChipUnit.
    //
    // Unlike DIP-8, we do NOT carry per-chip EasyEDA library entries. The
    // doc (EasyEDA_Export.md §2) notes the LCSC / Manufacturer / datasheet
    // fields are decorative, and §5 establishes the template approach: one
    // shared .esym cloned per chip, with the per-chip text substituted in.
    // So every DIP-14 chip is synthesised from a single shared template:
    //
    //   - One shared footprint (dip-14.efoo, the PDIP-14 body, verbatim
    //     from the 74HC393 reference). Looked up from device_fragments.json
    //     by Dip14FootprintUuid -- shared across all DIP-14 chips.
    //   - One shared .esym template (dip-14.esym), cloned per chip type:
    //     pin coordinates, body rectangle, pin-1 dot, and the 1..14 pin
    //     NUMBERs are fixed by the package; only the pin NAMEs, part title,
    //     and displayed symbol name vary, via SymbolTemplateTokens.
    //   - Per-chip-type device + symbol entries synthesised INLINE (the
    //     resistor InlineDeviceJson / InlineSymbolJson mechanism), with
    //     DETERMINISTIC UUIDs derived from the chip's full part number so
    //     re-exporting the same schematic is byte-stable (doc §7).
    //
    // Why per-chip-type symbol UUIDs (not one shared symbol): the exporter
    // and manifest dedup by SymbolUuid, and the pin NAMEs are baked into
    // each clone -- so two different chips must have different symbol UUIDs
    // or only the first chip's drawing would be emitted. The footprint is
    // genuinely identical so it IS shared; the device must bind to its own
    // symbol, so it's per-chip-type too. Both UUIDs are derived, not
    // hand-supplied: no per-chip library data is needed.
    //
    // The displayed chip name comes from each synthesised device's
    // Name = "={Manufacturer Part}" template, where Manufacturer Part is
    // set to the chip's FullPartNumber ("74HC393", "74HC74", "NE556", ...).
    // EmitTemplatedName on the CataloguePart makes the sheet emit the
    // template-fallback Name ATTR, exactly as DIP-8 does.
    internal const string Dip14FootprintUuid = "f8db98e4ac5c41b3b7fa99d6d16d6531";

    // The 74HC393 reference device entry -- used as the ATTRIBUTE TEMPLATE
    // for every synthesised DIP-14 device. The decorative fields (LCSC,
    // manufacturer, datasheet, 3D model) are carried verbatim from the
    // reference so the PCB router and 3D viewer have something to chew on;
    // only Manufacturer Part, Name, Symbol, Designator, and Footprint are
    // overwritten per chip. The 3D Model UUID is the PDIP-14 model from the
    // reference, shared across all DIP-14 chips (same body).
    private const string Dip14ReferenceSupplierPart = "C5329";
    private const string Dip14ReferenceManufacturer = "TI";
    private const string Dip14Reference3dModelUuid = "5399e5dcffba4e4c87798df762be2192";
    private const string Dip14Reference3dModelTransform =
        "751.967,309.842,0,0,0,0,0,0,-118.11";

    // ---------------------------------------------------- DIP-16 ICs
    //
    // Identical scheme to DIP-14 (see above): single-PART box symbol, one
    // shared footprint (dip-16.efoo, the DIP-16 body, verbatim from the
    // 74HC163 reference), one shared .esym template (dip-16.esym) cloned
    // per chip via SymbolTemplateTokens, and per-chip-type device + symbol
    // entries synthesised inline with deterministic UUIDs. No per-chip
    // EasyEDA library data is needed; pin names come from each chip's
    // ChipPartDefinition. Covers the box-drawn 16-pin ChipPartDefinitions
    // (390, 173, 175, 161, 163, 193, 595, 138, 139, 151, 153, 157, 257,
    // 47, 48, etc.) -- NOT the gate IcPartDefinitions.
    //
    // The .esym is drawn at 20px pin pitch (matching ChipUnit's PinPitch)
    // so the router's pins line up exactly and no diagonal wires appear --
    // the same pitch fix applied to DIP-8/DIP-14.
    internal const string Dip16FootprintUuid = "78717fccd6aa4a3b893611519dd3f0bc";

    // 74HC163 reference device attributes, used as the shared template for
    // every synthesised DIP-16 device (decorative per EasyEDA_Export.md §2).
    // The 3D Model is the DIP-16 model, shared across all DIP-16 chips.
    private const string Dip16ReferenceSupplierPart = "C2893";
    private const string Dip16ReferenceManufacturer = "TI";
    private const string Dip16Reference3dModelUuid = "c24a9876abec4714888ca3e3d8d19859";
    private const string Dip16Reference3dModelTitle =
        "DIP-16_L20.0-W6.4-H3.5-LS7.62-P2.54";
    private const string Dip16Reference3dModelTransform =
        "787.4,311.8104,0,0,0,0,-0.003,0.001,-118.11";

    // ---------------------------------------------------- DIP-18 ICs
    //
    // Identical scheme to DIP-14 / DIP-16 / DIP-20: single-PART box symbol,
    // one shared footprint (dip-18.efoo, the 0.3" DIP-18 body -- 7.62 mm
    // lead spacing -- lifted verbatim from the DIP-18.epro reference, a
    // PA1517G-D18-T placement), one shared .esym template (dip-18.esym)
    // cloned per chip via SymbolTemplateTokens, and per-chip-type device +
    // symbol entries synthesised inline with deterministic UUIDs. No
    // per-chip EasyEDA library data is needed; pin names come from each
    // chip's ChipPartDefinition. Covers the box-drawn 18-pin
    // ChipPartDefinitions (2114, ...) -- first user is the 2114 1Kx4 SRAM.
    //
    // The .esym is drawn at 20px pin pitch (matching ChipUnit's PinPitch)
    // so the router's pins line up exactly -- the same pitch fix applied to
    // DIP-8/DIP-14/DIP-16/DIP-20. NOTE: the reference save also contained a
    // BLOB (.eblob) with the 3D model mesh -- that is an artefact of
    // EasyEDA's Save-As-Local (offline snapshot), NOT part of the export
    // pattern. As with every other part, only the 3D Model UUID / Title /
    // Transform attributes ship; EasyEDA resolves the mesh from its cloud
    // library by UUID on import.
    internal const string Dip18FootprintUuid = "dcb37c45a02445ac";

    // PA1517G-D18-T reference device attributes, used as the shared template
    // for every synthesised DIP-18 device (decorative per EasyEDA_Export.md
    // §2). The 3D Model is the DIP-18 model, shared across all DIP-18 chips.
    private const string Dip18ReferenceSupplierPart = "C127026";
    private const string Dip18ReferenceManufacturer = "UTC";
    private const string Dip18Reference3dModelUuid = "f4d2489ed27243178ef9432c3b3a46b5";
    private const string Dip18Reference3dModelTitle =
        "DIP-18_L23.0-W6.5-H3.5-LS7.62-P2.54";
    private const string Dip18Reference3dModelTransform =
        "914.959,311.8104,0,0,0,0,-1.654,-5.906,-118.11";

    // ---------------------------------------------------- DIP-20 ICs
    //
    // Identical scheme to DIP-14 / DIP-16: single-PART box symbol, one
    // shared footprint (dip-20.efoo, the PDIP-20 body, lifted verbatim from
    // a 74HC273 reference .epro), one shared .esym template (dip-20.esym)
    // cloned per chip via SymbolTemplateTokens, and per-chip-type device +
    // symbol entries synthesised inline with deterministic UUIDs. No
    // per-chip EasyEDA library data is needed; pin names come from each
    // chip's ChipPartDefinition. Covers the box-drawn 20-pin
    // ChipPartDefinitions (273, 373, 377, 574, ...) -- NOT the gate
    // IcPartDefinitions.
    //
    // The .esym is drawn at 20px pin pitch (matching ChipUnit's PinPitch)
    // so the router's pins line up exactly -- the same pitch fix applied to
    // DIP-8/DIP-14/DIP-16.
    internal const string Dip20FootprintUuid = "890f22ce76a55c8f";

    // 74HC273 reference device attributes, used as the shared template for
    // every synthesised DIP-20 device (decorative per EasyEDA_Export.md §2).
    // The 3D Model is the PDIP-20 model, shared across all DIP-20 chips.
    private const string Dip20ReferenceSupplierPart = "C5238";
    private const string Dip20ReferenceManufacturer = "TI";
    private const string Dip20Reference3dModelUuid = "c925b2ccd25b4584ba0148e94a8429d8";
    private const string Dip20Reference3dModelTitle =
        "DIP-20_L26.2-W6.4-H5.4-LS7.62-P2.54";
    private const string Dip20Reference3dModelTransform =
        "1031.494,315.7474,0,0,0,0,0,0,-118.11";

    // ---------------------------------------------------- TO-92 (3-pin) parts
    //
    // Generic 3-pin TO-92 path for any ChipPartDefinition that opts in via
    // To92 (e.g. the DS1813 reset supervisor). UNLIKE the DIP parts, the
    // canvas symbol (To92Unit) is NOT a left/right box -- it draws three
    // legs along the BOTTOM edge pointing down, centred at the DIP pitch.
    // The export catalogue therefore uses a bespoke symbol (to92-3.esym,
    // authored to match To92Unit: three pins down at 20px pitch) and a
    // matching PinLocalPositions row -- the export placement math is
    // pin-direction-agnostic (it uses pin world positions + local offsets,
    // never PinDirection), so a down-pin part rides the same ComputePlacement
    // as everything else.
    //
    // Footprint (to92-3.efoo) and 3D model are lifted verbatim from a DS1813
    // TO-92-3 reference .epro; both are shared across all 3-pin TO-92 parts
    // (same body). Per-chip-type device + symbol entries are synthesised
    // inline with deterministic UUIDs, exactly like the DIP-14/16/20 parts.
    // The displayed name comes from each chip's FullPartNumber via the
    // device template Name = "={Manufacturer Part}".
    internal const string To92FootprintUuid = "5f194b391a8afb9b";

    // DS1813 TO-92-3 reference device attributes, used as the shared
    // (decorative, per EasyEDA_Export.md §2) template for every synthesised
    // TO-92 device. The 3D Model is the shared TO-92-3 body.
    private const string To92ReferenceSupplierPart = "C1354106";
    private const string To92ReferenceManufacturer = "Maxim";
    private const string To92Reference3dModelUuid = "86c9a28785b84e52859c2af3e4e264a5";
    private const string To92Reference3dModelTitle = "TO-92-3_L4.9-W3.7-P1.27-L";
    private const string To92Reference3dModelTransform =
        "176.28752,144.88554,0,180,0,0,0,20.669,-196.85";

    // ---------------------------------------------------- pre-built entries

    private static readonly CataloguePart LedPart = new(
        DeviceUuid: LedDeviceUuid,
        SymbolUuid: LedSymbolUuid,
        SymbolResourceName: "led.esym",
        FootprintUuid: LedFootprintUuid,
        FootprintResourceName: "led.efoo",
        PartTitle: "LED.1",
        PinLocalPositions: new()
        {
            // Mirrored led.esym: NUMBER=1 (anode, NAME="A") at left (-20, 0);
            // NUMBER=2 (cathode, NAME="K") at right (+20, 0). Matches TTLSim's
            // pin convention (1=anode-left, 2=cathode-right), so no swap.
            [1] = new Point(-20, 0),
            [2] = new Point(+20, 0),
        },
        EmitNameOverride: true,
        // Hand-tuned in EasyEDA, read back from
        // LEDs_positions_tweaked_in_EDA.epro. Value entries unused (LED
        // doesn't emit Value); zero them.
        LabelOffsets: new LabelOffsetsByRotation(
            Rot0: new LabelOffsetSet(new(-10, +20), new(-10, +10), default),
            Rot90: new LabelOffsetSet(new(-20, +5), new(-20, -5), default),
            Rot180: new LabelOffsetSet(new(-20, +20), new(-20, +10), default),
            Rot270: new LabelOffsetSet(new(-25, -5), new(-25, -15), default)));

    // VCC: single pin at the origin.
    private static readonly CataloguePart VccPart = new(
        DeviceUuid: VccDeviceUuid,
        SymbolUuid: VccSymbolUuid,
        SymbolResourceName: "vcc.esym",
        FootprintUuid: null,
        FootprintResourceName: null,
        PartTitle: "Power-VCC.1",
        PinLocalPositions: new() { [0] = new Point(0, 0) },
        IsNetFlag: true,
        NetName: "VCC");

    // GND: single pin at the origin.
    private static readonly CataloguePart GndPart = new(
        DeviceUuid: GndDeviceUuid,
        SymbolUuid: GndSymbolUuid,
        SymbolResourceName: "gnd.esym",
        FootprintUuid: null,
        FootprintResourceName: null,
        PartTitle: "Ground-GND.1",
        PinLocalPositions: new() { [0] = new Point(0, 0) },
        IsNetFlag: true,
        NetName: "GND");

    // Can oscillator (full-can, DIP-14). A real 4-pin component (not a net
    // flag): own symbol + real OSC-TH_4P footprint + 3D. Pin locals match the
    // authored tall symbol AND the CanOscillator canvas unit (corner pins
    // 1=EOH/NC, 7=GND, 8=Output, 14=VCC). Because it is a standalone
    // SchematicItem rather than a Device, EmitComponent currently emits its
    // designator as "?" -- annotate in EasyEDA after import.
    private static readonly CataloguePart CanOscDip14Part = new(
        DeviceUuid: CanOscDip14DeviceUuid,
        SymbolUuid: CanOscDip14SymbolUuid,
        SymbolResourceName: "osc-dip14.esym",
        FootprintUuid: CanOscDip14FootprintUuid,
        FootprintResourceName: "osc-dip14.efoo",
        PartTitle: "Can-Oscillator-DIP14.1",
        PinLocalPositions: new()
        {
            [1] = new Point(-50, +60),   // EOH / NC  (top-left)
            [7] = new Point(-50, -60),   // GND       (bottom-left)
            [8] = new Point(+50, -60),   // Output    (bottom-right)
            [14] = new Point(+50, +60),  // VCC       (top-right)
        },
        // Designator sits above the tall can body (top edge at +60). Cosmetic;
        // tune in the §9 round-trip.
        LabelOffsets: new LabelOffsetsByRotation(
            Rot0: new LabelOffsetSet(new(-50, +75), new(-10, +75), default),
            Rot90: new LabelOffsetSet(new(+75, 0), new(+85, 0), default),
            Rot180: new LabelOffsetSet(new(-50, +75), new(-10, +75), default),
            Rot270: new LabelOffsetSet(new(+75, 0), new(+85, 0), default)));

    // Half-can oscillator (DIP-8). Corners identical to the full can; only the
    // pin numbers (1=EOH/NC, 4=GND, 5=Output, 8=VCC) and the shorter body
    // differ. Pin locals match the authored symbol AND the CanOscillatorDip8
    // canvas unit. Same standalone "?" designator caveat as the DIP-14.
    private static readonly CataloguePart CanOscDip8Part = new(
        DeviceUuid: CanOscDip8DeviceUuid,
        SymbolUuid: CanOscDip8SymbolUuid,
        SymbolResourceName: "osc-dip8.esym",
        FootprintUuid: CanOscDip8FootprintUuid,
        FootprintResourceName: "osc-dip8.efoo",
        PartTitle: "Can-Oscillator-DIP8.1",
        PinLocalPositions: new()
        {
            [1] = new Point(-50, +30),   // EOH / NC  (top-left)
            [4] = new Point(-50, -30),   // GND       (bottom-left)
            [5] = new Point(+50, -30),   // Output    (bottom-right)
            [8] = new Point(+50, +30),   // VCC       (top-right)
        },
        // Designator above the shorter can body (top edge at +30).
        LabelOffsets: new LabelOffsetsByRotation(
            Rot0: new LabelOffsetSet(new(-50, +45), new(-10, +45), default),
            Rot90: new LabelOffsetSet(new(+45, 0), new(+55, 0), default),
            Rot180: new LabelOffsetSet(new(-50, +45), new(-10, +45), default),
            Rot270: new LabelOffsetSet(new(+45, 0), new(+55, 0), default)));

    // ---------------------------------------------------- pin headers
    //
    // 2.54 mm pitch, 1xN female headers, vertical. Pin local positions
    // match the .esym files verbatim: pins on the LEFT edge of the
    // symbol (x = -15 for 2/8-pin, x = -20 for 4/6-pin), with pin 1 at
    // the top and pins descending in Y by 10 EasyEDA units (= 1
    // TTL Sim grid cell). The PartTitle suffix ".1" names the first
    // (and only) PART in each .esym.
    //
    // MatchesEasyEdaRotationSense: true -- header pins use SwapR90R270
    // in their Pin objects so TTL Sim's canvas already shows them with
    // EasyEDA's rotation sense. The exporter's default (360 - r) % 360
    // shim is designed to undo the *other* parts' opposite-sense
    // rotation; applying it to headers would re-invert and ship a
    // wrong-rotation .epro.
    //
    // LabelOffsets were measured from a hand-placed reference
    // (Headers.epro upload, May 2026): each entry is the offset of the
    // Designator ATTR's world position from the COMPONENT's world
    // position, per rotation. EasyEDA writes these as absolute
    // post-rotation offsets, so they're consumed by EmitComponent
    // without further rotation. Name and Value offsets are unused by
    // headers (no Value, Name is the bookkeeping ATTR blanked to "").

    private static readonly Dictionary<int, Point> Header2PinLocals = new()
    {
        // Male HX PH254-01-02: pins at (-20, +5) and (-20, -5).
        [1] = new Point(-20, +5),
        [2] = new Point(-20, -5),
    };

    // Pins match hdr-out-3.esym verbatim: left edge at x=-15, pin 1 top,
    // descending by 10 EDA units (= 1 TTL Sim grid cell). Only the relative
    // Y spacing must match the canvas; the constant x just shifts the anchor.
    private static readonly Dictionary<int, Point> Header3PinLocals = new()
    {
        [1] = new Point(-15, +10),
        [2] = new Point(-15, 0),
        [3] = new Point(-15, -10),
    };

    private static readonly Dictionary<int, Point> Header4PinLocals = new()
    {
        [1] = new Point(-20, +15),
        [2] = new Point(-20, +5),
        [3] = new Point(-20, -5),
        [4] = new Point(-20, -15),
    };

    private static readonly Dictionary<int, Point> Header6PinLocals = new()
    {
        [1] = new Point(-20, +25),
        [2] = new Point(-20, +15),
        [3] = new Point(-20, +5),
        [4] = new Point(-20, -5),
        [5] = new Point(-20, -15),
        [6] = new Point(-20, -25),
    };

    private static readonly Dictionary<int, Point> Header8PinLocals = new()
    {
        // Male HX PZ2.54-1x8P ZZ: pins at (-20, ±35), (-20, ±25), (-20, ±15), (-20, ±5).
        [1] = new Point(-20, +35),
        [2] = new Point(-20, +25),
        [3] = new Point(-20, +15),
        [4] = new Point(-20, +5),
        [5] = new Point(-20, -5),
        [6] = new Point(-20, -15),
        [7] = new Point(-20, -25),
        [8] = new Point(-20, -35),
    };

    private static readonly CataloguePart Header2Part = new(
        DeviceUuid: Header2DeviceUuid,
        SymbolUuid: Header2SymbolUuid,
        SymbolResourceName: "hdr-out-2.esym",
        FootprintUuid: Header2FootprintUuid,
        FootprintResourceName: "hdr-out-2.efoo",
        PartTitle: "Header-Male-2.54_1x2.1",
        PinLocalPositions: Header2PinLocals,
        // Name offset = Designator offset + (+10, 0) -- name sits one
        // EasyEDA cell to the right of the designator, same Y, matching
        // the hand-edited reference (Headers_fixed_in_EDA.epro, May 2026).
        LabelOffsets: new LabelOffsetsByRotation(
            Rot0: new LabelOffsetSet(new(-5, +15), new(+5, +15), default),
            Rot90: new LabelOffsetSet(new(+20, +5), new(+30, +5), default),
            Rot180: new LabelOffsetSet(new(-15, +15), new(-5, +15), default),
            Rot270: new LabelOffsetSet(new(+20, -5), new(+30, -5), default)),
        EmitNameOverride: true,
        NameLabelUsesDesignatorStyle: true,
        MatchesEasyEdaRotationSense: true);

    private static readonly CataloguePart Header3Part = new(
        DeviceUuid: Header3DeviceUuid,
        SymbolUuid: Header3SymbolUuid,
        SymbolResourceName: "hdr-out-3.esym",
        FootprintUuid: Header3FootprintUuid,
        FootprintResourceName: "hdr-out-3.efoo",
        PartTitle: "Header-Male-2.54_1x3.1",   // matches the .esym's PART id
        PinLocalPositions: Header3PinLocals,
        // Label offsets between the 2-pin and 4-pin values (body height sits
        // between them). Cosmetic -- tune in the §9 round-trip if needed.
        LabelOffsets: new LabelOffsetsByRotation(
            Rot0: new LabelOffsetSet(new(-10, +20), new(0, +20), default),
            Rot90: new LabelOffsetSet(new(+25, 0), new(+35, 0), default),
            Rot180: new LabelOffsetSet(new(-10, +20), new(0, +20), default),
            Rot270: new LabelOffsetSet(new(+25, 0), new(+35, 0), default)),
        EmitNameOverride: true,
        NameLabelUsesDesignatorStyle: true,
        MatchesEasyEdaRotationSense: true);

    private static readonly CataloguePart Header4Part = new(
        DeviceUuid: Header4DeviceUuid,
        SymbolUuid: Header4SymbolUuid,
        SymbolResourceName: "hdr-out-4.esym",
        FootprintUuid: Header4FootprintUuid,
        FootprintResourceName: "hdr-out-4.efoo",
        PartTitle: "Header-Male-2.54_1x4.1",
        PinLocalPositions: Header4PinLocals,
        LabelOffsets: new LabelOffsetsByRotation(
            Rot0: new LabelOffsetSet(new(-10, +25), new(0, +25), default),
            Rot90: new LabelOffsetSet(new(+30, 0), new(+40, 0), default),
            Rot180: new LabelOffsetSet(new(-10, +25), new(0, +25), default),
            Rot270: new LabelOffsetSet(new(+30, 0), new(+40, 0), default)),
        EmitNameOverride: true,
        NameLabelUsesDesignatorStyle: true,
        MatchesEasyEdaRotationSense: true);

    private static readonly CataloguePart Header6Part = new(
        DeviceUuid: Header6DeviceUuid,
        SymbolUuid: Header6SymbolUuid,
        SymbolResourceName: "hdr-out-6.esym",
        FootprintUuid: Header6FootprintUuid,
        FootprintResourceName: "hdr-out-6.efoo",
        PartTitle: "Header-Male-2.54_1x6.1",
        PinLocalPositions: Header6PinLocals,
        LabelOffsets: new LabelOffsetsByRotation(
            Rot0: new LabelOffsetSet(new(-10, +35), new(0, +35), default),
            Rot90: new LabelOffsetSet(new(+40, 0), new(+50, 0), default),
            Rot180: new LabelOffsetSet(new(-10, +35), new(0, +35), default),
            Rot270: new LabelOffsetSet(new(+40, 0), new(+50, 0), default)),
        EmitNameOverride: true,
        NameLabelUsesDesignatorStyle: true,
        MatchesEasyEdaRotationSense: true);

    private static readonly CataloguePart Header8Part = new(
        DeviceUuid: Header8DeviceUuid,
        SymbolUuid: Header8SymbolUuid,
        SymbolResourceName: "hdr-out-8.esym",
        FootprintUuid: Header8FootprintUuid,
        FootprintResourceName: "hdr-out-8.efoo",
        PartTitle: "Header-Male-2.54_1x8.1",
        PinLocalPositions: Header8PinLocals,
        LabelOffsets: new LabelOffsetsByRotation(
            Rot0: new LabelOffsetSet(new(-5, +45), new(+5, +45), default),
            Rot90: new LabelOffsetSet(new(+50, +5), new(+60, +5), default),
            Rot180: new LabelOffsetSet(new(-15, +45), new(-5, +45), default),
            Rot270: new LabelOffsetSet(new(+50, -5), new(+60, -5), default)),
        EmitNameOverride: true,
        NameLabelUsesDesignatorStyle: true,
        MatchesEasyEdaRotationSense: true);

    // ---------------------------------------------------- switch / pushbutton
    //
    // SS11-RBDWQ-R20-R rocker switch and SKPMAPE010 tactile pushbutton:
    // schematic symbol taken verbatim from the EasyEDA library, but with
    // the H1 male 2.54mm 1x2P through-hole header footprint substituted
    // for the part's "real" footprint (the rocker's panel-mount and the
    // tactile's SMD pads). Rationale: TTL Sim treats switches and buttons
    // as off-board human-input devices that connect to the PCB via a
    // 2-pin header; the schematic still reads as a switch/button so the
    // intent is clear in the captured drawing.
    //
    // Pin local positions come from the .esym files verbatim. The switch
    // symbol's pin endpoints are at (-30, 0) and (+30, 0); the button's
    // are at (-30, -10) and (+30, -10) -- the button's body sits 10 EDA
    // units below its origin, so the pins do too. TTL Sim's canvas places
    // both units with pins at canvas-local Y=1, so the pin world position
    // the exporter calls with is the same in both cases -- the negative Y
    // in the button's local just means the exporter will anchor the
    // COMPONENT 10 units above where the pin actually lands, and the
    // symbol drawing inside that anchor renders downward to meet it.
    //
    // PinDirection is Left/Right on the units (like resistor/LED), so
    // MatchesEasyEdaRotationSense stays at its default false -- the
    // exporter's standard (360 - r) % 360 rotation shim applies.

    private static readonly CataloguePart SwitchPart = new(
        DeviceUuid: SwitchDeviceUuid,
        SymbolUuid: SwitchSymbolUuid,
        SymbolResourceName: "switch.esym",
        FootprintUuid: Hdr2MaleFootprintUuid,
        FootprintResourceName: "hdr-th-2-male.efoo",
        PartTitle: "SS11-RBDWQ-R20-R.1",
        PinLocalPositions: new()
        {
            // switch.esym pin endpoints: PIN e3 (NUMBER=1) at (-30, 0),
            // PIN e7 (NUMBER=2) at (+30, 0). TTL Sim's SwitchUnit pin 1
            // is on the left, pin 2 on the right, matching.
            [1] = new Point(-30, 0),
            [2] = new Point(+30, 0),
        },
        EmitNameOverride: true,
        // Initial offsets only -- expected to be hand-tuned in EasyEDA
        // after the first export, same workflow as resistors and LEDs.
        // Body BBOX is roughly (-13.5 .. +13.5, -3.5 .. +9.5).
        LabelOffsets: new LabelOffsetsByRotation(
            Rot0: new LabelOffsetSet(new(-10, +20), new(-10, +10), default),
            Rot90: new LabelOffsetSet(new(-25, +5), new(-25, -5), default),
            Rot180: new LabelOffsetSet(new(-10, +20), new(-10, +10), default),
            Rot270: new LabelOffsetSet(new(-25, -5), new(-25, -15), default)));

    private static readonly CataloguePart ButtonPart = new(
        DeviceUuid: ButtonDeviceUuid,
        SymbolUuid: ButtonSymbolUuid,
        SymbolResourceName: "button.esym",
        FootprintUuid: Hdr2MaleFootprintUuid,
        FootprintResourceName: "hdr-th-2-male.efoo",
        PartTitle: "SKPMAPE010.1",
        PinLocalPositions: new()
        {
            // button.esym pin endpoints: PIN e3 (NUMBER=1) at (-30, -10),
            // PIN e7 (NUMBER=2) at (+30, -10). TTL Sim's ButtonUnit pin 1
            // is on the left, pin 2 on the right, matching. The Y=-10
            // anchors the COMPONENT above the pin row so the symbol body
            // (which extends downward from its origin) renders on the
            // expected canvas cells.
            [1] = new Point(-30, -10),
            [2] = new Point(+30, -10),
        },
        EmitNameOverride: true,
        // Initial offsets only -- expected to be hand-tuned in EasyEDA
        // after the first export. Body BBOX is roughly (-20.5 .. +20.5,
        // -12.5 .. +10.5) so labels need wider clearance than the switch.
        LabelOffsets: new LabelOffsetsByRotation(
            Rot0: new LabelOffsetSet(new(-10, +20), new(-10, +10), default),
            Rot90: new LabelOffsetSet(new(-30, +5), new(-30, -5), default),
            Rot180: new LabelOffsetSet(new(-10, +20), new(-10, +10), default),
            Rot270: new LabelOffsetSet(new(-30, -5), new(-30, -15), default)));

    private static readonly CataloguePart Button4Part = new(
        DeviceUuid: Button4DeviceUuid,
        SymbolUuid: Button4SymbolUuid,
        SymbolResourceName: "button4.esym",
        FootprintUuid: Button4FootprintUuid,
        FootprintResourceName: "button4.efoo",
        PartTitle: "YZA-057-4.5.1",
        PinLocalPositions: new()
        {
            // button4.esym tips (EasyEDA Y-up): terminal A (pins 1,2) on the
            // LEFT, terminal B (pins 3,4) on the RIGHT.
            [1] = new Point(-30, 10),
            [2] = new Point(-30, -10),
            [3] = new Point(30, 10),
            [4] = new Point(30, -10),
        },
        EmitNameOverride: true,
        LabelOffsets: new LabelOffsetsByRotation(
            Rot0: new LabelOffsetSet(new(-10, +25), new(-10, +15), default),
            Rot90: new LabelOffsetSet(new(-30, +5), new(-30, -5), default),
            Rot180: new LabelOffsetSet(new(-10, +25), new(-10, +15), default),
            Rot270: new LabelOffsetSet(new(-30, -5), new(-30, -15), default)));

    // 2-pin jumper: identical canvas geometry to the SPST switch (both are
    // SwitchUnit -- pins 1/2 horizontal, left/right), so it reuses the same
    // (-30,0)/(+30,0) locals and the male 2-pin header footprint. Own
    // jumper-2.esym symbol (a header-style box) so it reads as a link.
    private static readonly CataloguePart Jumper2Part = new(
        DeviceUuid: Jumper2DeviceUuid,
        SymbolUuid: Jumper2SymbolUuid,
        SymbolResourceName: "jumper-2.esym",
        FootprintUuid: Hdr2MaleFootprintUuid,
        FootprintResourceName: "hdr-th-2-male.efoo",
        PartTitle: "Jumper-2P.1",
        PinLocalPositions: new()
        {
            // jumper-2.esym endpoints; match SwitchUnit pins 1 (left), 2 (right).
            [1] = new Point(-30, 0),
            [2] = new Point(+30, 0),
        },
        EmitNameOverride: true,
        LabelOffsets: new LabelOffsetsByRotation(
            Rot0: new LabelOffsetSet(new(-10, +20), new(-10, +10), default),
            Rot90: new LabelOffsetSet(new(-25, +5), new(-25, -5), default),
            Rot180: new LabelOffsetSet(new(-10, +20), new(-10, +10), default),
            Rot270: new LabelOffsetSet(new(-25, -5), new(-25, -15), default)));

    // 3-pin jumper: SpdtSwitchUnit jumper form -- inline pins, COM (pin 2)
    // tapped down from the centre, throws at the two ends. Locals match that
    // canvas layout; reuses the male 3-pin header footprint.
    private static readonly CataloguePart Jumper3Part = new(
        DeviceUuid: Jumper3DeviceUuid,
        SymbolUuid: Jumper3SymbolUuid,
        SymbolResourceName: "jumper-3.esym",
        FootprintUuid: Header3FootprintUuid,
        FootprintResourceName: "hdr-out-3.efoo",
        PartTitle: "Jumper-3P.1",
        PinLocalPositions: new()
        {
            // jumper-3.esym endpoints; match SpdtSwitchUnit jumper form:
            // pin 1 left, pin 2 COM tapped down-centre, pin 3 right.
            [1] = new Point(-60, +5),
            [2] = new Point(0, -5),
            [3] = new Point(+60, +5),
        },
        EmitNameOverride: true,
        LabelOffsets: new LabelOffsetsByRotation(
            Rot0: new LabelOffsetSet(new(-10, +20), new(0, +20), default),
            Rot90: new LabelOffsetSet(new(+25, 0), new(+35, 0), default),
            Rot180: new LabelOffsetSet(new(-10, +20), new(0, +20), default),
            Rot270: new LabelOffsetSet(new(+25, 0), new(+35, 0), default)));

    // SPDT switch: SpdtSwitchUnit switch form -- COM (pin 2) mid-left, throws
    // A/B (pins 1/3) top-right and bottom-right. Locals match that triangle;
    // reuses the male 3-pin header footprint (COM lands on the centre pad).
    private static readonly CataloguePart SpdtSwitchPart = new(
        DeviceUuid: SpdtSwitchDeviceUuid,
        SymbolUuid: SpdtSwitchSymbolUuid,
        SymbolResourceName: "spdt.esym",
        FootprintUuid: SpdtSwitchFootprintUuid,
        FootprintResourceName: "spdt-sw.efoo",
        PartTitle: "SPDT-Switch.1",
        PinLocalPositions: new()
        {
            // spdt.esym endpoints; match SpdtSwitchUnit switch form:
            // pin 1 throw-A (top-right), pin 2 COM (mid-left), pin 3 throw-B
            // (bottom-right).
            [1] = new Point(+30, +10),
            [2] = new Point(-30, 0),
            [3] = new Point(+30, -10),
        },
        EmitNameOverride: true,
        LabelOffsets: new LabelOffsetsByRotation(
            Rot0: new LabelOffsetSet(new(-10, +25), new(0, +25), default),
            Rot90: new LabelOffsetSet(new(+30, 0), new(+40, 0), default),
            Rot180: new LabelOffsetSet(new(-10, +25), new(0, +25), default),
            Rot270: new LabelOffsetSet(new(+30, 0), new(+40, 0), default)));

    private static readonly CataloguePart DiodePart = new(
        DeviceUuid: DiodeDeviceUuid,
        SymbolUuid: DiodeSymbolUuid,
        SymbolResourceName: "diode.esym",
        FootprintUuid: DiodeFootprintUuid,
        FootprintResourceName: "diode.efoo",
        PartTitle: "Diode.1",
        PinLocalPositions: new()
        {
            // PIN-CONVENTION SWAP. TTL Sim's DiodeUnit puts pin 1 (anode,
            // "A") on the LEFT and pin 2 (cathode, "K") on the RIGHT.
            // diode.esym has it REVERSED: NUMBER="1" is the cathode at
            // X=-20 and NUMBER="2" is the anode at X=+20 (per the source
            // .epro's 1N5819 symbol). So the mapping swaps:
            //   TTL Sim pin 1 (anode) -> EDA right pin  (+20, 0)
            //   TTL Sim pin 2 (cathode) -> EDA left pin (-20, 0)
            // The sheet writer drives wire endpoints from these positions,
            // so the swap puts the visible anode/cathode in the right
            // places in the exported schematic without needing to edit
            // the symbol file.
            [1] = new Point(+20, 0),
            [2] = new Point(-20, 0),
        },
        EmitNameOverride: true,
        EasyEdaRotationOffsetDeg: 180,
        // Initial offsets only -- expected to be hand-tuned in EasyEDA
        // after first export. The diode symbol's BBOX is roughly
        // (-10.5 .. +10.5, -7.5 .. +7.5) and includes the cathode bar
        // glyph at the left (the small notch around X=-5..-7, Y=±7).
        LabelOffsets: new LabelOffsetsByRotation(
            Rot0: new LabelOffsetSet(new(-10, +20), new(-10, +10), default),
            Rot90: new LabelOffsetSet(new(-20, +5), new(-20, -5), default),
            Rot180: new LabelOffsetSet(new(-10, +20), new(-10, +10), default),
            Rot270: new LabelOffsetSet(new(-20, -5), new(-20, -15), default)));


    // ---------------------------------------------------- public API

    public static CataloguePart LookupForDevice(Device device)
    {
        // Match against the canonical singletons in PassivePartDefinition.
        // Record value-equality means this works whether the device's
        // Definition is the singleton directly or a deserialised copy with
        // matching field values; ReferenceEquals would be safe too since
        // SchematicDtoMapper resolves into the singletons, but the value-
        // equality form is robust to future construction paths. The key
        // benefit over the previous `p.Identifier == "resistor"` form: if
        // the singleton is renamed (or removed), this becomes a compile
        // error rather than a runtime NotImplementedException.
        return device.Definition switch
        {
            PassivePartDefinition p when p == PassivePartDefinition.Resistor
                => BuildResistorPart(device),
            PassivePartDefinition p when p == PassivePartDefinition.ResistorNetwork
                => BuildResistorNetworkPart(device),
            PassivePartDefinition p when p == PassivePartDefinition.Capacitor
                => BuildCapacitorPart(device),
            PassivePartDefinition p when p == PassivePartDefinition.PolarizedCapacitor
                => BuildPolarizedCapacitorPart(device),
            PassivePartDefinition p when p == PassivePartDefinition.Led => LedPart,
            PassivePartDefinition p when p == PassivePartDefinition.Switch => SwitchPart,
            PassivePartDefinition p when p == PassivePartDefinition.Button => ButtonPart,
            PassivePartDefinition p when p == PassivePartDefinition.Button4 => Button4Part,
            PassivePartDefinition p when p == PassivePartDefinition.SpdtSwitch => SpdtSwitchPart,
            PassivePartDefinition p when p == PassivePartDefinition.Jumper2 => Jumper2Part,
            PassivePartDefinition p when p == PassivePartDefinition.Jumper3 => Jumper3Part,
            PassivePartDefinition p when p == PassivePartDefinition.Diode => DiodePart,

            HeaderPartDefinition h => h.PinCount switch
            {
                2 => Header2Part,
                3 => Header3Part,
                4 => Header4Part,
                6 => Header6Part,
                8 => Header8Part,
                _ => throw new NotImplementedException(
                    $"EasyEDA export: no catalogue entry for {h.PinCount}-pin header. " +
                    "Supported pin counts are 2, 3, 4, 6, 8."),
            },

            // TO-92 (To92 opt-in) is dispatched before the DIP pin-count arms:
            // it's a package/rendering choice, not a pin count, so a To92 part
            // must never fall into a DIP arm.
            ChipPartDefinition cp when cp.To92 => BuildTo92Part(device, cp),

            ChipPartDefinition cp when cp.PinCount == 8 => BuildDip8Part(cp),

            ChipPartDefinition cp when cp.PinCount == 14 => BuildDip14Part(device, cp),

            ChipPartDefinition cp when cp.PinCount == 16 => BuildDip16Part(device, cp),
            ChipPartDefinition cp when cp.PinCount == 18 => BuildDip18Part(device, cp),

            ChipPartDefinition cp when cp.PinCount == 20 => BuildDip20Part(device, cp),

            _ => throw new NotImplementedException(
                $"EasyEDA export: no catalogue entry for device {device.Designator} " +
                $"({device.Definition.Identifier}). Add a CataloguePart entry and " +
                "embedded .esym/.efoo resources before exporting this part.")
        };
    }

    public static CataloguePart LookupForStandaloneItem(SchematicItem item)
    {
        return item switch
        {
            VccSymbol => VccPart,
            GndSymbol => GndPart,
            // CanOscillatorDip8 derives from CanOscillator, so it must be
            // matched first -- otherwise the CanOscillator arm below swallows
            // it and hands it the DIP-14 pin map (no pin 4).
            CanOscillatorDip8 => CanOscDip8Part,
            CanOscillator => CanOscDip14Part,
            _ => throw new NotImplementedException(
                $"EasyEDA export: no catalogue entry for item of type " +
                $"{item.GetType().Name}. Add a CataloguePart entry and " +
                "embedded resource before exporting this item.")
        };
    }

    // ---------------------------------------------------- resistor synthesis
    //
    // The resistor catalogue is Frankenstein: every resistor instance
    // points at one shared EasyEDA library device, and the displayed
    // resistance text comes from a per-instance Value ATTR override
    // emitted by EasyEdaSheetWriter (see CataloguePart.ValueOverride).
    //
    // The placeholder LCSC part data (CR1/4W-1K, C2894660, VO) lets
    // EasyEDA Pro's PCB router accept the part, and pairs with a 3D
    // model so the 3D viewer shows a real axial resistor body. The BOM
    // then lists every resistor as that 1kΩ -- wrong, but harmless
    // because we don't use the BOM for production. Anyone who needs an
    // accurate BOM has to substitute the right MPNs in EasyEDA after
    // import.

    /// <summary>
    /// The value text for a passive: <see cref="Device.Value"/> -- the property
    /// the grid edits and the canvas draws beneath the designator -- falling
    /// back to the unit's Label for files that predate Device.Value, where the
    /// value was typed into the unit Label instead.
    /// </summary>
    private static string ReadPassiveValue(Device device)
    {
        string value = device.Value ?? "";
        if (!string.IsNullOrWhiteSpace(value)) return value;

        foreach (var u in device.Units)
            return u.Label ?? "";
        return "";
    }

    /// <summary>
    /// Build the value-failure exception for one passive, distinguishing
    /// "no value set" from "unparseable value". ExportValueException, NOT
    /// NotImplementedException: the part IS mapped for export; only its Value
    /// needs fixing, and MainForm headlines the two differently.
    /// </summary>
    private static ExportValueException ValueError(
        Device device, string kind, string label, FormatException ex, string supported)
    {
        string problem = string.IsNullOrWhiteSpace(label)
            ? "has no value. Set its Value in the property grid."
            : $"has an unparseable value '{label}'. {ex.Message}";
        return new ExportValueException(
            $"{kind} {device.Designator} {problem} {supported}");
    }

    private static CataloguePart BuildResistorPart(Device device)
    {
        string label = ReadPassiveValue(device);

        string displayValue;
        try
        {
            displayValue = ResistorValueParser.FormatForDisplay(label);
        }
        catch (FormatException ex)
        {
            throw ValueError(device, "resistor", label, ex,
                "Supported value forms: 100, 100R, 220Ω, 2k2, 2K2, 1.5K, 1M, 1M5.");
        }

        // Symbol template tokens: substitute the generic "Resistor" identifier
        // where the .esym template has @@PART_ID@@ and @@SYMBOL_NAME@@. The
        // displayed resistance is NOT baked into the symbol -- it flows
        // through the per-instance Value ATTR on the COMPONENT instead.
        var symbolTokens = new Dictionary<string, string>
        {
            ["@@PART_ID@@"] = "Resistor.1",
            ["@@SYMBOL_NAME@@"] = "Resistor",
        };

        return new CataloguePart(
            DeviceUuid: ResistorDeviceUuid,
            SymbolUuid: ResistorSymbolUuid,
            SymbolResourceName: "resistor.esym",
            FootprintUuid: ResistorFootprintUuid,
            FootprintResourceName: "resistor.efoo",
            PartTitle: "Resistor.1",
            PinLocalPositions: new()
            {
                // resistor.esym pins rescaled to ±30 (60px span) to match
                // ChipUnit/ResistorUnit's TTLSim pin span (Size.Width 6 ×
                // 10px). Previously ±20, which mismatched TTLSim's 60px and
                // produced diagonal wires at the far pin (same pitch bug as
                // the DIPs). NUMBER=1 at left, =2 at right; no swap.
                [1] = new Point(-30, 0),
                [2] = new Point(+30, 0),
            },
            EmitValueLabel: true,
            ValueOverride: displayValue,
            InlineDeviceJson: BuildResistorDeviceFragment(),
            InlineSymbolJson: BuildResistorSymbolFragment(),
            SymbolTemplateTokens: symbolTokens,
            // Hand-tuned in EasyEDA, read back from Adjusted_in_EDA.epro.
            // Resistor body is symmetric end-to-end so 0 == 180 and 90 == 270.
            // Name slot is the bookkeeping ATTR (empty string for resistor);
            // its offset is purely where the invisible "Name" record lives.
            LabelOffsets: new LabelOffsetsByRotation(
                Rot0: new LabelOffsetSet(new(-10, +10), new(-15, -20), new(-10, +5)),
                Rot90: new LabelOffsetSet(new(-20, 0), new(-15, -20), new(-20, -5)),
                Rot180: new LabelOffsetSet(new(-10, +10), new(-15, -20), new(-10, +5)),
                Rot270: new LabelOffsetSet(new(-20, 0), new(-15, -20), new(-20, -5))));
    }

    private static string BuildResistorDeviceFragment()
    {
        // Single shared device entry: same fields a per-value VO device
        // would have, with the LCSC stock data of the 1kΩ CR1/4W as a
        // generic placeholder so EasyEDA Pro's PCB router accepts the
        // part. The displayed value comes from each COMPONENT's per-
        // instance Value ATTR override, NOT from this template's Value
        // field -- though the Value here ("1k") matches the placeholder
        // MPN so any tool that reads the template directly sees a
        // consistent fallback.
        //
        // The 3D Model attributes reference an EasyEDA cloud-library 3D
        // asset by UUID -- the geometry isn't embedded in our export,
        // EasyEDA resolves it at render time. The Transform string is
        // copied verbatim from the source .epro and aligns the 3D body
        // with the footprint's pad pattern.
        var attrs = new System.Text.Json.Nodes.JsonObject
        {
            ["LCSC Part Name"] = "1kΩ ±5%",
            ["Supplier Part"] = "C2894660",
            ["Manufacturer"] = "VO",
            ["Manufacturer Part"] = "CR1/4W-1K±5%-ST52",
            ["Supplier Footprint"] = "\u8f74\u5411\u5f15\u7ebf",
            ["JLCPCB Part Class"] = "Extended Part",
            ["Datasheet"] = "https://datasheet.lcsc.com/datasheet/pdf/f7f033f43920630443647ff79c60c0bb.pdf?productCode=C2894660",
            ["Supplier"] = "LCSC",
            ["Add into BOM"] = "yes",
            ["Convert to PCB"] = "yes",
            ["Symbol"] = ResistorSymbolUuid,
            ["Designator"] = "R?",
            ["Footprint"] = ResistorFootprintUuid,
            ["3D Model"] = Resistor3dModelUuid,
            ["3D Model Title"] = "RES-TH_BD2.7-L6.2-P10.20-D0.4",
            ["3D Model Transform"] = "417.322,106.221,0,0,0,0,0,0,-196.85",
            ["Type"] = "Carbon Film Resistor",
            ["Value"] = "1k",
            ["Tolerance"] = "\u00b15%",
            ["Description"] = "Through-hole resistor -- value supplied per instance via Value ATTR override",
        };

        var fragment = new System.Text.Json.Nodes.JsonObject
        {
            ["title"] = "Resistor",
            ["attributes"] = attrs,
            ["description"] = "Through-hole resistor -- value supplied per instance via Value ATTR override",
            ["tags"] = new System.Text.Json.Nodes.JsonObject
            {
                ["parent_tag"] = new System.Text.Json.Nodes.JsonArray(),
                ["child_tag"] = new System.Text.Json.Nodes.JsonArray(),
            },
            ["images"] = new System.Text.Json.Nodes.JsonArray(""),
            ["source"] = "6d850419aea044bb805429ccd89d98eb|0819f05c4eef4c71ace90d822a990e87",
            ["version"] = "1660759335",
            ["custom_tags"] = "[\"Resistors\"]",
        };

        return fragment.ToJsonString();
    }

    private static string BuildResistorSymbolFragment()
    {
        var fragment = new System.Text.Json.Nodes.JsonObject
        {
            ["source"] = "2d1d4b07d6cd4afeaf2cd056f4440b61|0819f05c4eef4c71ace90d822a990e87",
            ["desc"] = "",
            ["tags"] = new System.Text.Json.Nodes.JsonObject
            {
                ["parent_tag"] = new System.Text.Json.Nodes.JsonArray(),
                ["child_tag"] = new System.Text.Json.Nodes.JsonArray(),
            },
            ["custom_tags"] = "[\"Resistors\"]",
            ["title"] = "Resistor",
            ["version"] = "1660802535",
            ["type"] = 2,
        };
        return fragment.ToJsonString();
    }

    // ------------------------------------------- resistor-network synthesis
    //
    // Same Frankenstein pattern as the discrete resistor: one shared library
    // device whose displayed resistance comes from the per-instance Value ATTR
    // override. The symbol (resistor-network.esym) and footprint
    // (resistor-network.efoo, SIP-9) ship verbatim; the footprint's manifest
    // block lives in device_fragments.json, the device + symbol blocks are
    // inlined here. Placeholder LCSC/Bourns part data (4609X-101-472LF) lets
    // EasyEDA's PCB router accept the part and gives the 3D viewer a real body;
    // the BOM lists every network as that 4.7k part, which is harmless because
    // the value is carried per instance.

    private static CataloguePart BuildResistorNetworkPart(Device device)
    {
        string label = ReadPassiveValue(device);

        string displayValue;
        try
        {
            displayValue = ResistorValueParser.FormatForDisplay(label);
        }
        catch (FormatException ex)
        {
            throw ValueError(device, "resistor network", label, ex,
                "Supported value forms: 100, 100R, 220Ω, 2k2, 2K2, 1.5K, 1M, 1M5.");
        }

        return new CataloguePart(
            DeviceUuid: ResistorNetworkDeviceUuid,
            SymbolUuid: ResistorNetworkSymbolUuid,
            SymbolResourceName: "resistor-network.esym",
            FootprintUuid: ResistorNetworkFootprintUuid,
            FootprintResourceName: "resistor-network.efoo",
            PartTitle: "ResistorNetwork.1",
            PinLocalPositions: new()
            {
                // resistor-network.esym pin tips: x = -20, 10px pitch, pin 1
                // (common) at the top (+40) down to pin 9 (-40). That 10px
                // pitch equals ResistorNetworkUnit's 1-cell pin pitch (×10),
                // so TTLSim pin N lands on EasyEDA pin N with no rescale.
                [1] = new Point(-20, 40),
                [2] = new Point(-20, 30),
                [3] = new Point(-20, 20),
                [4] = new Point(-20, 10),
                [5] = new Point(-20, 0),
                [6] = new Point(-20, -10),
                [7] = new Point(-20, -20),
                [8] = new Point(-20, -30),
                [9] = new Point(-20, -40),
            },
            EmitValueLabel: true,
            ValueOverride: displayValue,
            InlineDeviceJson: BuildResistorNetworkDeviceFragment(),
            InlineSymbolJson: BuildResistorNetworkSymbolFragment(),
            // Initial label-offset guesses (anchored at pin 1, top of the
            // symbol, Y-up). Hand-tune per EasyEDA_Export.md §0 after a first
            // export if the Designator / Value sit awkwardly.
            LabelOffsets: new LabelOffsetsByRotation(
                Rot0: new LabelOffsetSet(new(-20, 60), new(-20, -60), new(-20, 48)),
                Rot90: new LabelOffsetSet(new(60, 20), new(-60, 20), new(48, 20)),
                Rot180: new LabelOffsetSet(new(-20, 60), new(-20, -60), new(-20, 48)),
                Rot270: new LabelOffsetSet(new(-60, 20), new(60, 20), new(-48, 20))));
    }

    private static string BuildResistorNetworkDeviceFragment()
    {
        var attrs = new System.Text.Json.Nodes.JsonObject
        {
            ["LCSC Part Name"] = "4.7k\u03a9 \u00b12%",
            ["Supplier Part"] = "C840659",
            ["Manufacturer"] = "BOURNS",
            ["Manufacturer Part"] = "4609X-101-472LF",
            ["Supplier Footprint"] = "SIP-9-2.54mm",
            ["JLCPCB Part Class"] = "Extended Part",
            ["Datasheet"] = "https://item.szlcsc.com/datasheet/4609X-101-472LF/897111.html",
            ["Supplier"] = "LCSC",
            ["Add into BOM"] = "yes",
            ["Convert to PCB"] = "yes",
            ["Symbol"] = ResistorNetworkSymbolUuid,
            ["Designator"] = "RN?",
            ["Footprint"] = ResistorNetworkFootprintUuid,
            ["3D Model"] = "f10af32c56eb4a21a68d2ce3222feb1b|0819f05c4eef4c71ace90d822a990e87",
            ["3D Model Title"] = "RES-ARRAY-TH_9P-L22.0-W2.5-P2.54-L",
            ["3D Model Transform"] = "898.03,97.851,0,0,0,0,0,0,-137.795",
            ["Name"] = "={Value}",
            ["Type"] = "Resistor Network",
            ["Value"] = "4.7k\u03a9",
            ["Tolerance"] = "\u00b12%",
            ["Description"] = "Bussed SIP-9 resistor network -- value supplied per instance via Value ATTR override",
        };

        var fragment = new System.Text.Json.Nodes.JsonObject
        {
            ["title"] = "4609X-101-472LF",
            ["attributes"] = attrs,
            ["description"] = "Bussed SIP-9 resistor network -- value supplied per instance via Value ATTR override",
            ["tags"] = new System.Text.Json.Nodes.JsonObject
            {
                ["parent_tag"] = new System.Text.Json.Nodes.JsonArray(),
                ["child_tag"] = new System.Text.Json.Nodes.JsonArray(),
            },
            ["images"] = new System.Text.Json.Nodes.JsonArray(""),
            ["source"] = ResistorNetworkDeviceUuid + "|0819f05c4eef4c71ace90d822a990e87",
            ["version"] = "1758104403",
            ["custom_tags"] = "[\"Resistor Networks, Arrays\"]",
        };

        return fragment.ToJsonString();
    }

    private static string BuildResistorNetworkSymbolFragment()
    {
        var fragment = new System.Text.Json.Nodes.JsonObject
        {
            ["source"] = ResistorNetworkSymbolUuid + "|0819f05c4eef4c71ace90d822a990e87",
            ["desc"] = "",
            ["tags"] = new System.Text.Json.Nodes.JsonObject
            {
                ["parent_tag"] = new System.Text.Json.Nodes.JsonArray(),
                ["child_tag"] = new System.Text.Json.Nodes.JsonArray(),
            },
            ["custom_tags"] = "[\"Resistor Networks, Arrays\"]",
            ["title"] = "ResistorNetwork",
            ["version"] = "1758103521",
            ["type"] = 2,
        };
        return fragment.ToJsonString();
    }

    // ---------------------------------------------------- DIP-8 synthesis
    //
    // A DIP-8 chip exports as a single-PART symbol matching the DIP box.
    // The device and symbol metadata live in device_fragments.json
    // (looked up by UUID at manifest-build time, like LED / VCC / GND),
    // so BuildDip8Part doesn't synthesise InlineDeviceJson /
    // InlineSymbolJson -- it only supplies the per-chip symbol template
    // tokens and the pin-local positions.
    //
    // Pin local positions mirror dip-8.esym exactly: pins 1..4 run down
    // the left edge at x=-50, y = +15, +5, -5, -15; pins 5..8 run UP the
    // right edge at x=+50, y = -15, -5, +5, +15 (the DIP mirror). These
    // are the PIN-record tip coordinates from the reference symbol, in
    // EasyEDA pixels. ChipUnit lays its canvas pins out the same way
    // (pin 1 top-left, descending; second half bottom-right, ascending),
    // so no per-pin remapping is needed -- pin number N in TTL Sim maps
    // to pin number N in the symbol.
    // 20px pin pitch (2 grid cells), matching ChipUnit's PinPitch so the
    // router's TTLSim pin positions line up exactly with the EasyEDA pins
    // after scaling -- no snap offset, no diagonal wires. The dip-8.esym
    // resource was rescaled to match (pins at y = ±30, ±10; body half 40).
    private static readonly Dictionary<int, Point> Dip8PinLocals = new()
    {
        [1] = new Point(-50, 30),
        [2] = new Point(-50, 10),
        [3] = new Point(-50, -10),
        [4] = new Point(-50, -30),
        [5] = new Point(50, -30),
        [6] = new Point(50, -10),
        [7] = new Point(50, 10),
        [8] = new Point(50, 30),
    };

    private static CataloguePart BuildDip8Part(ChipPartDefinition cp)
    {
        if (!Dip8Chips.TryGetValue(cp.PartNumber, out var chip))
            throw new NotImplementedException(
                $"EasyEDA export: DIP-8 chip '{cp.PartNumber}' is not yet bound to " +
                "an EasyEDA library entry. Add a row to Dip8Chips in EasyEDACatalogue " +
                "(with the chip's library Device + Symbol UUIDs) and the matching " +
                "device + symbol fragments to device_fragments.json.");

        // Build a pin-number -> name map from the chip definition, then
        // turn each into a @@PIN_n_NAME@@ template token. Active-low names
        // (leading '/' in TTL Sim, e.g. "/TRIG") convert to EasyEDA's
        // overbar convention: a leading '#' (e.g. "#TRIG"), which EasyEDA
        // renders with a bar over the following text.
        var tokens = new Dictionary<string, string>
        {
            ["@@PART_TITLE@@"] = chip.SymbolName + ".1",
            ["@@SYMBOL_NAME@@"] = chip.SymbolName,
        };
        foreach (var pin in cp.Pins)
            tokens[$"@@PIN_{pin.Number}_NAME@@"] = ToEasyEdaPinName(pin.Name);

        // Every DIP-8 pin must have a name token, or the template ships a
        // literal "@@PIN_n_NAME@@" string into the .esym. Guard against a
        // chip definition that doesn't enumerate all 8 pins.
        for (int n = 1; n <= 8; n++)
            if (!tokens.ContainsKey($"@@PIN_{n}_NAME@@"))
                throw new NotImplementedException(
                    $"EasyEDA export: DIP-8 chip '{cp.PartNumber}' is missing a " +
                    $"definition for pin {n}. All 8 pins must be enumerated in its " +
                    "ChipPartDefinition before it can be exported.");

        return new CataloguePart(
            DeviceUuid: chip.DeviceUuid,
            SymbolUuid: chip.SymbolUuid,
            SymbolResourceName: "dip-8.esym",
            FootprintUuid: Dip8FootprintUuid,
            FootprintResourceName: "dip-8.efoo",
            PartTitle: chip.SymbolName + ".1",
            PinLocalPositions: Dip8PinLocals,
            SymbolTemplateTokens: tokens,
            EmitTemplatedName: true,
            // Label offsets hand-tuned in EasyEDA and read back from
            // DIP-8_labels.epro (EasyEDA_Export.md §0), then rescaled for
            // the 20px-pitch body. The chip name sits adjacent to the
            // designator:
            //   R0/R180  (horizontal body, half-height 40): labels ABOVE
            //     the body, text horizontal -- designator at y=+50, name
            //     +20 to its right: "U1 74HC393".
            //   R90/R270 (vertical body, rotated span x[-40,40] y[-50,50]):
            //     labels to the LEFT of the body, TEXT ROTATED 90 so it
            //     reads bottom-to-top alongside the body -- designator at
            //     x=-50, name +18 along the body. TextRotationDeg=90.
            // The body is symmetric, so each rotation pair shares one value.
            LabelOffsets: new LabelOffsetsByRotation(
                Rot0: new LabelOffsetSet(new(-40, +50), new(-20, +50), default),
                Rot90: new LabelOffsetSet(new(-50, -20), new(-50, -2), default, TextRotationDeg: 90),
                Rot180: new LabelOffsetSet(new(-40, +50), new(-20, +50), default),
                Rot270: new LabelOffsetSet(new(-50, -20), new(-50, -2), default, TextRotationDeg: 90)));
    }

    /// <summary>
    /// Convert a TTL Sim pin name to EasyEDA's pin-name convention. TTL Sim
    /// marks an active-low pin with a leading '/' (e.g. "/TRIG", "/RESET");
    /// EasyEDA uses a leading '#' to render an overbar over the following
    /// text. Names without a leading '/' pass through unchanged.
    /// </summary>
    private static string ToEasyEdaPinName(string name) =>
        !string.IsNullOrEmpty(name) && name[0] == '/'
            ? "#" + name.Substring(1)
            : name;

    // ---------------------------------------------------- DIP-14 synthesis
    //
    // 20px pin pitch (2 grid cells), matching ChipUnit's PinPitch so the
    // router's TTLSim pin positions line up exactly with the EasyEDA pins
    // after scaling -- no snap offset, no diagonal wires. The dip-14.esym
    // resource was rescaled to match: pins 1..7 DOWN the left edge at
    // x=-50, y = +60,+40,+20,0,-20,-40,-60; pins 8..14 UP the right edge
    // at x=+50, y = -60,-40,-20,0,+20,+40,+60 (the DIP mirror); body half
    // height 72. ChipUnit lays its canvas pins out the same way, so pin
    // number N in TTL Sim maps to pin number N in the symbol.
    private static readonly Dictionary<int, Point> Dip14PinLocals = new()
    {
        [1] = new Point(-50, 60),
        [2] = new Point(-50, 40),
        [3] = new Point(-50, 20),
        [4] = new Point(-50, 0),
        [5] = new Point(-50, -20),
        [6] = new Point(-50, -40),
        [7] = new Point(-50, -60),
        [8] = new Point(50, -60),
        [9] = new Point(50, -40),
        [10] = new Point(50, -20),
        [11] = new Point(50, 0),
        [12] = new Point(50, 20),
        [13] = new Point(50, 40),
        [14] = new Point(50, 60),
    };

    private static CataloguePart BuildDip14Part(Device device, ChipPartDefinition cp)
    {
        // Displayed chip name -- "74HC393", "74HC74", "NE556", etc.
        string chipName = device.FullPartNumber;

        // Deterministic per-chip-type UUIDs so re-exports are byte-stable
        // (doc §7). Keyed on the full part name (e.g. "74HC393"), which
        // uniquely identifies the chip type; two placements of the same
        // chip get the same UUIDs and dedup to one symbol/device in the zip.
        string symbolUuid = DeterministicUuid("dip14-symbol:" + chipName);
        string deviceUuid = DeterministicUuid("dip14-device:" + chipName);

        // Symbol template tokens: part title, displayed symbol name, and
        // the 14 pin names. Active-low '/' -> '#' (EasyEDA overbar).
        var tokens = new Dictionary<string, string>
        {
            ["@@PART_TITLE@@"] = chipName + ".1",
            ["@@SYMBOL_NAME@@"] = chipName,
        };
        foreach (var pin in cp.Pins)
            tokens[$"@@PIN_{pin.Number}_NAME@@"] = ToEasyEdaPinName(pin.Name);

        // All 14 pins must be enumerated, or the template ships a literal
        // "@@PIN_n_NAME@@" string into the .esym.
        for (int n = 1; n <= 14; n++)
            if (!tokens.ContainsKey($"@@PIN_{n}_NAME@@"))
                throw new NotImplementedException(
                    $"EasyEDA export: DIP-14 chip '{chipName}' is missing a " +
                    $"definition for pin {n}. All 14 pins must be enumerated in " +
                    "its ChipPartDefinition before it can be exported.");

        return new CataloguePart(
            DeviceUuid: deviceUuid,
            SymbolUuid: symbolUuid,
            SymbolResourceName: "dip-14.esym",
            FootprintUuid: Dip14FootprintUuid,
            FootprintResourceName: "dip-14.efoo",
            PartTitle: chipName + ".1",
            PinLocalPositions: Dip14PinLocals,
            SymbolTemplateTokens: tokens,
            InlineDeviceJson: BuildDip14DeviceFragment(chipName, symbolUuid),
            InlineSymbolJson: BuildDip14SymbolFragment(chipName),
            EmitTemplatedName: true,
            // Label offsets in the same style as DIP-8, rescaled for the
            // taller PDIP-14 body:
            //   R0/R180  (horizontal body, half-height 72): labels ABOVE
            //     the body, text horizontal -- designator at y=+80, name
            //     +20 to its right: "U1 74HC393".
            //   R90/R270 (vertical body, rotated span x[-72,72] y[-50,50]):
            //     labels to the LEFT of the body, TEXT ROTATED 90 so it
            //     reads bottom-to-top alongside the body -- designator at
            //     x=-82, name +18 along the body. TextRotationDeg=90.
            // The body is symmetric, so each rotation pair shares one value.
            // Hand-tune in EasyEDA after first export if wanted (§0).
            LabelOffsets: new LabelOffsetsByRotation(
                Rot0: new LabelOffsetSet(new(-40, +80), new(-20, +80), default),
                Rot90: new LabelOffsetSet(new(-82, -20), new(-82, -2), default, TextRotationDeg: 90),
                Rot180: new LabelOffsetSet(new(-40, +80), new(-20, +80), default),
                Rot270: new LabelOffsetSet(new(-82, -20), new(-82, -2), default, TextRotationDeg: 90)));
    }

    /// <summary>
    /// Synthesise a DIP-14 device fragment for project.json. Uses the
    /// 74HC393 reference's decorative attributes (LCSC stock, 3D model,
    /// etc.) as a shared template -- those fields are decorative per
    /// EasyEDA_Export.md §2 -- and overwrites the chip-specific fields:
    /// Manufacturer Part (drives the displayed Name via "={Manufacturer
    /// Part}"), Symbol, Designator, Footprint.
    /// </summary>
    private static string BuildDip14DeviceFragment(string chipName, string symbolUuid)
    {
        var attrs = new System.Text.Json.Nodes.JsonObject
        {
            ["Supplier Part"] = Dip14ReferenceSupplierPart,
            ["Manufacturer"] = Dip14ReferenceManufacturer,
            ["Manufacturer Part"] = chipName,
            ["Supplier Footprint"] = "DIP-14",
            ["JLCPCB Part Class"] = "Extended Part",
            ["Supplier"] = "LCSC",
            ["Add into BOM"] = "yes",
            ["Convert to PCB"] = "yes",
            ["Symbol"] = symbolUuid,
            ["Designator"] = "U?",
            ["Footprint"] = Dip14FootprintUuid,
            ["3D Model"] = Dip14Reference3dModelUuid,
            ["3D Model Title"] = "PDIP-14_L19.3-W6.4-P2.54-LS7.9-BL-1",
            ["3D Model Transform"] = Dip14Reference3dModelTransform,
            ["Name"] = "={Manufacturer Part}",
            ["Description"] = "DIP-14 logic IC",
        };

        var fragment = new System.Text.Json.Nodes.JsonObject
        {
            ["title"] = chipName,
            ["attributes"] = attrs,
            ["description"] = "DIP-14 logic IC",
            ["tags"] = new System.Text.Json.Nodes.JsonObject
            {
                ["parent_tag"] = new System.Text.Json.Nodes.JsonArray(),
                ["child_tag"] = new System.Text.Json.Nodes.JsonArray(),
            },
            ["images"] = new System.Text.Json.Nodes.JsonArray(""),
            ["source"] = "67204415f0444fada7b9208ff9e12036|0819f05c4eef4c71ace90d822a990e87",
            ["version"] = "1660159279",
            ["custom_tags"] = "[\"Logic\"]",
        };

        return fragment.ToJsonString();
    }

    /// <summary>
    /// Synthesise a DIP-14 symbol fragment for project.json. type:2 (NORMAL
    /// schematic symbol). The title matches the chip name so the symbols
    /// map reads sensibly; the .esym drawing itself is the cloned template.
    /// </summary>
    private static string BuildDip14SymbolFragment(string chipName)
    {
        var fragment = new System.Text.Json.Nodes.JsonObject
        {
            ["source"] = "1754d3aadc5c46de9f9881bdf1de48cf|0819f05c4eef4c71ace90d822a990e87",
            ["desc"] = "",
            ["tags"] = new System.Text.Json.Nodes.JsonObject
            {
                ["parent_tag"] = new System.Text.Json.Nodes.JsonArray(),
                ["child_tag"] = new System.Text.Json.Nodes.JsonArray(),
            },
            ["custom_tags"] = "[\"Logic\"]",
            ["title"] = chipName,
            ["version"] = "1681885513",
            ["type"] = 2,
        };
        return fragment.ToJsonString();
    }

    // ---------------------------------------------------- DIP-16 synthesis
    //
    // 20px pin pitch, matching ChipUnit's PinPitch (same pitch fix as
    // DIP-8/DIP-14). dip-16.esym: pins 1..8 DOWN the left edge at x=-50,
    // y = +70,+50,+30,+10,-10,-30,-50,-70; pins 9..16 UP the right edge at
    // x=+50, y = -70,-50,-30,-10,+10,+30,+50,+70 (the DIP mirror); body
    // half height 82. Pin number N in TTL Sim maps to pin number N here.
    private static readonly Dictionary<int, Point> Dip16PinLocals = new()
    {
        [1] = new Point(-50, 70),
        [2] = new Point(-50, 50),
        [3] = new Point(-50, 30),
        [4] = new Point(-50, 10),
        [5] = new Point(-50, -10),
        [6] = new Point(-50, -30),
        [7] = new Point(-50, -50),
        [8] = new Point(-50, -70),
        [9] = new Point(50, -70),
        [10] = new Point(50, -50),
        [11] = new Point(50, -30),
        [12] = new Point(50, -10),
        [13] = new Point(50, 10),
        [14] = new Point(50, 30),
        [15] = new Point(50, 50),
        [16] = new Point(50, 70),
    };

    private static CataloguePart BuildDip16Part(Device device, ChipPartDefinition cp)
    {
        // Displayed chip name -- "74HC163", "74HC595", "74HC138", etc.
        string chipName = device.FullPartNumber;

        // Deterministic per-chip-type UUIDs so re-exports are byte-stable
        // (doc §7), keyed on the full part name. Namespaced "dip16-" so they
        // never collide with the DIP-14 derivations.
        string symbolUuid = DeterministicUuid("dip16-symbol:" + chipName);
        string deviceUuid = DeterministicUuid("dip16-device:" + chipName);

        // Symbol template tokens: part title, displayed symbol name, and
        // the 16 pin names. Active-low '/' -> '#' (EasyEDA overbar).
        var tokens = new Dictionary<string, string>
        {
            ["@@PART_TITLE@@"] = chipName + ".1",
            ["@@SYMBOL_NAME@@"] = chipName,
        };
        foreach (var pin in cp.Pins)
            tokens[$"@@PIN_{pin.Number}_NAME@@"] = ToEasyEdaPinName(pin.Name);

        // All 16 pins must be enumerated, or the template ships a literal
        // "@@PIN_n_NAME@@" string into the .esym.
        for (int n = 1; n <= 16; n++)
            if (!tokens.ContainsKey($"@@PIN_{n}_NAME@@"))
                throw new NotImplementedException(
                    $"EasyEDA export: DIP-16 chip '{chipName}' is missing a " +
                    $"definition for pin {n}. All 16 pins must be enumerated in " +
                    "its ChipPartDefinition before it can be exported.");

        return new CataloguePart(
            DeviceUuid: deviceUuid,
            SymbolUuid: symbolUuid,
            SymbolResourceName: "dip-16.esym",
            FootprintUuid: Dip16FootprintUuid,
            FootprintResourceName: "dip-16.efoo",
            PartTitle: chipName + ".1",
            PinLocalPositions: Dip16PinLocals,
            SymbolTemplateTokens: tokens,
            InlineDeviceJson: BuildDip16DeviceFragment(chipName, symbolUuid),
            InlineSymbolJson: BuildDip16SymbolFragment(chipName),
            EmitTemplatedName: true,
            // Same label layout as DIP-14, raised to clear the taller DIP-16
            // body (half-height 82, body top at +82; designator at +90).
            //   R0/R180:  labels ABOVE the body, text horizontal.
            //   R90/R270: labels to the LEFT (rotated span x[-82,82]
            //     y[-50,50]), TEXT ROTATED 90 reading along the body.
            // The body is symmetric, so each rotation pair shares one value.
            LabelOffsets: new LabelOffsetsByRotation(
                Rot0: new LabelOffsetSet(new(-40, +90), new(-20, +90), default),
                Rot90: new LabelOffsetSet(new(-92, -20), new(-92, -2), default, TextRotationDeg: 90),
                Rot180: new LabelOffsetSet(new(-40, +90), new(-20, +90), default),
                Rot270: new LabelOffsetSet(new(-92, -20), new(-92, -2), default, TextRotationDeg: 90)));
    }

    /// <summary>
    /// Synthesise a DIP-16 device fragment for project.json. Same shape as
    /// the DIP-14 version, using the 74HC163 reference's decorative
    /// attributes (shared template, per §2) and overwriting the chip-
    /// specific fields. The 3D Model is the shared DIP-16 model.
    /// </summary>
    private static string BuildDip16DeviceFragment(string chipName, string symbolUuid)
    {
        var attrs = new System.Text.Json.Nodes.JsonObject
        {
            ["Supplier Part"] = Dip16ReferenceSupplierPart,
            ["Manufacturer"] = Dip16ReferenceManufacturer,
            ["Manufacturer Part"] = chipName,
            ["Supplier Footprint"] = "DIP-16",
            ["JLCPCB Part Class"] = "Extended Part",
            ["Supplier"] = "LCSC",
            ["Add into BOM"] = "yes",
            ["Convert to PCB"] = "yes",
            ["Symbol"] = symbolUuid,
            ["Designator"] = "U?",
            ["Footprint"] = Dip16FootprintUuid,
            ["3D Model"] = Dip16Reference3dModelUuid,
            ["3D Model Title"] = Dip16Reference3dModelTitle,
            ["3D Model Transform"] = Dip16Reference3dModelTransform,
            ["Name"] = "={Manufacturer Part}",
            ["Description"] = "DIP-16 logic IC",
        };

        var fragment = new System.Text.Json.Nodes.JsonObject
        {
            ["title"] = chipName,
            ["attributes"] = attrs,
            ["description"] = "DIP-16 logic IC",
            ["tags"] = new System.Text.Json.Nodes.JsonObject
            {
                ["parent_tag"] = new System.Text.Json.Nodes.JsonArray(),
                ["child_tag"] = new System.Text.Json.Nodes.JsonArray(),
            },
            ["images"] = new System.Text.Json.Nodes.JsonArray(""),
            ["source"] = "67204415f0444fada7b9208ff9e12036|0819f05c4eef4c71ace90d822a990e87",
            ["version"] = "1660159279",
            ["custom_tags"] = "[\"Logic\"]",
        };

        return fragment.ToJsonString();
    }

    /// <summary>
    /// Synthesise a DIP-16 symbol fragment for project.json (type:2).
    /// </summary>
    private static string BuildDip16SymbolFragment(string chipName)
    {
        var fragment = new System.Text.Json.Nodes.JsonObject
        {
            ["source"] = "1754d3aadc5c46de9f9881bdf1de48cf|0819f05c4eef4c71ace90d822a990e87",
            ["desc"] = "",
            ["tags"] = new System.Text.Json.Nodes.JsonObject
            {
                ["parent_tag"] = new System.Text.Json.Nodes.JsonArray(),
                ["child_tag"] = new System.Text.Json.Nodes.JsonArray(),
            },
            ["custom_tags"] = "[\"Logic\"]",
            ["title"] = chipName,
            ["version"] = "1681885513",
            ["type"] = 2,
        };
        return fragment.ToJsonString();
    }

    // ---------------------------------------------------- DIP-18 synthesis
    //
    // 20px pin pitch, matching ChipUnit's PinPitch (same pitch fix as
    // DIP-8/DIP-14/DIP-16/DIP-20). dip-18.esym: pins 1..9 DOWN the left
    // edge at x=-50, y = +80,+60,+40,+20,0,-20,-40,-60,-80; pins 10..18 UP
    // the right edge at x=+50, y = -80..+80 (the DIP mirror); body half
    // height 92. ChipUnit lays its canvas pins out the same way, so pin
    // number N in TTL Sim maps to pin number N in the symbol.
    private static readonly Dictionary<int, Point> Dip18PinLocals = new()
    {
        [1] = new Point(-50, 80),
        [2] = new Point(-50, 60),
        [3] = new Point(-50, 40),
        [4] = new Point(-50, 20),
        [5] = new Point(-50, 0),
        [6] = new Point(-50, -20),
        [7] = new Point(-50, -40),
        [8] = new Point(-50, -60),
        [9] = new Point(-50, -80),
        [10] = new Point(50, -80),
        [11] = new Point(50, -60),
        [12] = new Point(50, -40),
        [13] = new Point(50, -20),
        [14] = new Point(50, 0),
        [15] = new Point(50, 20),
        [16] = new Point(50, 40),
        [17] = new Point(50, 60),
        [18] = new Point(50, 80),
    };

    private static CataloguePart BuildDip18Part(Device device, ChipPartDefinition cp)
    {
        // Displayed chip name -- "2114", etc.
        string chipName = device.FullPartNumber;

        // Deterministic per-chip-type UUIDs so re-exports are byte-stable
        // (doc §7), keyed on the full part name. Namespaced "dip18-" so they
        // never collide with the other DIP derivations.
        string symbolUuid = DeterministicUuid("dip18-symbol:" + chipName);
        string deviceUuid = DeterministicUuid("dip18-device:" + chipName);

        // Symbol template tokens: part title, displayed symbol name, and
        // the 18 pin names. Active-low '/' -> '#' (EasyEDA overbar).
        var tokens = new Dictionary<string, string>
        {
            ["@@PART_TITLE@@"] = chipName + ".1",
            ["@@SYMBOL_NAME@@"] = chipName,
        };
        foreach (var pin in cp.Pins)
            tokens[$"@@PIN_{pin.Number}_NAME@@"] = ToEasyEdaPinName(pin.Name);

        // All 18 pins must be enumerated, or the template ships a literal
        // "@@PIN_n_NAME@@" string into the .esym.
        for (int n = 1; n <= 18; n++)
            if (!tokens.ContainsKey($"@@PIN_{n}_NAME@@"))
                throw new NotImplementedException(
                    $"EasyEDA export: DIP-18 chip '{chipName}' is missing a " +
                    $"definition for pin {n}. All 18 pins must be enumerated in " +
                    "its ChipPartDefinition before it can be exported.");

        return new CataloguePart(
            DeviceUuid: deviceUuid,
            SymbolUuid: symbolUuid,
            SymbolResourceName: "dip-18.esym",
            FootprintUuid: Dip18FootprintUuid,
            FootprintResourceName: "dip-18.efoo",
            PartTitle: chipName + ".1",
            PinLocalPositions: Dip18PinLocals,
            SymbolTemplateTokens: tokens,
            InlineDeviceJson: BuildDip18DeviceFragment(chipName, symbolUuid),
            InlineSymbolJson: BuildDip18SymbolFragment(chipName),
            EmitTemplatedName: true,
            // Same label layout as DIP-16, raised to clear the taller DIP-18
            // body (half-height 92, body top at +92; designator at +100 --
            // the DIP-16 rule, half + 8; Rot90/270 x at -(half + 10)).
            //   R0/R180:  labels ABOVE the body, text horizontal.
            //   R90/R270: labels to the LEFT (rotated span x[-92,92]
            //     y[-50,50]), TEXT ROTATED 90 reading along the body.
            // The body is symmetric, so each rotation pair shares one value.
            LabelOffsets: new LabelOffsetsByRotation(
                Rot0: new LabelOffsetSet(new(-40, +100), new(-20, +100), default),
                Rot90: new LabelOffsetSet(new(-102, -20), new(-102, -2), default, TextRotationDeg: 90),
                Rot180: new LabelOffsetSet(new(-40, +100), new(-20, +100), default),
                Rot270: new LabelOffsetSet(new(-102, -20), new(-102, -2), default, TextRotationDeg: 90)));
    }

    /// <summary>
    /// Synthesise a DIP-18 device fragment for project.json. Same shape as
    /// the DIP-16 version, using the PA1517G-D18-T reference's decorative
    /// attributes (shared template, per §2) and overwriting the chip-
    /// specific fields. The 3D Model is the shared DIP-18 model.
    /// </summary>
    private static string BuildDip18DeviceFragment(string chipName, string symbolUuid)
    {
        var attrs = new System.Text.Json.Nodes.JsonObject
        {
            ["Supplier Part"] = Dip18ReferenceSupplierPart,
            ["Manufacturer"] = Dip18ReferenceManufacturer,
            ["Manufacturer Part"] = chipName,
            ["Supplier Footprint"] = "DIP-18",
            ["JLCPCB Part Class"] = "Extended Part",
            ["Supplier"] = "LCSC",
            ["Add into BOM"] = "yes",
            ["Convert to PCB"] = "yes",
            ["Symbol"] = symbolUuid,
            ["Designator"] = "U?",
            ["Footprint"] = Dip18FootprintUuid,
            ["3D Model"] = Dip18Reference3dModelUuid,
            ["3D Model Title"] = Dip18Reference3dModelTitle,
            ["3D Model Transform"] = Dip18Reference3dModelTransform,
            ["Name"] = "={Manufacturer Part}",
            ["Description"] = "DIP-18 IC",
        };

        var fragment = new System.Text.Json.Nodes.JsonObject
        {
            ["title"] = chipName,
            ["attributes"] = attrs,
            ["description"] = "DIP-18 IC",
            ["tags"] = new System.Text.Json.Nodes.JsonObject
            {
                ["parent_tag"] = new System.Text.Json.Nodes.JsonArray(),
                ["child_tag"] = new System.Text.Json.Nodes.JsonArray(),
            },
            ["images"] = new System.Text.Json.Nodes.JsonArray(""),
            ["source"] = "b787ab6149aa6d78|0819f05c4eef4c71ace90d822a990e87",
            ["version"] = "1660160780",
            ["custom_tags"] = "[\"Logic\"]",
        };

        return fragment.ToJsonString();
    }

    /// <summary>
    /// Synthesise a DIP-18 symbol fragment for project.json (type:2). Source
    /// id reuses the device reference pair -- decorative per §2 (the PA1517
    /// reference symbol's own header carries no source id).
    /// </summary>
    private static string BuildDip18SymbolFragment(string chipName)
    {
        var fragment = new System.Text.Json.Nodes.JsonObject
        {
            ["source"] = "b787ab6149aa6d78|0819f05c4eef4c71ace90d822a990e87",
            ["desc"] = "",
            ["tags"] = new System.Text.Json.Nodes.JsonObject
            {
                ["parent_tag"] = new System.Text.Json.Nodes.JsonArray(),
                ["child_tag"] = new System.Text.Json.Nodes.JsonArray(),
            },
            ["custom_tags"] = "[\"Logic\"]",
            ["title"] = chipName,
            ["version"] = "1660160780",
            ["type"] = 2,
        };
        return fragment.ToJsonString();
    }

    // ---------------------------------------------------- DIP-20 synthesis
    //
    // 20px pin pitch, matching ChipUnit's PinPitch (same pitch fix as
    // DIP-8/DIP-14/DIP-16). dip-20.esym: pins 1..10 DOWN the left edge at
    // x=-50, y = +90,+70,+50,+30,+10,-10,-30,-50,-70,-90; pins 11..20 UP
    // the right edge at x=+50, y = -90,-70,-50,-30,-10,+10,+30,+50,+70,+90
    // (the DIP mirror); body half-height 102. Pin number N in TTL Sim maps
    // to pin number N here.
    private static readonly Dictionary<int, Point> Dip20PinLocals = new()
    {
        [1] = new Point(-50, 90),
        [2] = new Point(-50, 70),
        [3] = new Point(-50, 50),
        [4] = new Point(-50, 30),
        [5] = new Point(-50, 10),
        [6] = new Point(-50, -10),
        [7] = new Point(-50, -30),
        [8] = new Point(-50, -50),
        [9] = new Point(-50, -70),
        [10] = new Point(-50, -90),
        [11] = new Point(50, -90),
        [12] = new Point(50, -70),
        [13] = new Point(50, -50),
        [14] = new Point(50, -30),
        [15] = new Point(50, -10),
        [16] = new Point(50, 10),
        [17] = new Point(50, 30),
        [18] = new Point(50, 50),
        [19] = new Point(50, 70),
        [20] = new Point(50, 90),
    };

    private static CataloguePart BuildDip20Part(Device device, ChipPartDefinition cp)
    {
        // Displayed chip name -- "74HC273", "74HC574", "74HC377", etc.
        string chipName = device.FullPartNumber;

        // Deterministic per-chip-type UUIDs so re-exports are byte-stable
        // (doc §7), keyed on the full part name. Namespaced "dip20-" so they
        // never collide with the DIP-14 / DIP-16 derivations.
        string symbolUuid = DeterministicUuid("dip20-symbol:" + chipName);
        string deviceUuid = DeterministicUuid("dip20-device:" + chipName);

        // Symbol template tokens: part title, displayed symbol name, and
        // the 20 pin names. Active-low '/' -> '#' (EasyEDA overbar).
        var tokens = new Dictionary<string, string>
        {
            ["@@PART_TITLE@@"] = chipName + ".1",
            ["@@SYMBOL_NAME@@"] = chipName,
        };
        foreach (var pin in cp.Pins)
            tokens[$"@@PIN_{pin.Number}_NAME@@"] = ToEasyEdaPinName(pin.Name);

        // All 20 pins must be enumerated, or the template ships a literal
        // "@@PIN_n_NAME@@" string into the .esym.
        for (int n = 1; n <= 20; n++)
            if (!tokens.ContainsKey($"@@PIN_{n}_NAME@@"))
                throw new NotImplementedException(
                    $"EasyEDA export: DIP-20 chip '{chipName}' is missing a " +
                    $"definition for pin {n}. All 20 pins must be enumerated in " +
                    "its ChipPartDefinition before it can be exported.");

        return new CataloguePart(
            DeviceUuid: deviceUuid,
            SymbolUuid: symbolUuid,
            SymbolResourceName: "dip-20.esym",
            FootprintUuid: Dip20FootprintUuid,
            FootprintResourceName: "dip-20.efoo",
            PartTitle: chipName + ".1",
            PinLocalPositions: Dip20PinLocals,
            SymbolTemplateTokens: tokens,
            InlineDeviceJson: BuildDip20DeviceFragment(chipName, symbolUuid),
            InlineSymbolJson: BuildDip20SymbolFragment(chipName),
            EmitTemplatedName: true,
            // Same label layout as DIP-16, raised to clear the taller DIP-20
            // body (half-height 102, body top at +102; designator at +110).
            //   R0/R180:  labels ABOVE the body, text horizontal.
            //   R90/R270: labels to the LEFT (rotated span x[-102,102]
            //     y[-50,50]), TEXT ROTATED 90 reading along the body.
            // The body is symmetric, so each rotation pair shares one value.
            LabelOffsets: new LabelOffsetsByRotation(
                Rot0: new LabelOffsetSet(new(-40, +110), new(-20, +110), default),
                Rot90: new LabelOffsetSet(new(-112, -20), new(-112, -2), default, TextRotationDeg: 90),
                Rot180: new LabelOffsetSet(new(-40, +110), new(-20, +110), default),
                Rot270: new LabelOffsetSet(new(-112, -20), new(-112, -2), default, TextRotationDeg: 90)));
    }

    /// <summary>
    /// Synthesise a DIP-20 device fragment for project.json. Same shape as
    /// the DIP-16 version, using the 74HC273 reference's decorative
    /// attributes (shared template, per §2) and overwriting the chip-
    /// specific fields. The 3D Model is the shared PDIP-20 model.
    /// </summary>
    private static string BuildDip20DeviceFragment(string chipName, string symbolUuid)
    {
        var attrs = new System.Text.Json.Nodes.JsonObject
        {
            ["Supplier Part"] = Dip20ReferenceSupplierPart,
            ["Manufacturer"] = Dip20ReferenceManufacturer,
            ["Manufacturer Part"] = chipName,
            ["Supplier Footprint"] = "DIP-20",
            ["JLCPCB Part Class"] = "Extended Part",
            ["Supplier"] = "LCSC",
            ["Add into BOM"] = "yes",
            ["Convert to PCB"] = "yes",
            ["Symbol"] = symbolUuid,
            ["Designator"] = "U?",
            ["Footprint"] = Dip20FootprintUuid,
            ["3D Model"] = Dip20Reference3dModelUuid,
            ["3D Model Title"] = Dip20Reference3dModelTitle,
            ["3D Model Transform"] = Dip20Reference3dModelTransform,
            ["Name"] = "={Manufacturer Part}",
            ["Description"] = "DIP-20 logic IC",
        };

        var fragment = new System.Text.Json.Nodes.JsonObject
        {
            ["title"] = chipName,
            ["attributes"] = attrs,
            ["description"] = "DIP-20 logic IC",
            ["tags"] = new System.Text.Json.Nodes.JsonObject
            {
                ["parent_tag"] = new System.Text.Json.Nodes.JsonArray(),
                ["child_tag"] = new System.Text.Json.Nodes.JsonArray(),
            },
            ["images"] = new System.Text.Json.Nodes.JsonArray(""),
            ["source"] = "67204415f0444fada7b9208ff9e12036|0819f05c4eef4c71ace90d822a990e87",
            ["version"] = "1660159279",
            ["custom_tags"] = "[\"Logic\"]",
        };

        return fragment.ToJsonString();
    }

    /// <summary>
    /// Synthesise a DIP-20 symbol fragment for project.json (type:2).
    /// </summary>
    private static string BuildDip20SymbolFragment(string chipName)
    {
        var fragment = new System.Text.Json.Nodes.JsonObject
        {
            ["source"] = "1754d3aadc5c46de9f9881bdf1de48cf|0819f05c4eef4c71ace90d822a990e87",
            ["desc"] = "",
            ["tags"] = new System.Text.Json.Nodes.JsonObject
            {
                ["parent_tag"] = new System.Text.Json.Nodes.JsonArray(),
                ["child_tag"] = new System.Text.Json.Nodes.JsonArray(),
            },
            ["custom_tags"] = "[\"Logic\"]",
            ["title"] = chipName,
            ["version"] = "1681885513",
            ["type"] = 2,
        };
        return fragment.ToJsonString();
    }

    // ---------------------------------------------------- TO-92 synthesis
    //
    // to92-3.esym authored to match To92Unit: a body box with three legs
    // along the BOTTOM edge pointing down, pin 1 left -> pin 3 right at 20px
    // pitch, centred on x=0. Pin connection points (endpoints) are at
    // (-20,-30), (0,-30), (+20,-30) in the symbol's local frame.
    //
    // PinLocalPositions encode the SAME relative geometry as the canvas:
    // To92Unit places its three pins on a horizontal line one grid cell
    // apart (pins at canvas-local x = 2,4,6, all at y = Size.Height), so in
    // EasyEDA pixels the pins sit 20px apart horizontally at a common Y.
    // That makes every exported pin world-position equal its TTLSim world
    // position (anchor pin matches by construction; the other two match
    // because the local geometry equals the canvas geometry), so wires land
    // exactly where they were drawn. The common Y value (-30) is arbitrary
    // -- only the relative offsets matter -- and is chosen to match the
    // .esym's pin endpoints.
    private static readonly Dictionary<int, Point> To92PinLocals = new()
    {
        [1] = new Point(-20, -30),
        [2] = new Point(0, -30),
        [3] = new Point(20, -30),
    };

    private static CataloguePart BuildTo92Part(Device device, ChipPartDefinition cp)
    {
        // Displayed part name -- "DS1813", etc.
        string chipName = device.FullPartNumber;

        // The shared to92-3.esym has exactly three pin slots; a To92 part
        // with a different pin count would silently lose pins, so reject it
        // with a clear message rather than emit a broken symbol.
        if (cp.PinCount != 3)
            throw new NotImplementedException(
                $"EasyEDA export: TO-92 part '{chipName}' has {cp.PinCount} pins, but " +
                "the to92-3 symbol/footprint support exactly 3. Add a wider TO-92 " +
                "template before exporting this part.");

        // Deterministic per-chip-type UUIDs so re-exports are byte-stable
        // (doc §7), keyed on the full part name. Namespaced "to92-" so they
        // never collide with the DIP derivations.
        string symbolUuid = DeterministicUuid("to92-symbol:" + chipName);
        string deviceUuid = DeterministicUuid("to92-device:" + chipName);

        // Symbol template tokens: part title, displayed symbol name, and the
        // 3 pin names. Active-low '/' -> '#' (EasyEDA overbar).
        var tokens = new Dictionary<string, string>
        {
            ["@@PART_TITLE@@"] = chipName + ".1",
            ["@@SYMBOL_NAME@@"] = chipName,
        };
        foreach (var pin in cp.Pins)
            tokens[$"@@PIN_{pin.Number}_NAME@@"] = ToEasyEdaPinName(pin.Name);

        // All 3 pins must be enumerated, or the template ships a literal
        // "@@PIN_n_NAME@@" string into the .esym.
        for (int n = 1; n <= 3; n++)
            if (!tokens.ContainsKey($"@@PIN_{n}_NAME@@"))
                throw new NotImplementedException(
                    $"EasyEDA export: TO-92 part '{chipName}' is missing a " +
                    $"definition for pin {n}. All 3 pins must be enumerated in " +
                    "its ChipPartDefinition before it can be exported.");

        return new CataloguePart(
            DeviceUuid: deviceUuid,
            SymbolUuid: symbolUuid,
            SymbolResourceName: "to92-3.esym",
            FootprintUuid: To92FootprintUuid,
            FootprintResourceName: "to92-3.efoo",
            PartTitle: chipName + ".1",
            PinLocalPositions: To92PinLocals,
            SymbolTemplateTokens: tokens,
            InlineDeviceJson: BuildTo92DeviceFragment(chipName, symbolUuid),
            InlineSymbolJson: BuildTo92SymbolFragment(chipName),
            EmitTemplatedName: true,
            // Body box spans y[-20,+20]; legs hang to y=-30. Designator/Name
            // sit ABOVE the body at R0/R180 (text horizontal); to the LEFT,
            // text rotated 90, at R90/R270. Cosmetic -- verify/tune in the
            // §9 round-trip (this is the first down-pin part exported).
            LabelOffsets: new LabelOffsetsByRotation(
                Rot0: new LabelOffsetSet(new(-15, +30), new(+5, +30), default),
                Rot90: new LabelOffsetSet(new(-42, -15), new(-42, +3), default, TextRotationDeg: 90),
                Rot180: new LabelOffsetSet(new(-15, +30), new(+5, +30), default),
                Rot270: new LabelOffsetSet(new(-42, -15), new(-42, +3), default, TextRotationDeg: 90)));
    }

    /// <summary>
    /// Synthesise a TO-92 device fragment for project.json. Same shape as the
    /// DIP versions, using the DS1813 TO-92-3 reference's decorative
    /// attributes (shared template, per §2) and overwriting the chip-specific
    /// fields. The 3D Model is the shared TO-92-3 model.
    /// </summary>
    private static string BuildTo92DeviceFragment(string chipName, string symbolUuid)
    {
        var attrs = new System.Text.Json.Nodes.JsonObject
        {
            ["Supplier Part"] = To92ReferenceSupplierPart,
            ["Manufacturer"] = To92ReferenceManufacturer,
            ["Manufacturer Part"] = chipName,
            ["Supplier Footprint"] = "TO-92-3",
            ["JLCPCB Part Class"] = "Extended Part",
            ["Supplier"] = "LCSC",
            ["Add into BOM"] = "yes",
            ["Convert to PCB"] = "yes",
            ["Symbol"] = symbolUuid,
            ["Designator"] = "U?",
            ["Footprint"] = To92FootprintUuid,
            ["3D Model"] = To92Reference3dModelUuid,
            ["3D Model Title"] = To92Reference3dModelTitle,
            ["3D Model Transform"] = To92Reference3dModelTransform,
            ["Name"] = "={Manufacturer Part}",
            ["Description"] = "TO-92 3-pin part",
        };

        var fragment = new System.Text.Json.Nodes.JsonObject
        {
            ["title"] = chipName,
            ["attributes"] = attrs,
            ["description"] = "TO-92 3-pin part",
            ["tags"] = new System.Text.Json.Nodes.JsonObject
            {
                ["parent_tag"] = new System.Text.Json.Nodes.JsonArray(),
                ["child_tag"] = new System.Text.Json.Nodes.JsonArray(),
            },
            ["images"] = new System.Text.Json.Nodes.JsonArray(""),
            ["source"] = "67204415f0444fada7b9208ff9e12036|0819f05c4eef4c71ace90d822a990e87",
            ["version"] = "1660159279",
            ["custom_tags"] = "[\"Power Management\"]",
        };

        return fragment.ToJsonString();
    }

    /// <summary>
    /// Synthesise a TO-92 symbol fragment for project.json (type:2).
    /// </summary>
    private static string BuildTo92SymbolFragment(string chipName)
    {
        var fragment = new System.Text.Json.Nodes.JsonObject
        {
            ["source"] = "ecd861af66314767861cd03d2a6b0962|0819f05c4eef4c71ace90d822a990e87",
            ["desc"] = "",
            ["tags"] = new System.Text.Json.Nodes.JsonObject
            {
                ["parent_tag"] = new System.Text.Json.Nodes.JsonArray(),
                ["child_tag"] = new System.Text.Json.Nodes.JsonArray(),
            },
            ["custom_tags"] = "[\"Power Management\"]",
            ["title"] = chipName,
            ["version"] = "1727062214",
            ["type"] = 2,
        };
        return fragment.ToJsonString();
    }

    private static string DeterministicUuid(string seed)
    {
        byte[] hash = System.Security.Cryptography.SHA1.HashData(
            System.Text.Encoding.UTF8.GetBytes(seed));
        var sb = new System.Text.StringBuilder(32);
        for (int i = 0; i < 16; i++) sb.Append(hash[i].ToString("x2"));
        return sb.ToString();
    }

    // ---------------------------------------------------- capacitor synthesis
    //
    // Both kinds of capacitor follow the resistor Frankenstein pattern:
    // one library device per dielectric, value text overridden per
    // instance via the COMPONENT's Value ATTR. The two kinds differ
    // only in their .esym / .efoo resources and UUIDs -- the synthesis
    // logic, label offsets, and pin positions are identical because
    // the source symbols share the same body geometry (BBOX height,
    // pin spacing ±15 EDA units).
    //
    // Placeholder LCSC data: the non-polarised template carries the
    // 10nF film cap MPN, and the polarised template carries the 220µF
    // electrolytic MPN. Same reason as resistors -- EasyEDA's PCB
    // router refuses to route parts with blank stock numbers, and we
    // don't use the BOM for production so the placeholder is harmless.

    private static CataloguePart BuildCapacitorPart(Device device)
    {
        string label = ReadPassiveValue(device);

        string displayValue;
        try
        {
            displayValue = CapacitorValueParser.FormatForDisplay(label);
        }
        catch (FormatException ex)
        {
            throw ValueError(device, "capacitor", label, ex,
                "Supported value forms: 100 (=100pF), 100p, 10n, 1u, 4u7, 4.7u, 1m.");
        }

        var symbolTokens = new Dictionary<string, string>
        {
            ["@@PART_ID@@"] = "Capacitor.1",
            ["@@SYMBOL_NAME@@"] = "Capacitor",
        };

        return new CataloguePart(
            DeviceUuid: CapacitorDeviceUuid,
            SymbolUuid: CapacitorSymbolUuid,
            SymbolResourceName: "capacitor.esym",
            FootprintUuid: CapacitorFootprintUuid,
            FootprintResourceName: "capacitor-film.efoo",
            PartTitle: "Capacitor.1",
            PinLocalPositions: new()
            {
                // capacitor.esym pins rescaled to ±20 (40px span) to match
                // CapacitorUnit's TTLSim pin span (Size.Width 4 × 10px).
                // Previously ±15, mismatching TTLSim's 40px and producing
                // diagonal wires (same pitch bug as the DIPs). NUMBER=1 at
                // left, =2 at right.
                [1] = new Point(-20, 0),
                [2] = new Point(+20, 0),
            },
            EmitValueLabel: true,
            ValueOverride: displayValue,
            InlineDeviceJson: BuildCapacitorDeviceFragment(),
            InlineSymbolJson: BuildCapacitorSymbolFragment(),
            SymbolTemplateTokens: symbolTokens,
            // Initial guess based on resistor offsets, adjusted for the
            // narrower body (BBOX X = ±5.5 vs resistor's ±10) and taller
            // body (BBOX Y = ±8.5 vs resistor's ±4). Expect hand-tuning
            // in EasyEDA per the §0 procedure once a real export round-trips.
            LabelOffsets: new LabelOffsetsByRotation(
                Rot0: new LabelOffsetSet(new(-10, +15), new(-15, -25), new(-10, +10)),
                Rot90: new LabelOffsetSet(new(-25, 0), new(-15, -25), new(-25, -5)),
                Rot180: new LabelOffsetSet(new(-10, +15), new(-15, -25), new(-10, +10)),
                Rot270: new LabelOffsetSet(new(-25, 0), new(-15, -25), new(-25, -5))));
    }

    private static CataloguePart BuildPolarizedCapacitorPart(Device device)
    {
        string label = ReadPassiveValue(device);

        string displayValue;
        try
        {
            displayValue = CapacitorValueParser.FormatForDisplay(label);
        }
        catch (FormatException ex)
        {
            throw ValueError(device, "polarised capacitor", label, ex,
                "Supported value forms: 100 (=100pF), 100p, 10n, 1u, 4u7, 4.7u, 1m.");
        }

        var symbolTokens = new Dictionary<string, string>
        {
            ["@@PART_ID@@"] = "PolarizedCapacitor.1",
            ["@@SYMBOL_NAME@@"] = "PolarizedCapacitor",
        };

        return new CataloguePart(
            DeviceUuid: PolarizedCapacitorDeviceUuid,
            SymbolUuid: PolarizedCapacitorSymbolUuid,
            SymbolResourceName: "polarized-capacitor.esym",
            FootprintUuid: PolarizedCapacitorFootprintUuid,
            FootprintResourceName: "polarized-capacitor.efoo",
            PartTitle: "PolarizedCapacitor.1",
            PinLocalPositions: new()
            {
                // polarized-capacitor.esym pins rescaled to ±20 (40px span)
                // to match PolarizedCapacitorUnit's TTLSim pin span
                // (Size.Width 4 × 10px). Previously ±15, mismatching
                // TTLSim's 40px and producing diagonal wires (same pitch
                // bug as the DIPs). NUMBER=1 (the "+" pin) on the left
                // matches TTL Sim's pin 1; no swap.
                [1] = new Point(-20, 0),
                [2] = new Point(+20, 0),
            },
            EmitValueLabel: true,
            ValueOverride: displayValue,
            InlineDeviceJson: BuildPolarizedCapacitorDeviceFragment(),
            InlineSymbolJson: BuildPolarizedCapacitorSymbolFragment(),
            SymbolTemplateTokens: symbolTokens,
            // Polarised body is slightly wider on the LEFT to accommodate
            // the "+" glyph (BBOX X from -10.5 to +5.5), so the R0/R180
            // offsets sit a little further left than the non-polarised case.
            LabelOffsets: new LabelOffsetsByRotation(
                Rot0: new LabelOffsetSet(new(-15, +15), new(-15, -25), new(-15, +10)),
                Rot90: new LabelOffsetSet(new(-25, 0), new(-15, -25), new(-25, -5)),
                Rot180: new LabelOffsetSet(new(-15, +15), new(-15, -25), new(-15, +10)),
                Rot270: new LabelOffsetSet(new(-25, 0), new(-15, -25), new(-25, -5))));
    }

    private static string BuildCapacitorDeviceFragment()
    {
        // Placeholder LCSC data: a 100pF ceramic disc matching the new
        // smaller footprint. The displayed value is overridden per
        // instance via Value ATTR; this template Value is the fallback if
        // any tool reads the device entry directly.
        //
        // The 3D Model attributes reference an EasyEDA cloud-library 3D
        // asset by UUID -- the geometry isn't embedded in our export,
        // EasyEDA resolves it at render time. The Transform string is
        // copied verbatim from the source .epro and aligns the 3D body
        // with the footprint's pad pattern.
        var attrs = new System.Text.Json.Nodes.JsonObject
        {
            ["LCSC Part Name"] = "100pF 50V \u00b110%",
            ["Supplier Part"] = "C249147",
            ["Manufacturer"] = "Walsin",
            ["Manufacturer Part"] = "N06B1B101KN0B0S0B0",
            ["Supplier Footprint"] = "\u8f74\u5411\u5f15\u7ebf",
            ["JLCPCB Part Class"] = "Extended Part",
            ["Supplier"] = "LCSC",
            ["Add into BOM"] = "yes",
            ["Convert to PCB"] = "yes",
            ["Symbol"] = CapacitorSymbolUuid,
            ["Designator"] = "C?",
            ["Footprint"] = CapacitorFootprintUuid,
            ["3D Model"] = Capacitor3dModelUuid,
            ["3D Model Title"] = "CAP-TH_BD5.5-W2.5-P5.0",
            ["3D Model Transform"] = "230.016,106.096,0,0,0,0,-0.005,0,-137.795",
            ["Type"] = "Ceramic Disc Capacitor",
            ["Value"] = "100pF",
            ["Tolerance"] = "\u00b110%",
            ["Description"] = "Non-polarised through-hole capacitor -- value supplied per instance via Value ATTR override",
        };

        var fragment = new System.Text.Json.Nodes.JsonObject
        {
            ["title"] = "Capacitor",
            ["attributes"] = attrs,
            ["description"] = "Non-polarised through-hole capacitor -- value supplied per instance via Value ATTR override",
            ["tags"] = new System.Text.Json.Nodes.JsonObject
            {
                ["parent_tag"] = new System.Text.Json.Nodes.JsonArray(),
                ["child_tag"] = new System.Text.Json.Nodes.JsonArray(),
            },
            ["images"] = new System.Text.Json.Nodes.JsonArray(""),
            ["source"] = CapacitorDeviceUuid + "|0819f05c4eef4c71ace90d822a990e87",
            ["version"] = "1748390400",
            ["custom_tags"] = "[\"Capacitors\"]",
        };
        return fragment.ToJsonString();
    }

    private static string BuildCapacitorSymbolFragment()
    {
        var fragment = new System.Text.Json.Nodes.JsonObject
        {
            ["source"] = CapacitorSymbolUuid + "|0819f05c4eef4c71ace90d822a990e87",
            ["desc"] = "",
            ["tags"] = new System.Text.Json.Nodes.JsonObject
            {
                ["parent_tag"] = new System.Text.Json.Nodes.JsonArray(),
                ["child_tag"] = new System.Text.Json.Nodes.JsonArray(),
            },
            ["custom_tags"] = "[\"Capacitors\"]",
            ["title"] = "Capacitor",
            ["version"] = "1748390400",
            ["type"] = 2,
        };
        return fragment.ToJsonString();
    }

    private static string BuildPolarizedCapacitorDeviceFragment()
    {
        // Placeholder LCSC data: a 10µF 50V radial electrolytic matching
        // the new smaller footprint. Same Value-ATTR-override pattern as
        // the non-polarised capacitor.
        //
        // The 3D Model attributes reference an EasyEDA cloud-library 3D
        // asset by UUID -- the geometry isn't embedded in our export,
        // EasyEDA resolves it at render time. The Transform string is
        // copied verbatim from the source .epro and aligns the 3D body
        // (D5mm x H7mm radial can) with the footprint's pad pattern.
        var attrs = new System.Text.Json.Nodes.JsonObject
        {
            ["LCSC Part Name"] = "10\u00b5F 50V \u00b120%",
            ["Supplier Part"] = "C88726",
            ["Manufacturer"] = "Rubycon",
            ["Manufacturer Part"] = "50YXF10MEFC5X11",
            ["Supplier Footprint"] = "\u63d2\u4ef6",
            ["JLCPCB Part Class"] = "Extended Part",
            ["Supplier"] = "LCSC",
            ["Add into BOM"] = "yes",
            ["Convert to PCB"] = "yes",
            ["Symbol"] = PolarizedCapacitorSymbolUuid,
            ["Designator"] = "C?",
            ["Footprint"] = PolarizedCapacitorFootprintUuid,
            ["3D Model"] = PolarizedCapacitor3dModelUuid,
            ["3D Model Title"] = "CAP-TH_D5.0\u00d7H7.0\u00d7P2.0",
            ["3D Model Transform"] = "197.091,196.7911,0,0,0,0,0,0,-137.795",
            ["Type"] = "Aluminium Electrolytic Capacitor",
            ["Value"] = "10\u00b5F",
            ["Tolerance"] = "\u00b120%",
            ["Description"] = "Polarised through-hole electrolytic capacitor -- value supplied per instance via Value ATTR override",
        };

        var fragment = new System.Text.Json.Nodes.JsonObject
        {
            ["title"] = "PolarizedCapacitor",
            ["attributes"] = attrs,
            ["description"] = "Polarised through-hole electrolytic capacitor -- value supplied per instance via Value ATTR override",
            ["tags"] = new System.Text.Json.Nodes.JsonObject
            {
                ["parent_tag"] = new System.Text.Json.Nodes.JsonArray(),
                ["child_tag"] = new System.Text.Json.Nodes.JsonArray(),
            },
            ["images"] = new System.Text.Json.Nodes.JsonArray(""),
            ["source"] = PolarizedCapacitorDeviceUuid + "|0819f05c4eef4c71ace90d822a990e87",
            ["version"] = "1748390400",
            ["custom_tags"] = "[\"Capacitors\",\"Aluminium Electrolytic\"]",
        };
        return fragment.ToJsonString();
    }

    private static string BuildPolarizedCapacitorSymbolFragment()
    {
        var fragment = new System.Text.Json.Nodes.JsonObject
        {
            ["source"] = PolarizedCapacitorSymbolUuid + "|0819f05c4eef4c71ace90d822a990e87",
            ["desc"] = "",
            ["tags"] = new System.Text.Json.Nodes.JsonObject
            {
                ["parent_tag"] = new System.Text.Json.Nodes.JsonArray(),
                ["child_tag"] = new System.Text.Json.Nodes.JsonArray(),
            },
            ["custom_tags"] = "[\"Capacitors\",\"Aluminium Electrolytic\"]",
            ["title"] = "PolarizedCapacitor",
            ["version"] = "1748390400",
            ["type"] = 2,
        };
        return fragment.ToJsonString();
    }
}