# Blinky-M — Two-Stage GAL Control

**Rev 14 — ratified.** The machine's only control store: a two-stage
programmable-logic path with no EEPROM in it. A **sequencer** stage turns an
instruction and its T-state into a micro-op index; a **decode** stage expands that
index into the machine's control lines. Both stages, this reference, and the
assembler opcode table are generated from one C# source of truth (`BlinkyMGen`), so
the fuse maps cannot drift from the design.

---

## 1. Why two stages

A single microcode EEPROM would answer one question — "given (opcode, T, COND, INTP),
what are the 32 control lines?" — with a 13-bit lookup. Two facts about that lookup
motivate splitting it, and together they retired the EEPROM outright:

1. **It is mostly repetition.** Across the whole instruction set only 57 distinct
   control-line combinations ever occur. An EEPROM would store 8,192 rows to hold 57
   distinct answers; the rest is address-space mirroring (COND and INTP duplicated
   across opcodes).

2. **It is slow where it hurts.** An EEPROM sits in the T0 critical path, and a
   150 ns part caps the T-clock near 2–2.5 MHz. A 22V10 answers in ~20 ns.

Naming the 57 combinations — giving each a small **micro-op index** — separates the
two concerns. One stage decides *which* micro-op a given instruction step is (a
sequencing problem, naturally per-family). A second stage decides *what a micro-op
does* (a fixed decode, the same for every instruction that uses it). Both stages are
small enough for GALs, and the decode stage is a pure combinational function of a
7-bit index — the ideal shape for programmable logic.

The result is a control path with no EEPROM in it, running at GAL speed, an order of
magnitude faster than the EEPROM alternative and with no shadow-copy loader.

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

Downstream expansion: a `'138` fans SRC out to bus source enables, a `'154` fans DST
out to destination strobes (phase-gated by /CLK), an AMODE decoder drives the
address-bus source and page constants, a `'138` drives the DSP/RSP count controls from
SPOP, and a single AND gate derives PCINC.

---

## 3. The micro-op dictionary

Every T-state of every instruction is one of **57 micro-ops** (indices 0–56). The
dictionary is the list of those 57 combinations; Stage 1 emits an index into it, Stage
2 realises it. A 7-bit index (128 slots) leaves 71 free for future growth before either
stage needs re-fitting; `IDX6` is 0 in every bank.

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

Two housekeeping lines that would be separate μROM bits in an EEPROM design are
**folded away** here and reappear structurally:

- **/TRST** is a Stage-1 output (it belongs to sequencing, not to the datapath), wired
  straight to the T-counter reload — active-low, no inverter.
- **TOS load** is implied by DST = TOS, so it needs no field of its own.
- **PCINC** is derived: `SRC = RAM AND AMODE = PC`. Instruction-stream reads (which is
  every case PC should advance) match this; internal reads use AMODE = ADR and do not.
- **ICLR** rides as a DST code rather than a standalone line — CLI's step has no other
  destination.
- **PCBYTE** rides as two SRC codes (PCBlo / PCBhi) rather than a modifier bit.

These folds bring the control lines down from 32 to 20, letting Stage 2 fit in two
22V10s.

### 3.1 The 57 micro-ops (Stage-2 decode)

Emitted by `BlinkyMGen`. Notation: `SRC→DST @mode` is the bus transfer; the trailing
tokens are the edge-actions on the closing edge — `DspUp/DspDown`, `RspUp/RspDown`,
`PC++`, `ALU=…` (function source), `NZ`/`C` (flag writes), `ISET`, `SHIFT`. A row with
no bus transfer (53, 54, 55, 56) still occupies a T-state — it moves a pointer, shifts
TOS, or writes flags. `RamWe` marks a write into memory; `None` is a bus-idle step.

| IDX | Micro-op | Role |
|---|---|---|
| 0 | `Ram→Ir @Pc PC++` | fetch (universal T0) |
| 1 | `Ram→Ir @Pc DspDown PC++` | fetch, pop DSP on the same edge |
| 2 | `Ram→Ir @Pc RspDown PC++` | fetch, pop RSP on the same edge |
| 3 | `Ram→A @Pc PC++` | operand byte → ALU A |
| 4 | `Tos→A @Adr` | TOS → ALU A |
| 5 | `Tos→A @Adr DspDown` | TOS → ALU A, pre-pop |
| 6 | `Bp→A @Adr` | frame base → ALU A |
| 7 | `Ram→A @DStack` | NOS → ALU A |
| 8 | `Ram→A @DStack DspUp` | NOS → ALU A, grow stack |
| 9 | `Ram→A @DStack DspDown` | NOS → ALU A, shrink stack |
| 10 | `Ram→B @Pc PC++` | operand byte → ALU B |
| 11 | `Ram→B @DStack` | NOS → ALU B |
| 12 | `Ram→B @DStack DspUp` | NOS → ALU B, grow stack |
| 13 | `Ram→B @DStack DspDown` | NOS → ALU B, shrink stack |
| 14 | `Ram→Tos @Pc PC++` | immediate → TOS |
| 15 | `Ram→Tos @Adr` | absolute load result → TOS |
| 16 | `Alu→Tos @Adr ALU=IR NZ` | ALU logic writeback (C preserved) |
| 17 | `Alu→Tos @Adr ALU=IR NZ C` | ALU arithmetic writeback (C written) |
| 18 | `Alu→Tos @Adr ALU=Fa` | pass-A → TOS |
| 19 | `Alu→Tos @Adr ALU=Fb` | pass-B → TOS |
| 20 | `Ram→Tos @ZpAdrLo` | zero-page fetch → TOS |
| 21 | `Ram→Tos @IoAdrLo` | port read → TOS |
| 22 | `Ram→Tos @DStack` | refill TOS from new top-of-stack |
| 23 | `Ram→Tos @RPcLo` | R> read → TOS |
| 24 | `Ram→Tos @RPcLo RspUp` | R@ read → TOS, restore RSP |
| 25 | `Alu→PcLo @Adr ALU=Fb` | branch/JUMP target low → PC |
| 26 | `Vec→PcLo @Adr ISET` | interrupt vector low → PC, mask IRQ |
| 27 | `Ram→PcLo @RPcLo` | RET/RTI PC-lo restore |
| 28 | `Alu→PcHi @Adr ALU=Fa` | branch/JUMP target high → PC |
| 29 | `Vec→PcHi @Adr` | interrupt vector high → PC |
| 30 | `Ram→PcHi @RPcHi` | RET/RTI PC-hi restore |
| 31 | `Ram→AdrLo @Pc PC++` | absolute-address low byte |
| 32 | `Alu→AdrLo @Adr ALU=AddAB` | BP+offset → ADRlo |
| 33 | `Tos→AdrLo @Adr` | stack-form address → ADRlo |
| 34 | `Ram→AdrHi @Pc PC++` | absolute-address high byte |
| 35 | `Alu→Bp @Adr ALU=AddAB` | ENTER: BP+k → BP |
| 36 | `Ram→Bp @RBp` | RET/RTI BP restore |
| 37 | `Ram→Flags @RFlags ALU=IR NZ C` | RTI flags restore |
| 38 | `Tos→RamWe @Adr DspDown` | absolute STORE |
| 39 | `Alu→RamWe @ZpAdrLo ALU=Fb` | STORE! data write |
| 40 | `Tos→RamWe @ZpAdrLo DspDown` | zero-page store |
| 41 | `Alu→RamWe @IoAdrLo ALU=Fb` | OUT data write |
| 42 | `Tos→RamWe @IoAdrLo DspDown` | port write |
| 43 | `Alu→RamWe @DStack ALU=Fa DspUp` | pass-A back to stack (SWAP/ROT/TUCK) |
| 44 | `Tos→RamWe @DStack DspUp` | push TOS to data stack |
| 45 | `Tos→RamWe @RPcLo DspDownRspUp` | `>R`: move a byte data→return stack |
| 46 | `PcbLo→RamWe @RPcLo` | push return-address low |
| 47 | `PcbHi→RamWe @RPcHi` | push return-address high |
| 48 | `Bp→RamWe @RBp RspUp` | push BP; the per-frame RSP increment |
| 49 | `Flags→RamWe @RFlags` | push flags (interrupt/BRK frame) |
| 50 | `Ram→Halt @Adr` | stop the clock (HALT / illegal opcode) |
| 51 | `Ram→Iclr @Adr` | CLI: clear I |
| 52 | `Ram→None @Pc PC++` | Bcc not-taken operand skip |
| 53 | `ISET` | SEI: set I |
| 54 | `ALU=IR NZ` | flags-only (TST) |
| 55 | `ALU=IR NZ C` | flags-only with carry (CMP) |
| 56 | `SHIFT C` | in-place TOS shift; fill/direction from IR |

The index numbering is an output of `BlinkyMGen`, assigned to cluster on-sets for the
fitter; it carries no meaning a programmer or reviewer needs beyond decoding §7.

---

## 4. Opcode families

Opcodes are one byte; the high nibble names the family and selects the Stage-1 bank,
the low nibble selects the member. The family grouping is a genuine hardware boundary
here (it is the '138 decode), not merely a mnemonic convention.

| Family | Nibble(s) | Members | Character |
|---|---|---|---|
| **CTL** | 0x0_ | NOP, RET, BRK, RTI, CLI, SEI | Control and interrupt return; no operands |
| **SHIFT** | 0x1_ | SHL, SHR, ASR, ROL, ROR | In-place TOS shifts; fill in IR[3:2], direction in IR[0] |
| **STK** | 0x2_ | DUP, OVER, TUCK, SWAP, DROP, NIP, ROT, >R, R>, R@ | Data- and return-stack shuffles |
| **MEM** | 0x3_ | PUSH #, LOAD, STORE, FETCH, STORE!, IN #, OUT #, INS, OUTS | Memory and I/O; absolute and zero-page/stack forms |
| **ALU** | 0x4_–0x7_ | the '181 quadrant (64 slots); named ADD, ADC, SUB, XOR, NOT, AND, OR, TST | TOS/NOS arithmetic and logic; the member bits carry the '181 function |
| **FRM** | 0x8_ | ENTER, LOCAL@, LOCAL! | Frame open and BP-relative locals |
| **FLOW** | 0x9_ | JUMP, CALL, **CMP (0x96)**, and BEQ/BNE/BCS/BCC/BMI/BPL | Absolute control flow, full 16-bit reach; CMP parks in a spare slot |
| **CTLX** | 0xFF | HALT | Bus-idle anchor; unassigned opcodes decode here and stop the machine |

`NOP = 0x00` and `HALT = 0xFF` are fixed anchors: both are bus-idle patterns, so a
fetch from unprogrammed or floating memory halts the machine rather than running wild.

Three member placements are load-bearing wiring:

- **ALU quadrant (0x40–0x7F):** the opcode *is* the '181 control — `0 1 M CN S3 S2 S1 S0`.
  The six function bits wire (through the ALUFN mux) straight to the '181 S/M/CN pins,
  so the whole function table is a working instruction. This makes the ALU family's
  writeback a *single* micro-op (arithmetic vs logic) instead of one per function.
- **Branch members (0x9A–0x9F):** `IR[2:1]` select the flag (01 Z, 10 C, 11 N) and
  `IR[0]` the polarity, wired to the COND multiplexer with no decode.
- **Shift members (0x1_):** fill in `IR[3:2]` wires to the '153 fill mux; direction in
  `IR[0]` wires to the '194 direction and the C-source tap.

**CMP is encoded at 0x96, outside the ALU quadrant.** SUB (0x56) and CMP evaluate the
same '181 pattern (M=0, CN=1, S=0110), but SUB writes TOS while CMP writes flags only.
Rather than fork the quadrant's writeback micro-op on a member bit, CMP takes a spare
FLOW slot and runs its own flags-only sequence (§7). The quadrant stays uniform.

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

Worst line is AMODE0 at 12 product terms against the 22V10's 16-term maximum macrocell,
so both parts fit with margin.

---

## 6. Sequencer banks (Stage 1)

Nine 22V10s, one per bank plus ENTRY, each mapping `(opcode-low, COND, T)` to an index.
The full per-bank truth tables are the JEDEC maps (`BlinkyMGen --plds`); the
human-readable form of the sequencer is the instruction→micro-op decomposition of §7,
which is exactly these tables read out per opcode.

- **CTL, SHIFT, STK, MEM, FRM, FLOW, CTLX** — one 22V10 each, keyed on the opcode low
  nibble and T. SHIFT and CTLX have several constant low index bits (tied to GND on the
  board rather than consuming a pin).
- **ALU** — one merged 22V10 for `0x40–0x7F`, keyed on `opcode[5:0]` and T. Because
  every ALU opcode runs the same sequence, the only opcode dependence is arithmetic vs
  logic (M bit) and unary vs binary; the '181 function itself never reaches the GAL.
- **ENTRY** — enabled by `INTP`, keyed on T alone; runs the six-state hardware
  interrupt-entry sequence (§7).

The worst index line across all banks is 7 terms — one 22V10 per bank holds with room,
no bank needed a second.

---

## 7. Instruction → micro-op sequence

The authoritative per-opcode decomposition, emitted by `BlinkyMGen`. Each instruction
is a list of micro-op indices, one per T-state, T0 first. `•` marks the T-state that
carries `/TRST` (the sequencer reloads the T-counter and the instruction ends). Fetch
is T0 unless the interrupt-entry path replaces it. Names are from the §3.1 dictionary.

**Reading a row:** `RET 0x01 (4T): 2•? …` — the number is the index; look it up in
§3.1. Example, RET:

```
RET    0x01   4T
  T0   2   Ram→Ir @Pc  RspDown  PC++     (fetch; pre-pop the frame)
  T1  36   Ram→Bp @RBp                   (restore BP)
  T2  30   Ram→PcHi @RPcHi               (restore PC high)
  T3  27 • Ram→PcLo @RPcLo               (restore PC low; /TRST)
```

### CTL (0x0_)

| Opcode | Mnemonic | T | Sequence (indices) |
|---|---|---|---|
| 0x00 | NOP | 1 | 0• |
| 0x01 | RET | 4 | 2 · 36 · 30 · 27• |
| 0x02 | BRK | 7 | 0 · 46 · 47 · 49 · 48 · 26 · 29• |
| 0x03 | RTI | 5 | 2 · 36 · 37 · 30 · 27• |
| 0x04 | CLI | 2 | 0 · 51• |
| 0x05 | SEI | 2 | 0 · 53• |

### SHIFT (0x1_)

| Opcode | Mnemonic | T | Sequence (indices) |
|---|---|---|---|
| 0x10 | SHL | 2 | 0 · 56• |
| 0x11 | SHR | 2 | 0 · 56• |
| 0x13 | ASR | 2 | 0 · 56• |
| 0x14 | ROL | 2 | 0 · 56• |
| 0x15 | ROR | 2 | 0 · 56• |

All five share `0 · 56`; the fill (IR[3:2]) and direction (IR[0]) ride micro-op 56's
`SHIFT` as raw IR bits, so one sequence covers the family.

### STK (0x2_)

| Opcode | Mnemonic | T | Sequence (indices) |
|---|---|---|---|
| 0x20 | DUP | 2 | 0 · 44• |
| 0x21 | OVER | 4 | 1 · 8 · 44 · 18• |
| 0x22 | TUCK | 4 | 1 · 7 · 44 · 43• |
| 0x23 | SWAP | 4 | 1 · 7 · 44 · 18• |
| 0x24 | DROP | 2 | 1 · 22• |
| 0x25 | NIP | 1 | 1• |
| 0x26 | ROT | 6 | 1 · 9 · 11 · 43 · 44 · 19• |
| 0x28 | >R | 3 | 0 · 45 · 22• |
| 0x29 | R> | 3 | 2 · 44 · 23• |
| 0x2A | R@ | 3 | 2 · 44 · 24• |

NIP is a single state: the fetch's own `DspDown` (micro-op 1) discards NOS, so no
second transfer is needed.

### MEM (0x3_)

| Opcode | Mnemonic | T | Sequence (indices) |
|---|---|---|---|
| 0x30 | PUSH # | 3 | 0 · 44 · 14• |
| 0x31 | LOAD | 5 | 0 · 31 · 34 · 44 · 15• |
| 0x32 | STORE | 5 | 0 · 31 · 34 · 38 · 22• |
| 0x33 | FETCH | 3 | 0 · 33 · 20• |
| 0x34 | STORE! | 5 | 1 · 33 · 13 · 39 · 22• |
| 0x35 | IN # | 4 | 0 · 31 · 44 · 21• |
| 0x36 | OUT # | 4 | 0 · 31 · 42 · 22• |
| 0x37 | INS | 3 | 0 · 33 · 21• |
| 0x38 | OUTS | 5 | 1 · 33 · 13 · 41 · 22• |

### ALU (0x40–0x7F)

The quadrant runs one of four short sequences. The '181 function selected by the opcode
rides micro-op 16/17 as `ALU=IR`; it never changes the sequence.

| Range | Class | T | Sequence (indices) |
|---|---|---|---|
| 0x40–0x5F | binary arithmetic (M=0) | 4 | 0 · 5 · 11 · 17• |
| 0x60–0x7F | binary logic (M=1) | 4 | 0 · 5 · 11 · 16• |
| 0x60 | NOT (unary) | 3 | 0 · 4 · 16• |
| 0x6F | TST (unary, flags-only) | 3 | 0 · 4 · 54• |

Micro-op 17 writes C (arithmetic); 16 preserves it (logic). The two unary exceptions
(NOT, TST) skip the NOS fetch. Named slots: ADD 0x49, ADC 0x59, SUB 0x56 (arithmetic);
XOR 0x66, AND 0x6B, OR 0x6E (logic); NOT 0x60, TST 0x6F. Every other slot is a valid
`ALU #n` running the arithmetic or logic sequence for its half of the quadrant.

### FRM (0x8_)

| Opcode | Mnemonic | T | Sequence (indices) |
|---|---|---|---|
| 0x80 | ENTER | 4 | 0 · 10 · 6 · 35• |
| 0x81 | LOCAL@ | 6 | 0 · 10 · 6 · 44 · 32 · 20• |
| 0x82 | LOCAL! | 6 | 0 · 10 · 6 · 32 · 40 · 22• |

### FLOW (0x9_)

| Opcode | Mnemonic | T | Sequence (indices) |
|---|---|---|---|
| 0x90 | JUMP | 5 | 0 · 10 · 3 · 25 · 28• |
| 0x91 | CALL | 8 | 0 · 10 · 3 · 46 · 47 · 48 · 25 · 28• |
| 0x96 | CMP | 4 | 1 · 12 · 4 · 55• |
| 0x9A | BEQ | 3 / 5 | not taken: 0 · 52 · 52• · taken: 0 · 10 · 3 · 25 · 28• |
| 0x9B | BNE | 3 / 5 | not taken: 0 · 52 · 52• · taken: 0 · 10 · 3 · 25 · 28• |
| 0x9C | BCS | 3 / 5 | not taken: 0 · 52 · 52• · taken: 0 · 10 · 3 · 25 · 28• |
| 0x9D | BCC | 3 / 5 | not taken: 0 · 52 · 52• · taken: 0 · 10 · 3 · 25 · 28• |
| 0x9E | BMI | 3 / 5 | not taken: 0 · 52 · 52• · taken: 0 · 10 · 3 · 25 · 28• |
| 0x9F | BPL | 3 / 5 | not taken: 0 · 52 · 52• · taken: 0 · 10 · 3 · 25 · 28• |

CMP's fetch `DspDown` (1) is undone by the following `DspUp` (12), so the stack pointer
is unchanged — operands are held while micro-op 55 writes N/Z/C. The branch not-taken
path skips both operand bytes with two `Ram→None @Pc PC++` (52) states; the taken path
loads them and vectors PC exactly as JUMP does. `COND` steers the sequencer between the
two.

### CTLX (0xFF)

| Opcode | Mnemonic | T | Sequence (indices) |
|---|---|---|---|
| 0xFF | HALT | 2 | 0 · 50• |

Unassigned opcodes in the non-ALU families decode into their bank's HALT-equivalent and
stop the machine, same as the 0xFF anchor.

### Interrupt entry (ENTRY bank, INTP)

Not an opcode: when `INTP` is registered high, T0 of the fetch runs the ENTRY bank
instead of loading IR. Six states, `/TRST` on the last.

| Path | T | Sequence (indices) |
|---|---|---|
| Hardware entry | 6 | 46 · 47 · 49 · 48 · 26 · 29• |

The sequence pushes the return address (46, 47), flags (49) and BP (48, which counts
RSP once per frame), then loads both PC bytes from the vector driver (26 sets I, 29
carries /TRST). BRK (0x02) is the software form: an ordinary fetch followed by the same
six states, which is why its push resumes after the fetch rather than before it.

---

## 8. Package count

| Block | Parts |
|---|---|
| Bank select | 1× '138 |
| Stage 1 sequencer | 9× 22V10 (CTL, SHIFT, STK, MEM, ALU, FRM, FLOW, CTLX, ENTRY) |
| T-counter | 1× '161 |
| Stage 2 decode | 2× 22V10 (UOPA, UOPB) |
| Downstream expansion | '138 (SRC), '154 (DST), AMODE decode, '138 (SPOP), 1 gate (PCINC) |

The control store contains **no EEPROM** and runs an order of magnitude faster than the
EEPROM alternative. With the datapath on HC logic and code executing from SRAM after
the boot copy, the T-clock ceiling sits on the datapath — 8–10 MHz territory — with no
shadow-copy loader.

An instruction or T-state change is a re-fit of the affected sequencer GAL; a genuinely
new micro-op (a control-line combination not among the 57) re-fits the two decode GALs.
Both are regenerated by `BlinkyMGen` from the same instruction table, so the fuse maps
cannot drift from the design. Verification chain: BlinkyJED compile, WinCUPL fuse
cross-check, then the TTLSim sweep.

---

## 9. Status

All eleven GALs — UOPA, UOPB, and the nine sequencer banks (CTL, SHIFT, STK, MEM, ALU,
FRM, FLOW, CTLX, ENTRY) — compile clean through BlinkyJED at 5892 fuses each. The fits
are confirmed:

- **Stage 2 decoders fit** with margin — worst line is AMODE0 at 12 product terms
  against the 22V10's 16-term maximum macrocell.
- **Stage 1 sequencers fit** with room — the worst index line across all banks is 7
  terms. One 22V10 per bank; no bank needed a second.
- **Constant index bits take no macrocell.** `IDX6` is 0 in every bank (no micro-op
  index reaches 64), and a few low bits are constant in the small banks (SHIFT, CTLX);
  each is tied to GND on the board rather than consuming a pin.

`BlinkyMGen` emits both GAL stages, this control reference, and the assembler opcode
table from the one C# instruction table. What remains is downstream, not control-store
design: run the JEDEC maps through the TTLSim sweep against the datapath, and cut the
board.
