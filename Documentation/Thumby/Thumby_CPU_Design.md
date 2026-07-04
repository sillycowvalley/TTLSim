# Thumby CPU — Hardware Design

**DRAFT — first pass.** The ISA (`Thumby_ISA.md`) is locked; this document turns it
into boards, chips, and control wires. Decisions made here are marked **decided**;
everything still in play is collected under "Open decisions" at the end. Same
discipline as the UART doc: settle what can be settled, name what can't.

## Scope and ground rules

- Implements the ratified ISA exactly: 16-bit Von Neumann, word-addressable,
  8×16 register file, series barrel shifter on B, 74181×4 + 74182 lookahead,
  four-state sequencer (FETCH, EXEC, MEM, INT), single-level interrupts with
  hardware shadows.
- Reuses the existing clock module unchanged — its four-pin header (GND, +5,
  CLK, /RST) is the entire clock contract. The breakpoint card attaches to the
  address bus exactly as it does on Blinky and gets the watchpoint behaviour for
  free (one shared bus carrying fetches, loads, and stores).
- GAL discipline carries over: instruction bits in, control vector out, all
  GAL22V10 (20 ns), all through BlinkyJED, all provable on the Arduino tester
  before they touch the board.
- 74HC throughout; the LS '181 stock gets an HCT fence if the HC parts don't
  materialise (inventory currently shows 0× HC181, 26× LS181).
- Modular boards in the established format: each board adds one capability and
  is watchable on the front panel. The board partition is proposed at the end.

## Top-level block map

```
                       +------------------+
                       |  CLOCK MODULE    | CLK, /RST (existing board)
                       +--------+---------+
                                |
   +--------- SEQUENCER (2 FF + GAL: FETCH/EXEC/MEM/INT) ----------+
   |                                                               |
   |   +--------+     +-----------+      +--------------------+   |
   |   |   PC   |     |    IR     |      |  DECODE GALs       |   |
   |   | 4×'161 |     |  2×'574   +----->|  op decode 22V10   |   |
   |   +---+----+     +-----+-----+      |  cond eval 22V10   |   |
   |       |                |            +---------+----------+   |
   |       |          instruction bits             |  control vector
   |       |                                       v
   |   ADDRESS MUX      REGISTER FILE (8×16, 2R1W, 16× '670)
   |   PC / F           A port          B port
   |       |               |               |
   |       |        A-SOURCE MUX     B-SOURCE MUX
   |       |        (A / PC+1)       (B / imm / A-tap / IPC)
   |       |               |               |
   |       |               |        BARREL SHIFTER (16-bit, LSL/LSR/ASR/ROR)
   |       |               |               |
   |       |            74181 ×4  +  74182 lookahead
   |       |               |               |
   |       |          F result        flags NZCV ('175) + shadow + V logic
   |       v               |
   |   MEMORY (64K×16 SRAM, MMIO via '138)
   |       |
   |   WRITE-BACK MUX (F / MEM data / PC+1) → register file write port
   |
   +--- INTERRUPT BLOCK (IPC 2×'574, shadow '175, vector jam '541, IE + sync)
```

One result path, one write port. The ISA's datapath sketch is the law; this
document adds the three muxes it implies but doesn't draw: the **address mux**
(PC vs F onto the memory address bus), the **write-back data mux** (F vs memory
data vs PC+1 into the register file), and the **write-address mux** (IR[2:0] vs
constant 7 for link writes). All three are spelled out below.

## Sequencer

Four states, two flip-flops, one GAL — as ratified.

| State | Encoding | Does |
|---|---|---|
| FETCH | 00 | mem[PC] → IR; sample /IRQ; PC counts up at end of state |
| EXEC  | 01 | decode, shift, ALU; write-back, flag latch, PC load at end of state |
| MEM   | 10 | address = F on the bus; read → write-back, or write Rd → mem |
| INT   | 11 | IPC ← PC, shadow ← NZCV, IE ← 0, PC ← 0x0002, all in parallel |

Transitions:

- FETCH → INT when (/IRQ synchronized AND IE) at the fetch boundary; else FETCH → EXEC.
- EXEC → MEM for class 10 and the class-01 PC-relative literal load; else EXEC → FETCH.
- MEM → FETCH. INT → FETCH.

The state flops and the GAL live together; the encoding above makes the "needs
MEM" and "in INT" qualifiers single-bit tests. Instruction cost: 2 cycles for
everything except memory access (3) and interrupt entry (1 extra, once).

**Clocking discipline (decided).** All state lands on the same clock edge —
the clock module's single-edge doctrine, unchanged. Within a state, everything
is combinational settle; at the state-ending edge, the registers that this
state owns (IR, PC, register file, flags, IPC/shadow) take their new values
simultaneously. No mid-state strobes except the two write pulses below, which
are gated with the clock's low phase precisely because their target devices
have level-sensitive writes.

## PC unit

- **4× 74HC161**: 16-bit synchronous counter with synchronous parallel load and
  the asynchronous clear that gives reset PC = 0x0000 for free (the clock
  module's /RST wired straight to /CLR).
- **Count enable**: asserted at the end of FETCH (the post-increment that makes
  PC+1 "the pre-incremented counter value" during EXEC), and at the end of a
  not-taken conditional branch — which is just the same enable left alone,
  since a not-taken branch does nothing.
- **Load enable**: end of EXEC for taken branches, JA/JAL, JR/JALR, RETI; during
  INT for the vector jam.
- **Load data muxing**, per the ISA: hi byte always from F[15:8]; lo byte from
  F[7:0] or IR[7:0] — one 8-bit 2:1 mux (2× '157), selected only by JA/JAL. The
  '541 vector jam drives the load inputs during INT, overriding both (its /OE is
  the INT state; the F-path buffers are disabled that cycle).
- **PC+1 tap**: the counter outputs *after* the FETCH increment are PC+1 by
  definition — no adder, no extra register. They feed the A-source mux (branch
  target add, ADR, literal-pool base) and the write-back mux (link writes).

## Instruction register

**2× 74HC574**, loaded from the data bus at the end of FETCH. The IR's fields
fan out combinationally: class bits to the sequencer GAL, op4/op3/cond4 to the
decode GALs, register fields to the file's address inputs, immediates to the
B-source extenders and the shifter amount mux.

## Register file — the '670 array

8 registers × 16 bits, two read ports, one write port, from 74HC670 (4×4,
one read + one write port, tri-state outputs).

The arithmetic, stated once so nobody re-derives it wrong:

- One 16-bit-wide bank of 4 registers = 4 chips.
- 8 registers = 2 banks = 8 chips… with **one** read port.
- A second read port on a '670 array means a **second complete copy**, written
  in lockstep: 16 chips total. Inventory holds 32 — covered, with a full spare
  array.

Addressing:

- **A read address**: IR[2:0] (Rd) — the destructive accumulator side.
- **B read address**: IR[5:3] (Rm) normally; IR[10:8] for JA/JAL — the 3-bit
  2:1 mux the ISA already calls for (1× '157 covers it with a section spare).
- **Write address**: IR[2:0] (Rd) normally; **constant 7** for the link writes
  (BL, JAL, JALR write r7 = PC+1). One more 3-bit 2:1 mux — trivially, three
  OR gates, since forcing 7 is forcing all-ones (half a '32).
- Bank decode: address bit 2 selects which bank's /WE fires and which bank's
  /OE drives the port; the two banks' tri-state outputs share the port wires.
  This is the one deliberate tri-state share in the machine — two drivers, one
  wire, decode-guaranteed exclusive. The no-shared-bus doctrine survives
  everywhere else.

**The '670 write trap (decided handling).** The '670 write is a transparent
latch while /WE is low — not edge-triggered. An /WE derived only from decode
would write garbage while the ALU settles. Therefore: **/WE = decode-WE AND
state-is-write-phase AND CLK-low** — the write window opens only in the second
half of the state, after the shifter + '181 + lookahead path has settled, and
closes before the state edge changes anything. Same gating for the memory /WE
during MEM stores. This is the machine's only use of the clock as a level, and
it's fenced to exactly these two strobes.

## A-source and B-source muxes

**A-source (2:1, 16-bit, 4× '157):** register-file A port (default) or PC+1 —
the latter for relative-branch target adds, ADR, and the literal load, all of
which run the '181 ADD row.

**B-source (16-bit, tri-state legs on a common wire, '541/'244 per leg):**

| Leg | Drives when | Extension |
|---|---|---|
| Register-file B port | class 00 (except SHF), memory, JR/JALR, JA/JAL | — |
| imm8 / offset8 from IR[7:0] | class 01, conditional branches | zero-extend (class 01), sign-extend (offset8) |
| A-port tap | SHF only | — |
| IPC latch | RETI only | — |

Sign-extension of offset8 is IR[7] fanned to bits 15:8 through the branch leg's
buffer inputs; zero-extension is those inputs grounded on the imm8 leg. Two
'541s, one per extension flavour, /OE from decode — cheaper than a mux tree and
the exclusivity is a two-term GAL guarantee. (Four drivers, one wire: the
second and last tri-state share, same decode-exclusive justification as the
register banks.)

## Barrel shifter

The centrepiece, and the biggest single block. 16 bits, four types (LSL, LSR,
ASR, ROR), amounts 0–15, in series on B, no bypass.

**Structure (proposed): two radix-4 stages of '153.** Stage 1 shifts 0/1/2/3,
stage 2 shifts 0/4/8/12; each stage is 8× '153 (dual 4:1) for 16 bits →
**16 packages**, plus the fill/wrap steering. The alternative — four radix-2
stages of '157 (shift 1/2/4/8) — is also 16 packages but two more gate-delays
deep. Radix-4 wins on the critical path and loses nothing on count; inventory
has 30× '153. **Open decision #1** only in the sense that TTLSim wiring may
reveal a reason to prefer the '157 ladder; the '153 pair is the default.

**Direction and fill.** A right shift by n is a left rotate by 16−n with the
fill imposed — but the clean implementation keeps it direct: the mux *inputs*
are wired per-type via a small steering layer decoded once from the 2-bit type:

- LSL: vacated low bits ← 0.
- LSR: vacated high bits ← 0.
- ASR: vacated high bits ← pre-shift bit 15 (one wire, fanned).
- ROR: vacated bits ← wrapped data (the rotate connection).

The type decode (2 bits → fill-select lines) is a handful of gates or spare
GAL terms; it fans identically to both stages.

**Amount mux** (4 bits, per the shifter contract): imm4 (IR[9:6], class 00) /
B-port[3:0] (SHF) / constant 8 (ORH, JA/JAL) / ss = IR[10:9] zero-extended
(scaled memory) / constant 0 (everything else). Five sources, 4 bits: 2× '153
with the select lines from decode, constants strapped.

**Last-bit-out capture.** The C-source "shifter" input is the last bit shifted
out: for amount n, that's pre-shift bit (16−n) mod 16 for LSL/ROR and bit n−1
for LSR/ASR. Rather than a second mux tree, tap it from the stage-1/stage-2
boundary and the final output — worked in TTLSim before committing; if the tap
turns ugly, a dedicated 16:1 ('150 or 2× '151) off the pre-shift operand,
addressed by a trivially-derived index, is the fallback. The **amount ≠ 0**
qualifier (C unchanged on zero-amount shifts) is a 4-input OR on the amount
lines, gating the C latch enable — dynamic, so it covers SHF for free.

## ALU — 74181 ×4 + 74182

As ratified in the ISA's carry-architecture section; restated here only as
build facts:

- S3–S0, M, and the Cn mux select come from the op-decode GAL, unqualified by
  class (WE/flag gating makes them don't-care off class 00), **except** the MEM
  state forces the ADD row (S=1001, M=arith, Cn=H) for address generation — a
  state-input to the decode GAL, not a separate mux.
- Cn source mux: H / L / /C — three-way, one '153 section.
- '182 wiring per the ISA polarity notes: /P→X, /G→Y direct (both active-low),
  '182 carry outputs direct to slices 1–3 Cn, external Cn to slice 0 and the
  '182 in parallel. No inverters in the lookahead path.
- TTLSim substitutes the three-'04 ripple cascade (no '182 behavioural model);
  the validation sweep is ADD/SUB × both carry-ins × all F bits + final carry,
  with the SUB rows watched hardest.

## Flags, V logic, and the condition evaluator

- **Flag register: 1× '175** (N Z C V), clocked at the state edge, enabled by
  the FLAG_WE guard from decode (class-qualified, independent of register WE —
  the CMP/TST discipline).
- **N** = F[15]. **Z** = NOR across F[15:0]: 2× '688 against strapped zero is
  the lazy build (inventory: 3), but 2× 74HC4078-style 8-input NORs or four
  '02+'21 stages is cheaper if '688s are wanted elsewhere; call it '688s for
  now and note the swap. **Open decision #2.**
- **C-source mux: five inputs** (adder Cn+4 / shifter last-bit-out / constant 0 /
  constant 1 / hold). Hold is the '175's own Q fed back. One '151 (8:1, three
  inputs spare), select lines from decode. The CLC/SEC constants ride the same
  mux — no special path.
- **V logic**: computed from sign bits A[15], post-shift B[15], F[15] —
  same-signs-in/different-sign-out for add, mirrored for subtract. Two
  three-input product terms and a subtract-mode select; lives most naturally as
  spare terms in the op-decode GAL (the signals are already there). Latched
  only where the C column says "adder".
- **Condition evaluator: 1× GAL22V10** — NZCV + cond4 in, `taken` out, exactly
  the 15-row table. `taken` gates the PC load enable for class-1100; cond 1111
  stays reserved (SWI trap through the vector-jam mechanism, later).

## Interrupt block

Per the ISA, build-level:

- **IPC: 2× '574**, clocked by the INT state, D inputs from the address bus
  (which carries PC — the not-yet-fetched instruction — at that moment).
  Tri-state /OE is the RETI leg of the B-source mux.
- **Shadow: 1× '175**, clocked by INT from the live NZCV; drives the '175 flag
  register's D inputs through the RETI path (a 2:1 on the flag D inputs, or
  more cheaply: the flag register's D-side already has the C-source mux — the
  shadow restore is a sixth… no. Keep it clean: **4-bit 2:1 '157 on the flag
  D inputs**, normal-source vs shadow, selected by RETI).
- **IE**: one flip-flop ('74 half). Set by EI and RETI, cleared by DI and INT,
  all decode terms. Reset state: **cleared** by /RST — the machine wakes with
  interrupts off, and the reset stub EIs when ready. (The ISA leaves IE's reset
  state unstated; this document decides it: off. The alternative is a boot-time
  race against peripherals asserting /IRQ before a stack exists.)
- **/IRQ sync**: two '74 stages on CLK — the standard two-FF fence. Sampled by
  the sequencer GAL only at the FETCH boundary.
- **Vector jam: 1× '541** strapped to 0x0002, /OE = INT, onto the PC load
  inputs.

## Memory interface

- **64K × 16**: 2× 8-bit-wide SRAM (e.g. 32K×8 ×4 or 64K×8 ×2 — sized by what's
  procurable; **open decision #3**). Word-addressable means the CPU address bus
  is the SRAM address bus directly, no byte lane logic, no alignment anything.
- **Address mux**: PC (FETCH) vs F (MEM) — 4× '157, selected by the state. The
  breakpoint card taps this bus and therefore sees fetches *and* data traffic:
  the free watchpoint, as on Blinky.
- **Data bus**: bidirectional. Reads: SRAM /OE during FETCH (→ IR) and MEM-load
  (→ write-back mux). Writes: register-file A port (Rd's value) driven through
  a '245 pair onto the bus, SRAM /WE gated CLK-low per the write-strobe rule.
- **MMIO decode**: 1× '138 off the address top bits during MEM, carving the
  peripheral window (UART's 2-word block lands here; its doc already assumes
  this decoder). The map itself — how much of the top of the address space is
  I/O — is **open decision #4**; the UART needs only 2 words, so a 16-word
  window at 0xFFF0 is the strawman.

## Write-back mux

Three sources into the register file's write-port data inputs:

| Source | When |
|---|---|
| F (ALU result) | all writing ALU/immediate ops |
| Memory data bus | LDR (end of MEM) |
| PC+1 | BL, JAL, JALR (the r7 link write) |

3:1 × 16 bits: 8× '153 with one input spare, or two '157 ranks. The '153 build
is flatter; take it. Note the link write and a normal write can't coincide
(different instructions), and the LDR write happens at the end of MEM, not
EXEC — the write-address and /WE gating are state-qualified accordingly.

## Control vector inventory

Every control wire, its width, and its source. This is the checklist the
decode-GAL equations and the TTLSim harness are both written against.

| Signal | Bits | Source | Consumer |
|---|---|---|---|
| SHTYPE | 2 | op decode | shifter fill/wrap steering |
| SHAMT_SEL | 3 | op decode + class | shifter amount mux |
| S3–S0, M | 5 | op decode (MEM state forces ADD) | '181 |
| CN_SEL | 2 | op decode | Cn mux (H/L//C) |
| ASRC | 1 | class decode | A-source mux (A / PC+1) |
| BSRC | 2 | class decode | B-source leg /OEs (4 legs, exclusive) |
| BADDR_SEL | 1 | class decode | B read-address mux (Rm / IR[10:8]) |
| WADDR7 | 1 | link-write decode | write-address force-7 |
| WB_SEL | 2 | class/state | write-back mux (F / MEM / PC+1) |
| WE | 1 | op decode, class- and state-gated, CLK-low | register file /WE |
| FLAG_WE | 1 | op decode, class-gated | '175 clock enable |
| C_SEL | 3 | op decode | C-source mux (5-way) |
| V_EN | 1 | op decode | V latch qualifier |
| AMT_NZ | 1 | shifter amount OR | C latch gate (shift ops) |
| PC_CNT / PC_LD | 2 | sequencer + cond eval | '161 ENT / /LOAD |
| PCLO_SEL | 1 | JA/JAL decode | PC low-byte load mux |
| ADDR_SEL | 1 | sequencer | address mux (PC / F) |
| MEM_OE / MEM_WE | 2 | sequencer + L bit, CLK-low on WE | SRAM |
| IR_LD | 1 | sequencer (FETCH) | '574 clock gate |
| INT strobes | — | sequencer (INT) | IPC clk, shadow clk, IE clr, '541 /OE |
| RETI strobes | — | decode | flag-D mux, IE set, IPC /OE |
| taken | 1 | cond GAL | PC_LD gate |

Rough tally: ~30 wires. The op-decode 22V10 owns the per-op tuple (SHTYPE,
S/M, CN_SEL, WE, FLAG mask, C_SEL, V_EN); a second 22V10 covers class/state
steering (ASRC, BSRC, WB_SEL, address and PC controls, the link/RETI/INT
strobes); the condition evaluator is the third. **The GAL complement is
therefore three 22V10s plus the sequencer GAL — one more than the ISA doc's
count of two**, because the ISA counted decode GALs only and the class/state
steering has to live somewhere. If the sequencer GAL's spare terms absorb the
steering, it collapses back to three total. **Open decision #5**, settled by an
actual term count in BlinkyJED, exactly as the UART's one-GAL-or-two question
will be.

## Chip budget (estimate)

| Block | Parts | Qty |
|---|---|---|
| PC | '161 | 4 |
| PC low-byte mux + vector jam | '157 ×2, '541 ×1 | 3 |
| IR | '574 | 2 |
| Register file | '670 | 16 |
| B read-addr mux + write-addr force | '157, ¼ '32 | 2 |
| A-source mux | '157 | 4 |
| B-source legs | '541/'244 | 4 |
| Barrel shifter | '153 ×16 + steering gates | ~18 |
| Shifter amount mux | '153 | 2 |
| ALU | '181 ×4, '182 ×1 | 5 |
| Cn mux + C-source mux | '153 ¼, '151 | 2 |
| Z detect | '688 | 2 |
| Flags + shadow + restore mux | '175 ×2, '157 | 3 |
| Interrupt (IPC, IE, sync) | '574 ×2, '74 ×2 | 4 |
| Write-back mux | '153 | 8 |
| Address mux | '157 | 4 |
| Data-bus drivers | '245 | 2 |
| MMIO decode | '138 | 1 |
| Memory | SRAM | 2–4 |
| Sequencer | '74 + GAL | 2 |
| Decode + condition GALs | 22V10 | 3 |
| **Total** | | **~90 packages** |

Sobering and honest: the shifter and the register file are half the machine.
That is the price of the design thesis, paid knowingly — the shift is on every
instruction because the silicon is there on every instruction.

## Verification plan

1. **Blocks in TTLSim first, in dependency order**: shifter alone (all four
   types × 16 amounts × walking-ones data, plus the last-bit-out tap), '181
   cascade (the ADD/SUB carry sweep from the ISA doc), register file (write/read
   all 8 × both ports, then the WE-glitch test: /WE gated vs ungated with a
   settling input, demonstrating the trap), flag block (all five C sources,
   V truth table).
2. **GALs through the standard pipeline**: BlinkyJED equations → fuse map →
   WinCUPL diff → Arduino exhaustive test → T48 burn — nothing new, the whole
   point of having the toolchain.
3. **Integration in TTLSim per the video-arc order**: fetch loop (PC+IR only),
   unconditional branch, ALU + flags, conditional branch, memory, interrupts —
   each stage runnable and single-steppable before the next lands.
4. **First-program smoke test**: the reset stub, a 16-bit-constant build
   (MOV+ORH), a SEC/SBC subtract, a literal-pool load, a JAL/RET pair, and a
   UART poll loop — one program touching every class.

## Proposed board partition

Same modular format as Blinky: clock (existing) → PC+IR board → register file
board → shifter+ALU board (the big one) → memory board → interrupt/glue board.
Front panel taps: PC, IR, F, flags, state. Partition is a proposal, not a
ratification — it follows the video arc, but the shifter+ALU board's size may
force a split.

## Open decisions

1. **Shifter structure** — radix-4 '153 pair (default) vs radix-2 '157 ladder;
   settled by the TTLSim wiring of the last-bit-out tap.
2. **Z detect** — '688 pair vs discrete NOR tree; settled by final '688 demand
   elsewhere.
3. **SRAM organisation** — 2× 64K×8 vs 4× 32K×8; settled by procurement.
4. **MMIO window** — size and base (strawman: 16 words at 0xFFF0).
5. **GAL count** — does class/state steering fit the sequencer GAL's spare
   terms, or does it need its own 22V10? Settled by a BlinkyJED term count.
6. **Last-bit-out tap** — inter-stage tap vs dedicated '151 pair; falls out of
   decision 1.
7. **Board partition** — especially whether shifter and ALU share a board.

## Ratified in this document

- Four-state sequencer encoding and transition set as tabled.
- Single-edge state clocking; CLK-low-gated write strobes for the '670 array
  and the SRAM — the only level uses of the clock.
- 16× '670 register file (two full copies for the second read port), bank
  tri-state share decode-guaranteed exclusive.
- Write-address force-7 for link writes; three-source write-back mux
  (F / MEM / PC+1).
- Tri-state B-source legs with per-leg extension (zero vs sign) baked into the
  buffers.
- IE clears on reset — the machine boots with interrupts off.
- '182 in hardware, '04 ripple in simulation, validated by the ISA's carry
  sweep.
