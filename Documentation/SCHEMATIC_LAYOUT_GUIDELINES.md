# Schematic Layout Guidelines

Reference notes for building `.ttlproj` schematics in the TTLSim editor. These are conventions learned from review of hand-tuned layouts — apply them when generating a schematic from scratch so the result needs minimal manual cleanup.

## Coordinate model

- The grid is integer cells, x increasing right, y increasing down.
- Coordinates can go negative — the canvas isn't bounded at (0,0). Use this to push power-rail symbols off the "active" area of the schematic so they don't intrude on signal-routing space.
- Typical zoom for a moderately complex schematic is around 0.9–1.0. Plan for a working canvas of roughly 200×140 cells for ~5–8 ICs plus their support components. Don't try to cram things in.

## What gets VCC/GND wires (and what doesn't)

**Chips** (`partKind: "chip"` — every 74-series part in the catalogue, including the gate ICs '00, '02, '04, '08, '10, '14, '20, '30, '32, '86 as well as registers, counters, decoders, ALUs and memory) render as DIP boxes with all their pins drawn on the symbol, including VCC and GND. Every chip needs explicit VCC and GND connections in the JSON.

Historically the gate ICs were a separate `partKind: "ic"` rendered as individual gate symbols without visible power pins. That model is gone — they're now `partKind: "chip"` DIP boxes with miniature gate glyphs drawn inside the body for readability, and their power pins are wireable like any other chip's. If you encounter `partKind: "ic"` on a gate device in an old file, it's a pre-migration save; see `GATE_TTLPROJ_MIGRATION.md` for the conversion.

**Passives** (resistor, capacitor, LED, button, switch) have only their two physical terminals (pin 1 and pin 2). When a pull-down resistor's bottom goes to GND, *that's* the wire to a GND symbol. When a switch's "top end" goes to VCC, *that's* the wire to a VCC symbol.

## One VCC/GND symbol per functional sub-unit

A single shared `item_vcc` and `item_gnd` makes the schematic harder to read — every supply wire crosses the canvas to reach the same point and the result is a tangle. Instead, place a local VCC and/or GND symbol next to each functional sub-unit that needs supply, and wire only that group's pins to that group's symbol.

Group definition:
- Each chip is its own sub-unit (its own VCC and GND symbols, sitting near it).
- The control-pin block of a register (e.g. the '173's G1, G2, M, N, CLR all tied low) shares the chip's GND — no need for a separate symbol.
- Each switch/button bank shares one VCC for the tops and one GND for the pull-down bottoms.
- Each LED bank shares one GND for the limiter-resistor bottoms.
- One-off pull-ups (like the A=B open-collector pull-up on a '181) get their own VCC symbol.

Electrically these are all the same VCC and GND respectively, since VCC and GND symbols of the same `type` represent the same global net. The point is purely visual clarity.

## Where to place VCC/GND symbols

The symbol should sit **outside the bounding box of its sub-unit, well clear of the chip body or device cluster**. The instinct to put VCC "directly above the chip" and GND "directly below the chip" looks tidy in the JSON but produces wires that fight other signal traces.

Direction is flexible — a chip's GND symbol does not have to sit directly below the chip. It can sit far to the left, far to the right, or in the gap between two stacked components. The auto-router will find a path; what matters is that the symbol is in a place where its wire doesn't have to cut through a crowded zone.

Practical placements seen in good layouts:

- A chip's VCC symbol placed 20+ cells to the right of the chip, at roughly the same y. The wire from pin 24 (or wherever VCC is) takes a long horizontal route through empty canvas above the chip.
- A chip's GND symbol placed far to the left and far below the chip — e.g. a chip at (130, 60) with GND at (113, 105). The wire path is L-shaped but uses unused canvas in the lower-left corner.
- VCC/GND for a chip placed *in the gap between two stacked chips*, when that gap is generous (15+ cells). A vcc_u3 at (63, 61) servicing U3 below at y=79 fits in the routing channel between U2 (y=24) and U3.
- For switch/button banks, push VCC and GND symbols **well past the bank in the perpendicular direction**. A column of 4 switches at y=16..40 needs its GND at y=52+ (not just y=42). A row of 6 switches across the bottom needs its GND at y=146+ (not just y=130), and its VCC pushed off to one end at x=191+, not above the row.
- For LED banks, GND goes below the resistor row, with 4–8 cells of clearance from the lowest LED.

Rule of thumb: if a VCC or GND wire has to cross *any* signal wire to reach its symbol, the symbol is in the wrong place. Move it further away — usually further out, not closer in. The router can handle long L-shaped paths through empty canvas just fine; what it can't fix is symbols crowded into the signal flow.

The user has rejected layouts where I clustered power symbols too close to the chips. The lesson is: be generous with the clearance.

## Tying off unused inputs

When an unused chip input needs a fixed level, tie it to the **GND or VCC symbol already connected to that same chip**, not a fresh symbol. Reusing the chip's own supply keeps the initial layout from turning into a supply-wire tangle before the user has had a chance to arrange it.

Do tie them off — a part left with genuinely floating inputs is flagged by the simulator (TTL010 "unused", TTL011 "floating input"), and a part with a required pin unconnected can be dropped from the run entirely (TTL021 "absent from the simulation"). A constant that feeds a shared bus is a separate case — it must be *driven*, not just tied; see **Tristate bus discipline**.

## Wire colours

Set wire colour at generation time, don't leave them all `null`. Use this fixed legend so every schematic reads the same — one colour per signal class:

| Signal class | Colour |
|---|---|
| VCC | `"Red"` |
| GND | `"Navy"` |
| Data bus (D0–D7) | `"Blue"` |
| Carry (ripple Cn+4 → Cn, mux Cn) | `"Gray"` |
| Clock (CLK, gated CLK) | `"Orange"` |
| Control — strobes / enables (SRC & DST decoder outputs, `/FETCH`, `/PCLO`, `/PCHI`, `/TRST`, load and output enables) | `"Yellow"` |
| Address bus (PC → memory) | `"Olive"` |
| Opcode & micro-op index (OPL, OPH, IDX) | `"Green"` |
| Register / ALU operand feeds (IR·A·B outputs, latch → ALU, ALU → bus driver, the S select bus) | `"Cyan"` |

- **VCC wires** are every connection with an endpoint on a `vcc_*` symbol; **GND wires** every connection with an endpoint on a `gnd_*` symbol.
- Stick to one colour per logical signal bundle — don't split a bus across two colours.
- Pick colours during generation, not as a post-pass — it's cheaper and avoids the awkward bulk-recolour edit later.

## Active-low naming

Net names carry their polarity, matching the `!` convention already used in the PLD source:

- An active-low net takes a **`/` prefix** — `/RESET`, `/FETCH`, `/TRST`, `/PCLO`, `/A`. A net earns the `/` exactly when it is `!X` in the generated PLD, **or** it is driven by a decoder ('138/'154 pull their selected line low), **or** it drives an active-low pin (`/LOAD`, `/E`, `/OE`, `/CLR`, `/CE`). Those three conditions always agree.
- **Source output-enables take an `OE` suffix** — `/RAMOE`, `/ALUOE`, `/TOSOE`. This keeps a source strobe distinct from the same-named *load* strobe on the destination side. TOS, BP and FLAGS are each both a bus source and a bus destination, so a bare `/TOS` on both decoders would be the *same net* and would short the drive to the load. `/TOSOE` (drive onto the bus) versus `/TOS` (load from the bus) keeps them apart.
- Clocks and multi-bit buses carry no polarity marker — a clock is an edge, not a level, and a data or address bus has no single active sense.

## Named buses

Multi-bit signals ride a named netlabel bus rather than point-to-point wires: D, PC, OPL, OPH, IDX, SRC, DST, S, BANKEN, and so on.

- **Same label = same net.** A source tap labelled `SRC` and a consumer tap labelled `SRC0` are *different* nets — the mismatch silently disconnects the bus with no error raised. Every tap on a bus, source and consumer alike, must carry the identical label string.
- **Build the bus full-width at first touch.** A structural bus is cut to its final width the first time it's placed, even if only one tap is wired, so it never has to be re-cut when the next participant arrives.
- Bit order is explicit in each tap's `startBit` and width — verify it rather than trusting pin order (see **Pin-order traps**).

## Tristate bus discipline

On a shared (tristate) bus, exactly one driver may be active at a time:

- Each source gates its `/OE` off the SRC decoder; every other source sits in high-Z. The same holds for the address bus (AMODE-selected drivers) and the micro-op index bus (BANKEN-selected sequencers).
- A **constant** bit on a shared bus must be *actively driven* to its level while its owner is selected — e.g. a sequencer index bit that is always 0 is emitted as a driven GND (`IDX0 = GND`), output-enabled by BANKEN like every other line, not tied off on the board and not left floating. A floating "constant" corrupts the shared bus the moment that owner is selected.

## Pin-order traps

Two datasheet gotchas that have produced real bit-reversal bugs — check the pin numbers against the part, don't assume ascending pins mean ascending significance:

- **'161 counter outputs run high-pin / low-significance**: QA = pin 14, QB = 13, QC = 12, QD = 11. Wiring a nibble to a bus in ascending pin order reverses the bits within the nibble.
- **'154 address inputs put the LSB on the high pin**: A (LSB) = pin 23, B = 22, C = 21, D (MSB) = pin 20 — the reverse of the intuitive order. The same caution applies to any part where the least-significant input sits on the higher pin number. This one hides on bit-palindrome codes (0, 9, 15) and only surfaces on a non-palindrome value, so it can pass a limited test and still be wrong.

## Rotate passives that need to span a vertical or unusual route

Passive symbols are 4–6 cells wide on their default rotation. Most of the canvas time you want them horizontal (rotation 0). But:

- A column of switches with pull-downs that drop straight down to a GND symbol: rotate the **pull-down resistors 90°** so they extend vertically downward from the switch's bottom pin to the GND wire below.
- A pull-up resistor that goes upward from a chip pin to a VCC symbol: rotate **270°** to extend upward.
- Resistors in horizontal current-limiting position (LED → resistor → GND): leave at rotation 0.

`rotation` values in the schema are 0, 90, 180, 270 (degrees clockwise). Pin numbers don't change with rotation — pin 1 and pin 2 are still pin 1 and pin 2; the rotation only affects the geometry on the canvas.

## Component spacing within a group

- LEDs in a vertical bank: 6 cells of vertical spacing per LED is comfortable (LED is 2 tall + 4 cells of room).
- Switches in a vertical bank: 8 cells per switch.
- Chips in a column: leave 8–12 cells between chip bottoms and the next chip's top to give pins routing room.

## Component spacing between groups

Bigger gaps. The signal path between groups is where most wires live, and they need corridors:

- 15–20 cells between a switch bank and the chip it feeds.
- 15–20 cells between a chip's output column and the LED bank that displays it.
- 25–35 cells between a chip and the next chip in the data flow (e.g. between a '173 register and the '181 ALU it feeds via inverters).

## Functional bands

Lay the schematic out in bands so the data flow reads left-to-right or top-to-bottom:

- **Inputs** on the left (switches, buttons).
- **Input conditioning** (pull-downs, debounce — if any, inverters).
- **Registers / state-holding chips** in the next column.
- **Combinational logic** (ALU, decoders) in the centre.
- **Output conditioning** (inverters, pull-ups).
- **Output indicators** (LEDs) on the right.

For a left-right flow:
- x = 0–30: input switches and their pull-downs.
- x = 30–50: load buttons (if present) and their pull-downs.
- x = 50–70: input registers (the '173s).
- x = 70–90: register-output LEDs.
- x = 90–110: input-side inverter chips.
- x = 110–150: main logic (e.g. '181 ALU).
- x = 150–180: output-side inverter chips and pull-ups.
- x = 180–210: output LEDs and their limiters.

Bottom strip y = 100–140: control switches (operation selects, mode, carry-in, etc.) that feed up into the main logic.

## Selector switches: dedicated band along the bottom

Control inputs (S0..S3, M, Cn, etc.) that need to be exercised by the user but don't fit into the main data flow get their own band along the bottom of the canvas:
- Each switch on the same y, evenly spaced ~12 cells apart.
- Pull-down resistors directly below, rotated 90° so they run downward.
- Shared VCC symbol at one end of the row (right end works well, pushed further right with extra clearance).
- Shared GND symbol below the resistor row, pushed *well* below (~15+ cells below the resistors).

## Chip and component labels

The `label` field on a Unit shows beneath the designator on the canvas. Keep labels **short**:

- Chips identifying their role in the circuit: single word or short token. `"ALU"`, `"A"`, `"B"`, `"PC"`, `"IR"`. Not `"ALU '181"`, `"A reg '173"`, `"B register"`. The part number is already visible from the device's family and identifier; the label is for the schematic-level role.
- Switches and indicator LEDs: a single bit name or signal name — `"A0"`, `"S3"`, `"Cn"`, `"Cout"`, `"A=B"`. Not `"A bit 0"`.
- Buttons: short imperative or noun — `"Load A"`, `"Reset"`, `"Step"`. Two words is the upper bound.
- Passives: typically empty `""`, unless the value is unusual enough to call out (`"10k"` for a debounce resistor, `"PU"` for a one-off pull-up).

**Relabel a chip when its role changes** — don't carry a legacy name. A '138 first used only to derive PCINC but later serving as the source-select decoder should read `"SRC"` (the twin of the `"DST"` '154), not its original working name. The label tracks what the chip *does now*, not what it did when it first went on the sheet.

Long labels eat horizontal space and force wider component spacing. Pick the shortest label that identifies the role.

The apostrophe in part numbers (e.g. `74'181`) serialises as `\u0027` in JSON. When generating, prefer just `"ALU"` rather than `"ALU '181"` to avoid both the visual noise and the escape sequence.

## Switch state (`switchClosed`) is persisted

The `switchClosed` field on a switch unit is saved in the file. When the user is in the middle of testing a circuit, some switches will be `true` to represent the test inputs they've set up.

- When **generating a new schematic from scratch**, default all switches to `false`.
- When **modifying an existing schematic**, leave `switchClosed` values alone unless explicitly asked to reset them. Regenerating the schematic from scratch and overwriting the user's test state is a real annoyance to recover from.
- When **applying a small change to an existing file**, prefer surgical edits (`str_replace`) over full regeneration so the switch state survives.

Buttons have an `IsPressed` field but it's not persisted to file — buttons always read as released on load. So buttons don't have the same regeneration hazard.

## Wires field

The `wires` field exists in the schema but the user does manual wire routing in the GUI. Always emit `"wires": []` — let the user (or the auto-router) handle the visual layout of connections. The `connections` array carries the electrical truth; `wires` is a visual cache that the editor manages.

## Pre-flight checklist before saving a schematic

1. Every `connections` entry references either a unit `id` from the `units` array or an item `id` from the `items` array. No dangling refs.
2. Every chip has both its VCC pin (whichever pin number the part's `PowerPin` is) and its GND pin (`GroundPin`) wired to a VCC/GND symbol. No chip with a floating power pin.
3. Every VCC and GND symbol in `items` has at least one connection pointing to it (no floating symbols).
4. No chip left with a floating signal input — tie unused inputs off (see **Tying off unused inputs**), or the part may be dropped from the simulation.
5. Every bus tap carries the exact bus label — no `SRC` / `SRC0` style mismatches that split a net (see **Named buses**).
6. The canvas extent (max x, max y, min x, min y) is reasonable for the schematic size — if it's too compact, add spacing; too sparse, the zoom-to-fit will leave huge gaps.

## When unsure, ask before generating

These are good defaults, not laws. For an unusual schematic shape (a fan-out tree, a feedback loop, a multi-chip cascade with carry lookahead), the band layout may not fit and a different organising principle wins. Ask the user before committing to a layout choice if the topology doesn't fit one of the standard patterns above.

## Iteration discipline

The most expensive mistakes across a schematic-generation session are:

1. **Regenerating the whole file when a small edit would do.** Every regeneration loses user-applied state (switch positions, fine-tuned coordinates, test setups). Use `str_replace` for targeted edits — relabeling chips, recolouring wires, moving a single symbol — and only emit a full file when adding or removing a substantial number of components.

2. **Clustering VCC/GND symbols too tightly to their consumers.** They need substantial clearance — at least 15–20 cells from the consuming group's edge — so wires can take L-shaped routes through empty canvas. When the user pushes back on this, the fix is in this document; check it before redoing the work.

3. **Doing batch transformations by hand-edit when a script is cleaner.** A 60-connection bulk recolour by individual `str_replace` calls is slow and error-prone; a single scripted pass over the JSON is faster and safer. The user doesn't see the script — they just see the result. (Apologies if Python keeps slipping in for this kind of thing — use `dotnet script`, `awk`, or `sed` instead per the user's preference; never Python.)

4. **Not reading the source before claiming a fact about it.** Pin numbers, part identifiers, supported chips, colour-name validity, default behaviour of fields — all of these have lived in the source tree and have been wrong in my generated work when I went from memory. The project files are the truth; the search tool is fast; use it. The standing rule from the user: if I haven't verified it in this turn, I should say "I haven't checked," not assert.
