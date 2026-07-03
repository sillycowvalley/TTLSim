# Chip Labels — Design Document

Printable stick-on labels for the top of DIP ICs, in the style of Grant Searle's
chip label sheet, generated for every chip TTLSim supports. Labels are printed at
**100% scale (no printer scaling)**, cut out, and placed on the physical package:
pin names run down both edges aligned with the legs, and a large gray part number
sits behind them for at-a-glance identification.

Status: geometry ratified through physical print tests (three iterations,
verified against real DIP-16/20 packages and the 24-pin 6116). Ready for a
full-catalogue run and/or a C# exporter inside TTLSim.

---

## 1. The vector font

### Provenance

Grant Searle's `ChipLabels.pdf` (Visio → Microsoft Print to PDF, 2016) is pure
vector — no raster content. It embeds two subset TrueType fonts (Arial regular
and bold, CID-keyed, Identity-H) with ToUnicode character maps. The regular
font's glyph outlines were extracted directly from the embedded font program
(glyf/loca/hmtx tables) and mapped to characters via the ToUnicode CMap,
producing a self-contained reusable vector font. No font files are needed at
generation time, ever again.

### Artifact: `VectorFont.json`

The single required asset for label generation. Keep it with the project files.

Structure:

```json
{
  "regular": {
    "unitsPerEm": 2048,
    "glyphs": {
      "A": { "advance": 1366, "path": [ ["M",x,y], ["L",x,y], ["Q",cx,cy,x,y], ["Z"] ] },
      ...
    }
  },
  "bold": { ... }
}
```

- Coordinates are **font units** (2048/em), y-up, baseline at y=0.
- Path commands: `M` move, `L` line, `Q` quadratic Bézier (control point, end
  point), `Z` close contour. TrueType outlines are quadratics; converting to a
  cubic (PDF `c`, GDI+ Bézier) uses the standard 2/3 rule:
  `c1 = start + 2/3·(ctrl − start)`, `c2 = end + 2/3·(ctrl − end)`.
- `advance` is the horizontal advance width in font units.
- Text is rendered as **filled paths** (nonzero winding) — never as font text —
  so output is identical on any printer/viewer and needs no font embedding.

### Coverage (regular face)

```
(space) + - . / 0-9 =
A B C D E F G H I K L M N O P Q R S T U V W X Y Z
a b c d e f h i k l m n o p q r s t u v x y
```

- **`=` is synthesized**, not recovered: Grant's sheet never used it, but the
  '181's `A=B` pin needs it. Built to Arial metrics (advance 1196, two bars,
  x 154–1042, y 438–602 and 834–998 font units). Visually indistinguishable at
  label sizes.
- **Missing**: `J j g w z q` and all punctuation not listed. Rule: when a new
  chip's pin name needs a missing character, synthesize that one glyph to Arial
  metrics and add it to `VectorFont.json` (as done for `=`). Never substitute a
  different character or drop the name.
- The **bold face is too sparse to use** (only the characters from Grant's
  headings). Everything renders in the regular face.

---

## 2. Label geometry (ratified by print tests)

### Widths

| Package family | Label width |
|---|---|
| 0.3" DIP (8–20 pin) | **6.0 mm** (17 pt) |
| 0.6" DIP (24–32 pin) | **12.7 mm** (36 pt) |

### Lengths

Pin rows sit on **exact 0.1" (2.54 mm) pitch**, centred within the label length.
One row per opposing pin pair (rows = pins/2).

**Wide (0.6") packages** — length = rows × 2.54 mm. Verified on 6116/6264/W24512
(prints at 97–98% of body, slightly inset — correct look):

| Pins | Length |
|---|---|
| 24 | 30.48 mm |
| 28 | 35.56 mm |
| 32 | 40.64 mm |

**Narrow (0.3") packages** — rows × 2.54 does NOT track real body lengths
(end margins vary per package; DIP-14 and DIP-16 share one body). Lengths are a
per-package table targeting the **shortest** JEDEC body so the sticker fits any
manufacturer:

| Pins | Length |
|---|---|
| 8  | 9.0 mm  |
| 14 | 18.5 mm |
| 16 | 18.5 mm |
| 18 | 21.5 mm |
| 20 | 23.5 mm |

New packages (e.g. skinny DIP-24) get a new row measured the same way: shortest
common body length, minus nothing — the pitch centring absorbs the rest.

### Layering and content (draw order matters)

1. **Border**: 0.4 pt black stroked rectangle.
2. **Part number** — drawn FIRST, underneath: light gray (**78% white**),
   rotated 90° reading bottom-to-top, centred both ways. Sized to fill 62% of
   the label width (cap-height basis), shrinking until its length fits 88% of
   the label length. Displayed name is TTLSim's `FullPartNumber` form
   (`74HC161`, `74LS181`, `6264`, `NE555`).
3. **Pin names** — black, on top. Left column pins 1..N/2 top-to-bottom,
   right column pin N opposite pin 1 (DIP mirror). Left names left-aligned
   1.2 pt from the edge; right names right-aligned 1.2 pt from the edge.

### Pin-name typography

- Base size **4.0 pt** (matches Grant's originals).
- **Per-row shrink-to-fit**: if left + right names + 2×1.2 pt insets + 2.0 pt
  centre gap exceed the label width, step the row's size down by 0.2 pt (floor
  2.4 pt) until it fits. Rows are independent — only crowded rows shrink.
- **End-row clamping**: each row's text is vertically centred on its pin row,
  then clamped so glyph extents stay inside the border with 0.2 mm clearance —
  cap height (0.716 em) above the baseline, slash descender (0.21 em) below.
  With minimum-body lengths the top and bottom rows shift inward 0.2–0.5 mm;
  all other rows stay dead-centred on their pins. (This fixed the truncated
  `/CLR`/`VCC`/`GND` rows seen in the second print test.)

### Naming conventions

- Pin names **verbatim from `ChipPartDefinition.cs`** — never invented, never
  read from memory; read the definition in the same session it is used.
- Leading `/` (active-low) is kept literally, matching Grant's convention.
  (No overbar — unlike the EasyEDA export, which converts `/` to `#`.)
- `NC` pins print blank.
- `A=B`, `/Cn+4` etc. print exactly as defined (hence the `=` glyph).

---

## 3. Output format

Labels are emitted as a **single-page A4 PDF** (595.32 × 841.92 pt) written
directly — one content stream of path operators, no fonts, no images, no
compression dependencies. All text is filled glyph paths from `VectorFont.json`;
gray via `rg` fill colour; the quadratic→cubic conversion above produces the
PDF `c` operators. This is what guarantees the print is dimensionally exact:
the only requirement on the user side is printing at 100% / "no scaling".

A sheet header states the print requirement and the narrow-length table so a
printout is self-documenting.

---

## 4. Verified-by-print history

| Iteration | Result |
|---|---|
| 1 | Length formula rows × 0.1" — 6116 correct, DIP-16 overhung 5%, DIP-14 short 8%. Root cause: end margins don't scale with pin count. |
| 2 | Per-package narrow lengths (typical body) — sizes good, but end-row pin names truncated by the border on short labels. |
| 3 | Shortest-JEDEC-body lengths + end-row clamping — current, awaiting final print sign-off. |

Package assumption on record: **74LS181 = wide 0.6" DIP-24** (classic form).
If skinny-DIP LS181 stock appears, add a narrow-24 row to the length table.

---

## 5. Artifacts

| File | Role |
|---|---|
| `VectorFont.json` | The recovered + synthesized vector font. Required input. |
| `TTLSim_Labels_Sample.pdf` | Current ten-chip sample (74HC245, 74HC273, 74HC161, 74HC189, 2114, NE555, 74LS181, 6116, 6264, W24512). |
| `ChipLabels_Outlines.svg` / `ChipLabels_Full.svg` | The original extraction from Grant's sheet (geometry-only / faithful full page). Reference only — not inputs to generation. |

---

## 6. C# BOM exporter (implemented)

`ChipLabelSheetExporter.Export(Schematic, filePath)` in `TTLSim.UI.Export`
(files: `ChipLabelSheetExporter.cs`, `LabelVectorFont.cs`, plus
`VectorFont.json` as an **Embedded Resource**) produces the BOM label sheet
for the current schematic:

- One label per physical chip **including duplicates**, grouped by
  `Device.FullPartNumber` with a `"3 x 74HC157"` caption per group
  (continuation caption repeats after a row/page wrap so split groups stay
  identified). Groups sorted by part name; multi-page A4.
- Labelable = `ChipPartDefinition` devices, excluding TO-92 parts (DS1813 —
  no flat top). Passives, headers, and standalone items are skipped.
- **Package width rule**: `BodyWidth >= 12` → 0.6" label, else 0.3". This is
  a derived correlation that holds across the whole current catalogue —
  including the GAL20V8, a 24-pin **skinny** DIP that must not be widened by
  pin count. If a future definition breaks the correlation, add an explicit
  package field to `ChipPartDefinition` rather than patching the rule.
- Narrow length table gained a **24-pin row: 29.0 mm** (skinny DIP-24,
  GAL20V8 class) — provisional pending a caliper check against real stock.
  Unknown narrow pin counts fall back to rows × 2.54 mm so new packages
  still export; add measured rows afterwards.
- Layout algorithm (shelf packing) and label drawing were validated by a
  line-for-line mirror run against a mock BOM: 24 labels, zero overlaps,
  all inside margins, verified render.

## 7. GAL naming (implemented)

Standard JEDEC has no pin-name field, so BlinkyJED and TTLSim share a header
convention. `JedecWriter` emits the `.pld` PIN declarations into the JEDEC
design-specification header (free text between STX and the first `*` field):

```
Used Program:   BlinkyJED
Name:           GAL1_ALU
Device:         GAL16V8
Pins:           2:OP0 3:OP1 4:OP2 5:OP3 6:L0 7:L1 8:L2 9:L3
Pins:           12:TOS_M0 13:TOS_M1 14:C_SRC 15:ALU_M 16:ALU_S3 ...
```

Contract: tokens are `number:name`, sorted by pin number; CUPL's leading `!`
marks active-low; wrapped lines each repeat the `Pins:` label so every line
parses independently. The block is free header text — it changes only the
transmission checksum (recomputed), never the fuse area or the `*C` fuse
checksum, so the `*` field area still diffs byte-identical against WinCUPL
and burned chips need no re-burn; recompiling the `.pld` files is enough.

`GalJedecHeader` (TTLSim, alongside `GalPinModel`) parses the block from
`Device.Program`: `TryParsePinNames` (converting `!` to TTLSim's leading
`/`) and `TryParseDesignName`. Consumers layer names as: **header signal
names > fuse-derived role labels (`IN`/`OUT`/`CLK`/`/OE`/`NC`) > static
definition names**. `ChipUnit.GalLabels()` applies this for the schematic
symbol; the label exporter applies the same for stickers. Old `.jed` files
without the block (WinCUPL, earlier BlinkyJED) degrade gracefully to roles.

In the exporter, GALs additionally group by part number **plus fuse map**
(differently-programmed GAL16V8s never share a label), display the design
name in gray instead of the generic part number, and carry their schematic
designators in the group caption (`GAL1_ALU (U3)`).

Round-trip validated against a real BlinkyJED `.jed`: fuse area
byte-identical after insertion, all names recovered, active-low conversion
correct, no-block files return null.

## 8. Remaining open items

1. Synthesize further glyphs (`J`, lowercase `g w z q`, …) only when a chip
   definition actually needs them.
2. Caliper-verify the skinny DIP-24 29.0 mm row against GAL20V8 stock.
