# Thumby ALU Module — Design Summary

**DESIGN IN PROGRESS.** Decisions settled to date are recorded below; unresolved
choices are collected under "Open decisions" at the end. Targets the Blinky /
Thumby family as a **shared datapath board** — the slices, carry chain, operand
polarity, and flag block are identical on every machine that uses it; only the
control vector driving it differs.

## Design intent

A width-configurable 74181-based arithmetic/logic unit with a complete NZCV flag
block, on one board, presenting **true (active-high) data and true-sense control
at its headers**. Everything about the '181's awkward pin conventions — active-low
operands, active-low results, active-low carry-in against an active-high
carry-out — is absorbed on-board and never crosses the connector.

That absorption is the whole point of the board existing as a module. A host
machine attaches a function-select code, a carry-source select, and a flag write
mask, and gets a result bus and four flags back. It does not need to know that
there are '181s behind the header, nor which datasheet column applies.

**Genericity is a constraint, not an aspiration.** The board is specified so that
the same PCB serves:

- the ALU-and-flags stage of the video arc (a bench demo with switches on A and B,
  LEDs on F, and no CPU attached),
- Thumby's 16-bit datapath,
- a 4-bit or 8-bit testbed machine (Mini Blinky) by populating fewer slices,
- a 32-bit machine, by cascading two boards through the carry/group header.

Nothing on the board is instruction-aware. No opcode reaches it.

## What is deliberately not on this board

Named up front, because the partition discipline is what keeps the module
generic:

- **No instruction decode.** S3–S0, M, and every select and enable arrive as
  wires. The op → (S, M, Cn source, flag mask) expansion lives in the host's
  decode GAL, where it belongs, because that mapping is the one thing that is
  different on every machine.
- **No barrel shifter.** The shifter is a separate board with its own contract.
  Its last-bit-out arrives here as one input pin (`SH_C`) feeding the C-source mux.
- **No register file, no write-back mux, no address mux.** The board consumes two
  operand buses and produces one result bus.
- **No condition evaluator.** Flags go out on a header; the "taken" decision is
  the host's GAL.

## Module contract

Everything in this section is normative. A host that honours it does not need to
read the rest of the document.

### Data

| Signal    | Dir | Sense                | Notes                                                      |
| --------- | --- | -------------------- | ---------------------------------------------------------- |
| `A[15:0]` | in  | true binary          | operand A (the accumulator side on destructive machines)   |
| `B[15:0]` | in  | true binary          | operand B (post-shifter on machines with a series shifter) |
| `F[15:0]` | out | true binary, 3-state | result; enabled by `/FOE`                                  |

### Function control

| Signal         | Dir | Sense        | Notes                                                         |
| -------------- | --- | ------------ | ------------------------------------------------------------- |
| `S3..S0`       | in  | active-high  | '181 function select, **active-HIGH operand column**          |
| `M`            | in  | high = logic | high selects the 16 logic rows, low the 16 arithmetic rows    |
| `CIN_SEL[1:0]` | in  | —            | carry-in source: `00` = 0, `01` = 1, `10` = C flag, `11` = ¬C |
| `/FOE`         | in  | active-low   | result bus output enable                                      |

The host writes the **active-HIGH** function table. The board inverts operands and
result at its boundary so that column applies verbatim; a host that wired true data
straight onto the '181's pins would be reading the active-LOW column and getting the
De Morgan duals of the operations it asked for. This is the single most common way
to get a '181 design wrong, and the module exists partly to make it unavailable as a
mistake.

`CIN_SEL` is a four-way superset of what any one machine needs: constants for
CLC/SEC-style flag ops and for the +1 inject of a true subtract, `C` for
6502/ARM-style ADC, `¬C` for the borrow discipline. Thumby uses three of the four.

### Flag control

| Signal       | Dir | Sense           | Notes                                                    |
| ------------ | --- | --------------- | -------------------------------------------------------- |
| `NZ_WE`      | in  | active-high     | latch new N and Z at the next clock edge; otherwise hold |
| `V_WE`       | in  | active-high     | latch new V; otherwise hold                              |
| `C_SEL[2:0]` | in  | —               | C source: adder / shifter / 0 / 1 / hold / (3 spare)     |
| `V_SUB`      | in  | high = subtract | selects the subtract form of the overflow rule           |
| `SH_C`       | in  | true            | last bit out of the host's shifter                       |
| `SAVE`       | in  | active-high     | copy NZCV into the shadow latch at the next edge         |
| `RESTORE`    | in  | active-high     | load NZCV from the shadow instead of from the new values |
| `CLK`        | in  | rising          | single edge, from the clock module's four-pin contract   |
| `/RST`       | in  | active-low      | asynchronous clear of the flag and shadow latches        |

`C_SEL` carries "hold" as an explicit source rather than a separate enable, because
C is the flag with the most distinct sources and the mux is there anyway. N and Z
share one enable — no machine has yet wanted to write one without the other — while
V gets its own, because every logic operation writes N and Z and must not write V.

### Flags and cascade out

| Signal            | Dir | Notes                                                                                                                                                |
| ----------------- | --- | ---------------------------------------------------------------------------------------------------------------------------------------------------- |
| `N Z C V`         | out | the latched flags                                                                                                                                    |
| `N_R Z_R C_R V_R` | out | the same four **unlatched**, straight off the combinational logic, for front-panel and for hosts that want to evaluate a condition in the same cycle |
| `COUT`            | out | carry out of the most significant populated slice, true sense                                                                                        |
| `/GG /GP`         | out | group generate / propagate from the lookahead generator, for a second level                                                                          |
| `/AEQB`           | out | open-collector, wire-AND across boards for wider zero detect                                                                                         |
| `CIN_CASCADE`     | in  | link-selectable alternative carry-in for the low slice when cascading                                                                                |

### Power and mechanical

Four-pin clock contract (GND, +5, CLK, /RST) unchanged from the clock module.
Single-row headers, bus headers on the long edge, control on the short edge —
same backplane format as the register module. Front panel taps: `F[15:0]`, the
four flags, and `COUT`.

## Width configuration

Populate 1, 2, 3, or 4 '181 sockets for a 4, 8, 12, or 16-bit ALU. The
consequences are handled by link blocks, not by cutting traces:

- **`LK_MSB`** selects the sign-bit tap — F3 / F7 / F11 / F15 — feeding N and the
  V logic. The A and B sign taps for the V rule follow the same link position.
- **`LK_COUT`** selects which slice's carry-out becomes `COUT` and the adder input
  to the C-source mux.
- Unpopulated slices leave their `/P`, `/G` inputs to the lookahead generator tied
  HIGH (not asserted) through the pull-up network, exactly as the real part
  requires for unused inputs.
- The `/F` nets of unpopulated slice positions are pulled HIGH by the same network,
  so those lanes read **true zero** after the output inverters. Lanes above the
  configured width are therefore quiet on the front panel rather than floating.
- Zero detect is unaffected: an unpopulated slice drives nothing onto the
  wire-ANDed `A=B` line, so it cannot falsify the result.

The board is a 16-bit board that can be built narrow. It is not four independent
nibble cards, and the header pinout does not change with width — a narrow build
simply leaves the upper data pins reading zero.

## Operand polarity — the boundary inverters

The '181's data pins are active-low: an input pin driven LOW means a logical 1 to
the chip, and a result bit of 1 appears as a LOW on the corresponding `/F` pin.
The module therefore inverts on all three ports.

**6× 74HC240** (inverting octal 3-state buffer):

- 2× on A, 2× on B — permanently enabled; they buffer as well as invert, which the
  operand buses want anyway.
- 2× on F — `/OE` driven by `/FOE`, which gives the result bus its 3-state driver
  for free. No separate result buffer is needed anywhere in the host.

**There is no bypass option and no "already inverted" strap.** A population option
that changes the board's external sense would defeat the reason the board exists.
Six packages is what the contract costs; it is paid once, here, instead of being
re-derived on every machine.

Two consequences fall out of the inverting boundary, both of them good:

1. **Carry polarity normalises.** The '181's `Cn` injects +1 when LOW while `Cn+4`
   asserts HIGH. One inverter between the carry-source mux and the low slice's `Cn`
   makes the module's `CIN_SEL` and its `COUT` both true-sense, so a host never sees
   the opposition.
2. **Zero detect becomes free** — see below.

## Zero detect — the A=B wire-AND

Each '181 releases its open-collector `A=B` pin when its four-bit result is zero
**in the active-high interpretation**, and pulls it low otherwise. Because the
board inverts at the boundary, the chip's active-high result *is* the module's
true result. Wire-ANDing all four `A=B` pins to a single pull-up therefore yields a
16-bit zero detect for the cost of one resistor.

This replaces the '688 comparator pair and the discrete NOR tree outright, and it
scales: bringing `/AEQB` out to the cascade header lets two boards share one
pull-up for a 32-bit Z.

The caveat is timing, stated honestly: `A=B` is the '181's slowest documented
output path, and an open-collector rise through a pull-up is slower still. Z is
expected to be the module's critical path — not F. See "Timing" and **open
decision 2**.

Pull-up value comes from the 8× resistor network shared with the pull-ups on the
unused `/P`, `/G`, and unpopulated-`/F` nets. Discrete resistors are not used
anywhere on this board.

## Carry architecture

The four slices cascade through a lookahead generator. Three build options share
one set of footprints, selected by `LK_CARRY`:

| Option      | Parts              | Notes                                                                                                                                                                                             |
| ----------- | ------------------ | ------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------- |
| **74182**   | 1× '182, 16-pin    | The original part. Out of production; stock-dependent.                                                                                                                                            |
| **GAL16V8** | 1× GAL16V8, 20-pin | Drop-in equivalent from the project's existing `GAL_182.pld`, through the usual BlinkyJED → WinCUPL diff → Arduino exhaustive test pipeline.                                                      |
| **Ripple**  | 1× '04             | Three inverter sections between slices, covering the Cn/Cn+4 polarity opposition. Functionally identical, slower. This is also what the simulator runs, since there is no '182 behavioural model. |

Both DIP footprints are laid out; exactly one is populated. Being able to build a
working board with zero '182s in stock is a procurement decision as much as an
engineering one.

Wiring, in all three options:

- Slice `X` (/P) and `Y` (/G) are active-low and go directly to the generator's
  `/P`, `/G` inputs. No inverters.
- The generator's `Cn+x` / `Cn+y` / `Cn+z` assert LOW and go directly to slices
  1–3's `Cn` pins. No inverters.
- The carry-source mux output drives slice 0's `Cn` and the generator's `Cn` in
  parallel, through the single polarity inverter noted above.
- Group `/G` and `/P` come out on the cascade header. The '182's quirk that group-G
  excludes P0 is faithful in the GAL version and matters only at the second level.

## Flag block

The D-input path for all four flags is three stages: **new value → hold mux →
restore mux → register**. Orthogonal, and every host gets the same semantics.

```
   N_R ──┐                    ┌──────────────┐
   Z_R ──┤ hold mux '157 ─────┤              │
         │  (sel = /NZ_WE)    │  restore mux │      ┌──────────┐
   V_R ──┤ hold mux '157 ─────┤  '157        ├─────►│  '175    ├──► N Z C V
         │  (sel = /V_WE)     │ (sel=RESTORE)│      │  flags   │
   C_R ──┘ C-source '151 ─────┤              │      └────┬─────┘
             (sel = C_SEL)    └──────▲───────┘           │
                                     │                   │
                              ┌──────┴───────┐           │
                              │ '175 shadow  │◄──────────┘  (SAVE)
                              └──────────────┘
```

- **N** — the sign bit, via `LK_MSB`.

- **Z** — the `A=B` wire-AND.

- **C** — one '151 8:1: adder `COUT`, shifter `SH_C`, constant 0, constant 1, hold
  (the register's own Q fed back), three spare inputs brought to a test header.

- **V** — computed on-board from the three sign bits and `V_SUB`:
  
  ```
  Bx = B_msb XOR V_SUB          ; one '86 gate — folds subtract into the add rule
  V  = (A_msb XNOR Bx) AND (A_msb XOR F_msb)
  ```
  
  Three '86 gates plus the fourth used as an inverter, and one '08 gate. The
  operand sampled for the V rule is whatever arrives on the B header — on a machine
  with a series shifter that is the **post-shift** value, which is the correct one.

- **Shadow** — a second '175 plus the restore mux, for single-level interrupt entry
  and return. Optional by population: a machine without interrupts leaves both
  sockets empty and straps `RESTORE` low. The sockets and the two control pins are
  present on every board regardless, because a header pinout that varies by
  configuration is not a generic header pinout.

- **Spare hold-mux lanes** — two of the eight '157 bits are unused by NZCV. They are
  brought out to an auxiliary header as a general-purpose latched user flag with its
  own enable, for hosts that want an H flag, an IE bit, or a mode bit with the same
  hold semantics.

`V_SUB` may alternatively be derived on-board from the function code — the subtract
row is `S=0110` with `M` low — selected by `LK_VSUB`. Local derivation costs two
gates and one fewer control wire; host derivation is the default because the host's
decode GAL already knows.

## Chip budget

| Block                             | Parts                          | Qty    |
| --------------------------------- | ------------------------------ | ------ |
| Slices                            | '181                           | 4      |
| Lookahead                         | '182 **or** GAL16V8 **or** '04 | 1      |
| Operand inverters/buffers         | '240                           | 4      |
| Result inverters / 3-state driver | '240                           | 2      |
| Carry-in source mux               | '153 (half spare)              | 1      |
| Carry polarity + spares           | '04                            | 1      |
| C-source mux                      | '151                           | 1      |
| Hold muxes                        | '157                           | 2      |
| Restore mux                       | '157                           | 1      |
| Flags + shadow                    | '175                           | 2      |
| V logic                           | '86, '08                       | 2      |
| Pull-ups, LED resistors           | 8× networks                    | 4      |
| **Total ICs**                     |                                | **21** |

Plus 16 result LEDs, 4 flag LEDs, and a carry LED.

The six '240s are a fifth of the board. That is the price of the polarity contract,
and it is the right trade: it converts a per-machine mistake into a per-module fact.

## Timing

Figures from the catalogue models (a single representative delay per part, as
elsewhere in the project); datasheet spreads are wider and the bench build is the
arbiter.

| Path             | Stages                                                      | Estimate                    |
| ---------------- | ----------------------------------------------------------- | --------------------------- |
| **F, lookahead** | '240 in → '181 → '182 → '181 → '240 out                     | ≈ 95 ns                     |
| **F, ripple**    | as above, three carry hops through '181 + '04               | ≈ 180 ns                    |
| **Z**            | '240 in → '181 (`A=B`, the slow path) → open-collector rise | ≥ 110 ns, pull-up dependent |
| **C via adder**  | '240 in → '181 → '182 → `Cn+4` → '151                       | ≈ 90 ns                     |

**The ALU is not the machine's critical path.** On Thumby the barrel shifter
dominates, and the ~770 ns cycle at the datasheet-guaranteed 1.3 MHz leaves ample
margin even for the ripple build. Z is the slowest thing on this board, and the
open-collector rise is the part most likely to need a smaller pull-up than intuition
suggests — measure it before trusting it.

## Verification plan

1. **Slice and cascade in TTLSim first.** Sweep ADD (`S=1001`) and SUB (`S=0110`)
   with both carry-in states across all sixteen F bits and the final carry. The SUB
   rows are the ones to watch — P and G there encode comparisons, not sums, and an
   inverted reading of that condition breaks '182-carried subtraction while leaving
   addition looking perfect.
2. **Capture the full 32-row table from the board itself.** The module's published
   function table is not transcribed from a datasheet; it is the sweep output,
   captured once, checked against the simulator's `Hc181` model row for row, and
   shipped as the module's datasheet. Two operand values are not enough — sweep
   walking-ones, all-ones, alternating patterns, and the boundary cases at 0x0000
   and 0xFFFF for every S/M/Cn combination.
3. **Polarity proof.** The single test that catches a wrong-column build: assert
   `S=1001, M=low, CIN_SEL=00` with A = 0x0001, B = 0x0001 and confirm F = 0x0002 at
   the header. If the boundary inverters are wrong or absent, this reads as
   something else entirely.
4. **Zero detect.** Every operation that can produce zero, at every configured
   width, plus a scope check on the `A=B` rise time with the chosen pull-up.
5. **Flag block.** All eight C-source selections including hold; the V truth table
   in both add and subtract forms at the sign-bit boundaries; NZ_WE and V_WE holding
   independently; save/restore round-tripping all sixteen NZCV states.
6. **Narrow builds.** Repeat 1–5 with one slice populated and with two, confirming
   the `LK_MSB` and `LK_COUT` links move N, V, and COUT correctly and that the
   unpopulated lanes read zero.
7. **GAL lookahead** through the standard pipeline — BlinkyJED equations → fuse map
   → WinCUPL diff → Arduino exhaustive test → burn — before it goes near the board.

## Open decisions

1. **Lookahead part** — '182, GAL16V8, or ripple as the default populated option on
   the first build. Settled by '182 stock and by whether the ripple delay ever
   actually costs a cycle. All three footprints are laid out regardless.
2. **Z detect** — the `A=B` wire-AND is the decision, contingent on the measured
   open-collector rise time making the flag-latch setup window. If it does not, the
   fallback is a '688 pair or a NOR tree, both of which need board area reserved
   now. Reserve it.
3. **`V_SUB` source** — host wire (default) or local derivation from `S`/`M`.
   Settled by whether any host wants an overflow rule the S code does not imply.
4. **Auxiliary user flag** — whether the two spare hold-mux lanes get a header and
   a front-panel LED, or are left as test points.
5. **Cascade header pinout** — ratified only after two boards are wired together in
   TTLSim for a 32-bit build; the second-level lookahead is off-board and its shape
   determines what this header must carry.
6. **LED placement** — coordinated with the register file and PC+IR boards so the
   front panel reads as one instrument. Pending the panel layout.
7. **Header pinout ratification** — pending the TTLSim export, as with every other
   board in the family.

## Ratified in this document

- The module presents **true data and true-sense control at every header**; all
  '181 pin-convention inversion is on-board, with no bypass option.
- The published function select is the **active-HIGH operand column**. Hosts write
  those codes; the board makes them true.
- Boundary inversion is **6× '240**, with the result pair doubling as the module's
  3-state result driver via `/FOE`.
- **Carry-in and carry-out are both true-sense** at the header; the Cn/Cn+4 polarity
  opposition never leaves the board.
- **`CIN_SEL` is four-way** — 0, 1, C, ¬C — a superset of any single machine's needs.
- **Zero detect is the wire-ANDed `A=B`**, brought out open-collector for cascading.
- **The flag block lives here**: NZCV register, shadow, restore mux, C-source mux,
  and V logic. The enables and selects driving it come from off-board decode.
- **N and Z share an enable; V has its own.** C's hold is a mux input, not an enable.
- **Width is 4/8/12/16 by socket population**, with `LK_MSB` and `LK_COUT` link
  blocks carrying the consequences. The header pinout does not change with width.
- **No decode, no shifter, no register file, no condition evaluator** on this board.
- Three lookahead implementations share one footprint pair; the ripple option is
  first-class, not a fallback, because it is what the simulator runs.
