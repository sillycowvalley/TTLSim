# TTLSim — Simulator

Remaining work on the simulation engine. Companion to `PROJECT.md`.

---

## 1. Status (terse)

**Done:** `TTLSim.Core` engine — `Signal` (four-state), `Net` / `NetTable`
(union-find pin closure, totem-pole resolution), `EventQueue`, `Simulator`
(`Start` / `RunUntil` / `RunUntilQuiescent`, `IScheduler`), `IChip`,
`SchematicBuilder` → `BuildResult` with a subset of diagnostics. `TTLSim.Chips`
— `Hc08`, `Hc14`, `Hc32`, `Hc47`, `Hc161`, `Hc163`, `Hc393`, `SevenSegCa`,
`VccDriver`, `GndDriver`, `ClockSource`, `PullDriver`. UI — `SimulationController`
state machine (Edit / Built / Running / Paused), sim toolbar, `OutputPanel`,
canvas signal/LED colouring, button/switch input bindings. Tests for several
chips, NetTable, builder, sources.

Everything below is **not done**.

---

## 2. Remaining work — build pipeline diagnostics

`SchematicBuilder` currently emits only: unused-unit warning, floating-input
warning, dangling-output warning, VCC–GND short error. The following documented
categories are **missing**:

**Errors (must block Run):**

- Unpowered / ungrounded chip — every chip's VCC/GND pin must reach a rail.
- Unknown chip model — Device has no `IChip` factory binding.
- Floating clock input.

**Warnings (don't block Run):**

- All-tri-state net — net's drivers are all tri-state; suggest pull-up/down.
- Net with listeners but no driver — will stay `Unknown`.
- Zero-delay feedback path — infinite-loop risk.

Also missing: the **multi-driver totem-pole conflict** check (resolution exists
in `Net.Resolve`, but the builder doesn't surface it as a diagnostic;
should be toggleable error↔warning).

There is no explicit **power-resolution phase** or **sanity-scan phase** — the
builder jumps from net extraction + per-unit floating checks straight to chip
binding. Add them.

## 3. Remaining work — chip models

Models exist for **6 parts**. The catalogue has **~30**. Backlog, by group:

- **Flip-flops:** `74HC74` dual D — no model. Needs a `DFlipFlop`-style chip
  (D / CLK / Q / Q̄ / PRE / CLR, edge-triggered).
- **Registers:** `74HC574`, `74HC273`, `74HC377` — no models. Need an 8-bit
  D-register chip (tri-state variant for '574, async-clear for '273,
  clock-enable for '377).
- **ALU:** `74HC181`, `74HC182`, `74HC299` — no models. '181 is the big one
  (16 arith + 16 logic ops).
- **Bus / mux:** `74HC245`, `74HC244`, `74HC541`, `74HC257`, `74HC157` — no
  models. Tri-state output handling needed (depends on open-collector /
  HighZ resolution being finished).
- **Decoders:** `74HC138`, `74HC139`, `74HC154` — no models.
- **Memory:** `28C256` / `28C128` / `28C64` EEPROM, `62256` SRAM — no models.
  Separate device family; needs an addressable-store chip abstraction.
- **Timers:** `NE555` / `NE556` — no models.

`ChipFactory.CreateForDevice` currently always returns `null` — device-level
(as opposed to unit-level) chip binding is unimplemented. Box-style parts
(registers, counters as single units, ALU, memory) will need this path.

## 4. Remaining work — execution & UI

### 4.1 Sim-mode rendering — verify / finish

Canvas signal colouring and LED state are wired. Still to confirm or build:

- Junction blobs coloured by net state.
- Click-to-pin a net into a watch list (floating panel, top-right).

### 4.2 Toolbar & status

- **Step** button — `SimulationController.Step()` .
- **Engine status strip:** Events/sec │ Queue depth │ Net count │ Sim tick (ps).
  Not present.

### 4.3 Output panel

- `F8` / `Shift+F8` to cycle diagnostics — confirm bound.

## 5. Remaining work — tests

- Per-chip truth-table / sequential tests for every **unmodelled** part once
  its model lands (currently only 08 / 163 / 393 / 47 + sources + buttons).
- One minimal repro schematic per **missing diagnostic category** (§2).
- Integration: load `Seconds.ttlproj`, run 60 simulated seconds, assert
  segment state each second. (`Seconds.ttlproj` exists; the test does not.)
- Assert diagnostic messages against Serilog's test sink to catch regressions.

## 6. Logging — remaining

Serilog is wired (`%LOCALAPPDATA%\TTLSim\Logs\ttlsim-<date>.log`, daily roll,
50 MB cap, 30-file retention; verbose toggle persisted to
`logging-state.txt`). Remaining:

- Ensure **all** build diagnostics are also written to the log, not just the
  output panel.
- Confirm structured logging everywhere (`log.LogInformation("Net {NetId} …")`)
  — no string interpolation in log calls.
- `Trace` level (per-event scheduled/dispatched) — confirm it exists and is
  off by default but toggleable.

## 7. Deferred (explicitly out of scope for now)

- Open-collector + pull-up resolution (blocks proper 74HC245-class modelling).
- Analog-ish modelling (Schmitt + RC oscillator on a full clock circuit).
- Waveform pane (logic-analyzer view of watched nets).
- Tri-state bus contention beyond two drivers.
- Save / restore simulation state.
