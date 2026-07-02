# TTLSim — GAL22V10 / ATF22V10 Simulation: Task Brief (rev 2, 2026-07-02)

## Objective

Add the **GAL22V10** (and the fuse-compatible Atmel **ATF22V10**) as a simulated
part in TTLSim, so a placed '22V10 can be programmed by importing a WinCUPL/GALasm
`.jed` and will evaluate its logic in the discrete-event simulator — the same way
the GAL16V8 and GAL20V8 already work.

Identifier: **`GAL22V10`** (one part covers Lattice GAL22V10 and Atmel ATF22V10 —
they share the standard 5828-fuse map, exactly as `GAL20V8` already covers the
ATF20V8B). The on-hand physical part is a 24-pin PDIP (ATF22V10CQZ-20PU).

This brief is the **simulator import path only**. Having BlinkyJED *emit* 22V10
fuse maps is a separate, larger task — see "Out of scope" below.

**Decision adopted (2026-07-02): build the Tier 2 (stateful/registered)
architecture from the start.** Registered operation is the reason to own a
'22V10; AR's level-sensitivity shapes the evaluator's event model (it must
re-evaluate on any array-input change, not just clock edges), and retrofitting
state into a combinational evaluator later is more churn than designing it in.
Combinational-only can still be the first *shipped* slice if convenient, but on
the Tier 2 skeleton.

---

## Status of prerequisite work (completed 2026-07-02)

Context a fresh session needs, from the 16V8/20V8 mode-2/3 campaign that
preceded this task:

- **BlinkyJED now emits modes 1, 2, and 3 for the 16V8/20V8.** `Compiler.cs`
  and `PldModel.cs` were rewritten (`.d` and `.oe` suffixes, automatic mode
  selection, per-mode OE-row/logic-row layout and column routing). Delivered
  and committed; any statement elsewhere that BlinkyJED is "mode-1 only" is
  stale.
- **Validated three independent ways:** (1) fuse-level against WinCUPL
  references (OE rows exact; logic terms equal as sets; XOR/AC1/PT/SYN/AC0
  regions bit-exact); (2) the shipped C# build's `.jed` output is byte-identical
  to the reference implementation, checksums included; (3) live TTLSim
  simulation — four `*_livetest.ttlproj` projects (in the repo) ran hundreds of
  cycles each with a cycle-exact external checker: complex-mode truth tables
  with full input coverage, `.oe` tri-state windows, mode-2 pin feedback,
  mode-3 registered counting, reset, global /OE gating only registered cells,
  and carry tracking the *internal* register state through Hi-Z windows
  (proving register feedback, not pin feedback).
- **Therefore TTLSim's GAL engine demonstrably simulates 16V8/20V8 modes 2 and
  3 today**, including clocked registered behaviour and tri-state. The doc
  comment in `GAL.cs` ("combinational only, registered not modelled") and any
  matching claims below are stale — confirm the current `GAL.cs` /
  `GALDevice.cs` by fresh upload before coding; do not trust the
  project-knowledge index for these churned files.
- **Known intentional divergences from WinCUPL** (16V8/20V8, carry the same
  policy to any 22V10 comparisons): product-term *order* inside registered
  OLMCs differs (logically identical), and BlinkyJED leaves the UES/signature
  erased (programmer-verify workaround).
- **Empirical startup finding:** the TTLSim GAL misses its first clock edge
  (previous-clock state Unknown at build; Unknown→High is not treated as a
  rising edge). One count of lag until the first reset, then permanently
  aligned — observed identically on both parts. Judged acceptable (hardware
  power-up state is undefined), but for the '22V10 decide deliberately:
  initialise the previous-clock sample from the first observed level, and model
  the datasheet power-up state (registers reset).
- **Loose end in the repo:** the comment in `gal20v8_complex.pld` says pins 18
  and 19 are output-only in complex mode; on the 20V8 it is pins 15 and 22.
  Comment-only fix.

---

## Background: how TTLSim's GAL engine works today

Read these before writing anything (they are the current, in-repo GAL
subsystem). Per the status note above, request **fresh uploads** of the GAL
files rather than trusting the index:

- **`TTLSim.Chips/PLD/GALDevice.cs`** — `GalDevice` record holding the fuse-map
  *geometry*: `FuseCount, Rows, Cols, OlmcCount, OlmcOutputPins, XorFuseBase,
  SynFuse, Ac0Fuse, ColumnMapMode1/2/3`, plus pin-presentation metadata
  (`Ac1FuseBase`, `FirstOlmcPin`, `ClockPin`, `OePin`, `SpecialPins`,
  `DedicatedInputPins`). Product terms are **uniform**:
  `ProductTermsPerOlmc = Rows / OlmcCount` (= 8 for both existing parts).
  Statics `Gal16V8`, `Gal20V8`; `ForPartNumber(string)`.
- **`TTLSim.Chips/PLD/GAL.cs`** — `Gal : IChip`, the evaluator. Reads SYN/AC0 to
  pick a column map (galasm mode 1/2/3); per-OLMC sum-of-products with XOR
  polarity; one `Driver` per OLMC output pin; an all-intact (erased) OLMC block
  is treated as a plain input and does not drive. `PropagationDelayPs = 10_000`
  nominal, overridden from the part's "Propagation Delay (ns)" by the factory.
  Registered/tri-state behaviour works in practice (see status) — confirm how
  the current source implements it before extending.
- **`TTLSim.Chips/ChipFactory.cs`** — `TryCreateGal(device, pinToNet)`; GAL arms
  of `IsSimulated` and the GAL-part predicate.
- **`TTLSim.UI/Components/ChipPartDefinition.cs`** — `IcGal16V8` (20-pin),
  `IcGal20V8` (24-pin); macrocell pins declared `Out`.
- **`TTLSim.UI/MainForm.cs`** — `OnImportJedec`: `JedecFuseMap.Parse` →
  fuse-count gate (2194/2706) with a "looksLike" switch → sets
  `device.Program`. Also `SingleSelectedGal()`, `UpdateGalMenuItems()`.
- **`TTLSim.Core/JedecFuseMap.cs`** — generic JEDEC reader. **Confirmed from
  source:** sizes the fuse array from QF, verifies the `C` fuse checksum,
  tolerates STX/ETX and the design header. 5828- and 5892-fuse files parse
  unchanged; only the import gate needs to learn the counts.
- **`GalPinModel.cs`** — derives pin names/roles from the fuse map for dynamic
  GAL symbol rendering. Extend for '22V10 macrocell naming +
  registered/active-low indication.
- **`TTLSim.Tests/GalTests.cs`** — evaluator tests against a small synthetic
  `GalDevice` (AND/XOR-polarity/released-output pattern). Follow for '22V10.
- **`BlinkJED/Compiler.cs`, `BlinkJED/TargetDevice.cs`** — the *compiler*
  project: 16V8/20V8, modes 1–3. Not touched by this task except as reference.

---

## Why the 22V10 needs engine work (it is NOT a drop-in)

Three assumptions in the current engine break:

1. **Variable product terms + OE term.** Pins 14/23 → 8 terms, 15/22 → 10,
   16/21 → 12, 17/20 → 14, 18/19 → 16, and **each OLMC has one extra product
   term for output-enable (tri-state)**. The uniform `Rows / OlmcCount` model
   cannot represent this; the geometry needs explicit per-OLMC row offsets plus
   an OE row.
2. **No SYN/AC0 global modes.** Mode + polarity of **each** OLMC is a per-OLMC
   **S0/S1** bit pair. Input routing is fixed (no mode-variant column maps):
   44 columns = 22 array inputs × 2 (true/complement) = 12 dedicated inputs +
   10 feedbacks.
3. **Registered logic + global AR/SP.** Output pin driven by the **Q of the
   OLMC's D flip-flop**; `/Q` feeds back into the array; shared **clock on
   pin 1**; two global product terms — **AR (asynchronous reset)** and
   **SP (synchronous preset)** — feed all registers.

---

## GAL22V10 architecture (verified from the Lattice datasheet)

**24-pin DIP pinout:**

| Pin | Function | Pin | Function |
|----:|----------|----:|----------|
| 1 | I / CLK | 24 | VCC |
| 2 | I | 23 | I/O/Q (8 terms) |
| 3 | I | 22 | I/O/Q (10) |
| 4 | I | 21 | I/O/Q (12) |
| 5 | I | 20 | I/O/Q (14) |
| 6 | I | 19 | I/O/Q (16) |
| 7 | I | 18 | I/O/Q (16) |
| 8 | I | 17 | I/O/Q (14) |
| 9 | I | 16 | I/O/Q (12) |
| 10 | I | 15 | I/O/Q (10) |
| 11 | I | 14 | I/O/Q (8) |
| 12 | GND | 13 | I |

12 dedicated inputs (pins 1–11, 13; pin 1 doubles as the registered clock) and
10 I/O macrocells (pins 14–23). Macrocell pins declared `Out` in the part
definition (same convention as the '16V8/'20V8).

**Fuse-map totals (confirmed):**
- Array = **44 columns × 132 rows** = 5808 array fuses.
- Config bits = 20 S0/S1 fuses (a pair per OLMC) at 5808–5827 (last OLMC:
  S0 = 5826, S1 = 5827).
- **Total = 5828** (a Lattice part with the electronic signature reports
  **5892**; the extra 64 are the UES — accept both counts on import, evaluator
  reads only 0–5827).
- Rows: 120 logic (8+10+12+14+16+16+14+12+10+8) + 10 OE + AR + SP = 132.

**S0/S1 decode (per OLMC):** selects {combinational | registered} ×
{active-high | active-low}, replacing SYN/AC0 + XOR.

**Registered semantics:** clock = pin 1 rising edge; AR clears all registers
asynchronously while its term is true (**level-sensitive** — the evaluator must
re-evaluate on any array-input change, not just edges); SP presets all
registers on the clock edge when its term is true.

**Feedback mux is per-OLMC (session addition):** a **registered** cell feeds
`/Q` back into the array regardless of OE state; a **combinational** cell feeds
back the **pin** — so when its OE term disables the driver, the array sees
whatever drives the pin externally. The evaluator therefore needs the macrocell
pins as inputs for that path (analogous to the 16V8/20V8 mode-2 pin feedback,
already exercised live).

### Exact geometry to extract before coding (do NOT hand-guess)

The precise **per-OLMC row offsets**, **AR/SP row positions**, **OE-row position
within each OLMC block**, and the **44-column line→pin routing** must come from
the **GALasm 22V10 device tables** (same `PinToFuse*/ToOLMC*` / `galasm.h`
source the existing `GalDevice` constants came from), then be **proven against
a real `.jed`** (see Validation). Canonical layout to confirm: row 0 = AR, ten
OLMC blocks each `[1 OE term + its logic terms]` ordered pins 23→14,
row 131 = SP; fuse `n` at row `n / 44`, column `n % 44`.

---

## Design plan (Tier 2 architecture from the start)

- **Geometry:** sibling `Gal22V10Device` (agreed cleaner than generalising
  `GalDevice` — the 22V10 shares almost nothing with the SYN/AC0 model). Carries
  per-OLMC term counts, row offsets, OE rows, S0/S1 addresses, the fixed
  44-column routing, AR/SP rows. Reuse only the low-level SOP/column primitives.
- **Evaluator (`Gal22V10 : IChip`):** per-OLMC SOP over its own rows; S0/S1
  decode; polarity applied per cell. Combinational cells: honour the OE product
  term (drive only while satisfied), pin feedback. Registered cells: D-register
  sampled on the pin-1 rising edge, Q (with polarity) drives the pin, `/Q`
  feeds back; global AR (async, level-sensitive) and SP (sync, at the edge);
  registers power up reset; previous-clock initialised from the first observed
  sample (see startup finding). Erased/all-intact OLMC = pure input, not
  driven.
- Borrow the clocked-state/edge machinery pattern from the existing register
  chips ('374 / '173 / '191). Whether to also generalise so the 16V8/20V8 path
  shares it: optional, and only if the fresh `GAL.cs` upload shows it wouldn't
  disturb the just-validated behaviour.

---

## Registration sites — the parallel-list trap (update ALL in one pass)

- `GalDevice.ForPartNumber` or the sibling's resolver — `"GAL22V10"`.
- `ChipFactory.TryCreateGal` — dispatch; GAL arms of `IsSimulated` and the
  GAL-part predicate.
- `ChipPartDefinition.cs` — `IcGal22V10` (24-pin, pinout above).
- `LibraryPanel.cs` — PLD tree entry **and** `SimulatedChipIdentifiers`.
- `SchematicDtoMapper.cs` — `ChipLookup` entry.
- `Device.cs` — GAL/PLD identifier set.
- `MainForm.OnImportJedec` — accept **5828 and 5892**; extend "looksLike";
  `SingleSelectedGal()` / GAL predicate recognise the '22V10.
- `GalPinModel.cs` — '22V10 macrocell naming + registered/active-low
  indication.

---

## Work plan and owners

1. **[assistant, next]** Pull the GALasm 22V10 device tables and derive the
   full geometry (row offsets, OE rows, AR/SP, 44-column routing, S0/S1
   addresses and decode).
2. **[assistant]** Author the two WinCUPL reference sources:
   - *Combinational:* outputs spanning several OLMC sizes, mixed
     active-high/active-low, at least one `.oe` term.
   - *Registered:* 3-bit counter with an AR term, an SP term, `/Q` feedback in
     the equations, plus one combinational decode of the register state.
   **[user]** Compile both in WinCUPL, return the `.jed` files (keep `.pld`
   sources in the repo), plus expected truth/state tables if they deviate from
   the sources' obvious intent.
3. **[assistant]** Prove the geometry fuse-by-fuse against those `.jed` files
   in Node (no Python), including S0/S1 decode; document any WinCUPL
   term-ordering divergence (expected, same class as 16V8/20V8).
4. **[user]** Fresh uploads at coding time: `GALDevice.cs`, `GAL.cs`,
   `ChipFactory.cs`, `ChipPartDefinition.cs`, `LibraryPanel.cs`,
   `SchematicDtoMapper.cs`, `Device.cs`, `MainForm.cs`, `GalPinModel.cs`,
   `GalTests.cs`, plus one register chip ('374 or '173) as the clocked
   pattern reference.
5. **[assistant]** Implement: `Gal22V10Device`, `Gal22V10` evaluator, tests
   patterned on `GalTests`, all registration sites — finished files delivered
   for download.
6. **[both]** Livetest: assistant generates `gal22v10_*_livetest.ttlproj`
   projects (counter-driven stimulus, LEDs, same conventions as the four
   existing livetests); user imports the `.jed`s, runs, returns the simulator
   logs; assistant validates cycle-exactly (combinational truth-table sweep;
   registered count/AR/SP/feedback/tri-state; startup edge accounted for).

Nothing is blocked on the user until step 2's WinCUPL runs.

---

## Validation (the gold standard)

Same methodology that closed out the 16V8/20V8 campaign: fuse-level `.jed`
comparison first, C# behaviour against exhaustive Node checks second, live
TTLSim logs third. Checker scripts are rebuilt per session from the `.jed`/log
uploads (they are throwaway container artifacts); the methodology, not the
scripts, is the asset. Flag any timing/state assumption not directly verified
by a `.jed` or a log.

---

## Out of scope / separate tasks

- **BlinkyJED 22V10 emission.** Authoring '22V10 designs in the in-house
  toolchain (variable terms, S0/S1, AR/SP) is a distinct, larger job. This task
  uses WinCUPL/GALasm `.jed` as the input.
- **Loose ends (do not bundle):**
  - `GAL.cs`: `public const long PropagationDelayPs = 10_000;` literal should
    repoint to `PartDelayDefaults.GalDefaultDelayNs * 1000L` (one line, needs
    current `GAL.cs`).
  - `gal20v8_complex.pld`: comment says pins 18/19 are output-only in complex
    mode; correct is 15/22.
  - `ChipPartDefinition.cs`: stale "No simulation model yet" comment on
    `IcGal16V8`.

---

## Working conventions (for whoever picks this up)

- **Read the actual source in-turn before asserting any type/signature or
  writing code.** Do not rely on the project-knowledge index for
  recently-churned GAL files — request the current upload. Instrument rather
  than theorise when debugging.
- **Do not hand-guess fuse geometry.** Pull the 22V10 tables from GALasm and
  prove them against a real `.jed`.
- **Deliver finished files via download link**, never diffs or find/replace
  snippets, never container/Linux paths or line numbers (Chrome-browser
  workflow). For large churned files, prefer targeted `str_replace` on exact
  anchor text over regenerating the whole file from a possibly-stale index.
- Box-style chips use `partKind "chip"`; the `"ic"` catalogue is empty. No
  leading underscores in identifiers. Node.js only for scripts. C# for all
  deliverables. Keep it terse.
