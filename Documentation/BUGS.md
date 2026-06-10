# TTLSim — Bug Tracker

Open defects only. Feature/roadmap work lives in `PROJECT.md` and
`SIMULATOR.md`.

---

## B1. No way to opt into a `PowerUnit` for an IC

**Severity:** low (blocks simulator power-resolution work).
**Component:** library / context-menu / `DeviceFactory`.

`Device.PowerUnit` is nullable; `PowerUnit` is abstract with no concrete
subclass. No UI action materialises one, so a chip's VCC/GND pins are invisible
and unwirable — which also means the planned "unpowered chip" build diagnostic
has nothing to check against.

**Fix sketch.**

1. `IcPowerUnit : PowerUnit` — two-pin unit, VCC up + GND down, `U1?`
   designator, pin numbers from `IcPartDefinition.PowerPin` / `GroundPin`.
2. "Show power pins" right-click action (no canvas context menu exists yet)
   when Units of one Device are selected. Creates the `IcPowerUnit`, sets
   `Device.PowerUnit`, adds to `Schematic.Items`, places near the selection.
   One composite undo step.
3. No library entry — power units belong to a specific Device.

Delete semantics already take the `PowerUnit` with the Device.

---

## B2. `ChipFactory` ignores existing `Hc161` / `Hc163` models

**Severity:** medium. **Component:** `TTLSim.Chips/ChipFactory`.

`Hc161` and `Hc163` chip classes are implemented (and `Hc163` has tests), but
`ChipFactory.CreateForUnit` only dispatches part identifiers `08`, `14`, `32`,
`47`, `7seg-ca`. Placing a 74HC161/163 and building yields an "unknown chip
model" outcome despite the model existing.

**Fix:** add `"161"` / `"163"` cases to the dispatch switch with the
appropriate `TryCreate…` helpers.

---

## B3. Multi-select property edit records wrong undo values

**Severity:** medium. **Component:** `MainForm` / property grid.

`PropertyValueChanged` provides a single `OldValue`. The current handler reuses
it as the old value for *every* selected target, so undoing a multi-select
property change restores incorrect values on all but one item.

**Fix:** cache each target's value on `SelectedGridItemChanged`, before the
edit commits, and use the per-target cache when building the `SetPropertyCommand`s.

---

## Notes — confirmed still present, low priority

- `WireRouter` retains diagnostic `Debug.WriteLine` calls (~lines 80, 84, 111)
  from earlier routing debugging. Harmless in release (`Debug` is stripped) but
  should be removed.
- SchematicCanvas debug overlays (pink `RoutingBounds`, dotted-blue connector
  underlay) are `#if DEBUG`-gated rather than removed — decide between a
  View-menu toggle or deletion (see `PROJECT.md` §2.3).

## Notes — previously tracked, now fixed

- Wire colour no longer hardcoded red — `DrawWire` uses `connection.Color` or
  the sim `SignalProvider`.
- LED `RoutingBounds` covers emission arrows.
- `Unit.Device` exposed to the property grid via `ExpandableObjectConverter`.
- Gate labels render inside the body; fonts reduced.
- Star/chain merging with junction blobs works (though chain *overlap* is still
  a routing-quality item — see `PROJECT.md` §3, not a bug).
