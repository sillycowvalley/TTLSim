# TTLSim — Bug Tracker

Open defects only. Feature/roadmap work lives in `PROJECT.md` and
`SIMULATOR.md`.

---

## B1. Vestigial `PowerUnit` scaffolding (original defect superseded)

**Severity:** low (cleanup, not a defect).
**Component:** `TTLSim.UI/Model/Device.cs`, `Unit.cs`.

The original B1 ("a chip's VCC/GND pins are invisible and unwirable; the
unpowered-chip diagnostic has nothing to check against") was solved by the
box-chip migration, not by the planned `IcPowerUnit`:

- Every `ChipPartDefinition` carries `PowerPin` / `GroundPin`, and VCC/GND
  are real, named, wirable pins on the box symbol.
- `SchematicBuilder` Phase 1e emits **TTL002** (VCC unconnected) and
  **TTL003** (GND unconnected) as build-blocking errors, checked at device
  scope against the net table.
- `IcPartDefinition.Catalogue` is empty — no multi-unit gate symbols are
  placeable, so the scenario B1 described can no longer arise from the
  library.

What remains is dead scaffolding:

- Abstract `PowerUnit : Unit` with no concrete subclass.
- `Device.PowerUnit` — always null; the canvas delete path checks it
  (harmlessly).
- The `'?'` unit-letter / `"U2?"` designator convention in `Unit`.

**Decide:** delete the scaffolding, or keep it if multi-unit IC symbols ever
return. Not checked: whether the legacy `"ic"` partKind load path
(`SchematicSerializer.IcLookup`) still resolves any parts — if old
`.ttlproj` files can still materialise multi-unit gates, their power-pin
visibility should be verified before deleting.

---

## B3. Multi-select property edit records wrong undo values

**Severity:** medium. **Component:** `MainForm` / property grid.

Confirmed still present in the current `PropertyValueChanged` handler: the
multi-target branch builds every `SetPropertyCommand` with the single
`e.OldValue`, so undoing a multi-select property change restores incorrect
values on all but one item.

**Fix:** cache each target's value on `SelectedGridItemChanged`, before the
edit commits, and use the per-target cache when building the
`SetPropertyCommand`s.

---

## Notes — confirmed still present, low priority

- SchematicCanvas debug overlays (pink `RoutingBounds` fill and the
  dotted-blue `DrawConnector` underlay in `OnPaint`) are `#if DEBUG`-gated
  rather than removed — decide between a View-menu toggle or deletion (see
  `PROJECT.md` §2.3).
