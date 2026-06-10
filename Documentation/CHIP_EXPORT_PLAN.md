# EasyEDA Export — Plan for Passives Cleanup and First-IC Support

This document describes how to (1) collapse the existing per-value
resistor catalogue to a single Frankenstein device, (2) add ceramic
and electrolytic capacitors using the same Frankenstein pattern, and
(3) get **TTL Sim's first two ICs** — **74LS02** and **NE555** —
exporting cleanly to EasyEDA Pro. It is written to Future Claude as
the working notes for picking up where this conversation left off,
and as the on-ramp for supporting every chip in the catalogue
(≈40 of them) without per-chip work.

Read `EasyEDA_Export.md` first — especially §0 (the debugging
procedure), §4 (multi-part background, which we are *not* using
here), and the Switch/Pushbutton Frankenstein subsection in §5
(this work uses the same templating pattern, expanded).

## 1. What the work is

This plan covers two pieces of work, sequenced:

**Step 0 — Passive simplification.** Collapse the existing 71-row
per-value resistor catalogue to a single Frankenstein device entry
(same pattern as the switch and pushbutton), and add ceramic and
electrolytic capacitors as Frankenstein parts in the same style.
This unblocks `NE555_1Hz_Astable.ttlproj` (which uses non-E12
resistor values and capacitors) and removes a substantial
maintenance liability. See §3a.

**Steps A and B — Chip export.** Add support for chip-style devices
— both `IcPartDefinition` (gate ICs like 74LS02) and
`ChipPartDefinition` (named-pin ICs like NE555) — by rendering each
chip as a single DIP-N box in the schematic, with all N pins shown
regardless of how many units the user has placed on the canvas.

Not in scope: multi-part export (one schematic symbol per gate
unit, sharing one DIP footprint). That mechanism is documented in
`EasyEDA_Export.md` §4 but defers behind this DIP-box approach.
Once DIP-box works, multi-part becomes an optional refinement for
parts where the user prefers gate-style capture.

The approach for chips is the same templating pattern the Switch
and Pushbutton Frankenstein parts use, generalised:

- Borrow a real EasyEDA library DIP-N schematic symbol and footprint
  pair, verbatim, from a hand-saved `.epro` reference file.
- Insert placeholders in the `.esym` for the per-chip values
  (PART id, Symbol ATTR value, each pin's NAME ATTR).
- The `.efoo` stays verbatim — pad numbers 1..N are independent of
  pin names, and DIP-N is a real standardised physical part.
- Per-chip device manifest entry and per-chip symbol manifest entry
  are synthesised inline in C# from `Device.FullPartNumber`, with
  deterministic UUIDs (SHA-1 of a namespaced seed string) so
  re-exports are byte-identical.

One template + footprint pair per DIP pin count covers every chip
of that pin count.

## 2. Test circuits and what they prove

Two `.ttlproj` files in the project root drive this work, in order:

### Step A: `BE_6b_SRLatch_7402.ttlproj` — the DIP-14 test

Ben Eater 8-bit CPU Module 6 SR-latch. Parts:

- 1× 74LS02 (`IcPartDefinition`, DIP-14, gate-style)
- 2× pushbuttons (already supported)
- 2× LEDs (already supported)
- 4× resistors (already supported)

This is the **first DIP-14 export** and the **first
`IcPartDefinition` export**. It exercises:

- Template-substitution machinery for chip `.esym`s.
- Pin name derivation from `UnitSpec[]` (gate-IC chips don't carry
  named pins; names like `aA`/`aB`/`aY` come from gate letter +
  input/output role).
- Partial unit usage — gates `a` and `b` are wired in the SR latch;
  gates `c` and `d` are unused. The exported chip must still appear
  as a complete DIP-14 with all 14 pins.
- Chip-to-passive routing — first end-to-end test of wires going
  from a chip pin to a resistor/LED/button pin.

Designed to be the smallest possible exercise of every new code
path. Resistors in this project are all standard E12 values
(`470Ω`, `10kΩ`, etc.) so the existing catalogue covers them.

### Step B: `NE555_1Hz_Astable.ttlproj` — the DIP-8 test

Classic 555 astable, 1 Hz. Parts:

- 1× NE555 (`ChipPartDefinition`, DIP-8, named-pin)
- 2× resistors (R1 = 7.2 kΩ, R2 = 720 kΩ)
- 2× capacitors (C1 = 1 µF, C2 = 10 nF)

This circuit needs **two pieces of work that don't exist yet** —
the Step 0 passive simplification (which absorbs the
non-E12-resistor problem and introduces capacitor support) and the
DIP-8 template. **Do not attempt Step B until both are landed.**
Listed in §3.

Once those land, Step B exercises:

- DIP-8 template (one new resource pair on top of the Step A
  infrastructure — trivial).
- `ChipPartDefinition` rendering (vs Step A's `IcPartDefinition`).
- Named pins from `ChipPin[]` rather than derived from gate units.
- Capacitors as a new passive family.

## 3. Prerequisites and Step 0

### 3a. Step 0: Passive simplification (do this first)

Before any chip work begins, collapse the resistor catalogue and add
the capacitor parts. This is its own session — do not mix it with
chip work. It touches `EasyEdaCatalogue.cs`, `EasyEdaProjectManifest.cs`,
and the embedded resistor `.esym` template, and it changes the export
output for every project that contains resistors. Mixing it with
unrelated changes makes regression bisection impossible.

#### Resistor catalogue: Frankenstein simplification

**Decision (May 2026, Michael):** the existing 71-row `VoCatalogue` in
`EasyEdaCatalogue.cs` is being replaced with a single Frankenstein
resistor entry, mirroring the pattern used for the switch and
pushbutton. The 71-row table was built on the hope that EasyEDA's 3D
viewer would render the actual coloured resistor bands per value;
it doesn't, so the per-value LCSC stock-keeping units, MPNs, datasheet
URLs, and deterministic UUIDs are all overhead with no payoff.

Effect on `NE555_1Hz_Astable.ttlproj`: the non-E12 values
**R1 = 7.2 kΩ** and **R2 = 720 kΩ** stop being a problem. Any value
the parser accepts works without a catalogue entry.

The displayed value text comes from each placed COMPONENT's `Value`
ATTR override (set per instance from the parsed resistance), NOT from
the shared device template's Value field. The standard per-instance
attribute override mechanism documented in `EasyEDA_Export.md` §2.

**LCSC placeholder gotcha:** the new shared device entry CANNOT have
blank `Supplier Part`, `Manufacturer Part`, etc. EasyEDA Pro's PCB
router refuses to route components without an LCSC stock number, so
the new device entry carries a hard-coded `10kΩ` placeholder (`VO
CR1/4W-10K±5%-ST52`, LCSC `C2894649`). Every resistor on every
schematic now imports as that 10kΩ from a BOM perspective. The BOM
is wrong — but we don't use it for production. Anyone who needs an
accurate BOM has to manually substitute MPNs in EasyEDA after import.

**What was deleted:**

- `VoCatalogue` (71 rows of `VoResistor` records).
- `BuildResistorPart` (replaced — same name, different body).
- `BuildResistorDeviceFragment` (replaced — no parameters, no per-value
  data).
- `BuildResistorSymbolFragment` (replaced — no parameters).
- The `internal sealed record VoResistor(...)` type itself.
- `SupportedResistorKeys` (was not referenced anywhere else; confirmed
  before deletion).

**What was added:**

- `ResistorDeviceUuid` and `ResistorSymbolUuid` constants in
  `EasyEdaCatalogue` — fixed UUIDs shared by every resistor instance.
  `ResistorFootprintUuid` survived from before.
- `CataloguePart.ValueOverride` field (string, default null). When
  `EmitValueLabel` is true and `ValueOverride` is non-null, the sheet
  writer emits the override directly into the COMPONENT's `Value`
  ATTR. When null, it falls back to the original "let EasyEDA look up
  the template Value" behaviour.
- One new resistor device entry and one new resistor symbol entry in
  `device_fragments.json`. Both carry the LCSC placeholder data.
- `ResistorValueParser.FormatForDisplay(string)` — turns user-typed
  input ("2k2", "100", "1M5") into display text ("2.2kΩ", "100Ω",
  "1.5MΩ"). Calls `Normalise` internally; the canonicaliser is
  unchanged. New public API alongside the existing `Normalise`.
- Sheet writer's `EmitValueLabel` block reads
  `part.ValueOverride` and either emits it directly or falls back to
  `value=null` (template lookup).

**Regression validation done:** N/A as of writing — the test is to
re-export every existing `.ttlproj` that uses resistors and confirm
it imports into EasyEDA without errors and without grey-rendered
resistors. The grey-render gotcha (`"Add into BOM": "yes"`) applies
here too — the new entry has it correctly.

#### Capacitor support

**Decision (May 2026, Michael):** capacitors land as **two**
Frankenstein parts — `Capacitor` (non-polarised, film/ceramic) and
`PolarizedCapacitor` (electrolytic). Both share designator prefix
`"C"` per industry convention; designator allocation just finds the
next free `C<n>` across both kinds.

**Modelled on** `CapacitorUnit` for the existing canvas body
geometry and `DiodeUnit` for the polarity convention (pin 1 = the
polarised pin, named `"+"` instead of `"1"`).

What landed:

- `UnitKind.PolarizedCapacitor` enum value alongside `Capacitor`.
- `PassivePartDefinition.PolarizedCapacitor` singleton with prefix
  `"C"`.
- `PolarizedCapacitorUnit` class, structurally close to
  `CapacitorUnit`: same 4×2 cell footprint, same `RoutingBounds`,
  same `DrawPassiveLabels` invocation. The differences are in
  `DrawShape`: left plate stays straight (positive terminal),
  right plate is drawn as a shallow rightward-opening arc (negative
  terminal), and a `+` glyph sits above-left of the body near pin 1.
- `DeviceFactory`, `SchematicDtoMapper`, and `SchematicSerializer`
  all get the new kind added in their identifier maps and unit-
  construction switches.
- `CapacitorValueParser` — sibling of `ResistorValueParser` for
  capacitance. Accepts `100`, `100p`, `100pF`, `10n`, `10nF`,
  `1u`, `1uF`, `1µF`, `4u7`, `4.7u`, `1m`, etc. Bare-integer input
  defaults to **picofarads** (EE schematic convention: `100` next
  to a cap means 100pF). `FormatForDisplay` returns
  `"100pF"`/`"10nF"`/`"1µF"`/`"4.7µF"`.
- Two new `CataloguePart` instances in `EasyEdaCatalogue`:
  `BuildCapacitorPart` and `BuildPolarizedCapacitorPart`. Both follow
  the resistor Frankenstein pattern: one shared device UUID per
  dielectric, displayed capacitance via per-instance Value ATTR
  override.
- Two new `.esym` templates and two new `.efoo` footprint resources:
  - `capacitor.esym` (from EasyEDA's `MPP103J41LC407LC` 10nF film cap)
  - `polarized-capacitor.esym` (from `ERJ1VM221F12OT` 220µF radial)
  - `capacitor-film.efoo` (from `CAP-TH_L10.0-W5.0-P7.50-D1.2`)
  - `polarized-capacitor.efoo` (from `CAP-TH_BD8.0-P3.50-D0.6-FD`)
  All extracted verbatim from the May 2026 `Capacitor_templates.epro`.
- Four new entries in `device_fragments.json` (two devices, two
  symbols) plus the two new footprint entries.

**On the footprint-variants question (§8 q6):** deferred. Each
dielectric gets one footprint for now (the one its source template
uses). If a project comes up needing a different physical size, add
another `PolarizedCapacitorPart` variant beside this one rather than
parameterising. The TBD comment in §8 stays.

**On polarity enforcement (§8 q5):** the model carries polarity at
the **unit class** level, not as a flag on `CapacitorUnit`.
`PolarizedCapacitorUnit` is its own class, with pin 1 named `"+"`
and pin 2 named `"-"`. The simulator treats both kinds as no-ops
for digital simulation (capacitors don't drive nets); polarity
matters only for the schematic capture and the resulting PCB layout.



### 3b. DIP-8 template extraction

Same pattern as DIP-14 (see §5). A 555-in-DIP-8 reference would
typically be NE555P / NE555N / TLC555 (any narrow-DIP 8-pin
sample). Required:

- Schematic symbol from EasyEDA's library, narrow-DIP (0.3" /
  `LS7.6mm` lead spacing), body BBOX of approximately ±35 × ±25
  (extrapolating the DIP-14 ±35 × ±40 convention down to 8 pins
  at 10-unit pitch).
- Footprint: narrow-DIP-8 `LS7.6` to match the rest of the family.

Michael will need to author or source the reference `.epro`.

## 4. The full sequenced plan

Do these in order. **Do not batch.** Each step ends with a
verification against a test circuit before moving on.

| Step | Work | Validates against |
|------|------|-------------------|
| 0.1 | Collapse resistor catalogue to one Frankenstein device (see §3a). **Code landed, regression validated.** | `BE_6b_SRLatch_7402.ttlproj` exported and verified clean (May 2026). |
| 0.2 | Add non-polarised + polarised capacitor Frankenstein parts (see §3a). **Code landed; regression validation pending.** | TBD: small capacitor-only test project, or `NE555_1Hz_Astable.ttlproj` once DIP-8 lands |
| A.1 | Build the chip-templating infrastructure (see §5–6) | (no validation yet) |
| A.2 | Extract DIP-14 narrow `.esym` + `.efoo` resources | (no validation yet) |
| A.3 | Wire dispatcher branch for `IcPartDefinition` | `BE_6b_SRLatch_7402.ttlproj` |
| B.1 | Extract DIP-8 narrow `.esym` + `.efoo` resources (see §3b) | (no validation yet) |
| B.2 | Wire dispatcher branch for `ChipPartDefinition` | `NE555_1Hz_Astable.ttlproj` |
| Later | Extract more DIP templates as needed (DIP-16, DIP-20, DIP-24, DIP-28) | (per chip) |

Step 0 lands before Step A because the passive simplification
changes the export output for every project containing resistors,
and bisecting a regression is dramatically easier when only one
thing changed. Step A then builds on a stable passive baseline.
Step B reuses Step A's infrastructure with one new resource pair
and one new dispatcher branch.

## 5. Template source files

Hand-picked from `TTL_ICs.epro` (Michael's second upload, May 2026)
for visual consistency:

- **DIP-14 narrow**: `XD74LS74` symbol (UUID
  `177984e075d340a482a198c899c6f73d`) paired with
  `DIP-14_L19.4-W6.4-P2.54-LS7.6-BL` footprint (UUID
  `e8066c4631da4c19b74351636979622f`). BBOX ±35 × ±40, pins at
  `(±45, y)` for `y ∈ {+30, +20, +10, 0, -10, -20, -30}`, label
  edge at `X = ±31.3`.
- **DIP-16 narrow**: `CD4060BE` symbol with
  `DIP-16_L20.0-W6.4-P2.54-LS7.6-BL` footprint. Same `±35` body
  width.
- **DIP-20 narrow**: `SN74HC244N` symbol with
  `DIP-20_L26.8-W6.4-P2.54-LS7.6-BL` footprint. Same `±35` body
  width.

Not yet sourced, will be needed later:

- **DIP-8 narrow** — for NE555 (Step B). Michael to source.
- **DIP-24 narrow** — for HC181 (later — ALU projects).
- **DIP-28 wide** (0.6" / `LS15.24mm`) — for 28C-series memory
  parts (later — Ben Eater CPU full build). Note: 28-pin DIPs are
  almost always **wide** package, unlike the narrow DIP-14/16/20.

Template files **rejected** as inconsistent with the rest:

- `CD4011BE` (DIP-14, BBOX ±30, `LS10.9` wide-DIP) — wrong lead
  spacing for 74-series.
- `SN74LS245N` (DIP-20, BBOX ±30, 10mm body width) — non-standard
  package width.

The consistent narrow-DIP convention is **BBOX ±35 body width, pin
labels at ±31.3, pin endpoints at ±45, `LS7.6` lead spacing**. New
templates must match this or the visual style will diverge across
the catalogue.

## 6. Code structure

Three new files plus two edits.

### New: `EasyEda/ChipCatalogueEntryBuilder.cs`

Static class with one entry point:

```csharp
public static CataloguePart Build(Device device);
```

Internally branches on `device.Definition`:

- `ChipPartDefinition cp` → use `cp.PinCount`, `cp.BodyWidth`,
  `cp.Pins[]` directly.
- `IcPartDefinition ic` → derive pin names from `ic.Units[]`:
  power pin gets `"VCC"`, ground gets `"GND"`, each gate's pins
  get letter-prefixed names (`aA`, `aB`, `aY` for gate `a`,
  etc.). See §7 for the derivation rule.

The returned `CataloguePart` carries:

- `SymbolUuid = Sha1Uuid("chip-symbol|" + device.FullPartNumber)`
- `DeviceUuid = Sha1Uuid("chip-device|" + device.FullPartNumber)`
- `FootprintUuid` = a hard-coded constant per pin count
  (`Dip14NarrowFootprintUuid`, `Dip8NarrowFootprintUuid`, …).
- `SymbolResourceName = "dip-14-narrow.esym"` etc.
- `FootprintResourceName = "dip-14-narrow.efoo"` etc.
- `PartTitle = device.FullPartNumber + ".1"` (e.g.
  `"74LS02.1"`).
- `SymbolTemplateTokens` map: `@@PART_TITLE@@` →
  `FullPartNumber`, `@@PIN1_NAME@@` → derived pin 1 name, …,
  `@@PIN14_NAME@@` → derived pin 14 name.
- `InlineDeviceJson` — synthesised manifest fragment, see §6.3.
- `InlineSymbolJson` — synthesised manifest fragment, see §6.3.
- `PinLocalPositions` — DIP layout, deterministic from PinCount:
  pin 1 at `(-45, (PinCount/2 - 1) * 5)` descending in steps of
  10 to pin (PinCount/2) at the bottom-left, then mirroring up
  the right side from pin (PinCount/2 + 1) at the bottom-right
  to pin PinCount at the top-right.
- `EmitNameOverride: true` so `Device.Label` (the user's
  "Bob"/"Eric") becomes the `Name` ATTR.
- `LabelOffsets` — initial guesses based on the DIP-14 template's
  geometry; expect hand-tuning after first export per §0
  procedure.

### New: `EasyEda/Resources/dip-14-narrow.esym`

The `XD74LS74` symbol with placeholders. The substitutions to
make in the verbatim source:

| Source line content | Replace with |
|---|---|
| `["PART","XD74LS74.1",…]` | `["PART","@@PART_TITLE@@.1",…]` |
| `["ATTR","e1","","Symbol","XD74LS74",…]` | `…"Symbol","@@PART_TITLE@@",…` |
| `["ATTR","e5","e4","NAME","1CLR",…]` | `…"NAME","@@PIN1_NAME@@",…` |
| `["ATTR","e9","e8","NAME","1D",…]` | `…"NAME","@@PIN2_NAME@@",…` |
| (…and so on for pins 3 through 14) | |

Everything else stays verbatim: LINESTYLE, FONTSTYLE rows, the
RECT body, the pin-1 indicator CIRCLE, every `PIN` record's
coordinates, every `NUMBER` ATTR (the pin numbers `"1"` through
`"14"` stay literal — they're position-locked).

### New: `EasyEda/Resources/dip-14-narrow.efoo`

`DIP-14_L19.4-W6.4-P2.54-LS7.6-BL` footprint, verbatim. No
substitution.

### Edit: `EasyEda/EasyEdaCatalogue.cs`

Add two dispatcher branches in `LookupForDevice`:

```csharp
ChipPartDefinition => ChipCatalogueEntryBuilder.Build(device),
IcPartDefinition => ChipCatalogueEntryBuilder.Build(device),
```

Add UUID constants for each DIP footprint resource that ships:

```csharp
private const string Dip14NarrowFootprintUuid
    = "e8066c4631da4c19b74351636979622f";  // from TTL_ICs.epro
```

### Edit: `device_fragments.json`

Add **one footprint entry per DIP size**, taken verbatim from the
`TTL_ICs.epro` project.json. **No device or symbol entries** —
those are inlined per-chip from `InlineDeviceJson` /
`InlineSymbolJson`.

### 6.3 Inline device/symbol fragments

Per chip, `ChipCatalogueEntryBuilder` builds two JSON fragments,
mirroring what the resistor catalogue does:

**Device fragment** (mimicking the resistor pattern):

```json
{
  "title": "{FullPartNumber}",
  "attributes": {
    "Symbol": "{SymbolUuid}",
    "Footprint": "{FootprintUuid}",
    "Designator": "U?",
    "Manufacturer Part": "{FullPartNumber}",
    "Add into BOM": "yes",
    "Convert to PCB": "yes",
    "Description": "{Category} (e.g. \"Quad 2-input NOR gate\")"
  },
  ...standard tags/source/version fields
}
```

**Symbol fragment**:

```json
{
  "title": "{FullPartNumber}",
  "type": 2,
  ...standard fields
}
```

The `Description` field can come from the C# XML doc on each
`IcPartDefinition` / `ChipPartDefinition` static if it's
accessible at runtime, or be left blank initially.

## 7. Pin name derivation rules

### `ChipPartDefinition` chips

Direct: pin N name = `ChipPin` whose `Number == N`. The
`Pins[]` array on `ChipPartDefinition` carries
`(Name, Number, Role)` per pin, so the builder just indexes by
pin number.

### `IcPartDefinition` gate chips

Derived from `Units[]` plus the power-pin / ground-pin metadata:

- Pin N == `PowerPin` → `"VCC"`.
- Pin N == `GroundPin` → `"GND"`.
- Otherwise: walk `Units[]` until finding one that references
  pin N. The unit's `Letter` field (`'a'`, `'b'`, …) becomes the
  prefix. The position in the unit's input/output pins decides
  the suffix:
  - Input pin at index 0 → `"{letter}A"`.
  - Input pin at index 1 → `"{letter}B"`.
  - Input pin at index 2 → `"{letter}C"`.
  - …
  - Output pin → `"{letter}Y"`.

Examples for 7402 (quad NOR):

- Pin 1: gate a output → `"aY"`
- Pin 2: gate a input 0 → `"aA"`
- Pin 3: gate a input 1 → `"aB"`
- Pin 4: gate b output → `"bY"`
- Pin 5: gate b input 0 → `"bA"`
- Pin 6: gate b input 1 → `"bB"`
- Pin 7: GND
- Pin 8: gate c input 0 → `"cA"`
- Pin 9: gate c input 1 → `"cB"`
- Pin 10: gate c output → `"cY"`
- Pin 11: gate d input 0 → `"dA"`
- Pin 12: gate d input 1 → `"dB"`
- Pin 13: gate d output → `"dY"`
- Pin 14: VCC

Some `IcPartDefinition` gates have **NC** (no-connect) pins —
e.g. 7420, 7430. If walking `Units[]` finds no unit referencing
pin N, fall back to `"NC"`.

Some gate Units have `Letter == '\0'` (e.g. 7430 has a single
8-input gate). In that case omit the letter prefix and use
plain `"A"`, `"B"`, …, `"Y"`.

## 8. Things to confirm before writing code

These are the open design questions remaining from the May 2026
session. Re-check with Michael before committing to any of them.

1. **Pin name convention** for `IcPartDefinition` chips.
   Letter-prefixed (`aA`/`aB`/`aY`) was the discussed default.
   Alternatives: number-prefixed (`1A`/`1B`/`1Y`, TI datasheet
   style) or generic (`IN1`/`IN2`/`OUT`).
2. **Unused-units behaviour**. The discussed default: one DIP
   box for the whole chip regardless of how many units the
   canvas places. Confirmed reasonable; matches physical
   reality.
3. **Pin-name length tolerance**. Names like `/Cn+4` (5 chars,
   HC181) approach the limit of the `±31.3`-edge label
   positions. Initial assumption: 5 chars fits with the
   font-size-0 style; longer needs a wider body template. If
   any pin name exceeds 5 chars in `ChipPartDefinition` /
   `IcPartDefinition`, flag during the catalogue scan and pick
   a wider DIP body or shorten the name.
4. **Electrolytic footprint variants** — one conservative
   footprint per dielectric (deferred: see §3a Capacitor support).
   If a project comes up needing a physically smaller or larger
   electrolytic, the answer is to add another `CataloguePart`
   variant beside the existing one rather than parameterise.

Resolved (decided in this conversation, May 2026):

- **Resistors go Frankenstein** — one device entry, value text as
  per-instance `Value` ATTR override. Per-value catalogue deleted.
  See §3a.
- **Capacitor designator prefix** — shared `"C"` for both
  non-polarised and polarised. Industry convention.
- **Capacitor electrolytic polarity** — handled at the unit class
  level (`PolarizedCapacitorUnit` is its own class with pin 1 = `"+"`,
  pin 2 = `"-"`); not a flag on `CapacitorUnit`. Mirrors the
  `DiodeUnit` precedent.
- **One footprint per dielectric** for the first cut (deferred
  multi-size — see item 4 above).

Items 1, 2, 3 above remain open. The May 2026 conversation
proposed defaults for items 1 and 2 (letter-prefixed pin names,
one DIP box regardless of placed units) but Michael hadn't confirmed
either when work paused. Re-ask before committing.

## 9. Validation procedure

Per §0 of `EasyEDA_Export.md` — *not* "export and squint at it
in EasyEDA". The procedure for each step:

1. Export the test project.
2. Extract the resulting `.epro` zip.
3. Walk every `.esch`, `.esym`, `.efoo`, and `project.json`
   line-by-line. Don't skim.
4. Diff against the source template that was substituted. For
   the `.esym`: only placeholder positions should differ.
5. For the sheet: compare a chip's COMPONENT block against a
   known-working part's (e.g. a resistor) for ATTR structure
   parity. The `Symbol`, `Designator`, `Name`, `Device`,
   `Reuse Block`, `Group ID`, `Channel ID`, `Unique ID` ATTR
   set must be present and in the conventional order.
6. Open in EasyEDA Pro. If the chip renders grey or with
   missing pins, **do not theorise** — go back to the diff
   table.

A specific failure to watch for: when the export succeeds and
EasyEDA opens it without errors but renders the chip in **grey**.
This signals `"Add into BOM": "no"` on the device entry — the
gotcha learned during Switch/Button export (see EasyEDA_Export.md
§5 Switch/Pushbutton subsection). Default `"yes"` for chips even
though their BOM line is decorative.

## 10. Out of scope

- **Multi-part / gate-style export.** Documented in
  `EasyEDA_Export.md` §4 as a separate mechanism; not part of
  this work. Becomes relevant if Michael wants the schematic to
  show gates rather than DIP boxes.
- **PCB layer of any kind.** This work produces the schematic
  symbol and binds it to a footprint; the PCB-side rendering is
  EasyEDA's job once the device is recognised.
- **The remaining ~35 chips in the catalogue.** This document
  covers the first two. Once Step A and Step B are landed, each
  additional chip is "zero new code, one new line in the chip
  catalogue if any, plus possibly one new DIP-N template pair if
  that pin count isn't covered yet".

## 11. Next steps after this work lands

Once 74LS02 and NE555 both export cleanly:

1. **Cover the rest of the gate ICs** — HC04, HC08, HC32, HC14,
   HC00, HC10, HC20, HC30, HC86. All DIP-14, all `IcPartDefinition`.
   Each should "just work" — no new code, no new templates. The
   `Hc08Tests.cs`-style unit tests already exist for these chips on
   the simulation side; they don't need EDA-side tests separately.
2. **Cover the DIP-16 chips** — HC161, HC163, HC173, HC175, HC257,
   HC157, HC151, HC153, HC138, HC139, HC182, HC193, 74189, 74390,
   74595. Needs DIP-16 template extracted (one-time setup).
3. **Cover the DIP-20 chips** — HC574, HC273, HC377, HC373, HC299,
   HC245, HC244, HC541. Needs DIP-20 template.
4. **HC181** — needs DIP-24 narrow template, which has to be
   sourced (Michael's `TTL_ICs.epro` doesn't include one).
5. **Memory chips** — 28C16, 28C64, 28C128, 28C256, 62256. Need
   DIP-28 **wide** (0.6" / LS15.24mm) template. Distinct
   layout from the narrow templates; do not extrapolate the
   narrow geometry to 28 pins.

Each step is structurally trivial after Step A lands. The hard
work is the infrastructure; each subsequent chip is a few lines
or zero.
