# Generic Address Engine (PC + MAR + IR) for TTL CPUs

A reusable address-generation block for 8-bit TTL CPU projects: 16-bit
Program Counter, 16-bit counting Memory Address Register with a PC/DB input
mux, and Instruction Register, on one board. The board owns **everything
that generates or holds an address**; the data-path board never touches
A[15:0]. The inter-board contract is DB[7:0] plus the control header.

All registered elements clock on the rising edge of Φ2; no gated clocks
anywhere. Assumes an 8-bit data bus and 8-bit instructions; the 16-bit
DB/IRH variant discussed separately is orthogonal and not captured here.

MAR lives on this board rather than with the data path because the PC→MAR
fetch transfer is the widest private bus in the machine — 16 bits, every
instruction — and belongs on traces, not a connector. PC itself is exported
only as a debug tap; the outside world sees A[15:0], DB, and controls.

---

## 1. Design goals

- **One clock edge, no gated clocks.** Everything registers on the rising edge of CLK; all control signals are enables, valid for the whole cycle.
- **Byte-wise access to 16-bit registers over an 8-bit bus.** Split load enables on PC *and* MAR, split output enables on PC, so JMP/JSR/RTS/zero-page/stack addressing all work on a single 8-bit data bus with no extra latches.
- **Single-edge fetch address transfer.** MAR ← PC is a full 16-bit internal path through the input mux; F0 costs one cycle, always.
- **Counting MAR.** MAR is built from the same '161 core as PC, so MAR_INC is free — sequential reads (operand streams, pointer hi bytes, future RMW/DMA) take one control line instead of an ALU pass and a reload.
- **Reset works with a stoppable clock.** Both counters clear asynchronously; PC lands at $0000 the instant reset asserts, no clock edge required. Mandatory with the blinky clock module, whose machine CLK is mode- and halt-gated and whose reset release is synchronous to its own /OSC.
- **Portable across projects.** Configurable by socket population and one shunt field: bring-up machine (PC-direct addressing, no MAR), no-CALL host, full host.

## 2. Signal interface (the contract)

| Signal | Dir | Active | Function |
|---|---|---|---|
| CLK | in | rising | System Φ2. Common to PC, MAR, IR. |
| RESET# | in | low | **Asynchronous** clear of PC (and MAR) to $0000. Wire to the clock module's /RST. |
| PC_INC | in | high | PC ← PC + 1 on next edge. |
| PC_LD_L# | in | low | PC[7:0] ← DB on next edge. |
| PC_LD_H# | in | low | PC[15:8] ← DB on next edge. |
| PC_DB_L# | in | low | Drive PC[7:0] onto DB (JSR return-address push). |
| PC_DB_H# | in | low | Drive PC[15:8] onto DB. |
| MAR_SRC# | in | low | MAR input mux select: **low = PC**, high (default, pulled up) = DB. |
| MAR_LD_L# | in | low | MAR[7:0] ← mux on next edge. |
| MAR_LD_H# | in | low | MAR[15:8] ← mux on next edge. |
| MAR_INC | in | high | MAR ← MAR + 1 on next edge. |
| IR_LD# | in | low | IR ← DB on next edge. |
| DB[7:0] | bi | — | System data bus. |
| A[15:0] | out | — | Buffered MAR value — the machine's address bus (always driven). |
| IR[7:0] | out | — | Instruction register outputs, straight to microcode ROM address lines. |
| PC[15:0] | out | — | **Debug tap**, unbuffered from the '161 Q pins. For breakpoint comparators and front panels only; not a bus. |

All control inputs 10k pulled up — absent = inert, per the ecosystem
convention. (MAR_SRC# defaulting high means an unconnected pin selects DB,
which is harmless: nothing loads MAR unless a load enable is driven.)

Rules of use:

- Never assert an INC and a load *of the same register* in the same cycle — the un-loaded half would count while the other half loads. PC_INC concurrent with MAR loads (and vice versa) is fine and routine: they are different chips.
- MAR_LD_L# + MAR_LD_H# with MAR_SRC# low = the full 16-bit MAR ← PC fetch transfer, single edge.
- A memory **read can feed a MAR load on the same edge**: the ROM drives DB all cycle, the '157s pass it through, the '161s capture at the edge. This is how zero-page and indirect modes stay cheap (see §9).
- PC_DB_L#/PC_DB_H# are ordinary bus enables; microcode discipline keeps them exclusive with every other DB driver.
- Page constants ($00 for zero page, $01 for a 6502-style stack) are the **host's** business: a host-side '541 with the constant strapped on its inputs drives DB, and MAR captures it via MAR_LD_H#. Constants are driven, never tied.

## 3. PC core — 4 × 74HC161 (U1–U4)

Four synchronous 4-bit counters, cascaded low-nibble to high-nibble
(U1 = PC[3:0] … U4 = PC[15:12]).

**Why '161 and not '163:** identical pinout, identical synchronous load and
cascade — the only difference is the clear, which is **asynchronous** on the
'161. The blinky clock module's machine CLK only ticks in STEP MODE when
NEXT CYCLE is pressed, and reset release is synchronous to the board's
free-running /OSC, *not* to machine CLK. With a '163's synchronous clear,
pressing RESET while stepping (or halted at a breakpoint) asserts and
releases /RST without the machine ever seeing a clock edge — PC never
clears. The '161 clears the moment /RST asserts, regardless of clock state.
The clock module's own T counter is a '161 with /CLR = /RST for the same
reason. Do **not** substitute '163s.

| '161 pin | U1 (PC 3:0) | U2 (PC 7:4) | U3 (PC 11:8) | U4 (PC 15:12) |
|---|---|---|---|---|
| 1 MR# (async clear) | RESET# | RESET# | RESET# | RESET# |
| 2 CLK | CLK | CLK | CLK | CLK |
| 3–6 D0–D3 | DB0–DB3 | DB4–DB7 | DB0–DB3 | DB4–DB7 |
| 7 CEP (ENP) | PC_INC | PC_INC | PC_INC | PC_INC |
| 9 PE# (load) | PC_LD_L# | PC_LD_L# | PC_LD_H# | PC_LD_H# |
| 10 CET (ENT) | +5V | U1 pin 15 | U2 pin 15 | U3 pin 15 |
| 15 TC (RCO) | → U2 CET | → U3 CET | → U4 CET | n/c |
| 14,13,12,11 Q0–Q3 | PC0–3 | PC4–7 | PC8–11 | PC12–15 |

- **Loads are fully synchronous** — put the byte on DB, assert the load, wait for the edge. Only the clear is asynchronous, and it fires only during system reset.
- **Priority:** async clear wins by construction; on any edge, load beats count beats hold.
- **Cascade:** ENP is the master count enable; carry ripples through CET/TC. Worst case ~115 ns in HC at 5 V → good to ~8 MHz; never the machine's critical path.
- U1/U3 and U2/U4 D inputs share the same DB nibbles because loads are byte-at-a-time; only the half whose PE# is low captures.
- PC Q pins fan out to: the MAR input mux (§4), U7/U8 (§5), and the PC debug header (§6). All light loads.

## 4. MAR — input mux + counting core

### Mux stage — 4 × 74HC157 (U14–U17)

2:1, 16 bits wide, in front of the MAR D inputs.

| '157 pin | Connection |
|---|---|
| 1 S (select) | MAR_SRC# (low = A inputs = PC) |
| 15 E# (enable) | GND (always enabled) |
| A inputs | PC[15:0] |
| B inputs | DB[7:0], wired to **both** byte halves (DB0–7 → MAR D0–7 legs *and* MAR D8–15 legs) |
| Y outputs | MAR '161 D inputs |

The DB side uses the same one-bus/two-halves sharing trick as the PC's D
inputs: byte-wise loads mean only the half whose MAR_LD_x# is low captures,
so both halves can watch the same 8 wires.

### MAR core — 4 × 74HC161 (U10–U13)

Wired identically to the PC core (same table as §3) with substitutions:
D inputs ← U14–U17 Y outputs, PE# ← MAR_LD_L#/MAR_LD_H#, CEP ← MAR_INC,
MR# ← RESET# (contents are meaningless across reset; clearing is harmless
and saves a strap), Q → U5/U6 inputs (and nothing else). CET/TC cascade
identical.

**Why counting:** MAR_INC turns "address the next byte" into one control
line. Fetch reads the opcode and bumps MAR alongside PC, so operand fetches
need no MAR reload (§9); pointer-hi reads, RMW write-back, and a future
debug/DMA port walking memory all get the same discount. The 6502 burns ALU
passes on exactly this.

## 5. Bus drivers

**Address side — 2 × 74HC541 (U5, U6):** MAR[15:0] through always-enabled
buffers (both OE# pins grounded) to A[15:0]. In the bring-up configuration
the shunt field J1 (§7) feeds these from PC instead.

**Data-bus side — 2 × 74HC541 (U7 = PCL, U8 = PCH):** inputs from PC[7:0]
and PC[15:8], outputs onto DB, enables PC_DB_L#/PC_DB_H# (tie each chip's
two OE# pins together). **Strictly for JSR** — pushing the return address.
Not a debug read path: a machine halted mid-instruction still has the frozen
microstep's DB driver enabled, so a front panel asserting PC_DB_x# blind
risks contention. Debug reads PC from the dedicated tap (§6). No CALL in the
host ISA → omit U7/U8.

## 6. IR — 1 × 74HC377 (U9) — and the PC debug tap

| '377 pin | Connection |
|---|---|
| 1 E# | IR_LD# |
| 11 CLK | CLK |
| D1–D8 | DB0–DB7 |
| Q1–Q8 | IR0–IR7 → microcode ROM address lines |

The '377's clock-enable keeps the no-gated-clocks rule: it clocks every
cycle, captures only when IR_LD# is low. Outputs permanently enabled,
straight to the microcode ROM. Host needing IR read-back on DB: HC374 +
'541 swap, not paid for by default.

**IR at reset:** no clear on the '377, so IR is garbage at power-up. Safe
because the step counter — the blinky module's on-board '161 T counter — is
cleared by /RST, and step 0 of every opcode row is the identical fetch.
Garbage IR + T = 0 + fetch-at-step-0 = correct boot.

**PC debug tap (header, unbuffered from U1–U4 Q pins).** With a MAR in the
address path, **A[15:0] at T0 does not show the current instruction's
address** — MAR ← PC is being loaded on that very edge, so A still holds the
previous instruction's last address. A breakpoint comparator qualified with
FETCH must therefore compare against **PC, not A**. The tap exists for
exactly that comparator (and front-panel PC display). Comparator/display
inputs are a trivial load at these speeds; no buffer IC needed.

**NEXT INSTR alignment (blinky hosts):** NEXT INSTR freezes at T0 — MAR ← PC
set up, IR not yet loaded. PC (via the tap) shows the opcode address at the
halt, exactly what the comparator compared.

## 7. Configurations — shunt field J1

J1 is a 2×16 shunt field on the U5/U6 inputs selecting their source:
**MAR position** (normal) or **PC position** (bring-up: PC drives A[15:0]
directly, MAR/mux sockets empty). Populate one full side; the generator's
ERC must pass both netlists. If you never intend the bring-up build, omit J1
and hardwire U5/U6 to MAR.

| Configuration | Populate | ICs |
|---|---|---|
| **Bring-up** (NOP/HLT/JMP machine, PC-direct, one-cycle fetch) | U1–U6, U9; J1 → PC | 7 |
| **No-CALL host** (e.g. 74670 CPU + MAR) | all except U7/U8; J1 → MAR | 15 |
| **Full** (JSR/RTS-capable host, '181 Rev B class) | everything | 17 |

## 8. Reset and timing

RESET# (← the clock module's /RST) hits every '161 MR# asynchronously — PC
to $0000, MAR cleared alongside, no clock edge needed. Reset behaves
identically free-running, single-stepping, or frozen at a breakpoint. Boot
code lives at $0000. A high-vector host puts `JMP $E000` at $0000 (bytes
$0000–$0002; bottom-of-memory tables start at $0003).

| Path (HC @ 5 V, worst case) | Approx. |
|---|---|
| CLK → Q ('161) | 35 ns |
| INC critical path (ripple, either counter) | ~115 ns → ≥ 8 MHz |
| DB valid → '157 (B→Y) → '161 D setup | 25 + 20 = 45 ns before edge |
| PC → '157 (A→Y) → '161 D setup (fetch transfer) | 45 ns before edge |
| MAR_SRC# → Y settle ('157 S→Y) | 25 ns — assert early in the cycle, as microcode outputs naturally are |
| CLK → A valid ('161 Q + '541) | 65 ns, then memory tAA on top |
| MR# pulse width / recovery (async clear) | ~25 ns — irrelevant against a multi-cycle reset |
| CLK → IR outputs ('377) | 35 ns, then microcode ROM tAA |

The memory path (65 ns + tAA + data-path setup) dominates, as ever; nothing
on this board is the machine's critical path.

## 9. Microcode recipes

Signals not listed are inactive. **/TRST** is the clock module's T-counter
reload input: assert it in each instruction's **final** microstep or
FETCH/NEXT INSTR break. It's a sequencer output, not a board signal, shown
here because forgetting it is the classic integration bug.

**Fetch (every opcode, T0–T1):**

| Step | Controls | Effect |
|---|---|---|
| F0 (T0) | MAR_SRC# = 0, MAR_LD_L#, MAR_LD_H# | MAR ← PC, single edge. FETCH high. |
| F1 (T1) | MEM_RD, IR_LD#, PC_INC, **MAR_INC** | IR ← M[MAR]; PC and MAR both step to the first operand's address |

MAR_INC at F1 is the counting-MAR dividend: after fetch, MAR already
addresses operand byte 1 — no reload, no MAR_SRC change.

**JMP absolute (operands lo, hi) — 5 cycles total:**

| Step | Controls | Effect |
|---|---|---|
| T2 | MEM_RD, X_LD, PC_INC, MAR_INC | X ← lo; MAR → hi's address |
| T3 | MEM_RD, **PC_LD_H#** | PC[15:8] ← hi (PCL still old — harmless, no fetch this cycle) |
| T4 | X → DB, **PC_LD_L#**, /TRST | PC[7:0] ← lo. Done. |

**LDA zero page — 5 cycles, and the same-edge read-into-MAR trick:**

| Step | Controls | Effect |
|---|---|---|
| T2 | MEM_RD, **MAR_LD_L#** (MAR_SRC# = 1) | ZP address byte read from memory lands straight in MAR[7:0] — one cycle |
| T3 | host $00 constant → DB, **MAR_LD_H#** | MAR[15:8] ← $00 |
| T4 | MEM_RD, A_LD, PC_INC, /TRST | A ← M[$00zz]; PC steps past the operand |

(PC_INC bookkeeping can sit on any spare cycle — shown late here; T2 is
equally valid since PC and MAR are independent chips.)

**JSR absolute (host SP + $01 page constant) — push return, then load target:**

| Step | Controls | Effect |
|---|---|---|
| T2 | MEM_RD, X_LD, PC_INC, MAR_INC | X ← lo; MAR → hi's address |
| T3 | $01 → DB, MAR_LD_H# | MAR high ← stack page |
| T4 | SPL → DB, MAR_LD_L# | MAR ← $01,SP |
| T5 | **PC_DB_H#**, MEM_WR, SP− | push PCH (PC currently = addr of hi operand = return−1, per host convention) |
| T6 | SPL → DB, MAR_LD_L# | MAR ← $01,SP (decremented) |
| T7 | **PC_DB_L#**, MEM_WR, SP− | push PCL |
| T8 | MAR_SRC# = 0... no — target hi still in memory: MAR ← saved hi address needs reload; simplest is host-specific ordering | see note |

Exact JSR ordering bends to each host's temp budget and return-address
convention (6502 pushes PC-of-last-operand-byte; a cleaner ISA pushes
PC-after-operands and fetches hi *before* pushing). The board doesn't
care — it exposes the enables; the recipes above are shapes, not gospel.
**RTS** is the same two-edge load pattern as JMP: pop lo → X, pop hi → DB
with PC_LD_H#, then X → DB with PC_LD_L# and /TRST.

## 10. Bill of materials

| Ref | Part | Role | Optional? |
|---|---|---|---|
| U1–U4 | 74HC161 | PC, 16 bits (async clear — no '163s, §3) | no |
| U5–U6 | 74HC541 | MAR → A[15:0] drivers (PC → A in bring-up via J1) | no |
| U7–U8 | 74HC541 | PCL/PCH → DB, JSR push only | omit if no CALL |
| U9 | 74HC377 | IR | no |
| U10–U13 | 74HC161 | MAR, 16 bits, counting | omit in bring-up |
| U14–U17 | 74HC157 | MAR input mux, PC/DB | omit in bring-up |
| J1 | 2×16 shunt field | U5/U6 source select PC/MAR | omit if bring-up build never wanted |
| — | headers | control, DB, A[15:0], PC debug tap, power | no |
| — | 100 nF X7R per IC | decoupling | no |

**HC vs HCT:** all-HC bus → HC throughout. Where LS outputs share DB ('181
Rev B), use HCT for anything whose inputs see LS levels ('157s and the DB-fed
'161/'377 inputs), per the existing boundary policy.

## 11. Drop-in notes per project

- **Bring-up machine (NOP/HLT/JMP, GAL microcode):** 7-IC build, J1 → PC, one-cycle fetch (no F0). Graduating to the full build restores F0 and two-cycle fetch — more representative of the target machine anyway.
- **'181 Rev B:** replaces PC + IR *and* the MAR in one board; existing "MAR ← PC" microcode maps to F0 directly. Add /TRST to each opcode row's final step if absent.
- **74670 register-file CPU:** 15-IC no-CALL build.
- **Any host on the blinky clock module v3:** RESET# ← /RST, CLK ← CLK, /TRST per §9, breakpoint comparator on the **PC tap** qualified with FETCH (§6).
- **16-bit DB / 16-bit IR variant:** orthogonal extension (DBL/DBH rail split + second '377), not captured here; the MAR mux stage would grow the same jumpered rail split.

## 12. Capture notes (TTLSim)

- **'161 Q outputs on pins 14, 13, 12, 11 = Q0–Q3** — descending order; ascending-pin bus wiring reverses the nibble. Eight '161s on this board now; generate taps from a table.
- **'157 select is pin 1, enable pin 15** — verify A/B/Y groupings against the model source, same rule as any multi-section part.
- **'377 D/Q pins:** verify against the model source; package symmetry, not sequential numbering ('273 incidents).
- **'541 dual OE# pins** — both tied on every instance (grounded U5/U6, ganged enables U7/U8); validator fails the build on a floater.
- **J1 both-netlist ERC:** PC-position and MAR-position netlists must each pass; one driver per net in both.
- **Colours:** PC and MAR pre-buffer nets Olive (address sources before buffers), A[15:0] Green, DB Blue, enables/selects Yellow, CLK Orange, VCC Red, GND Navy.
- Taps on the pin's side, y-aligned to the pin row, left side rotated 180°; unused inputs tied to the chip's own supply symbols; constants that touch DB are driven, never tied.
