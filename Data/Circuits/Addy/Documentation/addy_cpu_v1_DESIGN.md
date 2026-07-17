# Addy CPU Module v1 — Design

A **minimal 16-bit test CPU** built around two existing boards: the Blinky
Clock Module v3 (clock, stepping, halt, T-state counter, reset supervision)
and the Register File Module in its Thumby configuration (8 × 16, two read
ports). This board adds only what a CPU needs beyond those: instruction
memory, an instruction register, two operand registers, an adder-based ALU,
flags, an output register, and a GAL control decoder.

Working name "Addy" — everything it does, it does through the adder.

Design principles inherited from the host modules: one clock domain, every
register on the same CLK edge, no gated clocks, latched operands so the
register file's write-through can never race, and a uniform **three T-states
for every instruction** so the sequencer is nearly opcode-independent.

- 8 registers × 16 bits; **r7 is the program counter**
- Any instruction that writes r7 is a jump; there are no dedicated jump opcodes
- 3-address register ALU ops (`ADD rd, rs, rt`)
- 8-bit immediates carried in every instruction word
- Program in EEPROM, 16-bit words, up to 32 K words addressable
- ~30 ICs, 74HC, 5 V

---

## Programmer's model

### Registers

| Reg | Role |
|---|---|
| r0–r6 | general purpose, 16 bits |
| r7 | program counter — readable and writable like any register |

Reading r7 as an operand yields **PC + 1** (the address of the next
instruction), because the operand is latched after the PC writeback — see
Sequencing. This is deterministic and is the intended base for relative
jumps.

Flags: **Z** (result was zero) and **C** (adder carry-out), latched by every
class-00 and class-01 operation, held by everything else. v1 hardware stores
C but no opcode consumes it yet; carry conditionals land later in class-11
spare rows, as GAL-only changes.

### Instruction word

One 16-bit word per instruction, fixed fields:

```
 15 14 | 13 12 11 | 10  9  8 |  7  6  5 |  4  3 |  2  1  0
 class |    op    |    rd    |    rs    | rsvd  |    rt
                             |           imm8 (IR[7:0])    |
```

- **rd** (IR[10:8]) — destination / read-modify-write register, always here
- **rs** (IR[7:5]) — port A source for register ops
- **rt** (IR[2:0]) — port B source for register ops
- **imm8** (IR[7:0]) — immediate, aliases rs/rt; zero-extended to 16 bits

Register ops ignore imm8; immediate ops ignore rs/rt. IR[4:3] are reserved
(future shift count / condition field).

### Opcode table

**Class 00 — register ALU.** Operands rs (port A) and rt (port B).

| IR[15:11] | Mnemonic | Action | Flags |
|---|---|---|---|
| 00000 | ADD rd, rs, rt | rd ← rs + rt | Z C |
| 00001 | SUB rd, rs, rt | rd ← rs − rt | Z C |
| 00010 | MOV rd, rs | rd ← rs | Z C |
| 00011 | — | spare (logic ops if a logic unit is ever added) | |
| 00100 | CMN rs, rt | flags ← rs + rt, no write | Z C |
| 00101 | CMP rs, rt | flags ← rs − rt, no write | Z C |
| 00110 | TST rs | flags ← rs, no write | Z C |
| 00111 | — | spare | |

**Class 01 — immediate ALU.** Operands rd (port A) and imm8.

| IR[15:11] | Mnemonic | Action | Flags |
|---|---|---|---|
| 01000 | ADDI rd, imm8 | rd ← rd + imm | Z C |
| 01001 | SUBI rd, imm8 | rd ← rd − imm | Z C |
| 01010 | LDI rd, imm8 | rd ← imm | Z C |
| 01011 | ADDIH rd, imm8 | rd ← rd + (imm ≪ 8) | Z C |
| 01100 | — | spare | |
| 01101 | CMPI rd, imm8 | flags ← rd − imm, no write | Z C |
| 01110–01111 | — | spare | |

**Class 10 — conditional ops.** WE gated by the flag; flags not updated.

| IR[15:11] | Mnemonic | Action |
|---|---|---|
| 10000 | MOVZ rd, rs | rd ← rs if Z |
| 10001 | MOVNZ rd, rs | rd ← rs if not Z |
| 10010 | LDIZ rd, imm8 | rd ← imm if Z |
| 10011 | LDINZ rd, imm8 | rd ← imm if not Z |
| 10100 | ADDIZ rd, imm8 | rd ← rd + imm if Z |
| 10101 | ADDINZ rd, imm8 | rd ← rd + imm if not Z |
| 10110 | SUBIZ rd, imm8 | rd ← rd − imm if Z |
| 10111 | SUBINZ rd, imm8 | rd ← rd − imm if not Z |

With rd = r7 the lower four are absolute conditional jumps (page 0) and the
upper four are **relative conditional branches** — ±255 reach from anywhere,
relocatable. Carry-conditional variants, when a consumer for C arrives, take
class-11 spares.

**Class 11 — special.**

| IR[15:11] | Mnemonic | Action |
|---|---|---|
| 11000 | OUT rs | LED register ← rs |
| 11001 | IN rd | rd ← input switches (8 bits, zero-extended; closed = 1) |
| 11111 | HLT | assert /HALTREQ; clock module freezes the machine |
| others | — | spare |

**HLT is deliberately all-ones:** an erased EEPROM reads 0xFFFF, so a
runaway PC into blank memory freezes the machine with the panel HALT LED
lit instead of executing noise.

### Encoding regularities (these are the decoder)

Within classes 00 and 01:

- **IR[13] = suppress write** — CMN, CMP, TST, CMPI all sit in IR[13]=1 rows
- **IR[11] = subtract** — SUB, CMP, SUBI, CMPI (exception: within the
  IR[12]=1 pair of class 01, IR[11] distinguishes LDI from ADDIH instead)
- **IR[12] = operand kill/steer** — class 00: B off (MOV, TST);
  class 01: LDI (A off, imm8 on) / ADDIH (A on, imm-high on)

Class 10: **IR[11] = polarity** (0 = Z, 1 = not Z) throughout.
**IR[13] = 0** — move-style: IR[12] = source (0 = register via port A,
1 = imm8). **IR[13] = 1** — immediate arithmetic on rd: IR[12] = subtract.

### Idioms

| Intent | Instruction |
|---|---|
| JMP addr | LDI r7, addr |
| JZ / JNZ addr (page 0) | LDIZ / LDINZ r7, addr |
| Conditional branch ±255 | ADDIZ/ADDINZ r7, n · SUBIZ/SUBINZ r7, k+1 |
| Computed jump | MOV r7, rs |
| Skip n forward | ADDI r7, n (lands at PC+1+n; r7 reads as PC+1) |
| Jump back to PC−k | SUBI r7, k+1 |
| Computed relative | ADD r7, r7, rt |
| NOP | MOV r0, r0 (= 0x1000) |
| INC / DEC r | ADDI / SUBI r, 1 |
| 16-bit constant | LDI r, low ; ADDIH r, high |
| Conditional select | MOVZ / MOVNZ into any register |

There is no ADDI with a negative immediate — imm8 is zero-extended, so
subtracting is always SUBI.

---

## Datapath

```
                 WADDR◄──┐ D[15:0]◄────────────────────────────┐
              ┌──────────┴─────────┐                           │
   AADDR ────►│    Register File   │◄──── BADDR = IR[2:0]      │
              │  (Thumby 8×16 ×2)  │                           │
              └───┬────────────┬───┘                           │
                 QA            QB                              │
        ┌─────────┤             │                              │
        ▼         ▼             ▼                              │
     EEPROM    A '574        B '574 ─┐                         │
     addr        │ /OE         /OE   │                         │
        │        ▼                   ▼                         │
        ▼      A bus              B bus ◄── imm8 '541          │
     IR '377   (10 k pulldowns)      ▲  ◄── imm-high '541      │
        │        │                   │  ◄── input '541         │
        └─►GALs  │             4× '86 XOR ◄─ SUBTRACT          │
                 ▼                   ▼                         │
              ┌──────────────────────────┐                     │
              │  4× '283 adder   ◄─ Cin  │                     │
              └────────────┬─────────────┘                     │
                           │ result bus ──────────────────────►┘
                           ├──► zero detect (2× '688) ─► Z ─► flags '74
                           └──► LED register (2× '377)
```

### The write-through fix: latched operands

The register file's storage is transparent-latch with write-through, so Q of
the register being written follows D during the write window. A
combinational loop Q → ALU → D would race. The loop is broken **on the input
side**: two edge-clocked 16-bit operand registers, **A** (fed by QA) and
**B** (fed by QB). D is only ever driven by the adder from A/B/immediates —
all edge-clocked or static sources — never live from Q. This holds for every
instruction including `ADD r7, r7, r7`.

A and B are plain '574s **clocked on every CLK edge, no enables**. This is
safe by construction: each is re-latched with the correct value on the edge
before the state that consumes it, and the garbage it captures on other
edges is never used. No gated clocks, per house rules.

### Operand buses

Both ALU input buses carry 10 k pulldowns (matching register-file policy),
so **a disabled bus reads zero** — this is how MOV, LDI, TST and the PC
increment get their "+ 0" operand without any multiplexers.

- **A bus**: one source — the A '574 (/OE from GAL). Disabled for LDI,
  LDIZ/LDINZ, IN, and during reset.
- **B bus**: four enable-selected sources, at most one on:
  - B '574 — register operand rt
  - imm8 '541 — IR[7:0] onto bits 7:0 (bits 15:8 pulled down: zero-extend)
  - imm-high '541 — IR[7:0] onto bits **15:8** (bits 7:0 zero) — the ADDIH
    source; the low byte of the addend is zero, so nothing carries into or
    corrupts bits 7:0 of rd
  - input '541 — 8 switches onto bits 7:0, zero-extended (IN). **Polarity
    contract: active-high** — switches pull to VCC when closed, 10 k SIP
    pulldowns define open bits low, so closed = 1, open = 0. The ISA
    self-test's stage 12 expects the bank set to 0xA5 (bits 0/2/5/7 closed).

### ALU: adder only

Four **74HC283** ripple-rippled to 16 bits. Subtraction is two's complement:
the B bus passes through four **74HC86** XOR packages whose common second
input is SUBTRACT, which also drives Cin. There is no logic unit and no
'181 — every opcode reduces to an add (see the module-selection note at the
end).

**Zero detect**: two **74HC688** comparators against ground on the result
bus; the two equal-outputs combine in the GAL to form Z_new. **Flags**: one
'74, clocked every edge like everything else, with a recirculating input mux
(½ '157): FLAGEN selects Z_new/Cout when a flag-setting op completes,
otherwise the flop's own Q. Carry-out of the top '283 is latched in the
other half from day one, even though nothing reads it yet.

### Instruction register

2× **74HC377** (clock-enable, no gated clock). /CE is active only during T0,
so IR loads exactly once per instruction, at the fetch edge. IR outputs feed
the GALs, the address steering, and the two immediate '541s.

### Output register

2× **74HC377** driving 16 LEDs; /CE active on T2 of an OUT. This is the
machine's one output device in v1.

---

## Address steering

Here the choice of **r7** (not r0) as PC pays off: 7 is binary 111, so
"select the PC" is not a mux — it is **forcing the address bits high**
through OR gates.

- **BADDR = IR[2:0], hardwired.** No mux at all. Port B is only sampled at
  the edge ending T1, by which time IR is the current instruction; whatever
  port B shows during T0 is never latched.
- **AADDR** = (one '157: rd vs rs) then per-bit OR with FETCH → 111 during
  T0. The '157 selects **rs** for class 00, class 10 register-source ops,
  and OUT; **rd** for class 01 (read-modify-write) — one GAL select line.
- **WADDR** = rd, per-bit OR with WSEL7 (= T1 + RST) → 111 for the PC
  writeback and the reset vector.

Total steering hardware: one '157 and six OR gates (2× '32).

---

## Sequencing

Three T-states per instruction, identical for every opcode. The clock module
owns the T counter; this board drives **/TRST during T2**, so T runs
0 → 1 → 2 → 0 forever. Only T[1:0] are consumed (T1 pin = state 1, T2 via
the T[1] bit); the module's FETCH output (T = 0) is used directly as the
fetch-phase select and the IR clock enable. Registers clock on the edge that
**ends** a state; the register file commits writes during the **low half**
of a state.

### T0 — fetch

AADDR forced to 7; QA = PC drives the EEPROM address pins directly (no MAR —
PC is stable all state). All bus sources off, WE low. On the ending edge:
**IR ← EEPROM**, **A ← QA (= PC)**.

### T1 — PC writeback ∥ operand fetch

- Write side: A drives the adder, B bus empty (zero), **Cin = 1** → D =
  PC + 1, settled early in the high phase. WADDR forced to 7, **WE high**;
  the write commits during CLK-low — the register-file contract verbatim.
- Read side: AADDR/BADDR now show the IR fields; QA/QB settle within
  ~70 ns. On the ending edge: **A ← QA, B ← QB** (the operands).

Write-through lands here deterministically: an operand register equal to 7
reads the in-flight PC + 1. That is the documented "r7 reads as next
address" rule — a feature, not a race.

### T2 — execute + writeback

Per-opcode enables: A on/off, one B-bus source or none, SUBTRACT/Cin. The
adder result drives D, settling in the high phase. WADDR = rd; **WE =
write-op · condition** during CLK-low. On the ending edge: flags latch (if
flag-setting), LED register latches (if OUT). **/TRST asserted throughout**
→ T returns to 0. /HALTREQ asserted throughout if HLT.

### Control grid

| Signal | T0 | T1 | T2 |
|---|---|---|---|
| AADDR | 7 (forced) | rs / rd | don't care |
| BADDR | IR[2:0] always | IR[2:0] | don't care |
| WADDR | don't care (WE=0) | 7 (forced) | rd |
| IR /CE | **active** | — | — |
| A, B clocks | every edge | every edge | every edge |
| A /OE | off | **on** | per opcode |
| B-bus source | none | none | per opcode |
| SUBTRACT | 0 | 0 | per opcode |
| Cin | 0 | **1** | = SUBTRACT |
| WE | 0 | **1** | write-op · cond |
| Flags mux | recirculate | recirculate | Z/C if flag op |
| LED /CE | — | — | OUT only |
| /TRST | — | — | **asserted** |
| /HALTREQ | — | — | HLT only |

### Worked example — `ADD r3, r1, r2` at address 0x0010

- T0: QA = 0x0010 → EEPROM → IR; A ← 0x0010
- T1: r7 ← 0x0011; ports read r1, r2; edge: A ← r1, B ← r2
- T2: D = r1 + r2; WE writes r3; flags latch; /TRST → next fetch at 0x0011

A taken and a not-taken branch are both exactly three cycles; NEXT INSTR on
the panel always advances exactly one program row.

---

## Reset — the PC vector

The register file has no reset input, so r7 is garbage at power-up. The
reset vector is implemented **in the decoder**: while RST (H2 pin 3) is
asserted, the GAL forces the T0-independent state *WE = 1, WADDR = 7 (via
WSEL7), all bus sources off, Cin = 0* → D = 0, so **r7 ← 0** commits on any
CLK-low phase that occurs inside the reset window. Flag and LED updates and
/HALTREQ are suppressed by ·/RST.

Operationally:

- **In RUN**: hold RESET for at least one clock period (~⅓ s at panel rate,
  instant at can rates) so the window contains a full CLK-low phase. Release;
  the machine restarts from address 0.
- **In STEP / at power-on**: if CLK idles low between steps (expected — the
  panel shows one flash per step), the write window is already open during
  RST and r7 clears with no cycles needed; RST release self-times through
  the module's synchronizer. **Verify CLK idle polarity at bring-up**; if it
  idles high, the fallback is one NEXT CYCLE tap while holding RESET, or a
  brief reset-in-RUN before stepping.

RESET does not disturb the mode, per the clock module's design; "STEP MODE,
then RESET, then step" remains the way to inspect a program from the top.

## Halt

`/HALTREQ = HLT · T2 · /RST`, driven onto H2 pin 6. The clock module
edge-detects entry, freezes CLK mid-T2, and lights HALT. PC already points
past the HLT (incremented at T1), so any resume — NEXT CYCLE, NEXT INSTR, or
a RUN button — continues cleanly with the following instruction. Software
breakpoints are therefore just HLT instructions patched into EEPROM.

---

## Control decode — two GAL22V10s

Inputs to both: T[1:0], RST, IR[15:11], plus Z to GAL1 and the two '688
equal-outputs to GAL2. All outputs combinational; the only registered state
on the whole CPU board is IR, A, B, flags, and the LED register.

Shorthand: `c00 = /IR15·/IR14`, `c01 = /IR15·IR14`, `c10 = IR15·/IR14`,
`c11 = IR15·IR14`; `LDI = c01·IR12·/IR11`; `ADDIH = c01·IR12·IR11`;
`OUT = c11·/IR13·/IR12·/IR11`; `IN = c11·/IR13·/IR12·IR11`;
`HLT = c11·IR13·IR12·IR11`; `COND = Z⊕IR11` (satisfied condition).

**GAL1 — sequencer / ALU control**

```
WE       = RST + T1 + T2·[ (c00 + c01)·/IR13 + c10·COND + IN ]
SUBTRACT = T2·[ IR11·( c00 + c01·/IR12 ) + c10·IR13·IR12 ]·/RST
CIN      = T1·/RST + SUBTRACT
WSEL7    = T1 + RST                        ; OR onto WADDR bits
ASEL_RS  = c00 + c10·/IR13·/IR12 + OUT     ; '157: rs (else rd)
TRST     = T2                              ; (active-low pin: /TRST = /T2)
HALTREQ  = HLT·T2·/RST                     ; active-low open assertion
FLAGEN   = T2·(c00 + c01)·/RST             ; flags recirculate mux
```

**GAL2 — bus-source enables** (all active-low at the pins)

```
AOE   = T1 + T2·[ c00 + c01·/LDI + c10·(/IR12 + IR13) + OUT ]
BOE   = T2·c00·/IR12
IMMOE = T2·[ c01·(/IR12 + LDI) + c10·(IR12 + IR13) ]
IMHOE = T2·ADDIH
INOE  = T2·IN
LEDCE = T2·OUT·/RST
Z_NEW = EQ_H · EQ_L                        ; from the two '688s → flag mux
```

IR /CE comes from FETCH (one inversion, folded into a spare GAL pin).
Everything fits with pins and product terms to spare; the encoding
discipline is why — most terms are two or three literals.

---

## Program memory

2× **AT28C256** side by side for the 16-bit instruction word, addressed by
QA[14:0], outputs permanently enabled into the IR inputs (they drive nothing
else, and IR only samples them at the fetch edge). 32 K words of program
space.

Absolute immediate jumps (`LDI r7`) reach addresses 0–255. **Relative jumps
reach ±255 from anywhere**: `ADDI r7, n` lands at PC+1+n (skip n
instructions) and `SUBI r7, n` at PC+1−n (`SUBI r7, 1` is jump-to-self; a
k-instruction loop closes with `SUBI r7, k+1`). imm8 is zero-extended, so
direction selects the opcode. Loops and local control flow therefore work
identically in every page. Conditional control flow has the same reach:
LDIZ/LDINZ for page-0 absolute targets, ADDIZ/ADDINZ/SUBIZ/SUBINZ on r7 for
±255 relative. Arbitrary far targets, conditional or not: compose in a
register (`LDI` + `ADDIH`), then `MOV`/`MOVZ`/`MOVNZ` `r7, rs`.

---

## Timing

Everything is bounded by the register-file contract: **D must settle within
CLK-high; the low phase belongs to the write.** Datasheet-maximum HC at
5 V:

| Path | Worst case |
|---|---|
| T0: QA (addr change, bank crossing) → EEPROM (150 ns part) → IR setup | ~250 ns high phase |
| T1/T2: A or B /OE → '86 → 16-bit ripple carry → D setup | ~150–180 ns high phase |
| Write window (register-file internal) | ~100 ns low phase |

So ~350 ns total, ≈ 2.5–3 MHz ceiling at 50/50 duty — comfortably inside
the register file's own ~6.5 MHz write ceiling, with the EEPROM as the
long pole. Asymmetric duty (long high, short low) buys headroom exactly as
the register-file manual recommends. At panel rates all of this is
invisible; nothing in the design depends on faster-than-datasheet parts.

The register file's "nothing edge-sensitive on Q" rule is respected: QA/QB
feed only D-inputs and address pins, clocked from CLK; the bank-handover
blink can never produce a false edge.

---

## Module integration

**Clock module** (headers per its manual):

| Pin | Use |
|---|---|
| H2.2 CLK | machine clock — CPU registers, register-file CLK |
| H2.3 RST | GAL input (reset vector) |
| H2.6 /HALTREQ | driven by GAL1 (HLT) |
| H3.2–.3 T0,T1 | T[1:0] state bits |
| H3.6 FETCH | fetch-phase select and IR clock enable |
| H3.7 /TRST | driven by GAL1 during T2 |

**Register file**: Thumby build — 16× '670, JW/JA/JB = 8R,
JOEA/JOEB = GND (both ports permanently enabled; nothing else shares QA/QB).
WADDR[3]/AADDR[3]/BADDR[3] strapped low at the connector, D from the result
bus, WE and CLK from above. Optional D/WADDR buffers stay zero-ohm-bypassed
at 16 chips.

## Parts list (CPU board)

| Function | Parts |
|---|---|
| Instruction register | 2× 74HC377 |
| A, B operand registers | 4× 74HC574 |
| imm8 / imm-high / input buffers | 3× 74HC541 |
| Adder | 4× 74HC283 |
| Subtract XOR | 4× 74HC86 |
| Zero detect | 2× 74HC688 |
| Flags | 1× 74HC74 + 1× 74HC157 (recirc mux, 2 sections spare) |
| Address steering | 1× 74HC157 + 2× 74HC32 |
| Control | 2× GAL22V10 (ATF22V10C) |
| Program memory | 2× AT28C256 |
| Output register + LEDs | 2× 74HC377 |

**≈ 30 ICs.** 10 k pulldown networks on both operand buses; 0.1 µF per
socket per house policy; every unused input tied.

## Bring-up

Staged so each addition is observable with the panel alone:

1. **Sequencer dry run.** GALs + clock module only, IR inputs strapped to
   NOP (0x1000). STEP through and scope the grid: WE pulses on T1/T2,
   /TRST on T2, FETCH lap of exactly three states.
2. **PC alive.** Add register file, A register, adder. Hold RESET (r7 ← 0),
   then step: WE at T1 writes PC+1 each lap. LEDs on the register file's QA
   show the address counting during every T0.
3. **First fetch.** Add IR + EEPROMs programmed all-NOP. The machine now
   genuinely executes: three flashes of CLK per address, forever.
4. **First program.** `LDI r0,0 / OUT r0 / ADDI r0,1 / LDI r7,1` —
   hex `5000 C000 4001 5701` from address 0. A binary counter on the LED
   register, one count per four instructions. This exercises fetch, PC
   writeback, immediate path, register write, OUT, and jump.
5. **Flags and branches.** `LDI r0,10 / SUBI r0,1 / SUBINZ r7,2 / HLT` —
   hex `500A 4801 BF02 FFFF` — counts down and freezes with HALT lit;
   NEXT INSTR walks the loop one line at a time. Exercises Z, conditional
   WE, relative branching, and the /HALTREQ path. (Swap the branch for
   `LDINZ r7,1` = `9F01` to test the absolute form; the SUBINZ version is
   relocatable — burn it at any address and it still works.)
6. **The corners.** `ADD r7,r7,r0` relative jump (r7-as-operand = PC+1
   rule); IN → OUT switch echo; verify STEP-mode reset behaviour (CLK idle
   polarity note above).

## Why '283 + '86 and not a '181

Recorded so future-us doesn't relitigate it: the '181 offers logic ops and
fewer packages, but does not exist in 74HC — mixing LS levels into an HC
machine means pullups or HCT patches, and its S/M control codes cost GAL
product terms that the '283's single SUBTRACT wire doesn't. This machine's
job needs arithmetic only; if logic ops are ever wanted, a separate
enable-selected logic unit joins the result bus and the class-00 spare rows
(IR[13]/IR[11] already positioned to mean the right things) take the
opcodes.
