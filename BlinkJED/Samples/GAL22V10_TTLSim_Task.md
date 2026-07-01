# TTLSim — GAL22V10 / ATF22V10 Simulation: Task Brief

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

---

## Background: how TTLSim's GAL engine works today

Read these before writing anything (they are the current, in-repo GAL subsystem):

- **`TTLSim.Chips/PLD/GALDevice.cs`** — `GalDevice` record holding the fuse-map
  *geometry*: `FuseCount, Rows, Cols, OlmcCount, OlmcOutputPins, XorFuseBase,
  SynFuse, Ac0Fuse, ColumnMapMode1/2/3`. Product terms are **uniform**:
  `ProductTermsPerOlmc = Rows / OlmcCount` (= 8 for both existing parts). Statics
  `Gal16V8`, `Gal20V8`; `ForPartNumber(string)` maps `"GAL16V8"`/`"GAL20V8"`.
- **`TTLSim.Chips/PLD/GAL.cs`** — `Gal : IChip`, the evaluator. Reads the SYN/AC0
  fuses to pick a column map (galasm mode 1/2/3), then per OLMC computes a
  sum-of-products with an XOR polarity fuse and drives that output pin. One
  `Driver` per OLMC output pin; an all-intact (erased) OLMC block is treated as a
  plain input and does not drive. `PropagationDelayPs = 10_000` (nominal 10 ns);
  the factory overrides it from the part's "Propagation Delay (ns)" when set.
  **Combinational only** — registered OLMCs, OE product terms, and clocked state
  are explicitly *not* modelled.
- **`TTLSim.Chips/ChipFactory.cs`** — `TryCreateGal(device, pinToNet)`: resolves
  `GalDevice.ForPartNumber`, parses the JEDEC in `device.Program`, applies the
  per-part delay, builds a `Gal`. Also the GAL arms of `IsSimulated` / the GAL
  predicate.
- **`TTLSim.UI/Components/ChipPartDefinition.cs`** — `IcGal16V8` (20-pin),
  `IcGal20V8` (24-pin) pin definitions; macrocell pins declared `Out`. NOTE: the
  "No simulation model yet" comment on `IcGal16V8` is **stale** — a model exists
  via the factory.
- **`TTLSim.UI/MainForm.cs`** — `OnImportJedec`: file dialog → `JedecFuseMap.Parse`
  (validate) → **fuse-count gate** that only accepts 2194 (16V8) / 2706 (20V8),
  with a "looksLike" switch → sets `device.Program`. Also `SingleSelectedGal()`,
  the GAL-part predicate, `UpdateGalMenuItems()`.
- **`TTLSim.Core/JedecFuseMap.cs`** (and the `JedecData` it returns) — generic
  JEDEC parser (`Parse(text) -> { FuseCount, Fuses[] }`). Should handle 5828
  fuses unchanged; confirm by reading it.
- **`GalPinModel.cs`** — derives pin names/roles from the fuse map for dynamic GAL
  symbol rendering. Extend so a '22V10 macrocell shows the right name and
  registered/active-low indication.
- **`TTLSim.Tests/GalTests.cs`** — combinational-evaluator tests against a small
  synthetic `GalDevice`. Follow this pattern for '22V10 tests.
- **`BlinkJED/Compiler.cs`, `BlinkJED/TargetDevice.cs`** — the *compiler* project
  (separate from the sim): 16V8/20V8, mode-1 combinational only. Not touched by
  this task except as reference.

---

## Why the 22V10 needs engine work (it is NOT a drop-in like a new SRAM was)

Three assumptions in the current engine break:

1. **Variable product terms + OE term.** The 22V10's OLMCs do not all get the same
   term count. Pins 14/23 → 8 terms, 15/22 → 10, 16/21 → 12, 17/20 → 14, 18/19 →
   16, and **each OLMC has one extra product term for output-enable (tri-state)
   control**. The uniform `Rows / OlmcCount` model cannot represent this; the
   geometry needs explicit per-OLMC row offsets plus an OE row.
2. **No SYN/AC0 global modes.** Instead the mode + polarity of **each** OLMC is set
   by a per-OLMC pair of bits **S0/S1**. Input routing is fixed (no simple/complex/
   registered column-map variants): 44 columns = 22 array inputs × 2 (true/
   complement), where the 22 inputs are 12 dedicated + 10 feedbacks.
3. **Registered logic + global AR/SP.** The current `Gal` is combinational-only.
   The 22V10's headline feature is registered OLMCs: the output pin is driven by
   the **Q of that OLMC's D flip-flop**, `/Q` feeds back into the array, there is a
   shared **clock on pin 1**, plus two global product terms — **AR (asynchronous
   reset)** and **SP (synchronous preset)** — feeding all registers.

---

## GAL22V10 architecture (verified from the Lattice/datasheet)

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

12 dedicated inputs (pins 1–11, 13; pin 1 doubles as the registered clock) and 10
I/O macrocells (pins 14–23). Macrocell pins declared `Out` in the part definition
(same convention as the '16V8/'20V8), so the floating-input diagnostic leaves them
alone and a fuse map that leaves one as a pure input simply doesn't drive.

**Fuse-map totals (confirmed):**
- Array = **44 columns × 132 rows** = 5808 array fuses.
- Config bits = 20 S0/S1 fuses (a pair per OLMC), at fuse addresses 5808–5827
  (the last OLMC's are S0 = 5826, S1 = 5827).
- **Total = 5828 fuses** (a Lattice part with the electronic signature reports
  5892; a standard 5828-fuse map still programs it — accept both counts on import).
- Rows: 120 logic terms (8+10+12+14+16+16+14+12+10+8) + 10 OE terms + AR + SP = 132.

**S0/S1 decode (per OLMC):** selects {combinational | registered} × {active-high |
active-low}. This replaces the '16V8/'20V8 SYN/AC0 + XOR-polarity scheme.

**Registered semantics:** clock = pin 1 (rising edge); AR clears all registers
asynchronously when its term is true; SP presets all registers on the clock edge
when its term is true; `/Q` of each registered OLMC feeds back into the array
(true + complement columns).

### Exact geometry to extract before coding (do NOT hand-guess these)

The precise **per-OLMC row offsets**, the **AR/SP row positions**, the **OE-row
position within each OLMC block**, and the **44-column line→pin routing** must come
from an authoritative source, not memory. Use the **GALasm 22V10 device tables**
(the same `PinToFuse*/ToOLMC*` / `galasm.h` source the existing `GalDevice`
constants were taken from) and then **validate against a real `.jed`** (below).
Canonical layout to confirm: row 0 = AR, then ten OLMC blocks each of
`[1 OE term + its logic terms]` ordered pins 23→14, row 131 = SP; fuse `n` lives at
row `n / 44`, column `n % 44`.

---

## Design plan

Do it in two tiers. Decide up front which is needed first — if the '22V10 is wanted
for a **state machine/counter**, plan for Tier 2 from the start; if for
**combinational decode** that just needs more inputs/terms than a '16V8, Tier 1
stands alone.

### Tier 1 — combinational-only '22V10
- New geometry describing variable per-OLMC term counts + row offsets + OE rows +
  S0/S1 addresses + the fixed 44-column routing. Either generalise `GalDevice`
  (add an optional per-OLMC term-count/offset array; keep the uniform path for the
  '16V8/'20V8) or add a sibling `Gal22V10Device`. A sibling is cleaner given how
  little the 22V10 shares with the SYN/AC0 model — reuse only the low-level
  SOP/column primitives.
- Evaluate each OLMC's SOP over its own rows; apply S1 polarity. Honour the OE
  product term (drive only when the OE term is satisfied) or, to match today's
  engine exactly, treat a configured combinational output as always-enabled.
  Registered cells: ignore in Tier 1.

### Tier 2 — registered '22V10
- Add a D-register per OLMC whose S0/S1 select registered mode; sample the D input
  (the OLMC's SOP) on the pin-1 rising edge; drive Q (with S1 polarity) to the pin;
  feed `/Q` back into the array.
- Model the global **AR** term (async clear of all registers, level-sensitive) and
  **SP** term (sync preset on the clock edge).
- Borrow the clocked-state/edge machinery from the existing register chips
  ('374 / '173 / '191). Consider whether it is worth generalising so the '16V8/
  '20V8 registered modes come along "for free" (currently a documented gap).

---

## Registration sites — the parallel-list trap (update ALL of these)

Adding a simulated part silently fails unless every site is updated in the same
pass. For `GAL22V10`:

- `GalDevice.ForPartNumber` (or the new sibling's resolver) — return the 22V10
  geometry for `"GAL22V10"`.
- `ChipFactory.TryCreateGal` — dispatch 22V10; plus the GAL arm of `IsSimulated`
  and the GAL-part predicate.
- `ChipPartDefinition.cs` — add `IcGal22V10` (24-pin, pinout above).
- `LibraryPanel.cs` — PLD tree entry **and** `SimulatedChipIdentifiers`.
- `SchematicDtoMapper.cs` — `ChipLookup` entry for `IcGal22V10`.
- `Device.cs` — any GAL/PLD identifier set (mirror how `GAL16V8`/`GAL20V8` are
  listed).
- `MainForm.OnImportJedec` — extend the fuse-count gate to accept **5828** (and
  5892 for the Lattice-signature variant); add it to the "looksLike" switch; make
  sure `SingleSelectedGal()` / the GAL predicate recognise the '22V10.
- `GalPinModel.cs` — 22V10 macrocell naming + registered / active-low indication.

---

## Validation (the gold standard)

Validate the fuse geometry the same way the '16V8/'20V8 were validated — against a
**known-good WinCUPL (or GALasm) `.jed`**, not against the datasheet alone. Needed
from the user:

1. A **combinational** '22V10 design: a couple of outputs = known AND/OR of inputs.
2. A small **registered** '22V10 design: a 2–3-bit counter or a few D-FFs with a
   known next-state / reset / preset behaviour (exercises clock, AR, SP, feedback).

For each: the source `.pld` and the produced `.jed`, plus the expected truth/state
table. Then write a Node.js exhaustive check (sweep the input space, compare the
evaluator's outputs to the table) — no Python. Flag any timing/state assumptions
that aren't directly verified by the `.jed`.

---

## Out of scope / separate tasks

- **BlinkyJED 22V10 emission.** BlinkyJED (`Compiler.cs` / `TargetDevice.cs`) is
  16V8/20V8, mode-1 combinational only. Authoring '22V10 designs in the in-house
  toolchain (variable terms, S0/S1, AR/SP, registered `.D`/`.OE` suffixes) is a
  distinct, larger job. This task uses WinCUPL/GALasm `.jed` as the input.
- **Unrelated loose end (do not bundle):** `TTLSim.Chips/PLD/GAL.cs` still has
  `public const long PropagationDelayPs = 10_000;` as a literal; it should be
  repointed to `PartDelayDefaults.GalDefaultDelayNs * 1000L` (add
  `using TTLSim.Chips;`). One-line change, needs `GAL.cs` uploaded.

---

## Working conventions (for whoever picks this up)

- **Read the actual source in-turn before asserting any type/signature or writing
  code.** Do not rely on the project-knowledge index for recently-churned GAL
  files — request the current upload. Instrument rather than theorise when
  debugging.
- **Do not hand-guess fuse geometry.** Pull the 22V10 tables from GALasm and prove
  them against a real `.jed`.
- **Deliver finished files via download link**, never diffs or find/replace
  snippets, never container/Linux paths or line numbers (Chrome-browser workflow).
  For large churned files, prefer targeted `str_replace` on exact anchor text over
  regenerating the whole file from a possibly-stale index.
- Box-style chips use `partKind "chip"`; the `"ic"` catalogue is empty. No leading
  underscores in identifiers. Node.js only for scripts. Keep it terse.
