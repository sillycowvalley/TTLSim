# TTLSim — 74-Series TTL Schematic Editor & Emulator

Windows desktop app for designing and emulating 74-series TTL logic IC circuits.
C# / WinForms / .NET 8. Single solution, no third-party UI dependencies.

This document tracks **remaining work**. Roadmap for the simulator is in
`SIMULATOR.md`; known defects are in `BUGS.md`.

---

## 1. Status (terse)

**Done:** four-project solution (UI / Core / Chips / Tests); model layer
(Schematic, Device, Unit, Connection, Pin); component catalogue (~30 ICs across
Gates / FlipFlops / Registers / Counters / Decoders / Multiplexers / Buffers /
ALU, plus memory and timers; passives; power/clock symbols); library panel with
drag-drop; canvas (grid, zoom, pan, selection, marquee, move, rotate, wire
placement, delete); copy / cut / paste; undo/redo; `.ttlproj` save/load;
property grid; simulator engine (Signal, Net, NetTable, EventQueue, Simulator,
scheduler); build pipeline with a subset of diagnostics; chip models for
74HC08/14/32/47/393 + SevenSegCa + VCC/GND/Clock drivers; sim toolbar + state
machine + output panel; Serilog logging.

Clipboard: copy/cut/paste of a selection through a private `TTLSim.Schematic`
clipboard format (a `SchematicDto` serialised to JSON — nothing other apps can
read). Cut copies then reuses the delete-composite path, so undo is identical to
a delete. Paste rebuilds with fresh ids/designators and lands under the cursor,
or steps at a cascade offset when the cursor is off-canvas; Ctrl+drag duplicates.
Wired into the Edit menu (Ctrl+C/X/V) with the canvas owning the logic.

Everything below is **not done**.

---

## 2. Remaining work — editor

### 2.1 Selection & property grid

- **Selection summary still uses `GetType().Name`.** Status bar reads
  "Selected: NandGateUnit"; should read "Selected: U1a" via `DisplayDesignator`.
  Single fix in `MainForm` selection-changed handler.
- **Multi-select property edit — per-target old values.**
  `PropertyValueChanged` supplies only one `OldValue`; it is currently reused for
  every target, so undo of a multi-select edit is wrong. Cache per-target values
  on `SelectedGridItemChanged` before the edit commits.

### 2.2 Editing features (not started)

- **Snap-to-pin.** Nudge components onto nearby existing pins while moving.
- **Bus / labelled nets.** Symbolic labels for cross-schematic connections.
- **Canvas context menu.** No right-click menu exists yet. Needed at minimum for
  "Show power pins" (see 2.4); could also host a "Paste here" entry — the canvas
  already exposes `PasteAt`.

### 2.4 Power units

`Device.PowerUnit` is a nullable property; abstract `PowerUnit` exists; **no
concrete subclass and no UI to create one.** See `BUGS.md` B1.

- Concrete `IcPowerUnit : PowerUnit` — two-pin unit (VCC up, GND down), `U1?`
  designator, pin numbers from `IcPartDefinition.PowerPin` / `GroundPin`.
- "Show power pins" right-click action on selected Units of one Device. One
  composite undo step. No library entry — power units belong to a Device.

## 3. Remaining work — routing

- **Chain wires overlap.** A↔B, B↔C, C↔D route through the shared B/C pins and
  stack on top of each other. Fix: when a sub-star's trunk+branches model can't
  capture a chain, route the chain as one continuous polyline through all pins
  in order, then split per-`Connection` for the result dict.
- **Star trunk choice on disfavoured geometry.** Trunk = longest leg. When other
  leaves sit on the opposite side of the common pin they route back through it
  with no usable merge. Needs a smarter trunk score (e.g. minimise other leaves'
  distance to the trunk bbox).

## 4. Remaining work — component library

The catalogue is broad, but **simulation models lag the catalogue badly**. Most
library parts have no `IChip` behaviour. See `SIMULATOR.md` for the modelling
backlog. From a *placement/visual* standpoint the library is largely complete;
gaps are:

- **Dedicated unit shapes** for the box-style parts (registers, counters, ALU,
  decoders, muxes) — verify each is more than a generic rectangle where a
  recognisable symbol is expected.
- **I/O & passive variants** (later): DIP switch, logic probe, bus indicator,
  polarised cap, pot, inductor.

## 5. Remaining work — settings & theming (not started)

None of this infrastructure exists yet beyond a bare `SignalColors` class.

- **`AppSettings` POCO** in `TTLSim.Core`, persisted to
  `%APPDATA%\TTLSim\settings.json` (System.Text.Json, `Color` ↔ `#RRGGBB`
  converter). Sections: Signals, Displays, Editor, Sim, Engine, Logging, Paths.
  Defaults in property initialisers so a fresh install needs no file.
- **`AppSettingsService`** raising `SettingsChanged`.
- **`ThemeProvider`** (in `TTLSim.UI`) caching `Pen`/`Brush` instances, rebuilt
  on change. Goal: no `Color` literal anywhere in rendering code.
- **Tools → Options dialog** — tabbed, tabs mirror `AppSettings` sections,
  colour fields are clickable swatches over `ColorDialog`, live preview,
  per-tab and global "Reset to defaults". No Tools menu exists yet.

## 6. Polish — later

- Print / export to PNG / SVG.
- BOM extraction.

## 7. Open questions

- **Pin labels.** Currently numbers. Functional names (`D`, `Q`, `CLK`) when
  zoomed in? Both at appropriate zooms?
