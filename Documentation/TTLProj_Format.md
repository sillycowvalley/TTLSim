# `.ttlproj` File Format

The on-disk format TTLSim reads and writes. This is the complete contract a
*producer* needs to generate a schematic TTLSim will open. It is the schema
only; for layout aesthetics (spacing, where to place power symbols, label
brevity) see `SCHEMATIC_LAYOUT_GUIDELINES.md`.

A `.ttlproj` is a UTF-8 JSON file, pretty-printed, **camelCase** property
names. The reader is case-insensitive on names but always write camelCase to
match the editor's own output.

---

## Top-level shape

```json
{
  "schematic": { ... },
  "view": { "zoom": 1.0, "pan": { "x": 0, "y": 0 } }
}
```

`view` is optional — omit it and the editor frames the schematic itself on
open. Everything electrical lives under `schematic`.

```json
"schematic": {
  "devices":     [ ... ],
  "units":       [ ... ],
  "items":       [ ... ],
  "connections": [ ... ],
  "links":       [ ... ],
  "layers":      [ ... ],
  "wires":       []
}
```

| Array | Holds |
|---|---|
| `devices` | Logical parts: chips, passives, headers, displays. A device owns one or more units. |
| `units` | The placed, drawable pieces of each device. Connections attach to **units**, never to devices. |
| `items` | Standalone items that aren't part of a device: VCC, GND, clock, oscillators, net labels, cosmetic shapes. |
| `connections` | Pin-to-pin electrical connections. This is the electrical truth of the schematic. |
| `links` | Ribbon-cable links between equal-width header units. |
| `layers` | Layer table. Every unit/item's `layer` field indexes into this. Index 0 is the always-visible Default. |
| `wires` | Legacy visual cache. **Always emit `[]`** (see below). |

---

## Conventions that apply everywhere

**IDs.** Every `id` is an arbitrary unique non-empty string. On load the file's
ids are preserved verbatim and connections resolve by matching id strings, so
any scheme works as long as ids are unique within the file. TTLSim writes
32-character lowercase GUIDs (no dashes); match that for consistency, but short
readable ids are equally valid in a generated file.

**Coordinates** (`position`, `pan`) are integer grid units, not pixels. The
editor multiplies by the grid pitch and zoom at paint time. `position` is the
top-left of the unit's *unrotated* bounding box.

**Rotation** is an integer and must be exactly `0`, `90`, `180`, or `270`
(clockwise). Any other value makes the load fail.

**`layer`** is an integer index into the `layers` array. Use `0` unless you are
deliberately placing something on another layer.

---

## `devices[]`

```json
{
  "id": "dev-u1",
  "designator": "U1",
  "partKind": "ic",
  "partIdentifier": "00",
  "family": "HC",
  "value": null,
  "program": null,
  "propagationDelayNs": null,
  "function1": null,
  "function2": null,
  "frequencyHz1": null,
  "frequencyHz2": null
}
```

| Field | Type | Notes |
|---|---|---|
| `id` | string | Referenced by each unit's `deviceId`. |
| `designator` | string | `"U1"`, `"R3"`, etc. Drawn on the canvas. |
| `partKind` | string | One of `"ic"`, `"passive"`, `"header"`, `"display"`. |
| `partIdentifier` | string | The library key for the part (see below). |
| `family` | string? | ICs only. The TTL family name: `"Standard"`, `"LS"`, `"HC"`, `"HCT"`, etc. Null for non-ICs. |
| `value` | string? | Passives only. `"10k"`, `"red"`, `"100n"`, etc. |
| `program` | string? | Memory/PLD only. Embedded program image: Intel HEX text for EEPROM/ROM, JEDEC text for a GAL. Null otherwise. |
| `propagationDelayNs` | int? | Memory/PLD only. Explicit delay override in ns; null = use the part's default speed grade. |
| `function1` | string? | 555/556 only. Timer-1 role: `"Schmitt"` or `"Astable"`. |
| `function2` | string? | 556 only. Timer-2 role. |
| `frequencyHz1` | double? | 555/556 only. Timer-1 astable frequency. |
| `frequencyHz2` | double? | 556 only. Timer-2 astable frequency. |

**`partIdentifier` by kind:**

- **ic** — the chip's part number as it appears in the TTLSim library: `"00"`,
  `"08"`, `"32"`, `"181"`, `"374"`, `"GAL16V8"`, `"GAL20V8"`, `"DS1813"`, and so
  on. No `74`/`74HC` prefix — just the number, with the family carried
  separately in `family`.
- **passive** — the passive identifier: e.g. `"resistor"`, `"led"`,
  `"capacitor"`, `"button"`, `"switch"`, `"spdt-switch"`, `"jumper-2pin"`,
  `"jumper-3pin"`. The human-facing value (resistance, colour) goes in `value`.
- **header** — the header-out identifier (2/3/4/6/8-pin variants).
- **display** — the seven-segment identifier.

Only the IC examples above are confirmed verbatim here; for any other part take
the identifier from the library entry in TTLSim and place it in the file
unchanged.

---

## `units[]`

A unit is one placed, wirable piece of a device. The chips in the current
library are **box-style, single-unit** — one box carrying every pin of the
package, including power — so a chip device has exactly one unit. (Multi-gate
symbols where one 7400 would become four `U1a`..`U1d` units are not in the
current catalogue.)

```json
{
  "id": "u1",
  "deviceId": "dev-u1",
  "unitLetter": "",
  "position": { "x": 40, "y": 24 },
  "label": "NAND",
  "rotation": 0,
  "layer": 0,
  "switchClosed": null
}
```

| Field | Type | Notes |
|---|---|---|
| `id` | string | What connections reference. |
| `deviceId` | string | Must match a `devices[].id`. |
| `unitLetter` | string | `"a"`, `"b"`, … for multi-unit parts; **`""` for the single-unit box chips and passives in the current library**; `"?"` for a power unit. |
| `position` | point | `{ "x", "y" }` grid units, top-left of the unrotated body. |
| `label` | string | Free text drawn beside the symbol. Keep it short; `""` is fine. |
| `rotation` | int | `0` / `90` / `180` / `270`. |
| `layer` | int | Index into `layers`. Usually `0`. |
| `switchClosed` | bool? | SPST switch / SPDT switch units only. `false` for an open SPST (or SPDT throw A), `true` for closed (throw B). **Null for everything else.** When generating from scratch, default switches to `false`. |

---

## `items[]`

Standalone items not owned by a device. The `type` field discriminates.

```json
{
  "type": "vcc",
  "id": "vcc1",
  "label": "",
  "position": { "x": 60, "y": 18 },
  "rotation": 0,
  "layer": 0
}
```

`type` is one of: `"vcc"`, `"gnd"`, `"clock"`, `"canosc"`, `"canosc8"`,
`"netlabel"`, `"rect"`, `"text"`.

Common fields on every item: `type`, `id`, `label`, `position`, `rotation`,
`layer`. Type-specific fields:

| Type | Extra fields | Meaning / default |
|---|---|---|
| `vcc`, `gnd` | — | Power rail symbols. Single connectable pin, **number `0`**. |
| `clock` | `frequencyHz` (1e6), `dutyCycle` (0.5), `startHigh` (false) | Simulator clock source. |
| `canosc` | `frequencyHz` (1e6), `designator` (e.g. `"X1"`) | Canned oscillator, DIP-14. |
| `canosc8` | `frequencyHz` (1e6), `designator` | Canned oscillator, DIP-8. |
| `netlabel` | `width` (int), `startBit` (0), `mirrored` (false), `color` | Named-net / bus tap. See the dedicated section below. |
| `rect` | `width` (20), `height` (12), `filled` (true), `fillColor`, `borderColor` | Cosmetic rectangle. No electrical meaning. |
| `text` | `fontSize` (4.0), `textColor` | Cosmetic text label; the text rides on `label`. |

Values in parentheses are the defaults applied when the field is absent. Colour
fields are TTLColor enum **names** (`"Red"`, `"Black"`, `"Blue"`, `"Grey"`,
`"Yellow"`, `"Olive"`, `"Cyan"`, …); an unrecognised or missing colour falls
back to `"Grey"`.

`canosc`/`canosc8` carry a `designator` (the `X`-series reference). VCC, GND and
clock do not.

---

## `netlabel` — named nets and buses

A `netlabel` item is an electrical **tap on a named global net or bus**. It is
the mechanism for carrying a signal or a bundle of signals across the canvas
without drawing point-to-point connections between distant pins.

```json
{
  "type": "netlabel",
  "id": "nl-ir-reg",
  "label": "IR",
  "position": { "x": 105, "y": 174 },
  "rotation": 180,
  "layer": 0,
  "width": 8,
  "startBit": 0,
  "mirrored": false,
  "color": "Yellow"
}
```

| Field | Type | Notes |
|---|---|---|
| `label` | string | The **net name**. This is the identity of the net: every netlabel in the file with the same label string belongs to the same global net/bus. Case and punctuation are significant — `"T"`, `"Tn"`, `"/SRC"`, `"SRC"` are four different nets. |
| `width` | int | Number of bit-pins this tap exposes. `1` for a plain single-signal net (CLK, /RESET); >1 for a bus tap. |
| `startBit` | int | The global bus bit that this tap's **pin 1** maps to. Default `0`. |
| `mirrored` | bool | Cosmetic draw flag (flips the symbol). No electrical meaning. |
| `color` | string? | TTLColor name for the tap and its wires. Use one colour per logical bus, consistently across every tap of that bus. |
| `rotation` | int | 0/90/180/270 as usual; cosmetic. |

### Pins and bit mapping

A netlabel's connectable pins are numbered **1..width** (unlike VCC/GND, which
use pin 0). Pin *k* of a tap maps to **global bus bit `startBit + k − 1`**.

So a full-width tap on an 8-bit bus is `width: 8, startBit: 0` (pins 1..8 =
bits 0..7), while a tap exposing only the upper nibble is `width: 4,
startBit: 4` (pin 1 = bit 4, pin 4 = bit 7). A `width: 1, startBit: 5` tap
picks out just bit 5 of a wider bus — single-bit taps of a wide bus are the
normal way to route one control line from a decoded bundle (e.g. a `/DST`
bit-0 tap at a register's enable pin, while the 16-bit `/DST` source tap sits
at the '154).

Connections attach to netlabel pins exactly like any other pin ref:

```json
{ "id": "c-ir0", "a": { "itemId": "u-ir", "pinNumber": 2 },
                 "b": { "itemId": "nl-ir-reg", "pinNumber": 1 }, "color": "Yellow" }
```

### Net identity and electrical semantics

- **Same label ⇒ same net.** All taps sharing a label are shorted together
  bit-for-bit according to their `startBit` mapping. There is no separate
  net-declaration record; the bus exists implicitly as the union of its taps.
- **Different label ⇒ different net**, no matter how related the signals are
  conceptually. An encoded value and its decode (e.g. a 3-bit binary state
  counter on `T` and its 8-line one-hot active-low decode on `Tn`) must be two
  distinctly named buses, joined only through the decoder chip that computes
  one from the other.
- The **effective width of the global bus** is implied by its taps (the
  maximum `startBit + width` in use). Individual taps may be any slice of it.
- Netlabels carry ordinary signals only. Power still goes through `vcc`/`gnd`
  symbols, and the one-driver-per-net rule applies to a named bus the same as
  to a drawn wire — a netlabel does not arbitrate multiple drivers.

### Conventions for generated files

- Give each bus one colour and use it on every tap and every connection
  touching that bus (e.g. all control-signal netlabels yellow, address bus
  olive, data bus cyan).
- Name active-low signals with a leading `/` (`/RESET`, `/SRC`, `/DST`) so the
  polarity is visible at every tap.
- Place a tap adjacent to the pin group it serves and connect with short local
  wires; let the shared label do the long-distance routing. This keeps the
  canvas readable and is the whole point of the mechanism.
- Prefer slice taps (`width`/`startBit`) over full-width taps with unused
  pins: a consumer that reads only bits 4–7 should carry a `width: 4,
  startBit: 4` tap, not an 8-wide tap with four floating pins.

---

## `connections[]`

A pure pin-to-pin link. No geometry — the editor's router draws the wire.

```json
{
  "id": "c1",
  "a": { "itemId": "u1",   "pinNumber": 14 },
  "b": { "itemId": "vcc1", "pinNumber": 0 },
  "color": "Red"
}
```

| Field | Type | Notes |
|---|---|---|
| `id` | string | Unique. |
| `a`, `b` | pin ref | The two endpoints. |
| `color` | string? | TTLColor name. Missing → Black on load. |

**Pin ref:**

| Field | Type | Notes |
|---|---|---|
| `itemId` | string | The id of a **unit** or a standalone **item**. Units and items share one id space on load, so either kind of id resolves. Never a device id. |
| `pinNumber` | int | The pin number on that unit/item. |
| `external` | bool | Clipboard-only. **Omit it (or set `false`) in a file.** |

Pin numbers are the part's real package pin numbers (pin 14 = VCC on a DIP-14,
pin 7 = GND, and so on). Two exceptions: the VCC and GND symbols' single pin is
number **0**, and netlabel taps use **1..width** (see the netlabel section).

---

## `links[]`

Ribbon-cable links between two header-out units of equal pin count. Ties pin *i*
of A to pin *i* of B.

```json
{ "id": "lnk1", "aId": "hdrA", "bId": "hdrB", "reversed": false }
```

`aId`/`bId` are header **unit** ids. Pin count is not stored — it is re-derived
from the two headers on load, and the link is dropped if they no longer match.
`reversed` is a cosmetic draw flag only. Omit the whole array (or `[]`) if you
use no ribbon links.

---

## `layers[]`

```json
[ { "name": "Default", "visible": true } ]
```

Index 0 is the Default layer and must exist (everything with `layer: 0` lands
here). Add more entries only if you are spreading the schematic across layers;
each unit/item's `layer` indexes this list.

---

## `wires[]`

Legacy field. The current model carries no wire geometry — `connections` holds
the electrical truth and the editor's auto-router lays out the visual path.

**Always emit `"wires": []`.** Do not synthesise wire geometry.

---

## `view`

```json
"view": { "zoom": 1.0, "pan": { "x": 0, "y": 0 } }
```

Optional. `zoom` is a float, `pan` is a grid-unit point. Omit it to let the
editor zoom-to-fit on open.

---

## Power wiring — required, build-blocking

Every IC must have **both** its VCC pin and its GND pin connected, or the build
fails:

- `TTL002` — VCC pin not connected (error).
- `TTL003` — GND pin not connected (error).

Wire each chip's power pin (the part's VCC/GND package pins) to a `vcc` / `gnd`
item, whose pin is number `0`:

```json
{ "id": "cvcc", "a": { "itemId": "u1", "pinNumber": 14 }, "b": { "itemId": "vcc1", "pinNumber": 0 }, "color": "Red"   },
{ "id": "cgnd", "a": { "itemId": "u1", "pinNumber": 7  }, "b": { "itemId": "gnd1", "pinNumber": 0 }, "color": "Black" }
```

Multiple chips may share one VCC symbol and one GND symbol — several
connections can terminate on the same symbol pin. Passives, headers and displays
have no power pins and are not checked. Power never travels over netlabels.

---

## Wire-colour convention

Set colours at generation time rather than leaving them null:

- **VCC connections → `"Red"`** (any connection touching a `vcc` symbol).
- **GND connections → `"Black"`** (any connection touching a `gnd` symbol).
- **Bus connections → the bus's colour**: every connection touching a netlabel
  tap uses that bus's single chosen colour.
- Other signal wires: a colour if it aids reading, otherwise omit (loads as
  Black).

---

## Pre-flight checklist before writing the file

1. Every `connections` endpoint `itemId` matches a `units[].id` or an
   `items[].id`. No dangling references.
2. Every IC has its VCC pin **and** GND pin wired to a power symbol (TTL002 /
   TTL003).
3. Every `vcc` / `gnd` symbol has at least one connection — no floating power
   symbols.
4. Every netlabel has at least one connection, its pin references fall within
   1..width, and every net name resolves to at least one driver and one
   consumer — a label that appears on only one tap is a routing dead-end.
5. No two drivers on the same named net bit (the one-source rule applies to
   buses exactly as to drawn wires).
6. Every unit's `deviceId` matches a `devices[].id`.
7. Every `rotation` is 0/90/180/270.
8. `wires` is `[]`.
9. Switches default to `switchClosed: false` on a freshly generated schematic.

---

## Minimal complete example

A single 74HC00 with its power pins wired, one gate input pulled to VCC, and
the gate output published on a named net.

```json
{
  "schematic": {
    "devices": [
      {
        "id": "dev-u1",
        "designator": "U1",
        "partKind": "ic",
        "partIdentifier": "00",
        "family": "HC"
      }
    ],
    "units": [
      {
        "id": "u1",
        "deviceId": "dev-u1",
        "unitLetter": "",
        "position": { "x": 40, "y": 24 },
        "label": "NAND",
        "rotation": 0,
        "layer": 0,
        "switchClosed": null
      }
    ],
    "items": [
      { "type": "vcc", "id": "vcc1", "label": "", "position": { "x": 64, "y": 12 }, "rotation": 0, "layer": 0 },
      { "type": "gnd", "id": "gnd1", "label": "", "position": { "x": 30, "y": 60 }, "rotation": 0, "layer": 0 },
      { "type": "netlabel", "id": "nl1", "label": "Y0", "position": { "x": 56, "y": 26 }, "rotation": 0, "layer": 0,
        "width": 1, "startBit": 0, "mirrored": false, "color": "Yellow" }
    ],
    "connections": [
      { "id": "cvcc", "a": { "itemId": "u1", "pinNumber": 14 }, "b": { "itemId": "vcc1", "pinNumber": 0 }, "color": "Red"    },
      { "id": "cgnd", "a": { "itemId": "u1", "pinNumber": 7  }, "b": { "itemId": "gnd1", "pinNumber": 0 }, "color": "Black"  },
      { "id": "cin",  "a": { "itemId": "u1", "pinNumber": 1  }, "b": { "itemId": "vcc1", "pinNumber": 0 }, "color": "Red"    },
      { "id": "cout", "a": { "itemId": "u1", "pinNumber": 3  }, "b": { "itemId": "nl1",  "pinNumber": 1 }, "color": "Yellow" }
    ],
    "links": [],
    "layers": [ { "name": "Default", "visible": true } ],
    "wires": []
  },
  "view": { "zoom": 1.0, "pan": { "x": 0, "y": 0 } }
}
```
