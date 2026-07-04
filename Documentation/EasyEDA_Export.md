# EasyEDA Pro Export — Design Notes

Target: `File → Export EasyEDA…` in TTLSim, producing a `.epro` file that
EasyEDA Pro imports cleanly with footprints pre-bound so "Update PCB from
Schematic" works without manual intervention.

## 0. How to debug this exporter

**Read this section before touching anything else when the output is
wrong.** It exists because at least four rounds of debugging on this
exporter were burned reasoning from log messages and "the obvious cause"
when a reference file was sitting right there with the actual answer.
The rules below are the antidote.

### The procedure

When the export is wrong AND you have a reference file (an EasyEDA-
authored or hand-corrected `.epro` that imports the way you want):

1. **Extract both files completely.** Treat them as opaque ZIPs. Don't
   trust any GUI viewer to show you everything — go to the raw bytes.
   `python -m zipfile -e file.epro out/` works when `unzip` chokes on
   STORE-only archives.

2. **Dump every record, every field.** Walk every `.esch`, `.esym`,
   `.efoo`, and `project.json` line by line. Don't skim. Don't decide
   in advance which records "look relevant".

3. **Build a complete difference table.** For each record kind in each
   file, list every field that differs between ours and the reference.
   Include "cosmetic" diffs — type:2 vs type:18, presence or absence of
   an empty-string field, ATTR ordering. These are exactly the diffs
   that turn out to matter.

4. **Propose fixes for every difference in one pass.** Not "the most
   important one first then we'll see". One coherent plan that
   addresses every diff in the table.

5. **Apply the fixes together** and re-export.

6. **Verify by re-extracting** the new output and diffing against the
   reference again. The table should be empty (or contain only diffs
   you understand and accept).

### What is forbidden

- **Reasoning from a log message when the reference file is sitting
  right there.** The reference is ground truth; the log is at best a
  hint. If you've been arguing about what the code "must be doing" for
  more than ten minutes, stop and go to the reference.

- **Picking "the obvious cause" without building the diff table.** The
  obvious cause has been wrong every single time in this codebase.

- **Flip-flopping.** If you advised one thing last round and the data
  showed it was wrong, admit it explicitly before recommending the
  opposite. Don't pretend it was always your position.

- **Hurrying.** Taking shortcuts and guessing is what caused every
  bug round we've burned on this code. Do the procedure. It's slower
  per round, but it converges in one round instead of five.

### When stuck

If three rounds in a row of "fix and re-export" haven't converged,
**throw away the working theory and restart from step 3** — re-extract
both files, re-build the diff table from scratch, ignore your prior
hypotheses. The theory you're attached to is the one keeping you stuck.

## 1. Status

**Working baseline.** The exporter produces a `.epro` that EasyEDA Pro
imports without errors, with all parts placed, all wires connected (in
their TTLSim colours), and footprints pre-bound (no manual footprint
binding needed). The SmokeTest schematic (VCC → LED → GND, with a
second mirrored LED) round-trips through Export → Import → EasyEDA's
"Update PCB from Schematic" with no DRC failures relating to device
bindings.

Implemented parts: **resistor**, **LED**, **VCC** (power net flag), **GND**
(ground net flag), **headers** (2/3/4/6/8-pin, 1×N, 2.54 mm pitch, optionally
mirrored across the long axis — see §5), **net labels / bus ports** (no
component — a visible NET ATTR on the wire; see "Net labels" in §5), **switch**
(SPST rocker, off-board via 2P header — see §5), **pushbutton** (SPST
momentary tactile, off-board via 2P header — see §5). Other parts in the
catalogue throw `NotImplementedException` from `EasyEDACatalogue` until
added.

## 2. Format: `.epro`

A `.epro` file is a plain **ZIP archive** (compression method: STORE). The
`.epro` extension is just a relabel — `unzip -l` works on it. The
user-facing path to produce one from EasyEDA Pro itself is
**File → Save As Local**.

### Zip layout

```
project.json                              -- manifest tying everything together
SHEET/<schematic-uuid>/<sheet-id>.esch    -- one file per schematic sheet
SYMBOL/<symbol-uuid>.esym                 -- schematic symbol drawing
FOOTPRINT/<footprint-uuid>.efoo           -- PCB footprint drawing
SHEET/  INSTANCE/  SYMBOL/  PCB/          -- empty placeholder folders at root
FOOTPRINT/  POUR/  PANEL/  BLOB/  FONT/      (mirror what the editor writes)
```

All inner files are **plain text NDJSON** — one JSON array per line, no
base64, no gzip.

### `project.json` shape

Top-level keys: `schematics`, `pcbs`, `panels`, `symbols`, `footprints`,
`devices`, `boards`, `config`.

**Field-naming quirk worth noting**: the `symbols` and `footprints`
sections use different field names from `devices`:

- `devices` entries use `"description"` and have no `type` field.
- `symbols` entries use **`"desc"`** (not `description`) and **must
  include `"type": 2`**.
- `footprints` entries use **`"desc"`** and **must include `"type": 4`**.

Missing `type` or wrong-named `desc` makes EasyEDA reject the import
with "Device : create device failed No permission or project does not
exist", or "Found abnormal data, the Device property of the following
element is incorrect".

### Device entry — minimum required attributes

Required:

- `Symbol` — UUID reference into `symbols`
- `Footprint` — UUID reference into `footprints` (omit for net flags)
- `Designator` — auto-numbering prefix like `R?`, `D?`
- `Name` — display template, e.g. `"={Value}"` or a literal
- `Supplier` — typically `"LCSC"` (drives auto-association)
- `Supplier Part` — supplier part number (drives auto-association)
- `Manufacturer Part` — used as a fallback name lookup
- `JLCPCB Part Class` — typically `"Extended Part"`
- `Add into BOM` — `"yes"`
- `Convert to PCB` — `"yes"`
- For resistors: `Value` — placeholder, overridden per instance

Everything else from EasyEDA's library entries (LCSC Part Name,
Manufacturer, Supplier Footprint, Datasheet, 3D Model triple,
Tolerance, Voltage, Power, etc.) is decorative.

### Per-instance attribute overrides

A `COMPONENT` on the sheet can carry ATTRs that **override** any field
in its referenced device. Empty-string ATTRs blank a field; non-empty
ones replace it. This is how the resistor stays generic: the device
template holds `Value: "10k"` and `Name: "={Value}"`; each placed
resistor emits a `Value` ATTR carrying its actual value (`"220"`,
`"4.7k"`, etc.), and the displayed name follows the template.

## 3. NDJSON record shapes

Each line is `[CMD_KEY, id, ...args]`. Some records nest by referring back
to a parent id (e.g. an `ATTR` whose third field is `e4` belongs to the
`PIN` whose id is `e4`).

### Symbol (`.esym`)
```
["DOCTYPE","SYMBOL","1.1"]
["HEAD",{"symbolType":2,"originX":0,"originY":0,"version":"0.13.0"}]
["LINESTYLE","st1",...]   ["FONTSTYLE","st2",...]
["PART","Title.1",{"BBOX":[xMin,yMin,xMax,yMax]}]   -- one PART per sub-part
["ATTR",id,"","Symbol",partName,...]      -- top-level symbol attrs
["ATTR",id,"","Designator","R?",...]
["RECT"|"POLY"|"PIN",id,...]              -- body geometry for THIS part
["ATTR",id,parentPinId,"NAME"|"NUMBER"|"Pin Type",value,...]
["PART","Title.2",{"BBOX":[...]}]                   -- second sub-part starts here
... its own geometry and pins ...
```
Pin format:
`["PIN", id, ?, null, x, y, length, rotationDeg, null, 0, 0, 1]`
Rotation 0 = pin extends to the right; 180 = to the left.

### Footprint (`.efoo`)
Begins with ~20 `LAYER` rows (TOP=1, BOTTOM=2, TOP_SILK=3, MULTI=12,
etc), two `ACTIVE_LAYER` rows, then an empty line, then geometry.
Coordinates are in **0.01 mm units**. PAD records have nested arrays
for hole and mask. Through-hole pads sit on layer 12 (MULTI). We
currently ship pre-built `.efoo` files verbatim from EasyEDA's library
rather than authoring our own.

### Sheet (`.esch`)
```
["DOCTYPE","SCH","1.1"]
["HEAD",{"originX":0,"originY":0,"version":"2","maxId":N}]
["FONTSTYLE","st4",null,...]              -- styles referenced below

-- A placed regular component (resistor):
["COMPONENT",id,"PartTitle.1",worldX,worldY,rotationDeg,mirror,{},0]
```

The field after `rotationDeg` is the **mirror flag**: `0` = normal, `1` =
flipped across the vertical axis through the component anchor, applied
**after** rotation in world space. See "Headers: mirroring" in §5 for the
semantics and the emit rule.

```
["ATTR",id,parentComponentId,"Symbol",     symbolUuid,...,"st6",0]
["ATTR",id,parentComponentId,"Designator", "R1",...,"st6",0]
["ATTR",id,parentComponentId,"Name", null, ...,"st4",0]
["ATTR",id,parentComponentId,"Device",     deviceUuid,...,"st5",0]
["ATTR",id,parentComponentId,"Reuse Block", "",...,"st4",0]
["ATTR",id,parentComponentId,"Group ID",   "",...,"st4",0]
["ATTR",id,parentComponentId,"Channel ID", "",...,"st4",0]
["ATTR",id,parentComponentId,"Unique ID",  "ggXXX",...,"st4",0]
["ATTR",id,parentComponentId,"Value",      "220",...,"st4",0]  -- override

-- A placed Net Flag (VCC/GND): much slimmer
["COMPONENT",id,"",worldX,worldY,0,0,{},0]     -- title is empty
["ATTR",id,parentComponentId,"Symbol", symbolUuid,...]
["ATTR",id,parentComponentId,"Name",   "VCC",...]   -- the net name
["ATTR",id,parentComponentId,"Device", deviceUuid,...]
["ATTR",id,parentComponentId,"Relevance","[]",...]

-- A wire (one LINESTYLE per wire so colours can vary):
["LINESTYLE",styleId,"#RRGGBB",null,null,null,null]
["WIRE",id,[[x1,y1,x2,y2,...],[...]], styleId, 0]
["ATTR",id,parentWireId,"Relevance","[]",...]
["ATTR",id,parentWireId,"NET",netName,0,visible,x,y,rot,styleRef,0]
```

The wire's NET ATTR doubles as the **net label**: `visible` 0 is the
ordinary hidden name (empty or "VCC"/"GND"); `visible` 1 with a position
and rotation renders the name on the sheet — that IS a net label in
EasyEDA Pro; there is no separate record or component. Measured from the
`Nets_and_Buses.epro` reference:

```
["ATTR","e184","w1","NET","WE",0,1,410,575,0,"st1",0]
```

The same reference gives the **bus** record shapes (thick trunk, entry
ticks, colon-range name). These are graphical grouping only — the
electrical fusion is carried entirely by the per-wire bit names — and
the exporter emits them for multi-bit net-label ports (see "Net labels"
in §5 for the geometry rules):

```
["BUS",id,[[x1,y1,x2,y2],...], lineStyleRef, 0]      -- line width 2
["BUSENTRY",id,parentBusId,seq,x,y,rotDeg]           -- wire-side endpoint
["ATTR",id,parentBusId,"NET","D[0:7]",0,1,x,y,rot,styleRef,0]  -- colon range
```

Each WIRE's segments must be **axis-aligned**: each inner array is a
horizontal or vertical run. Diagonal segments are not produced by
EasyEDA's editor.

Sheet coordinates: TTLSim grid units × 10 = EasyEDA pixels. **Y is
inverted** during export — TTLSim is Y-down, EasyEDA is Y-up. Rotation
passes through unchanged (no negation needed once Y is flipped).

## 4. Multi-part devices (the key insight for gates)

EasyEDA Pro has first-class support for **multi-part devices**: one
device with one footprint but multiple schematic symbols. Designators
auto-suffix as `U1.1`, `U1.2`, `U1.3` ... Each sub-part is drawn separately
on the schematic; the PCB sees one physical chip with one DIP footprint.

This maps directly onto TTLSim's existing model: one `Device`, multiple
`Unit`s. **No editor rework needed.** Schematic stays as gates; PCB sees
the DIP. The mechanism is contained entirely in the exporter.

How it works in the file format:

- One `.esym` per chip type, containing **one `PART` record per
  `UnitSpec`**. A 7400 NAND has four `PART`s named `7400.1`..`7400.4`.
  The pins on each PART carry the physical DIP pin numbers (e.g. `.1`
  has pins 1, 2, 3; `.2` has 4, 5, 6; etc).
- One `.efoo` per chip type — a single DIP-14 footprint shared by all
  sub-parts. Pad numbers (1..14) match the pin numbers used across the
  PARTs.
- One device entry in `project.json` binding that symbol to that
  footprint. Every placed gate references this same device UUID.
- On the sheet, each `COMPONENT` references its sub-part by the
  suffixed title (`7400.1`, `7400.2` ...). All sub-parts of one chip
  share the same `Device` UUID and `Designator` ATTR — EasyEDA handles
  the `.N` suffix display itself.

Power handling for multi-unit ICs uses a dedicated **power sub-part**
(conventional EasyEDA practice). A 7400 gets a fifth `PART` (`7400.5`)
containing pins 7 (GND) and 14 (VCC), with both pins **hidden and named
GND / VCC respectively**. EasyEDA auto-connects hidden named power pins
to global nets of the same name, satisfying the "implicit VCC/GND for
multi-unit ICs" rule without us needing to emit explicit power
components on the sheet.

**Unverified.** The multi-part mechanism is documented from EasyEDA's
side and looks intuitive, but we haven't actually built a multi-part
`.epro` yet. The HC393 step in the plan below is the first test of it.

## 5. Implementation as it stands

### Dependencies
- `System.IO.Compression.ZipArchive` (.NET 8 built-in) — no NuGet needed.
- `System.Text.Json` (already used by `SchematicSerializer`).
- No SQLite dependency. Just zip + text.

### Code structure
- `TTLSim.UI/Persistence/EasyEda/EasyEDAExporter.cs` — public entry
  point `Export(Schematic schematic, string path)`. Discovers used
  parts, calls the sheet writer and manifest builder, assembles the
  zip with one `.esym` per distinct value used (applying template
  substitution where applicable).
- `TTLSim.UI/Persistence/EasyEda/EasyEDACatalogue.cs` — maps TTLSim
  parts to EasyEDA library entries. Static `CataloguePart` instances
  for LED, VCC, GND. **75 resistor entries synthesised from a table**
  keyed by canonical value string ("1R", "2K2", "10K", "1M5"…), each
  with its own MPN, LCSC part number, deterministic UUIDs, and inline
  device/symbol JSON fragments. Adding a value = one new row.
- `TTLSim.UI/Persistence/EasyEda/ResistorValueParser.cs` — normalises
  user-typed values (`"100"`, `"100R"`, `"220Ω"`, `"2k2"`, `"2K2"`,
  `"1.5K"`, `"1M5"`) into canonical lookup keys.
- `TTLSim.UI/Persistence/EasyEda/EasyEDASheetWriter.cs` — builds the
  `.esch` NDJSON for one sheet. Looks up `CataloguePart`s directly
  via `EasyEDACatalogue` per item (rather than through a pre-built
  dictionary), because per-value resistor parts make the
  PartDefinition-keyed dictionary insufficient.
- `TTLSim.UI/Persistence/EasyEda/EasyEDAProjectManifest.cs` — builds
  `project.json`. Static `device_fragments.json` supplies the LED/
  VCC/GND device entries and the shared resistor footprint;
  per-value resistor device + symbol entries come from
  `CataloguePart.InlineDeviceJson` / `InlineSymbolJson`.
- `TTLSim.UI/Persistence/EasyEda/Resources/` — embedded resources:
  - `resistor.esym` — **template** with `@@PART_ID@@` and
    `@@SYMBOL_NAME@@` placeholders; substituted at export time.
  - `resistor.efoo` — single shared YAGEO MFR-25-axial footprint.
  - `led.esym`, `led.efoo`, `vcc.esym`, `gnd.esym` — verbatim copies.
  - `device_fragments.json` — JSON device/symbol/footprint fragments
    for LED, VCC, GND, and the shared resistor footprint.
- `MainForm.cs` — `File → Export EasyEDA…` menu item invoking the
  exporter through a SaveFileDialog with filter
  `"EasyEDA Pro (*.epro)|*.epro"`.

### Coordinate / rotation handling

- TTLSim grid units → EasyEDA pixels: multiply by 10.
- Y axis: negate during export (TTLSim Y-down → EasyEDA Y-up).
- Rotation: TTLSim and EasyEDA use **opposite rotation senses** for
  asymmetric symbols. The same numeric value (e.g. 90) produces
  visually mirrored bodies. The exporter converts in
  `ComputePlacement`:
  ```csharp
  int easyEdaRotDeg = (360 - ttlSimRotDeg) % 360;
  ```
  0 and 180 are self-complementary (no change); 90 and 270 swap.
  The EasyEDA value is used for `RotatePoint`, the COMPONENT
  record's rotation field, and pin world positions, so they stay
  internally consistent with how EasyEDA renders the symbol.
- `RotatePoint` formulas are derived empirically from EasyEDA
  reference files (see §0 procedure):
  `90 ⇒ (-y, x)`, `180 ⇒ (-x, -y)`, `270 ⇒ (y, -x)`.

### Placement logic

For each placeable item:
1. Pick the first pin in its catalogue entry as the **anchor pin**.
2. Compute the TTLSim world position of that pin (via
   `Pin.WorldPosition`) and scale to EasyEDA pixels.
3. Place the EasyEDA `COMPONENT` so that its anchor pin (per the
   catalogue's `PinLocalPositions` rotated by the item's rotation)
   lands exactly on the scaled target.
4. Compute world positions for all other pins by the same offset.

Wires emit between TTLSim pin world coordinates (scaled by ×10) and
are always axis-aligned — the router (see below) only produces
orthogonal segments. Component placement is independent of wire
emission; the wire-emission pass runs after placements are computed.

### Wire colours

Each `Connection.Color` (a `WireColor` enum) is converted to its
TTLSim RGB via `WireColors.ToColor()`, formatted as `"#RRGGBB"`, and
emitted as a `LINESTYLE` record immediately before the WIRE that uses
it. The WIRE's style-id field references that LINESTYLE. One LINESTYLE
per wire — simple, and matches what EasyEDA's editor exports.

### Net Flag handling

VCC and GND symbols are emitted as EasyEDA "Net Flag" components,
which have a different ATTR shape from regular parts (no Designator,
no bookkeeping fields; just Symbol/Name/Device/Relevance). Their
`Name` ATTR carries the net name (`"VCC"` / `"GND"`). The wire
attached to a Net Flag gets a matching `NET` ATTR so EasyEDA's DRC
links them.

### Label positioning

The Designator, Name, and Value ATTRs of each placed component are
positioned by per-part, per-rotation offsets carried on
`CataloguePart.LabelOffsets`:

```csharp
public sealed record LabelOffsetsByRotation(
    LabelOffsetSet Rot0,
    LabelOffsetSet Rot90,
    LabelOffsetSet Rot180,
    LabelOffsetSet Rot270);

public readonly record struct LabelOffsetSet(
    LabelOffset Designator,
    LabelOffset Name,
    LabelOffset Value);
```

Offsets are in EasyEDA pixels relative to the COMPONENT anchor,
Y-up. Catalogue authors derive them by hand-tuning a placed
component in EasyEDA, exporting, and reading the ATTR positions
back from the saved `.epro` (see §0 procedure).

**Indexed by TTLSim rotation**, not EasyEDA rotation: the offsets
were measured against the user's drawing intent, so the same offset
applies whether or not the rotation-sense conversion has flipped
90↔270 in the COMPONENT record. The placement record carries both
rotations so `EmitComponent` can pick the right one for each use.

Two distinct ATTR shapes for the `Name` field:
- **`EmitNameOverride: true`** (LED) — Name carries the user's
  label as a visible inscription. Style `st7` (size 6),
  `keyVisible = null`. Matches what EasyEDA's editor writes when
  the user toggles Name visibility on.
- **`EmitNameOverride: false`** (resistor) — Name is the
  bookkeeping ATTR blanked to `""`. Style `st4` (default size),
  `keyVisible = 0`.

Parts with `EmitValueLabel: true` (resistor) emit an extra `Value`
ATTR with `value = null, valVisible = 1` — telling EasyEDA to
display the device template's Value at this position with style
`st7`. Without this ATTR, EasyEDA stores the templated value
internally but doesn't render it on the schematic.

### Wire emission via `WireRouter`

Wire geometry is produced by `Routing.WireRouter.RouteAll(schematic)`
— the same router the canvas uses. It returns:
- A `Polylines` dict mapping each `Connection` to its polyline (a
  list of `Point`s in TTLSim grid units, always axis-aligned).
- A `Junctions` list of grid points where three or more polylines of
  the same net meet (T-junctions and crossings).

For a net with N pins, the router emits a **star**: the longest leg
gets a full pin-to-pin polyline (the trunk), and each remaining leg
gets a polyline from its leaf pin to the *trunk's existing path*
(branches end on the trunk, not at a hub pin). The junction points
between trunk and branches go into `Junctions`.

Each Connection's polyline is then run through `BuildEasyEdaPolyline`
which scales TTLSim grid units to EasyEDA pixels and snaps the
polyline endpoints to the EasyEDA pin world positions (the EasyEDA
library symbol's pin offsets aren't always the same as TTLSim's, so
a small extender segment is sometimes inserted).

### Endpoint snap by pin proximity

`BuildEasyEdaPolyline` decides per endpoint whether the router's
polyline endpoint corresponds to a pin (and so needs to be pulled
onto the EasyEDA pin position) or to a trunk-junction cell (and
so must be emitted as-is). The decision is local — based only on
the endpoint's distance to the two pins of the Connection — and
needs no knowledge of the global net topology.

For each end:

- distance to scaled `conn.A` pin position ≤ 0.5 grid (= 5 EasyEDA
  px) → snap to `easyEdaA`
- distance to scaled `conn.B` pin position ≤ 0.5 grid → snap to
  `easyEdaB`
- neither → it's a trunk-junction cell; emit as-is

```csharp
PointF? snapStart = ClosestPinSnap(pts[0],
    gridScaledA, gridScaledB, easyEdaA, easyEdaB, PinSnapToleranceSq);
PointF? snapEnd = ClosestPinSnap(pts[^1],
    gridScaledA, gridScaledB, easyEdaA, easyEdaB, PinSnapToleranceSq);

if (snapStart.HasValue) SnapEndpoint(pts, atEnd: false, snapStart.Value);
if (snapEnd.HasValue)   SnapEndpoint(pts, atEnd: true,  snapEnd.Value);
```

If neither endpoint is within tolerance of a pin, the wire is
still emitted (as-is, no snap) and a warning is raised — the
geometry is suspect.

### Tolerance for `BuildEasyEdaPolyline`

`PinSnapToleranceSq = 25f` — 0.5 grid units (5 EasyEDA pixels)
squared. The schematic editor forces all placements onto integer
grid units, so a router endpoint that's "at a pin" lands exactly
on the scaled pin coordinate; the half-grid tolerance covers any
sub-grid drift. Anything farther than 0.5 grid from both pins is
treated as a junction cell.

### Resistor: per-value parts from a table + template

LED, VCC, and GND each get one library entry in EasyEDA. **Resistors
don't:** every value has its own LCSC stock-keeping unit, its own
manufacturer part number, its own data sheet — so each value needs
its own device entry in `project.json` and its own `.esym` in the
zip. Authoring 75 separate `.esym` files would be silly because they
differ only in two strings (the PART id and the Symbol ATTR value).

The solution is templating:

- **One shared footprint** (`resistor.efoo`): the YAGEO axial
  RES-TH_BD2.4-L6.3-P10.30-D0.6 body. Same physical pad pattern
  and 3D model for every value.
- **One shared 3D model UUID** in every device entry. EasyEDA's
  library has the model; we just reference it. (The 3D model is a
  beige cylinder — per-value coloured-band models don't exist in
  EasyEDA's library for any series we've examined.)
- **One template `.esym`** with two placeholders: `@@PART_ID@@`
  (the `PART` record's id, e.g. `"MFR-25JT-52-2K2.1"`) and
  `@@SYMBOL_NAME@@` (the `Symbol` ATTR value, e.g.
  `"MFR-25JT-52-2K2"`). String-substituted at export time.
- **One table** of 75 entries in `EasyEDACatalogue.cs` covering the
  E12 series from 1R to 10M. Each row carries `ManufacturerPart`,
  `ValueText` (e.g. `"2.2kΩ"`), `LcscPartNumber`, datasheet URL,
  and two **deterministic UUIDs** (SHA-1 of a namespaced seed
  string — re-runs of the table generator produce stable output).
- The device manifest and per-value symbol JSON fragments are
  **synthesised at lookup time** in C# from the table row, then
  carried on the `CataloguePart` via the optional
  `InlineDeviceJson` and `InlineSymbolJson` fields. The manifest
  builder uses these in preference to `device_fragments.json` when
  present.

The user-facing value string is parsed by `ResistorValueParser`,
which accepts engineering form (`"2K2"`, `"1M5"`), decimal form
(`"2.2K"`, `"1.5M"`), bare integers (`"100"` = 100Ω), and the omega
suffix (`"220Ω"`). All forms normalise to the MPN suffix style used
as the table's key (`"2K2"`, `"1M5"`, `"100R"`).

Unsupported values throw `NotImplementedException` with a clear
message naming the offending value and the canonical form it
normalised to. Add a missing value = add one row to the `Yageo`
dictionary in `EasyEDACatalogue.cs`.

### Headers: pin rotation sense and per-part Name styling

Pin headers were the first parts that exposed a structural mismatch
between TTL Sim's and EasyEDA's rotation conventions. TTL Sim
rotates symbols **clockwise** internally; EasyEDA's library headers
(and at least the asymmetric symbols we've seen) render rotated
visually **the opposite way**. The original exporter dealt with this
globally by negating rotation at export time:

```csharp
int easyEdaRotDeg = (360 - ttlSimRotDeg) % 360;
```

That works fine when the TTL Sim canvas can show whatever it likes
and only the exported `.epro` has to match EasyEDA. But for the
header symbol we wanted the **canvas to also match EasyEDA's
rotation sense**, so that an R90 placement on the canvas points the
pins the same way a user would expect from EasyEDA's library.

The chosen mechanism, scoped strictly to the header:

1. **`Pin.SwapR90R270`** (optional, default `false`). When set, the
   pin's `WorldPosition` and `Direction` properties swap R90↔R270
   before applying the standard rotation math, so wire endpoints and
   router approach directions follow the EasyEDA convention. The
   header constructor sets this on its pins; every other part keeps
   the default behaviour and renders identically to before.

2. **`HeaderOutputUnit.DrawShape`** applies an extra 180° rotation
   around the body pivot when `Rotation` is R90 or R270, so the
   *visible* body agrees with where the pins now resolve.

3. **`CataloguePart.MatchesEasyEdaRotationSense`** (default `false`).
   When set, the exporter's `ComputePlacement` skips the
   `(360 - r) % 360` shim and uses the rotation value verbatim.
   Without this, the global shim would double-invert the header's
   already-correct pin world positions and ship a wrong-rotation
   `.epro`. Every other catalogue entry leaves this `false` and is
   exported via the original shim path.

These three pieces have to stay consistent: if any later part also
uses `Pin.SwapR90R270`, its `CataloguePart` must also set
`MatchesEasyEdaRotationSense: true`, and if its `DrawShape` doesn't
do the extra 180 it'll visually disagree with where the pins are.

### Headers: mirroring (flip across the long axis)

`HeaderOutputUnit.Mirrored` flips a header across its long axis on the
TTLSim canvas — pins exit from the opposite edge of the body, pin order
and numbering unchanged. The export maps this onto EasyEDA's native
flip via the COMPONENT record's **mirror field** (the field after
rotation): `0` = normal, `1` = flipped across the vertical axis
through the component anchor.

**Renderer semantics** (measured, not inferred — both from §0
reference-pair diffs):

- At rotation 0, setting the flag negates each pin's local X about the
  anchor, Y untouched. Only that one field changes.
  (`Headers.epro` vs `Headers_Flipped.epro`.)
- The mirror is applied **after** rotation, as a world-space X flip.
  Flipping a rot-90 header vertically in EasyEDA rewrites the record
  to **rot 270 + mirror 1** — rotation negated, flag set.
  (`Headers.epro` vs `Flipped_Vertical.epro`, H6.)

**Emit rule** in `ComputePlacement` / `EmitComponent`:

1. Pin-world math is **mirror-first**: negate the catalogue pin-local
   X in the symbol frame, then `RotatePoint` as usual. This encodes
   TTLSim's drawing intent (mirror across the long axis, then rotate)
   and keeps wire snapping and No-Connect positions on the flipped
   pin endpoints for free.
2. The emitted rotation is **negated when mirrored**:
   `emitRot = (360 - r) % 360`, alongside `mirror = 1`. The two
   compositions are the same transform —
   `Rotate(r)·MirrorX == MirrorX·Rotate(360 - r)` — but EasyEDA's
   renderer only speaks the mirror-after-rotation form. At rotations
   0 and 180 the compositions coincide, so an R0-only reference
   cannot expose the difference; the H6 pair was needed to pin it
   down.

The anchor is solved by the exporter as usual (EasyEDA's own flip
pivot moves the anchor in hand-flipped files; irrelevant to us).
`LabelOffsets` are reused unchanged for mirrored placements: measured
drift in the references was ≤5 EasyEDA px (8-pin designator) or zero
(4-pin), below hand-tuning tolerance.

### Net labels (and why buses emit no BUS records)

A TTLSim `NetLabelItem` (net label / bus port) has **no EasyEDA
component**. Its entire presence on the exported sheet is the wire's
NET ATTR emitted visible and positioned at the label pin (record shape
in §3). Same-named nets fuse in EasyEDA purely by that name — the
`Nets_and_Buses.epro` reference shows four disconnected `WE` stubs
becoming one net this way, which is EasyEDA's own idiom for labels.

How the exporter handles them (`EasyEdaSheetWriter`):

- **Not placed.** `NetLabelItem` joins cosmetic items in the placeable
  filter: no COMPONENT, no ATTRs, no No-Connect flags (spare port bits
  are invisible to EasyEDA, which is correct — they exist only to tie).
  There are TWO enumeration sites that must both skip any item type
  that exports without a component: the sheet writer's placeable
  filter AND `EasyEdaExporter.CollectUsedParts` (no catalogue part, no
  embedded resource, no manifest entry). The exporter's runs FIRST —
  miss it there and export throws before the sheet writer's guards
  execute.
- **Wire endpoints.** `WorldPinPosition` answers a label pin with its
  own scaled TTLSim position, so the endpoint snap is a no-op there: no
  extender segments, and EDA002 cannot fire. The exported stub simply
  ends at the label text, exactly as a hand-drawn EasyEDA label does.
- **Net names.** Per group: net-flag name (VCC/GND) wins, then the
  label's `BitName` ("PC5", "CLK0"), else unnamed. Multi-bit ports fall
  out for free — each bit is its own group with its own per-bit name,
  which is precisely how the reference carries buses electrically. The
  visible NET text is rotated to run ALONG its wire (0 on horizontal
  stubs, 90 on vertical — the ATTR record's rotation field, which
  EasyEDA itself writes for labels on vertical wires; the editor UI has
  no manual rotate for these, but the field renders). Bus range names
  sit one cell beyond the trunk on the far side from the wires, along
  the trunk, so they clear the mid-port bit labels.
- **Diagnostics.** `EDA004` (warning) fires when one group carries two
  different label names (ordinal-first wins) or a label sits on a power
  net (the flag name wins and the visible text shows it).
- **EDA003 ids.** Groups joined only by a shared label name collapse to
  one net id for the coincident-corner check — a corner between two
  same-named clusters is same-net sharing, not a collision.

**Buses.** Multi-bit ports (width ≥ 2, two or more wired pins) emit
EasyEDA's bus graphics: one `BUS` trunk per port plus a `BUSENTRY` tick
per wired pin and a visible colon-range `NET` ATTR (`D[0:7]`, not
`D[0..7]`) at the trunk midpoint. This is cosmetic grouping only — the
electrical fusion is carried entirely by the per-bit wire NET names,
which remain visible on their stubs. Geometry (measured from the
`Nets_and_Buses.epro` reference, all four orientations): each entry
sits AT a wire endpoint with rotation pointing toward the trunk
(0 = +x, 90 = +y, 180 = −x, 270 = −y); the trunk runs exactly one grid
cell (10 px) beyond the wire ends on the pins' inward side — where
TTLSim draws the port bracket — spanning first-to-last wired entry with
no overhang, line width 2. No wire trimming is needed: our stubs
already end at the label pins. Ports with fewer than two wired pins
emit no bus. One measured quirk worth knowing when reading hand-drawn
references: EasyEDA's own bit-name derivation produced `A0]`…`A17]`
(trailing bracket) in the reference file. TTLSim never depends on that
derivation because it names every bit explicitly.

### Headers: Name label styling

The shared `Name` ATTR mechanism in `EmitComponent` historically had
two shapes — LED's "visible user inscription at size 6" and the
resistor's "bookkeeping ATTR blanked to `""`". The hand-edited
header reference (`Headers_fixed_in_EDA.epro`) introduced a third
shape: visible user label at size 8 (designator-sized), keyVisible=0,
rotation=0, sitting one EasyEDA cell to the right of the designator
on the same line.

That third shape is opt-in via a flag pair on `CataloguePart`:

- `EmitNameOverride: true` — write the user's `Label` as the Name
  ATTR value (existing flag, LED uses it).
- `NameLabelUsesDesignatorStyle: true` — render that Name with
  `StyleDesignator` (size 8) and the bookkeeping-style envelope
  (`keyVisible=0`, `rotation=0`) instead of the LED's
  `StyleValue` (size 6) envelope.

Header `LabelOffsetsByRotation` entries now populate both the
`Designator` and `Name` slots — Name = Designator offset
`+ (+10, 0)` in EasyEDA coords, putting the label one cell to the
right of the designator. An empty `Label` still produces a Name ATTR
with `value=""` (EDA renders nothing for that), preserving the
bookkeeping behaviour.

### Switch and Pushbutton: Frankenstein parts

`SwitchInput` and `ButtonInput` are off-board human-input devices — the
user flips a panel switch or presses a panel button, the PCB sees the
signal change. There's no need (or expectation) to mount the actual
rocker/tactile part on the board: the PCB just needs a connector for
the panel wiring. The export models this directly:

- **Schematic side** uses the real EasyEDA library symbols
  (`SS11-RBDWQ-R20-R` for the rocker, `SKPMAPE010` for the tactile),
  so the captured drawing reads as a switch / pushbutton and the
  user's intent is unambiguous.
- **PCB side** uses the H1 male 2.54 mm 1×2P through-hole header
  footprint (`HDR-TH_2P-P2.54-V-M-1`), shared between Switch and
  Button. The physical build is a 2-pin header that the panel
  switch / button wires into.

Both parts have their own `CataloguePart` and their own device UUID in
`device_fragments.json` (separate entries because the schematic
symbols differ). They share the male-2P-header footprint UUID, which
is distinct from `Header2FootprintUuid` (the female header used by
output pin-header components).

Symbol pin local positions come from the `.esym` files verbatim. The
switch sits at pin Y = 0; the button at pin Y = -10 because its
symbol body extends downward from its origin — the negative Y just
anchors the COMPONENT 10 EasyEDA units above the pin row, and the
symbol draws downward into the canvas pin line.

**Gotcha:** when these were first added, `"Add into BOM": "no"` was
set on the new device entries on the theory that they shouldn't
appear on a BOM (no physical switch is fitted; only the header is).
**EasyEDA Pro renders BOM-excluded components in grey with disabled
pins** — its visual signal for "this isn't part of the build". Both
new devices keep `"Add into BOM": "yes"`. If a BOM is exported, the
unwanted entries get pruned downstream — never via this flag, which
is overloaded with rendering meaning.

## 6. Known issues and improvements

The current minimal export imports cleanly and the netlist is correct,
but there are visible bugs and a handful of spec details we haven't
acted on yet. Organised by category so the ones that matter most
stand out.

### 6.1 User-visible bugs

1. **One WIRE per Connection, not per net.** Stylistic only —
   EasyEDA's renderer reconstructs net topology from segment
   endpoint coincidence regardless of how segments are grouped
   into WIRE records. EasyEDA's editor itself writes one WIRE per
   net. If ever wanted: group connections by net (reuse
   `EasyEDASheetWriter.GroupConnectionsByNet`), concatenate their
   polylines' segments, emit one WIRE with one LINESTYLE per
   group. No observable bug motivates it.

### 6.2 Spec details we haven't acted on yet

These come from the KiCad importer spec (see Section 9). None block
anything we do today; they matter as we expand part coverage.

2. **Multi-part `PART` record is just a marker.** When we author
   multi-part symbols for HC393 / gates, the `PART` line is
   `["PART", partId]` — a separator that begins a new unit. The
   `.1` suffix in our existing `PartTitle` values
   (`"Resistor.1"`, `"LED.1"`, `"Header-Female-2.54_1x8.1"`, etc.)
   is meaningful — it names the first sub-part. For single-PART
   symbols it's a no-op as far as rendering goes, but the format
   parser requires it.

3. **`invertedFlag: 2` on output PINs** is what gives NAND/NOR
   gates their output bubble. Not relevant until gate symbols are
   authored.

4. **Per-unit ATTRs only apply to unit 1 in some importers.** The
   KiCad importer documents this limitation. EasyEDA Pro itself
   probably accepts per-unit ATTRs everywhere, but if we ever
   need round-trip compatibility with KiCad imports, this matters.

### 6.3 Resource hygiene

5. **`device_fragments.json` provenance isn't documented.** The
   file is hand-curated copies of EasyEDA library device entries,
   stripped of decorative fields. There's no comment explaining
   which `.eprj` they came from, what was deliberately removed,
   or when they were last refreshed. Add a header comment block
   (or a sibling README) capturing this so the next person doesn't
   have to guess.

6. **Pre-flight validation pass before export.** If a schematic
   has three unsupported chip types, the exporter throws on the
   first one and the user has to re-export after each fix.
   `CollectUsedParts` should accumulate all unsupported parts and
   throw once with a complete list — or better, return a
   structured result so the UI can show a "these parts are not
   yet supported: …" dialog.

## 7. Open questions / risks

- **Stable UUIDs for our catalogue.** Currently each part is keyed by
  a hard-coded UUID matching the library entry we cribbed from.
  Devices we author ourselves (later, for ICs that don't exist in
  EasyEDA's library — like the HC393 if we end up authoring our own
  `.esym`) need fixed UUIDs minted once and never regenerated, so
  re-exporting the same schematic produces byte-identical output.

- **Multi-part smoke test still pending.** Mechanism is documented but
  unverified end-to-end.

## 8. Next steps

1. **Capacitor** — passive, same shape as resistor. Add to
   `EasyEDACatalogue` with appropriate `.esym` / `.efoo` resources.
   May benefit from the same template approach: one footprint per
   family (axial/ceramic/electrolytic) shared across values.
2. **Multi-part validation: HC393.** First test of the multi-part
   mechanism. Author (or borrow) a DIP-14 footprint, build an `.esym`
   with one PART per UnitSpec plus a hidden-power PART, bind to the
   DIP-14 in `device_fragments.json`. Verify designator auto-suffix
   (`U1.1`, `U1.2`, …) and hidden-pin power binding work in EasyEDA.
3. **Gate ICs** — HC08, HC32 next. Same multi-part pattern as HC393.
   Once HC393 works, gates should be a small extension.
4. **Authored symbols/footprints for parts EasyEDA's library doesn't
   carry.** TTL chips not in LCSC will need their `.esym`/`.efoo`
   built from `PartDefinition` — via `EasyEDASymbolBuilder` /
   `EasyEDAFootprintCatalogue`. Scope: parts we can't borrow.
5. **Full Clock_393 smoke test.** End-to-end validation.

## 9. References

### Authoritative third-party docs

- **KiCad EasyEDA import spec** — the closest thing to an authoritative
  spec for the on-disk format. Documents both Standard (`.json`) and
  Pro (`.epro`) variants in detail, including line-type layouts,
  symbol/footprint type enums, layer numbering, and coordinate
  scaling.
  <https://dev-docs.kicad.org/en/import-formats/easyeda/>

- **EasyEDA Pro user guide — import / export** — the vendor's own
  user-facing pages. Useful for confirming what the UI does
  (File → Save As Local produces `.epro`; File → Import accepts
  `.epro` and `.zip`; "Associate footprint automatically" needs the
  Supplier/Manufacturer Part fields).
  <https://prodocs.easyeda.com/en/import-export/import-easyeda-pro/>
  <https://prodocs.easyeda.com/en/import-export/easyeda-pro-format-converter/>

- **KiCad C++ importer source** — ground truth for edge cases. The
  `pcb_io_easyedapro` family of classes in the KiCad repo parses
  `.epro` files and shows exactly which fields the importer reads,
  which it tolerates as missing, and which it requires.
  <https://gitlab.com/kicad/code/kicad>

### Useful facts pulled from those sources

The following clarify or correct details we worked out by reverse
engineering. Anywhere this contradicts an earlier section, the
references win.

- **`.epro` vs `.eprj`.** `.eprj` is the editor's *internal* SQLite
  working file (offline projects). `.epro` is the import/export
  format — a renamed ZIP. The two formats are not convertible by
  rename. We target `.epro`.

- **Symbol type values** (in a symbol's HEAD record `symbolType`, and
  also as the `type` field on entries in the `symbols` map of
  `project.json`):
  - `2` — NORMAL (standard schematic symbol; what resistors, LEDs,
    chips use).
  - `18` — POWER_PORT (used for VCC / power-flag style symbols that
    bind to a global power net).
  - `19` — NETPORT (global label).
  - `20` — SHEET_SYMBOL (sheet hierarchy).
  - `22` — SHORT.

  Worth confirming our shipped `vcc.esym` and `gnd.esym` use
  `symbolType: 18`. If they declare type `2`, EasyEDA may still
  accept them as net flags but the power-port semantics aren't
  guaranteed.

- **Footprint type** is `4` in the `footprints` map's `type` field
  (matching DOC_TYPE 4 / PCB_COMPONENT). We already emit this.

- **`PART` line.** The spec lists it simply as `["PART", partId]` —
  a marker that begins a new unit in a multi-unit symbol. The
  `partId` is just an identifier; bounding boxes and per-PART
  attributes are encoded separately. Earlier in this doc the BBOX
  field was shown inline with PART — that may be a format-version
  variation. When we build the HC393 we should match what
  `prodocs.easyeda.com` examples or a hand-exported multi-part
  symbol does, rather than relying on the inline-BBOX form.

- **`PIN` inverted flag** (for NAND/NOR/inverter output bubbles):
  the `invertedFlag` field (last fixed slot in a PIN record) takes
  the value `2` to mark a pin as inverted (drawn with a bubble).
  Plain pins use `0`.

- **`FONTSTYLE` field layout**: `[id, unk1, unk2, color, fontName,
  fontSize, ..., valign, halign]`. Font size is scaled by 0.62 on
  import to KiCad — meaning EasyEDA's stored values are roughly
  1.6× nominal point size. Alignment values: 0 = top/left,
  1 = centre, 2 = bottom/right.

- **`LINESTYLE` field layout**: confirmed `["LINESTYLE", id,
  "#RRGGBB", ..., dash, thickness]` form. We emit one per wire to
  carry per-wire colours. The doc notes a few trailing slots
  (dash pattern, thickness) that we currently leave null — they
  default sensibly.

- **Coordinate scaling**: TTLSim grid × 10 = EasyEDA pixels,
  matching KiCad's `value * 10 → mils` conversion. Our existing
  multiplier was empirically right; this confirms why.

- **Y-axis flip**: explicitly documented — EasyEDA Pro Y increases
  downward. KiCad inverts it on import. We do the same inversion
  on export (TTLSim is Y-down internally; the EasyEDA file ends up
  Y-up because we negate). The chain works because each end
  inverts once.

- **`ATTR` layout for placed components**:
  `[id, parentComponentId, key, value, keyVisible, valVisible, x, y,
  rotation, fontStyleId]`. Standard keys EasyEDA recognises include
  `Device`, `Symbol`, `Designator`, `Name`, `Value`,
  `Global Net Name`, `Description`, `Footprint`, `Unique ID`.

- **JSON Lines format**: blank lines are separators, not noise. In
  `.efoo` files specifically, a blank line marks the boundary
  between footprint-level and PCB-level data within one file.

- **`localAttribs`**: the customProps slot at index 7 of a sheet
  COMPONENT record is for per-instance attribute overrides — same
  mechanism we already use via instance-level ATTR records.

- **Auto-association** (the "Associate footprint automatically" /
  "Associate 3D model automatically" import checkboxes) keys off
  the device's `Supplier`, `Supplier Part`, and `Manufacturer Part`
  fields. With those populated, EasyEDA looks up the canonical
  footprint and 3D model from the supplier database. With them
  blank or missing, the auto-association fails — and historically
  surfaced as the unhelpful "create device failed: no permission
  or project does not exist" error. We keep `Supplier=LCSC` plus a
  supplier part number on each device for this reason.

### When to consult which

- *I'm not sure about a record's field layout* → KiCad importer spec.
- *I'm not sure what the editor accepts as input* → prodocs.easyeda.com.
- *I'm not sure how the parser actually handles a quirk* → KiCad C++
  source.
- *I want to know what's typical in real-world files* → diff against
  our hand-saved reference exports (`From_EasyEDA.epro`).

