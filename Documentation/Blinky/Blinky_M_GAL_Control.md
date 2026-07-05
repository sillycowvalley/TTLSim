# Blinky-M — Two-Stage GAL Control

**Design note, rev A — UNRATIFIED.** A proposal for replacing the four microcode
EEPROMs with a two-stage programmable-logic control path: a **sequencer** stage that
turns an instruction and its T-state into a micro-op index, and a **decode** stage
that expands that index into the machine's control lines. It supersedes nothing until
ratified; the EEPROM control store of the main design document remains the baseline.

---

## 1. Why two stages

The single microcode EEPROM answers one question — "given (opcode, T, COND, INTP),
what are the 32 control lines?" — with a 13-bit lookup. Two facts about that lookup
motivate splitting it:

1. **It is mostly repetition.** Across the whole instruction set only 57 distinct
   control-line combinations ever occur. The EEPROM stores 8,192 rows to hold 57
   distinct answers; the rest is address-space mirroring (COND and INTP duplicated
   across opcodes).

2. **It is slow where it hurts.** The EEPROM sits in the T0 critical path, and a
   150 ns part caps the T-clock near 2–2.5 MHz. A 22V10 answers in ~20 ns.

Naming the 57 combinations — giving each a small **micro-op index** — separates the
two concerns. One stage decides *which* micro-op a given instruction step is (a
sequencing problem, naturally per-family). A second stage decides *what a micro-op
does* (a fixed decode, the same for every instruction that uses it). Both stages are
small enough for GALs, and the decode stage is a pure combinational function of a
7-bit index — the ideal shape for programmable logic.

The result is a control path with no EEPROM in it, running at GAL speed, regenerated
from the same C# source of truth (`BlinkyMGen`) that produces the microcode today.

---

## 2. The two stages

```
   opcode[7:4] ──►┌────────┐ bank enables
                  │  '138  │──────────────┐
                  └────────┘              │   ┌────────┐  T[2:0]
                                          │   │ '161   │──────┐
                                          ▼   │ counter│      │
   opcode[3:0] ─┐        ┌─────────────────────────────┐◄─────┘
   COND    ─────┼───────►│  STAGE 1 — SEQUENCER GALs    │
                │        │  one 22V10 per bank (CTL,     │
                │        │  SHIFT, STK, MEM, ALU, FRM,   │
                │        │  FLOW, CTLX) + ENTRY          │
                └───────►└───────────────┬──────────────┘
                                         │ IDX[6:0]  micro-op index
                                         │ /TRST ────► '161 /LOAD (reload T=0)
                                         ▼
                         ┌─────────────────────────────┐
                         │  STAGE 2 — DECODE GALs       │
                         │  UOPA + UOPB (2× 22V10)      │
                         └───────────────┬──────────────┘
                                         │ 20 folded control lines
                                         ▼
              ┌──────────┬───────────┬───────────┬──────────┐
            '138 SRC   '154 DST   AMODE dec   SPOP '138   + PCINC gate
```

### Stage 1 — the sequencer (per bank)

A `'138` decodes `opcode[7:4]` into one bank enable. Each bank owns a **combinational
22V10** whose inputs are the opcode low bits, `COND`, and the T-state `T[2:0]` from an
external `'161` counter, and whose outputs are the 7-bit micro-op index plus `TRSTN`
(active-low `/TRST`). `/TRST` drives the counter's `/LOAD` — asserting it reloads the
counter to zero, ending the instruction. There are eight banks — CTL, SHIFT, STK, MEM,
ALU, FRM, FLOW, CTLX (the `HALT` anchor at `0xFF`) — plus an ENTRY bank for interrupt
entry, enabled by `INTP` with `T` as its only live input.

The ALU quadrant (`0x40..0x7F`) is one merged bank: the `'138` ORs its four nibble
enables together, so its GAL sees `opcode[5:0]` and `T` (9 inputs). Every ALU opcode
runs the same short sequence, so the sequencer collapses to a handful of terms per line
regardless of which of the 64 functions the opcode selects.

Keeping the T-counter external (rather than registering `T` inside each GAL) is what
lets a single 22V10 hold a whole bank: seven index outputs plus `TRSTN` is eight
outputs, comfortably within the ten macrocells, with no registered-feedback or
async-reset logic to fit. `TRSTN` lands on pin 23 (pin 13 is input-only on a 22V10).
The `'161` is a part the datapath already carries, and it was never on the critical
path, so the speed story is unchanged.

### Stage 2 — the decode (shared)

Two 22V10s, `UOPA` and `UOPB`, take the 7-bit index and expand it to the 20 folded
control lines (§5). This stage is identical hardware regardless of which instruction is
executing — it is the dictionary made of fuses. It is purely combinational; its speed
is the array propagation delay alone.

Downstream expansion is unchanged from the EEPROM design: a `'138` fans SRC out to bus
source enables, a `'154` fans DST out to destination strobes (phase-gated by /CLK), an
AMODE decoder drives the address-bus source and page constants, a `'138` drives the
DSP/RSP count controls from SPOP, and a single AND gate derives PCINC.

---

## 3. The micro-op dictionary

Every T-state of every instruction is one of **57 micro-ops**. The dictionary is the
list of those 57 combinations; Stage 1 emits an index into it, Stage 2 realises it.
Reserving a 7-bit index (128 slots) leaves 71 free for future growth before either
stage needs re-fitting.

A micro-op is fully described by nine fields:

| Field | Width | Meaning |
|---|---|---|
| SRC | 3 | bus source: RAM, ALU, TOS, PCBlo, PCBhi, BP, FLAGS, VEC |
| DST | 4 | bus destination: IR, A, B, TOS, PClo, PChi, ADRlo, ADRhi, BP, FLAGS, RAM-WE, HALT, ICLR |
| ALUFN | 2 | '181 function source: IR-driven, F=A, F=B, A+B |
| AMODE | 4 | address-bus source / page: PC, ADR, {00,ADRlo}, {01,ADRlo}, {02,DSP}, {03–06,RSP} |
| SPOP | 3 | stack pointer op: none, DSP+, DSP−, RSP+, RSP−, DSP−&RSP+ |
| TOSSH | 1 | TOS shift (fill and direction from raw IR) |
| NZ_WE | 1 | write N,Z flags |
| C_WE | 1 | write C flag |
| ISET | 1 | set I |

Two housekeeping lines that were separate μROM bits in the EEPROM design are **folded
away** here and reappear structurally:

- **/TRST** is a Stage-1 output (it belongs to sequencing, not to the datapath), wired
  straight to the T-counter reload — active-low, no inverter.
- **TOS load** is implied by DST = TOS, so it needs no field of its own.
- **PCINC** is derived: `SRC = RAM AND AMODE = PC`. Instruction-stream reads (which is
  every case PC should advance) match this; internal reads use AMODE = ADR and do not.
- **ICLR** rides as a DST code rather than a standalone line — CLI's step has no other
  destination.
- **PCBYTE** rides as two SRC codes (PCBlo / PCBhi) rather than a modifier bit.

These folds are what bring the control lines down from 32 to 20, letting Stage 2 fit in
two 22V10s.

---

## 4. Opcode families

Opcodes are one byte; the high nibble names the family and selects the Stage-1 bank,
the low nibble selects the member. The family grouping is a genuine hardware boundary
here (it is the '138 decode), not merely a mnemonic convention.

| Family | Nibble | Members | Character |
|---|---|---|---|
| **CTL** | 0x0_ | NOP, RET, BRK, RTI, CLI, SEI | Control and interrupt return; no operands |
| **ALU** | 0x1_ | ADD, ADC, SUB, XOR, NOT, TST, AND, CMP, OR, and the five shifts | TOS/NOS arithmetic and logic; the member nibble carries the '181 function and, for shifts, the fill and direction |
| **STK** | 0x2_ | DUP, OVER, TUCK, SWAP, DROP, NIP, ROT, >R, R>, R@ | Data-stack and return-stack shuffles; no memory outside the stack pages |
| **MEM** | 0x3_ | PUSH #, LOAD, STORE, FETCH, STORE!, IN #, OUT #, IN, OUT | Memory and I/O; absolute and zero-page/stack forms |
| **FRM** | 0x4_ | ENTER, LOCAL@, LOCAL! | Frame open and BP-relative locals |
| **FLOW** | 0x5_ | JUMP, CALL, and the six conditionals BEQ/BNE/BCS/BCC/BMI/BPL | Absolute control flow, full 16-bit reach |
| **HALT** | 0xFF | HALT | Bus-idle anchor; unassigned opcodes decode here and stop the machine |

`NOP = 0x00` and `HALT = 0xFF` are fixed anchors: both are bus-idle patterns, so a
fetch from unprogrammed or floating memory halts the machine rather than running wild.

The conditional branches encode their test in the opcode: `IR[2:1]` select the flag
(Z, C, N) and `IR[0]` the polarity, wired to the COND multiplexer with no decode. The
shift members encode fill in `IR[3:2]` and direction in `IR[0]`, wired to the TOS
shifter. These are the two places where member-nibble placement is load-bearing wiring
rather than free choice.

---

## 5. Stage-2 control lines

The 20 lines the two decode GALs produce, with the term count each needed after
minimisation over the 57 live indices (the remaining 71 index values are don't-cares,
which is what keeps the counts low). Polarity is chosen per line — the 22V10's output
XOR makes either sense free — and the heavier lines are placed on the wider macrocells.

**UOPA — source, destination, ALU function, interrupt set**

| Line | Terms | Polarity | Role |
|---|---|---|---|
| SRC0 | 7 | active-high | bus source, bit 0 |
| SRC1 | 5 | active-high | bus source, bit 1 |
| SRC2 | 3 | active-high | bus source, bit 2 |
| DST0 | 11 | active-low | bus destination, bit 0 |
| DST1 | 11 | active-low | bus destination, bit 1 |
| DST2 | 9 | active-high | bus destination, bit 2 |
| DST3 | 10 | active-high | bus destination, bit 3 |
| ALUFN0 | 2 | active-high | '181 function source, bit 0 |
| ALUFN1 | 4 | active-high | '181 function source, bit 1 |
| ISET | 2 | active-high | set the I flag |

**UOPB — address mode, stack-pointer op, shift, flag writes**

| Line | Terms | Polarity | Role |
|---|---|---|---|
| AMODE0 | 12 | active-low | address source / page, bit 0 |
| AMODE1 | 7 | active-high | address source / page, bit 1 |
| AMODE2 | 9 | active-high | address source / page, bit 2 |
| AMODE3 | 2 | active-high | address source / page, bit 3 |
| SPOP0 | 6 | active-high | stack-pointer op, bit 0 |
| SPOP1 | 7 | active-high | stack-pointer op, bit 1 |
| SPOP2 | 2 | active-high | stack-pointer op, bit 2 |
| TOSSH | 1 | active-high | TOS shift enable |
| NZ_WE | 3 | active-high | write N, Z |
| C_WE | 4 | active-high | write C |

Worst line is 12 terms against the 22V10's 16-term maximum macrocell, so both parts fit
with margin. The term counts are a greedy-minimiser upper bound; a proper fitter
(BlinkyJED, cross-checked in WinCUPL) will meet or beat them.

---

## 6. Micro-instruction reference

The 57 micro-ops, grouped by role. Notation: `SRC → DST` is the bus transfer for the
T-state; `@mode` is the address-bus source; `SP` is the pointer op taken on the closing
edge; trailing flags are the write-enables and I control. A micro-op with no bus
transfer still occupies a T-state (it may shift TOS, move a pointer, or write flags).

**Fetch and instruction stream**

| Micro-op | Transfer | Notes |
|---|---|---|
| FETCH | RAM → IR @PC, PC++ | the universal T0; four variants add SP−− / RSP−− / /TRST folded onto the same edge |
| OPERAND→A | RAM → A @PC, PC++ | high byte of a 16-bit operand |
| OPERAND→B | RAM → B @PC, PC++ | low byte of a 16-bit operand |
| OPERAND→ADRlo | RAM → ADRlo @PC, PC++ | absolute-address low byte |
| OPERAND→ADRhi | RAM → ADRhi @PC, PC++ | absolute-address high byte |
| PUSH-IMM | RAM → TOS @PC, PC++ | immediate byte into TOS |
| SKIP | (no transfer) @PC, PC++ | Bcc not-taken operand skip |

**Data-stack transfers**

| Micro-op | Transfer | Notes |
|---|---|---|
| SPILL | TOS → RAM @{02,DSP}, DSP++ | push TOS to the data stack |
| REFILL | RAM → TOS @{02,DSP} | pop into TOS (paired with a prior DSP−−) |
| NOS→A | RAM → A @{02,DSP} | fetch second-on-stack into the ALU A latch |
| NOS→B | RAM → B @{02,DSP} | fetch NOS into the ALU B latch |
| TOS→A | TOS → A | move TOS into the ALU A latch |
| ALU→RAM | ALU → RAM @{02,DSP} | write an ALU result back to the stack (SWAP/ROT/TUCK) |

**ALU writeback**

| Micro-op | Transfer | Notes |
|---|---|---|
| ALU→TOS (arith) | ALU → TOS, NZ_WE, C_WE | function from IR; arithmetic sets carry |
| ALU→TOS (logic) | ALU → TOS, NZ_WE | function from IR; carry preserved |
| FLAGS-ONLY | (no transfer), NZ_WE (,C_WE) | TST and CMP set flags without writing TOS |
| TOS-SHIFT | (no transfer), TOSSH, C_WE | in-place shift; fill and direction from IR |

**Return stack and frames**

| Micro-op | Transfer | Notes |
|---|---|---|
| PUSH-PClo | PCBlo → RAM @{03,RSP} | CALL/entry return-address low lane |
| PUSH-PChi | PCBhi → RAM @{04,RSP} | return-address high lane |
| PUSH-BP | BP → RAM @{05,RSP}, RSP++ | frame-pointer lane; the per-frame RSP increment |
| PUSH-FLAGS | FLAGS → RAM @{06,RSP} | interrupt/BRK flag lane |
| POP-BP | RAM → BP @{05,RSP} | RET/RTI restore |
| POP-PChi | RAM → PChi @{04,RSP} | |
| POP-PClo | RAM → PClo @{03,RSP} | |
| POP-FLAGS | RAM → FLAGS @{06,RSP}, NZ_WE, C_WE | RTI restore through the flag mux |
| BP→A | BP → A | frame-base into the ALU for BP+offset |
| ALU→BP | ALU → BP | ENTER writes the advanced frame base |
| ALU→ADRlo | ALU → ADRlo | LOCAL@/LOCAL! effective-address low byte |
| >R-MOVE | TOS → RAM @{03,RSP}, DSP−−, RSP++ | move a byte from data to return stack |

**PC load and vectors**

| Micro-op | Transfer | Notes |
|---|---|---|
| ALU→PClo | ALU → PClo (F=B) | JUMP/CALL/Bcc target low byte |
| ALU→PChi | ALU → PChi (F=A) | target high byte; usually carries /TRST |
| VEC→PClo | VEC → PClo, ISET | interrupt entry, low vector byte, masks further IRQ |
| VEC→PChi | VEC → PChi | high vector byte |

**Address register and I/O**

| Micro-op | Transfer | Notes |
|---|---|---|
| TOS→ADRlo | TOS → ADRlo | stack-form FETCH/STORE!/IN/OUT address |
| MEM→TOS | RAM → TOS @ADR | absolute LOAD result |
| TOS→MEM | TOS → RAM @ADR, DSP−− | absolute STORE |
| ZP→TOS | RAM → TOS @{00,ADRlo} | zero-page fetch (locals, FETCH) |
| IO→TOS | RAM → TOS @{01,ADRlo} | port read |
| TOS→ZP | TOS → RAM @{00,ADRlo}, DSP−− | zero-page store |
| TOS→IO | TOS → RAM @{01,ADRlo}, DSP−− | port write |
| ALU→ZP | ALU → RAM @{00,ADRlo} | STORE! data write via F=B |
| ALU→IO | ALU → RAM @{01,ADRlo} | OUT data write via F=B |

**Control**

| Micro-op | Transfer | Notes |
|---|---|---|
| I-SET | (no transfer), ISET | SEI |
| I-CLR | DST = ICLR | CLI |
| HALT | DST = HALT | stop the clock; the illegal-opcode and 0xFF landing point |

The exact index numbering is an output of `BlinkyMGen`; the table above is by role
rather than by index, since the numbering is assigned to cluster on-sets for the fitter
and carries no meaning a programmer or reviewer needs.

---

## 7. Package count

| Block | Parts |
|---|---|
| Bank select | 1× '138 |
| Stage 1 sequencer | ~7× 22V10 (one per active family + entry) |
| Stage 2 decode | 2× 22V10 (UOPA, UOPB) |
| Downstream expansion | '138 (SRC), '154 (DST), AMODE decode, '138 (SPOP), 1 gate (PCINC) |

Against the baseline of four 28C64s, the control store grows in package count but
**contains no EEPROM** and runs an order of magnitude faster. With the datapath on HC
logic and code executing from SRAM after the boot copy, the T-clock ceiling moves off
the control store entirely and onto the datapath — 8–10 MHz territory — without ever
building the shadow-copy loader.

The trade to weigh: an instruction or T-state change is a re-fit of the affected
sequencer GAL rather than an EEPROM burn, and a genuinely new micro-op (a control-line
combination not among the 57) re-fits the two decode GALs. Both are regenerated by
`BlinkyMGen` from the same instruction table, so the fuse maps cannot drift from the
design. The verification chain is the established one: BlinkyJED compile, WinCUPL fuse
cross-check, then the TTLSim sweep.

---

## 8. Status

All eleven GALs — UOPA, UOPB, and the nine sequencer banks (CTL, SHIFT, STK, MEM, ALU,
FRM, FLOW, CTLX, ENTRY) — compile clean through BlinkyJED at 5892 fuses each. The fits
are confirmed, not estimated:

- **Stage 2 decoders fit** with margin — worst line is AMODE0 at 12 product terms
  against the 22V10's 16-term maximum macrocell.
- **Stage 1 sequencers fit** with room — the worst index line across all banks is 7
  terms. One 22V10 per bank holds, as hoped; no bank needed a second.
- **Constant index bits take no macrocell.** `IDX6` is 0 in every bank (no micro-op
  index reaches 64), and a few low bits are constant in the small banks (SHIFT, CTLX);
  each is tied to GND on the board rather than consuming a pin.

The fold set (§3) is ratified into rev 14; `BlinkyMGen` emits both GAL stages, the
three-view HTML control reference, and the assembler opcode table from the one C#
instruction table. What remains is downstream, not control-store design: run the JEDEC
maps through the TTLSim sweep against the datapath, and cut the board.
