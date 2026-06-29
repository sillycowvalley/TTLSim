# 74xx Schematic Diagram Style Guide

Conventions for hand-authored educational SVG schematics of 74-series logic ICs
(e.g. `74HC151.svg`, `74HC245.svg`). The goal is a consistent, readable, single-page
reference per chip: pinout, symbol, function table, plain-language explanation, and legend.

---

## 1. Canvas and layout

- **viewBox**: `0 0 1040 850`. Fixed width 1040; height 850 fits all five regions with margin.
- **Background**: full-canvas `rect` in `--bg` (`#fafaf7`), a warm off-white.
- **Left gutter**: content starts at x = 40.
- **Five regions**, always in this order and roughly these bands:

  | Region | Position | Purpose |
  |---|---|---|
  | Title block | top, y 0–88 | chip number, one-line description, divider rule |
  | Pinout | left column, y ~90–535 | DIP package, top view |
  | Schematic symbol | right column, y ~90–600 | logic/function symbol |
  | Function table | lower-centre, y ~610–830 | truth / operation table |
  | How it works | lower-left, y ~630–840 | 8–10 short prose lines |
  | Colour key | lower-right, y ~630–780 | legend |

- Title is the chip number at 32 px bold; subtitle at 15 px in `--sub` grey, format:
  `Function  ·  Package  ·  key trait` (e.g. `8-Line to 1-Line Multiplexer / Data Selector  ·  DIP-16  ·  combinational (no clock)`).
- A horizontal rule (`.thin`) under the title at y = 88 spans x 40–1000.

---

## 2. Semantic colour palette

Colour encodes **signal role**, and the meaning is identical across every chip in the set.
Never reuse a role colour for a different role just because a given chip lacks that role.

| Role | Class | Hex | Applies to |
|---|---|---|---|
| Input data | `.green` | `#2e7d32` | input pins, and bidirectional data buses with no fixed direction (D-inputs; the A and B ports of a transceiver) |
| Output | `.purple` | `#7b1fa2` | **any pin that is always an output** — mux Y/W, buffer/line-driver Y, adder sum/carry, register Q |
| Select / address | `.blue` | `#1565c0` | select lines, address lines |
| Control / enable | `.orange` | `#e65100` | enables, direction, strobe (E̅, DIR, O̅E̅, etc.) |
| Power | `.red` | `#c62828` | VCC |
| Ground | `.slate` | `#37474f` | GND |

The input/output rule is **directional, not functional**: a pin is purple whenever the chip
*always* drives it, whether the value is computed (mux select, adder sum) or merely buffered
(a line driver's Y). Do not downgrade a buffered output to green — if its direction is fixed
as "out", it is purple.

Structural (non-signal) greys, never used for pin names:

| Use | Class | Hex |
|---|---|---|
| Ink / borders / arrowheads | (inline) | `#1f2933` |
| Pin **numbers** | `.pinno` | `#7b8794` |
| Subtitles / secondary prose | `.sub` / `.small` | `#52606d` |
| Section captions | `.panel` / `.noteh` | `#3e4c59` / `#1f2933` |
| Grid lines | `.grid` | `#cbd2d9` |

**Bidirectional buses (e.g. transceiver A/B):** these are the *only* output-capable pins that
stay `.green`. A transceiver port has no fixed direction — either side can be the output
depending on the control pin — so there is no "always an output" pin to colour purple, and
both ports are treated as data. Distinguish them by their letter labels (A1…A8 vs B1…B8),
**not** by colour. A unidirectional buffer/line driver is different: its Y pins are always
outputs, so they are `.purple` (see the '244 and '541).

---

## 3. The cascade rule (important)

SVG `<text>`/`<tspan>` elements are styled by `class`. Several structural classes set a
`fill` (`.lbl`, `.tbl`, `.tblh`, `.sig`, `.note`…). When an element carries **two** classes —
a structural one plus a colour one, e.g. `class="lbl green"` — both rules have equal
specificity, so **the one defined later in the stylesheet wins.**

If the colour classes are defined *before* `.lbl`/`.tbl`/`.tblh`, the structural dark fill
overrides the colour and pin names silently render in ink. This is exactly the bug that left
the schematic-symbol pin names and the function-table headers/cells uncoloured while the
pinout (which uses `.sig`, a class with *no* fill) came through correctly.

**Rule:** give every colour class `!important` so it always wins regardless of order:

```css
.green  { fill:#2e7d32 !important; }
.blue   { fill:#1565c0 !important; }
.orange { fill:#e65100 !important; }
.purple { fill:#7b1fa2 !important; }
.red    { fill:#c62828 !important; }
.slate  { fill:#37474f !important; }
```

Then `class="lbl green"`, `class="tblh orange"`, and `class="tbl green"` all colour correctly.

---

## 4. Pin-name colouring rule

**Every occurrence of a pin name is drawn in its role colour — everywhere it appears:**

- Pinout labels
- Schematic-symbol labels
- Function-table headers and any cell that names a pin (e.g. the `Y` column showing `D0…D7`)
- Prose in "How it works" — wrap each pin-name token in a coloured `<tspan>`
- Legend text — colour the pin-name tokens, not just the swatch

Pin **numbers** are never coloured; they stay `.pinno` grey. Logic levels (`H`, `L`, `X`) and
ordinary words stay in their structural colour.

Inline example:

```xml
<text class="note" x="40" y="697">The 3-bit code on <tspan class="blue">S2 S1 S0</tspan> (binary</text>
```

---

## 5. DIP package (pinout)

- Top view, **notch up**, drawn as a small arc cut into the top edge:
  `<path d="M 218 150 A 17 17 0 0 0 252 150" .../>` (centre the arc on the body).
- **No pin-1 orientation dot.** The notch alone marks orientation; a dot overlaps the pin-1
  label and is removed.
- Body: white `.body` rect, ~120 wide, height = pin-count-dependent.
- Pin numbering: pin 1 at top-left, down the left side, continuing up the right side
  (standard DIP). Right-side labels read top-to-bottom as highest→… .
- Each pin: a short `.thin` stub; the **number** outside the body in `.pinno`; the **name**
  inside the body in `.sig` + its role colour.
- Vertical pitch: pick a constant (≈ 38–45 px) so pins are evenly spaced with ~20–40 px
  top/bottom margin inside the body.

---

## 6. Schematic symbol

- Use the conventional shape for the function: trapezoid for a mux, rectangle for a
  transceiver/buffer/register, standard gate bodies for gates.
- Inputs left, outputs right, controls top, selects/address bottom — where the function allows.
- Pin **names** in `.lbl` + role colour; pin **numbers** alongside in `.pinno`.
- **Pin-label placement — the exact mirror of the pinout.** On the symbol the pin **name** is
  the *primary* label and goes **OUTSIDE** the body, beyond the end of the pin stub. The pin
  **number** is *secondary* and goes **INSIDE** the body, in `.pinno` grey, set just inside
  each pin's entry point. This is the precise opposite of the pinout (§5), where the **name
  goes inside** the body and the **number outside**:

  | Diagram | Name | Number |
  |---|---|---|
  | Pinout (§5) | inside the body | outside the body |
  | Schematic symbol (§6) | outside the body | inside the body |

  Keep the inside number small (`.pinno`, 11 px) and offset clear of the function label and
  any inversion bubbles, which share the body interior.
- Label the function inside the body (`MUX`, `8:1`, `× 8 lines`, etc.) in ink.
- Include a small `VCC = pin n · GND = pin n` note in the symbol area, VCC/GND coloured.

---

## 7. Active-low and inverted signals

- Name: overbar via a trailing combining overline `&#x305;` on each letter, e.g.
  `O&#x305;E&#x305;` → O̅E̅, `E&#x305;` → E̅. (Renders correctly in Chrome.)
- Symbol: draw an inversion **bubble** (small `--bg`-filled circle, `.thin` stroke, r ≈ 6)
  on the body edge at that pin.
- A complementary output is shown as `W = Y̅` with a bubble on its symbol pin.

---

## 8. Tri-state and bidirectional

- Tri-state buffer: a triangle (`polygon`) with the point in the signal-flow direction.
- Bidirectional channel (transceiver): draw **two opposing** buffer triangles, one per
  direction, each annotated with the controlling condition (e.g. `A → B (DIR = H)`),
  and a `× 8 lines` multiplicity note. Direction arrowheads use the shared `#arr` marker.

---

## 9. Function table

- Header row with light fill (`#eef1f4`); outer border `#9aa5b1`; inner rules `.grid`.
- Column headers that are pin names are coloured (control columns orange, select blue,
  output purple, etc.).
- Cell values: `H` / `L` / `X` for high / low / don't-care; data routed to an output shown
  as the source pin name in its colour (e.g. `D3` green).
- A one-line footnote under the table defines `H/L/X` and states any global rule
  (e.g. "W is always Y̅", or the disabled/high-Z condition).

---

## 10. Typography

- Font: `Segoe UI, Arial, sans-serif` set on the root `<svg>`.
- Sizes: title 32, subtitle 15, section caption (`.panel`/`.noteh`) 14, body/symbol labels 13,
  pin numbers / footnotes (`.small`/`.pinno`) 11.
- Pin names and table headers are bold (700).

---

## 11. Canonical `<style>` block

Copy-paste starting point. Colour classes carry `!important` per §3.

```css
.bg     { fill:#fafaf7; }
.thin   { stroke:#1f2933; stroke-width:1.2; fill:none; }
.body   { fill:#ffffff; stroke:#1f2933; stroke-width:2; }
.title  { fill:#1f2933; font-size:32px; font-weight:700; }
.sub    { fill:#52606d; font-size:15px; }
.panel  { fill:#3e4c59; font-size:14px; font-weight:600; letter-spacing:.4px; }
.pinno  { fill:#7b8794; font-size:11px; font-weight:600; }
.sig    { font-size:13px; font-weight:700; }
.green  { fill:#2e7d32 !important; }
.blue   { fill:#1565c0 !important; }
.orange { fill:#e65100 !important; }
.purple { fill:#7b1fa2 !important; }
.red    { fill:#c62828 !important; }
.slate  { fill:#37474f !important; }
.lbl    { fill:#1f2933; font-size:13px; font-weight:700; }
.small  { fill:#52606d; font-size:11px; }
.tbl    { fill:#1f2933; font-size:13px; }
.tblh   { fill:#1f2933; font-size:13px; font-weight:700; }
.note   { fill:#3e4c59; font-size:13px; }
.noteh  { fill:#1f2933; font-size:14px; font-weight:700; }
.grid   { stroke:#cbd2d9; stroke-width:1; }
```

Shared arrowhead marker:

```xml
<marker id="arr" markerWidth="9" markerHeight="9" refX="7" refY="4" orient="auto">
  <path d="M0,0 L8,4 L0,8 Z" fill="#1f2933"/>
</marker>
```

---

## 12. Pre-ship checklist

- [ ] viewBox `0 0 1040 850`; off-white background present.
- [ ] Colour classes carry `!important`.
- [ ] Every pin name — pinout, symbol, table, prose, legend — is in its role colour.
- [ ] Pin numbers are grey, never coloured.
- [ ] Pinout: name **inside** the body, number **outside**. Schematic symbol: name **outside**
      the body (primary), number **inside** the body (secondary). The two are exact mirror images.
- [ ] Bidirectional buses share the data colour; letters carry the distinction.
- [ ] Notch only for orientation; no pin-1 dot.
- [ ] Active-low names overbarred and bubbled on the symbol.
- [ ] Function table footnote defines H/L/X and any global behaviour.
- [ ] Legend role→colour mapping matches the rest of the set exactly.
