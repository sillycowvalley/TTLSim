# 8080A CPU — Instruction Set Analysis

Pure Intel 8080A. No Z80 extensions, no shadow registers, no index registers.

Byte-fetched, variable-length instructions. One machine cycle per fetch,
memory access or I/O operation; each machine cycle is 3–5 T-states. The
longest machine cycle in the whole instruction set is **5 T-states**, and the
longest instruction is 18 T-states (XTHL).

Cycle counts are the real 8080A figures throughout. The `M-cycles` column is
the decomposition, because that — not the total — is what the sequencer has
to produce.

**Target implementation:** register file module + counter module + a 16-bit
'181 ALU, clocked from the Blinky clock module.

---

## Assumptions about the counter module

The counter module's specification was not available when this was written.
The design below assumes a 16-bit synchronous counter board with:

- parallel load from a 16-bit input,
- synchronous count enable,
- **up/down control**,
- three-state output buffering onto the address bus,
- asynchronous clear.

If the board is up-only ('161-based rather than '191/'193-based), see
**Fallback: up-only counters** at the end — SP moves into the register file
and PUSH/POP get more expensive, but nothing breaks.

---

## Register placement

The 8080A's programmer-visible state does **not** all belong in the register
file. Three independent constraints from the module documents force the split.

| State | Lives in | Why |
|---|---|---|
| BC, DE, HL | Register file, slots 0–2 | 16-bit slots make INX/DCX/DAD single operations; two read ports make DAD a one-pass add |
| W, Z (temp) | Register file, slot 3 | Holds the immediate address during LDA/STA/JMP/CALL/LHLD |
| A | Discrete 8-bit register | See *write-port pressure* below |
| F | Discrete flag flip-flops | See *flag granularity* below |
| PC | Counter module | Increment must be free; also needs reset, which the file has none of |
| SP | Counter module | ±1 during PUSH/POP/CALL/RET must be free |

### Write-port pressure

The register file has **one write port**. Every 8080A ALU operation writes A
*and* F. If both live in the file they are either two writes (impossible in
the 4 T-states of `ADD r`) or one 16-bit AF slot write (which then can't do
partial flag updates — see below). Putting A and F outside the file removes
the conflict entirely and leaves the write port free for the 16-bit pairs.

This also mirrors the real 8080A, where A and the TMP/ACT registers sit
outside the B/C/D/E/H/L array.

### Flag granularity

The 8080A uses exactly four flag-write masks:

| Mask | Instructions |
|---|---|
| All five (S, Z, AC, P, C) | ALU ops, CMP, DAA, POP PSW |
| All but carry | INR, DCR |
| Carry only | DAD, RLC, RRC, RAL, RAR, STC, CMC |
| None | INX, DCX, MOV, MVI, loads, stores, jumps |

A byte-merge write into a 16-bit AF slot gives half-word granularity, which
does not cover "update S, Z, AC, P but leave C". Discrete flags do:

- **one '377** holding S, Z, AC, P — enable group 1
- **one '74 flip-flop** holding C — enable group 2

Two enables cover all four masks. STC/CMC drive the C flip-flop directly.
POP PSW needs a mux selecting the bus byte instead of the ALU-derived values
on both groups' inputs.

F's constant bits must be reproduced on PUSH PSW: bit 1 = 1, bits 3 and 5 = 0.
Tie them at the bus driver.

### PC as a counter, not a slot

Every M1 fetch increments PC. If PC were a file slot, every fetch would
consume the single write port and an ALU pass, colliding with the
instruction's own write in the same machine cycle. As a counter it is a free
count-enable pulse, and JMP/CALL/RET/PCHL/RST become parallel loads.

The register file also has **no reset** — its contents power up random. PC
must be resettable, which settles the question on its own.

---

## Register file configuration

**8 × 16, two read ports (16 × '670).** Jumpers JW/JA/JB = 8R,
JOEA/JOEB = GND.

Slot map:

| Slot | Contents |
|---|---|
| 0 | BC |
| 1 | DE |
| 2 | HL |
| 3 | WZ (temp) |
| 4–7 | unused / scratch |

**The bank boundary is address bit 2.** Crossing it costs ~70 ns instead of
~45 ns and puts the port through a brief high-impedance blink. With all four
live slots in bank 0, **no instruction ever crosses a bank boundary** — every
read is the fast path and the blink never happens in normal operation.
Keeping slots 4–7 for scratch that isn't touched mid-instruction preserves
this.

Populating both banks costs eight extra '670s for headroom that isn't yet
needed; a bank-0-only build is the minimum viable machine. If you do build
bank 0 only, remember the mirror-array rule — the second read port is a
complete mirror of whatever the primary array is, or nothing.

---

## The write-through hazard — and the fix

The register file is **asynchronous-read with write-through**: reading the
register currently being written returns the incoming data once the write
window opens.

Combined with the write contract — D and WADDR stable *before* CLK falls and
held until CLK rises — this makes any read-modify-write of a file slot a
combinational loop:

```
QA (read r) -> ALU -> D (write r) -> write-through -> QA changes -> ...
```

The loop closes the instant CLK falls, and it changes D during exactly the
window in which D must be held. It affects INR/DCR, INX/DCX, DAD, and the
byte-merge path of MOV r,r'.

**Fix: transparent operand latches on both read ports.**

- 4 × '373 (two 16-bit banks, one per read port)
- Latch enable = CLK. Transparent during CLK-high, closed during CLK-low.

The ALU then sees frozen operands across the whole write window, so D is
stable from CLK-fall to CLK-rise as the contract requires. **Cost: zero
T-states.**

Two secondary benefits:

1. The module's rule is *"Q ports feed combinational logic inputs only —
   never hang anything edge-sensitive on QA or QB."* A transparent latch is
   level-sensitive and therefore legal; a '374 would not be. Downstream of
   the '373s, edge-triggered parts are fine.
2. The bank-handover high-Z blink, if it ever does occur, happens early in
   the high phase while the latch is transparent and has settled long before
   CLK falls.

---

## Timing budget

| Item | ns |
|---|---|
| Register file read, within bank | 45 |
| Operand latch propagation | ~15 |
| 16-bit '181 + '182 lookahead | ~80 |
| Result mux / byte merge | ~15 |
| Setup to write path | ~20 |
| **Required CLK-high phase** | **~175** |
| **Required CLK-low phase** (module minimum) | **100** |
| **Minimum period** | **~275 ns → 3.6 MHz** |

A 2 MHz 8080A period is 500 ns. At 50/50 duty that is 250 ns high and 250 ns
low — comfortable margin on both halves. Asymmetric duty (long high, short
low) is available if the ALU path grows.

These are datasheet maxima across temperature; room-temperature parts run
meaningfully faster, and nothing here depends on that.

---

## Clock module integration

### T-state counting must be per machine cycle

The clock module's T counter is a 4-bit '161 — valid to 16 T-states. CALL is
17 and XTHL is 18, so **whole-instruction T counting overflows.**

Count T-states **within a machine cycle** instead. The longest machine cycle
is 5 T-states, well inside the counter, and a small M-cycle sequencer on the
CPU board tracks which machine cycle is in progress. `/TRST` is asserted
during the last T-state of each machine cycle.

Consequences:

- **FETCH becomes SYNC.** The 8080A asserts SYNC at T1 of *every* machine
  cycle, not just M1 — so per-machine-cycle counting makes the clock module's
  FETCH output line up exactly with real 8080A SYNC. This is more faithful
  than the alternative.
- **NEXT INSTR degrades to next-machine-cycle.** The clock module arms on
  FETCH, which now fires every machine cycle. Acceptable for debug and
  arguably more useful; if true instruction stepping is wanted, gate the
  module's FETCH input against the CPU's M1 signal externally.
- Breakpoints qualified with FETCH become machine-cycle breakpoints. Qualify
  with M1 as well for PC breakpoints.

### HALT parks CLK low

`CLK = CLK_pre · /HALT`, so a halted machine leaves CLK **low** — which is
the register file's write window, held open indefinitely. Nothing changes
while frozen so the latch simply re-writes the value it already holds, but
the margin is thin and it depends on the sequencer being genuinely static.

**Qualify WE with /HALT.** One gate, and it closes the window on halt.

### HLT semantics

The real 8080A exits HLT on an interrupt. If HLT asserts /HALTREQ and the
clock module stops the clock, the only exit is RESET or a front-panel RUN
press. That's a defensible simplification but it is a **stated deviation**,
not an oversight. Interrupt-driven HLT exit requires the clock module's halt
flop to be clearable from the CPU side, which the v3 header set does not
expose.

### Reset

`/RST` clears the CPU sequencer and the PC counter. The register file has no
reset — BC/DE/HL/WZ power up random and boot code must not assume otherwise.
SP is undefined on a real 8080A too, so this is faithful.

RESET while in RUN restarts free-running from 0000h. Press STEP MODE first,
or set a breakpoint on the reset vector.

---

## Datapath summary

```
                  +-- '373 operand latch A --+
  Register file --|                          |-- 16-bit '181 ALU --+
  (QA, QB)        +-- '373 operand latch B --+                     |
                                                                   |
  A register (discrete 8-bit) --------------------------------------+
                                                                   |
  Memory data in ---------------------------------------------------+
                                                                   |
                          +----------------------------------------+
                          |
                          +-- rotate mux ('157 x2)
                          +-- DAA correction (GAL22V10)
                          +-- byte merge ('157 x6)
                          |
                          +--> register file D / A register / memory data out
```

Address bus sources, buffered onto the bus with '244s (the file's Q ports are
**not** bus drivers — the module says so explicitly):

- PC counter
- SP counter
- Register file port B (HL for `M` references, WZ for direct addressing)

No MAR is needed: the file's read is combinational and stays valid as long as
BADDR is held, so port B can hold the address for the whole machine cycle
through its buffer.

---

## Group 1 — Register-to-Register MOV

| Mnemonic | Bytes | T | M-cycles | Notes |
|---|---|---|---|---|
| MOV r, r' | 1 | 5 | 5 | Byte select from port A, merge into destination half |
| MOV r, M | 1 | 7 | 4+3 | HL on port B → address bus, SRAM read, byte merge |
| MOV M, r | 1 | 7 | 4+3 | HL → address, byte select drives data out |

`MOV r, r'` where both registers live in the same 16-bit slot (e.g. `MOV B, C`)
reads the slot on port A, selects the source byte, and writes the slot back
with the other half unchanged — the unchanged half comes from the *latched*
port A value, which is why the operand latch is mandatory here.

**Byte merge cost:** 2 × '157 to select the source byte from a 16-bit port,
4 × '157 to steer it into either half while the other half passes the latched
old value. Six packages for what looks like the simplest instruction in the
set.

`MOV A, r` and `MOV r, A` route between the discrete A register and the file
and skip the source-select mux.

---

## Group 2 — 8-bit Immediate Load

| Mnemonic | Bytes | T | M-cycles |
|---|---|---|---|
| MVI r, n | 2 | 7 | 4+3 |
| MVI M, n | 2 | 10 | 4+3+3 |

Immediate byte arrives on the data bus and enters the merge path directly —
no ALU pass.

---

## Group 3 — 16-bit Immediate Load

| Mnemonic | Bytes | T | M-cycles |
|---|---|---|---|
| LXI B, nn | 3 | 10 | 4+3+3 |
| LXI D, nn | 3 | 10 | 4+3+3 |
| LXI H, nn | 3 | 10 | 4+3+3 |
| LXI SP, nn | 3 | 10 | 4+3+3 |

Two bytes assemble into WZ, then one 16-bit write to the target slot — or a
parallel load into the SP counter for `LXI SP`.

---

## Group 4 — 8-bit ALU

All operate on A. The '181 handles every function natively.

| Mnemonic | Bytes | T | M-cycles | Notes |
|---|---|---|---|---|
| ADD r / ADC r / SUB r / SBB r | 1 | 4 | 4 | Second operand from port B, latched |
| ANA r / ORA r / XRA r / CMP r | 1 | 4 | 4 | CMP suppresses the A write |
| ADD M … CMP M | 1 | 7 | 4+3 | Second operand from memory |
| ADI n … CPI n | 2 | 7 | 4+3 | Second operand from the immediate byte |

**No register-file write occurs at all** — A is discrete and the second
operand is read-only. The write port is idle for this entire group, which is
most of the instruction set.

### Flag derivation — the part the '181 does not give you free

- **Z is not the '181's A=B pin.** A=B is an open-collector AND of the F
  outputs and asserts when the result is all *ones* — 0xFF, not zero, with
  active-high data. You need a real 8-bit all-zeros detect ('30 plus an
  inverter, or a '688 against ground). On a 16-bit ALU it must mask the upper
  byte for 8-bit operations.
- **Carry polarity inverts twice.** With active-high operands the '181's Cn
  and Cn+4 are active *low*; and on subtract the 8080A's C is a **borrow**,
  the inverse of the adder's carry-out. Two inversions in opposite senses
  that cancel misleadingly on some test vectors. Test with `SUI 0` and
  `SUI 1` against A = 0.
- **AC is one existing pin.** The carry between bits 3 and 4 is the low
  slice's Cn+4 (or the corresponding '182 intermediate). Free.
- **P needs a '280** parity generator on the low byte of the result.
- **S** is result bit 7.

---

## Group 5 — 8-bit Increment / Decrement

| Mnemonic | Bytes | T | M-cycles | Notes |
|---|---|---|---|---|
| INR r | 1 | 5 | 5 | S, Z, AC, P updated; C **unchanged** |
| DCR r | 1 | 5 | 5 | Same flag rules |
| INR M | 1 | 10 | 4+3+3 | Read SRAM[HL], ±1, write back |
| DCR M | 1 | 10 | 4+3+3 | |

For a file register this is a read-modify-write of the same slot — the
canonical write-through loop. The operand latch is what makes it work.

Flag enable group 1 only. This is precisely the mask a 16-bit AF slot could
not have produced.

---

## Group 6 — 16-bit Increment / Decrement / Add

| Mnemonic | Bytes | T | M-cycles | Notes |
|---|---|---|---|---|
| INX B / D / H | 1 | 5 | 5 | Slot read → ALU +1 → slot write; no flags |
| INX SP | 1 | 5 | 5 | Counter count-up pulse; no ALU, no write port |
| DCX B / D / H | 1 | 5 | 5 | No flags |
| DCX SP | 1 | 5 | 5 | Counter count-down pulse |
| DAD B / D / H / SP | 1 | 10 | 4+3+3 | HL ← HL + rp; **C flag only** |

**DAD is the payoff for two read ports.** HL on port A, the source pair on
port B, one 16-bit add, one write. The real 8080A uses an internal temp and
two 8-bit passes.

`DAD SP` reads SP from the counter's output buffer rather than the file, so
the operand B mux needs an SP input.

Flag enable: none for INX/DCX, group 2 only for DAD.

---

## Group 7 — Direct Memory Load / Store

| Mnemonic | Bytes | T | M-cycles | Notes |
|---|---|---|---|---|
| LDA nn | 3 | 13 | 4+3+3+3 | Address into WZ, WZ → address bus |
| STA nn | 3 | 13 | 4+3+3+3 | |
| LHLD nn | 3 | 16 | 4+3+3+3+3 | L ← [nn], H ← [nn+1] |
| SHLD nn | 3 | 16 | 4+3+3+3+3 | |
| LDAX B / D | 1 | 7 | 4+3 | Address from port B |
| STAX B / D | 1 | 7 | 4+3 | |

LHLD/SHLD need WZ incremented between the two memory cycles — a read-modify-
write on slot 3, another operand-latch case.

---

## Group 8 — Stack Operations

| Mnemonic | Bytes | T | M-cycles | Notes |
|---|---|---|---|---|
| PUSH B / D / H | 1 | 11 | 5+3+3 | SP−−, store high; SP−−, store low |
| PUSH PSW | 1 | 11 | 5+3+3 | A and F; F's constant bits tied at the driver |
| POP B / D / H | 1 | 10 | 4+3+3 | Load low, SP++; load high, SP++ |
| POP PSW | 1 | 10 | 4+3+3 | Flag register inputs mux to the bus byte |
| SPHL | 1 | 5 | 5 | Parallel load SP from port B (HL) |
| XTHL | 1 | 18 | 4+3+3+3+5 | H ↔ [SP+1], L ↔ [SP] |

With SP in an up/down counter, every SP adjustment is a count pulse in the
same T-state as the memory access — no ALU, no write port. PUSH and POP land
on their real cycle counts without effort.

XTHL uses WZ to hold the popped value while HL is written out.

**POP PSW is the only path that writes flags from the data bus**, and it
needs both enable groups plus an input mux on the '377 and the C flip-flop.

---

## Group 9 — Control Transfer (Jumps)

| Mnemonic | Bytes | T | M-cycles | Notes |
|---|---|---|---|---|
| JMP nn | 3 | 10 | 4+3+3 | Parallel load PC from WZ |
| JZ / JNZ / JC / JNC | 3 | 10 | 4+3+3 | 10 T whether taken or not |
| JP / JM / JPE / JPO | 3 | 10 | 4+3+3 | |
| PCHL | 1 | 5 | 5 | Parallel load PC from port B |

Conditional jumps always fetch all three bytes and always cost 10 T-states —
only the PC load is suppressed. This is the easy case; conditional calls and
returns are not.

---

## Group 10 — Subroutine Call / Return

| Mnemonic | Bytes | T | M-cycles (taken) | M-cycles (not taken) |
|---|---|---|---|---|
| CALL nn | 3 | 17 | 5+3+3+3+3 | — |
| CZ / CNZ / CC / CNC | 3 | **11/17** | 5+3+3+3+3 | 5+3+3 |
| CP / CM / CPE / CPO | 3 | **11/17** | | |
| RET | 1 | 10 | 4+3+3 | — |
| RZ / RNZ / RC / RNC | 1 | **5/11** | 5+3+3 | 5 |
| RP / RM / RPE / RPO | 1 | **5/11** | | |
| RST n | 1 | 11 | 5+3+3 | — |

**Conditional returns are 5/11, not 5/10.** RET is 10 but a taken Rcc is 11 —
the extra T-state is the condition evaluation in M1, which stretches M1 from
4 to 5. This is the one place the 8080A charges you for the test.

**The sequencer must be able to abort remaining machine cycles mid-instruction.**
A not-taken Ccc fetches its operand bytes and then stops; a not-taken Rcc
ends after M1. This is a real requirement, distinct from ordinary sequencing,
and it is the main reason the M-cycle counter needs a conditional terminate
input rather than a fixed per-opcode length.

RST loads PC from a constant derived from opcode bits 5–3 (`00 nnn 000` →
`0000 0000 00nn n000`). Wiring, not logic.

---

## Group 11 — Exchange

| Mnemonic | Bytes | 8080A T | Ours | M-cycles |
|---|---|---|---|---|
| XCHG | 1 | 4 | **5** | 5 |

**This is the one place cycle parity breaks.**

DE ↔ HL is two 16-bit writes and the register file has one write port. With
M1's execute phase being a single T-state, 4 T-states cannot do it.

Implementation: read DE on port B into a 16-bit hold register (2 × '374)
while simultaneously writing DE ← HL from port A; the next T-state writes
HL ← hold. Two write windows, so M1 stretches from 4 to 5 T-states.

Cost: 2 × '374 and one T-state. The alternative — an address-remap flip-flop
that swaps DE/HL decoding — buys back that T-state but introduces hidden
architectural state that every implicit HL reference (M addressing, DAD H,
PCHL, SPHL, XTHL) must honour, and it makes a front-panel dump of the
register file show mislabelled contents. Not worth it for one T-state.

---

## Group 12 — Rotate and Shift

| Mnemonic | Bytes | T | M-cycles | Notes |
|---|---|---|---|---|
| RLC | 1 | 4 | 4 | A rotate left; bit 7 → C and bit 0 |
| RRC | 1 | 4 | 4 | A rotate right; bit 0 → C and bit 7 |
| RAL | 1 | 4 | 4 | Rotate left through carry |
| RAR | 1 | 4 | 4 | Rotate right through carry |

**Cheaper than a shift register.** `RAL` is literally `A + A + Cn` — the '181
does it natively and the carry lands in the right place at both ends. `RLC`
is the same with A7 fed into Cn.

The right rotates are a fixed permutation of the result bus (pure wiring),
selected by an 8-bit 2:1 mux (2 × '157) with one extra mux bit choosing
between A0 and C for the incoming bit 7.

Loading A into a '194 pair and shifting would cost T-states that the 4-cycle
budget does not have. Don't.

Flag enable group 2 only.

---

## Group 13 — Accumulator / Flag Specials

| Mnemonic | Bytes | T | M-cycles | Notes |
|---|---|---|---|---|
| CMA | 1 | 4 | 4 | '181 NOT function; no flags |
| CMC | 1 | 4 | 4 | Toggle the C flip-flop |
| STC | 1 | 4 | 4 | Set the C flip-flop |
| DAA | 1 | 4 | 4 | BCD adjust — see below |

### DAA is not hard

Inputs are A[7:0], C and AC — **exactly 10 signals**. Outputs are the 8-bit
correction value plus the new C and new AC — **exactly 10 signals**. That is
a **GAL22V10** with no pins to spare and no logic to spare either: 12
dedicated inputs, 10 I/O.

The correction is then applied through the existing ALU (`A ← A + correction`)
using the DAA mux input on operand B. A GAL22V10 at ~15 ns fits inside the
high phase alongside the ALU with room; a ROM lookup at 150 ns would not.

Note the 8080A's DAA is add-only correct — after a subtract it produces
garbage. That is the reason the Z80 added the N flag. Matching the 8080A
means you don't have to care, which removes the hardest part of the problem.

**Move DAA from "defer" to "one GAL".**

---

## Group 14 — I/O

| Mnemonic | Bytes | T | M-cycles | Notes |
|---|---|---|---|---|
| IN port | 2 | 10 | 4+3+3 | Port number duplicated on A15–A8 and A7–A0 |
| OUT port | 2 | 10 | 4+3+3 | |

The port byte drives both halves of the address bus, matching the real 8080A.

---

## Group 15 — Control

| Mnemonic | Bytes | T | M-cycles | Notes |
|---|---|---|---|---|
| NOP | 1 | 4 | 4 | |
| HLT | 1 | 7 | 4+3 | Assert /HALTREQ; see HLT semantics above |
| DI | 1 | 4 | 4 | IFF ← 0 |
| EI | 1 | 4 | 4 | IFF ← 1, **effective after the next instruction** |

**EI's one-instruction delay is a sequencer behaviour, not a flip-flop.** It
is what makes `EI ; RET` work at the end of an interrupt service routine.
Implement as a second flip-flop: EI sets a pending bit, and the pending bit
transfers to IFF at the end of the *following* instruction's M1.

---

## Interrupts

DI and EI are trivial flip-flop writes, but without the rest of the mechanism
they are decorative. A working 8080A interrupt needs:

- an **INT input** sampled at the end of each instruction,
- an **INTA machine cycle** — an M1-like cycle that asserts INTA instead of
  /MEMRD and takes its opcode from the interrupting device rather than memory,
- **RST-from-bus**: the device supplies an RST opcode, which executes
  normally (PUSH PC, PC ← vector),
- **IFF cleared automatically** on acknowledge,
- **HLT exit** on interrupt, which the clock module's halt flop does not
  currently support from the CPU side.

Deferring interrupts entirely is reasonable for a first machine. Deferring
them while leaving DI/EI in the decode is not — either implement the
mechanism or mark DI/EI as documented no-ops.

---

## Opcode map coverage

The 8080A defines **244 of 256 opcodes**. The 12 undefined ones are:

`08 10 18 20 28 30 38 CB D9 DD ED FD`

On real silicon these alias to NOP (the `x8`/`x0` group), JMP (`CB`), CALL
(`DD`, `ED`, `FD`) and RET (`D9`). Choose deliberately:

- **NOP** for all twelve — simplest, matches the `x8`/`x0` group's real
  behaviour, diverges on the other five.
- **Trap** — assert /HALTREQ and stop. Far better for debugging, and the
  clock module already provides the mechanism. Recommended.

State the choice in the decode source. An undefined opcode falling through a
GAL's default term into whatever the last matched product said is the classic
way to lose an afternoon.

---

## Reset state

| State | On reset |
|---|---|
| PC | 0000h (counter clear) |
| IFF | 0 |
| Flags | Undefined — clear them anyway; it costs nothing |
| A | Undefined |
| BC, DE, HL, WZ | Undefined (register file has no reset) |
| SP | Undefined on real 8080A; boot code must LXI SP first |

The clock module releases RST synchronously two clock edges after the
supervisor, so the first M1 starts cleanly.

---

## Hardware requirements summary

| Feature | Status | What's needed |
|---|---|---|
| 8-bit ALU ops | Easy | 16-bit '181 chain, low byte |
| 16-bit pair ops | Easy | Two read ports + 16-bit '181 |
| **Operand latches** | **Required** | 4 × '373 — fixes the write-through loop |
| Byte merge | Moderate | 6 × '157 |
| S flag | Easy | Result bit 7 |
| **Z flag** | Moderate | Real all-zeros detect — **not** the '181 A=B pin |
| C flag | Easy | '181 carry-out, inverted; borrow sense on subtract |
| AC flag | Easy | Existing carry-between-bits-3-and-4 pin |
| P flag | Easy | 1 × '280 on the low byte |
| Flag write masks | Easy | '377 (S,Z,AC,P) + '74 (C), two enables |
| Rotates | Easy | '181 for left, 2 × '157 + wiring for right |
| **DAA** | Easy | 1 × GAL22V10 — exact pin fit |
| XCHG | Moderate | 2 × '374 hold register, costs one T-state |
| Stack PUSH/POP | Easy | Up/down SP counter |
| Conditional abort | Moderate | M-cycle counter with terminate input |
| Interrupts | Hard | INTA cycle, RST-from-bus, HLT exit path |

---

## Fallback: up-only counters

If the counter module is '161-based (up-only, no down count), SP cannot live
there — PUSH and CALL need decrement.

Move SP to **register file slot 4** and accept:

- SP adjustments become read → ALU ±1 → write, consuming the write port and
  an ALU pass.
- Slot 4 is in **bank 1**, so every SP access crosses the bank boundary:
  70 ns instead of 45 ns, plus the high-Z blink. The operand latch already
  covers the blink, but the read is on the slow path.
- PUSH's two decrements and POP's two increments now need write windows.
  PUSH is 5+3+3: M1 has the spare T-states, but M2 and M3 are 3 T-states each
  and must each fit a memory access *and* an SP write. Tight but feasible,
  since the SP write and the memory access use different resources.
- Better: swap the slot map so SP takes slot 3 and WZ takes slot 4. WZ is
  touched less often per instruction than SP.

PC stays in the counter regardless — it only ever counts up, and reset
demands it.
