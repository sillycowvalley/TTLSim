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
| `items` | Standalone items that aren't part of a device: VCC, GND, clock, oscillators, cosmetic shapes. |
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
  "switchClosed": null,
  "mirrored": null
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
| `mirrored` | bool? | Header units only. `true` flips the header across its long axis so the pins exit from the opposite edge of the body; pin order, pin numbering, and ribbon-link mapping are unchanged. **Null for everything else.** Null or absent loads as `false`, so files written before the feature are unaffected. |

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
`"rect"`, `"text"`.

Common fields on every item: `type`, `id`, `label`, `position`, `rotation`,
`layer`. Type-specific fields:

| Type | Extra fields | Meaning / default |
|---|---|---|
| `vcc`, `gnd` | — | Power rail symbols. Single connectable pin, **number `0`**. |
| `clock` | `frequencyHz` (1e6), `dutyCycle` (0.5), `startHigh` (false) | Simulator clock source. |
| `canosc` | `frequencyHz` (1e6), `designator` (e.g. `"X1"`) | Canned oscillator, DIP-14. |
| `canosc8` | `frequencyHz` (1e6), `designator` | Canned oscillator, DIP-8. |
| `rect` | `width` (20), `height` (12), `filled` (true), `fillColor`, `borderColor` | Cosmetic rectangle. No electrical meaning. |
| `text` | `fontSize` (4.0), `textColor` | Cosmetic text label; the text rides on `label`. |

Values in parentheses are the defaults applied when the field is absent. Colour
fields are TTLColor enum **names** (`"Red"`, `"Black"`, `"Blue"`, `"Grey"`, …);
an unrecognised or missing colour falls back to `"Grey"`.

`canosc`/`canosc8` carry a `designator` (the `X`-series reference). VCC, GND and
clock do not.

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
pin 7 = GND, and so on). The VCC and GND symbols are the exception: their single
pin is number **0**.

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
have no power pins and are not checked.

---

## Wire-colour convention

Set colours at generation time rather than leaving them null:

- **VCC connections → `"Red"`** (any connection touching a `vcc` symbol).
- **GND connections → `"Black"`** (any connection touching a `gnd` symbol).
- Signal wires: a colour if it aids reading, otherwise omit (loads as Black).

---

## Pre-flight checklist before writing the file

1. Every `connections` endpoint `itemId` matches a `units[].id` or an
   `items[].id`. No dangling references.
2. Every IC has its VCC pin **and** GND pin wired to a power symbol (TTL002 /
   TTL003).
3. Every `vcc` / `gnd` symbol has at least one connection — no floating power
   symbols.
4. Every unit's `deviceId` matches a `devices[].id`.
5. Every `rotation` is 0/90/180/270.
6. `wires` is `[]`.
7. Switches default to `switchClosed: false` on a freshly generated schematic.

---

## Minimal complete example

A single 74HC00 with its power pins wired and one gate input pulled to VCC.

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
        "switchClosed": null,
        "mirrored": null
      }
    ],
    "items": [
      { "type": "vcc", "id": "vcc1", "label": "", "position": { "x": 64, "y": 12 }, "rotation": 0, "layer": 0 },
      { "type": "gnd", "id": "gnd1", "label": "", "position": { "x": 30, "y": 60 }, "rotation": 0, "layer": 0 }
    ],
    "connections": [
      { "id": "cvcc", "a": { "itemId": "u1", "pinNumber": 14 }, "b": { "itemId": "vcc1", "pinNumber": 0 }, "color": "Red"   },
      { "id": "cgnd", "a": { "itemId": "u1", "pinNumber": 7  }, "b": { "itemId": "gnd1", "pinNumber": 0 }, "color": "Black" },
      { "id": "cin",  "a": { "itemId": "u1", "pinNumber": 1  }, "b": { "itemId": "vcc1", "pinNumber": 0 }, "color": "Red"   }
    ],
    "links": [],
    "layers": [ { "name": "Default", "visible": true } ],
    "wires": []
  },
  "view": { "zoom": 1.0, "pan": { "x": 0, "y": 0 } }
}
```
