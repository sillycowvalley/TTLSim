# 8080A CPU — Instruction Set Analysis

**Design contract: 100% software compatibility with the Intel 8080A.
Hardware signals and instruction timing are explicitly *not* constrained.**

Any program that runs on a real 8080A must run here and produce identical
results — same registers, same flags, same memory, same I/O behaviour. How
many T-states it takes, what the bus looks like, and how many clock phases
there are is our business.

**Target implementation:** register file module + a 16-bit '181 ALU, clocked
from the Blinky clock module, plus PC/SP holding registers and a memory
subsystem still to be chosen (see Open questions).

---

## What the contract rules out of scope

These were live concerns under a cycle-accuracy mandate and are now closed:

| Retired | Why it no longer matters |
|---|---|
| Exact T-state counts | Not software-visible |
| M-cycle decomposition | Sequencer may take any shape |
| XCHG's 4-vs-5 T-state gap | No longer a divergence |
| SYNC status byte / 8228 compatibility | Bus-level only |
| READY / wait states, HOLD / HLDA | No DMA, fast SRAM |
| Two-phase 12 V / −5 V clocking | Single-phase 5 V throughout |

Cycle counts still appear in the tables below as **reference only** — they
document the real part, they do not bind the implementation.

### The one residual software-visible timing effect

Software that bit-bangs a protocol with cycle-counted delay loops (serial
without a UART, floppy timing) will produce different wall-clock delays.
Unavoidable, small class, and it is the only exception to "software
compatible" in the whole design.

---

## What the contract makes mandatory

These were deferrable before and are not now:

1. **All 244 defined opcodes**, exactly.
2. **The 12 undefined opcodes**, aliased to their real silicon behaviour.
3. **Interrupts** — INT, INTA, RST-from-bus, IFF, EI's delay.
4. **HLT exits on interrupt**, which forces a change to the halt design.
5. **All five flags exact**, including the awkward AC cases.
6. **DAA exact**, including its inability to clear carry.
7. **A byte-addressable 16-bit memory space**, with RAM wherever running code
   and data live (the ROM/RAM split is a decode decision — see Memory model).

Each is expanded below.

---

## HLT must move to the CPU

The clock module gates the clock: `CLK = CLK_pre · /HALT`. A CPU halted that
way is frozen and can never sample INT — so it can never leave HLT, which
breaks software compatibility outright.

**HLT becomes a sequencer spin state with the clock running.** The machine
sits in a state that performs no memory access, no register write and no PC
increment, and leaves on an accepted interrupt or on reset. HLTA (halt
acknowledge) is available externally if wanted.

The clock module's `/HALTREQ` input reverts to what it was designed for:
breakpoints, the HALT REQ button, and external comparator cards. It is a
**debug** facility and is no longer in the instruction path.

Consequence for debug: a machine sitting in HLT is *running* as far as the
clock module is concerned, so single-stepping through a HLT walks the spin
state rather than freezing. That is correct behaviour and matches how a real
8080A behaves under a front panel.

---

## Memory model

**Byte-addressable, read/write where it needs to be, 16-bit address space
(0x0000–0xFFFF).** PC, SP and all 16-bit pairs wrap 0xFFFF → 0x0000 naturally.

The **split between ROM and RAM is a later decode decision**, not a constraint
fixed here. 8080 software self-modifies and shares one address space between
code and data, so the region the running program lives in must be RAM — but
that does not mean the whole 64 K is RAM. The normal 8080-era shape is ROM low
(reset vector, monitor, boot code) with RAM above and I/O decoded into its own
space; where the boundaries fall is settled when the address bus is decoded.

That decision governs which memory boards apply. A ROM in low memory is a
legitimate — and conventional — part of the map, so a word-store like the
Code & IR module is not ruled out on principle the way a single unified-RAM
mandate would rule it out; whether it fits depends on the fetch-path width and
the map chosen at decode time. The register file and counter modules are
unaffected either way.

---

## Register placement (unchanged)

Internal organisation is invisible to software, so the split derived under the
previous mandate stands — it was driven by the register file's single write
port and write-through behaviour, not by timing.

| State | Lives in | Why |
|---|---|---|
| BC, DE, HL | Register file, slots 0–2 | 16-bit slots; two read ports make DAD one pass |
| W, Z (temp) | Register file, slot 3 | Immediate addresses, and the XCHG temp |
| A | Discrete 8-bit register | Frees the single write port across the whole ALU group |
| F | Discrete flag flip-flops | Needs bit granularity a slot write cannot give |
| PC | A counting register outside the file | Increment on fetch must be free, and it needs reset — which the file lacks |
| SP | A counting register, or the file (open) | Wants free ±1; see Open questions for the two options |

Register file: **8 × 16, two read ports (16 × '670)**, JW/JA/JB = 8R,
JOEA/JOEB = GND. All four live slots sit in bank 0, so the bit-2 bank
boundary is never crossed and every read takes the fast ~45 ns path with no
high-Z blink.

Byte ordering *is* software-visible through PUSH/POP: B is the high half of
BC, D of DE, H of HL, A of PSW.

### The write-through hazard still applies

The register file is asynchronous-read with write-through, and the write
contract requires D stable from CLK-fall to CLK-rise. Any read-modify-write
of a slot closes a combinational loop through the read port the instant the
write window opens.

**Fix: 4 × '373 transparent operand latches on the two read ports**, enable =
CLK, transparent on CLK-high and closed on CLK-low. Costs no cycles, and the
module's own rule — *"never hang anything edge-sensitive on QA or QB"* —
requires a level-sensitive latch rather than a '374 anyway.

---

## Flags — the exact specification

Five flags. Bits 3 and 5 of PSW always read 0; bit 1 always reads 1. POP PSW
must force those three regardless of the popped value.

| Bit | Flag | Definition |
|---|---|---|
| 7 | S | Result bit 7 |
| 6 | Z | Result == 0 (all eight bits) |
| 5 | — | Always 0 |
| 4 | AC | Carry out of bit 3 — see below |
| 3 | — | Always 0 |
| 2 | P | Even parity of the low 8 bits (P = 1 when the count of ones is even) |
| 1 | — | Always 1 |
| 0 | C | Carry / borrow — see below |

### Write masks

| Mask | Instructions | Enable |
|---|---|---|
| All five | ALU ops, CMP, DAA, POP PSW | groups 1 + 2 |
| All but carry | INR, DCR | group 1 |
| Carry only | DAD, RLC, RRC, RAL, RAR, STC, CMC | group 2 |
| None | INX, DCX, MOV, MVI, loads, stores, jumps, calls, returns | neither |

Implementation: one '377 holding S, Z, AC, P (group 1) plus one '74 flip-flop
holding C (group 2). Two enables cover all four masks. POP PSW needs an input
mux selecting the bus byte on both.

### The three that will not fall out of a '181 by accident

**Z is not the A=B pin.** A=B is an open-collector AND of the F outputs and
asserts on all *ones* — 0xFF, not zero, with active-high data. Build a real
all-zeros detect ('30 plus an inverter, or a '688 against ground), and mask
the upper byte for 8-bit operations on a 16-bit ALU.

**C inverts twice.** With active-high operands the '181's Cn and Cn+4 are
active low; and on subtract the 8080A's C is a *borrow*, the inverse of the
adder's carry-out. Two inversions in opposite senses. Directed test: `SUI 0`
and `SUI 1` against A = 0.

**AC on logic operations is synthesised, not carried.** Logic ops generate no
carries at all, so AC is a gate:

| Operation | C | AC |
|---|---|---|
| ANA / ANI | 0 | `A3 OR operand3` |
| ORA / ORI | 0 | 0 |
| XRA / XRI | 0 | 0 |

**AC on subtraction — verify, do not assume.** The model I believe correct is
the *uninverted* carry out of bit 3 of the internal two's-complement addition
(`A + ~operand + 1`), so AC = 1 means no half-borrow — the opposite sense to
the C flag on the same operation. References disagree, and the error passes
every test that does not read AC back through PUSH PSW. Settle it against the
exerciser (below), not against a datasheet paraphrase.

---

## DAA — one GAL

The 8080A's DAA is a pure function of A[7:0], C and AC — 10 inputs. It emits
an 8-bit correction value (0x00, 0x06, 0x60 or 0x66) plus a force-carry line:
9 outputs. That is a **GAL22V10** (12 dedicated inputs, 10 I/O) with a pin to
spare.

Algorithm:

1. If `(A & 0x0F) > 9` **or** AC set → add 0x06.
2. If `(A >> 4) > 9`, **or** C set, **or** the step-1 addition carried out of
   the low nibble → add 0x60 and force C.

The correction goes through the existing ALU as operand B, so S, Z, P and the
new AC all fall out of that addition naturally — the GAL does not compute them.

**Two quirks that must be encoded exactly:**

- **DAA can only ever set C, never clear it.**
  `new C = old C OR high-correction-applied`.
- **8080A DAA is add-only correct.** After a subtract it produces garbage.
  That is deliberate — it is why the Z80 added the N flag — and reproducing
  the garbage faithfully is part of the contract. Do not "fix" it.

A GAL at ~15 ns fits inside the high phase alongside the ALU; a 150 ns ROM
lookup would not.

---

## Interrupts — now mandatory

Required mechanism:

- **INT input**, sampled at instruction boundaries only.
- **IFF** (interrupt enable flip-flop), cleared on reset and on acknowledge.
- **EI's one-instruction delay** — EI sets a *pending* bit; the pending bit
  transfers to IFF at the end of the following instruction. This is what makes
  `EI ; RET` work at the end of a service routine, and it is software-visible.
- **INTA cycle** — an M1-like fetch that takes its opcode from the interrupting
  device instead of memory, and does not increment PC.
- **RST-from-bus** — the supplied opcode (normally an RST) executes normally:
  push PC, load the vector.
- **HLT exit** — an accepted interrupt leaves the HLT spin state.

DI and EI without this mechanism are decorative. Under the contract they
cannot be left that way.

RST vectors are 0x0000, 0x0008, 0x0010 … 0x0038, decoded from opcode bits 5–3.
Wiring, not logic.

---

## Undefined opcodes — alias, don't trap

The 8080A defines 244 of 256 opcodes. Software compatibility means the other
12 behave as real silicon does:

| Opcodes | Behaviour |
|---|---|
| 08, 10, 18, 20, 28, 30, 38 | NOP |
| CB | JMP (as C3) |
| D9 | RET (as C9) |
| DD, ED, FD | CALL (as CD) |

These are decode aliases — a handful of extra product terms, near zero cost.

**Keep trap as a switchable debug mode.** A panel switch or control-port bit
that redirects the 12 to /HALTREQ instead is worth having during bring-up and
costs one gate. Ship with aliasing as the default: software compatibility is
the contract, debug convenience is the option.

---

## Instruction groups

Cycle counts are the real 8080A figures, **for reference only**.

### Group 1 — MOV

| Mnemonic | Bytes | 8080A T | Notes |
|---|---|---|---|
| MOV r, r' | 1 | 5 | Byte select from port A, merge into destination half |
| MOV r, M | 1 | 7 | HL on port B → address bus |
| MOV M, r | 1 | 7 | Byte select drives data out |

`MOV B, C` and friends read a slot, select a byte, and write the slot back
with the other half unchanged — from the *latched* port value. Six '157
packages: 2 to select the source byte, 4 to steer it into either half.

`MOV A, r` / `MOV r, A` route between the discrete A register and the file.

`MOV M, M` does not exist — that encoding (0x76) is HLT.

### Group 2 — 8-bit immediate

| Mnemonic | Bytes | 8080A T |
|---|---|---|
| MVI r, n | 2 | 7 |
| MVI M, n | 2 | 10 |

No ALU pass; the immediate byte enters the merge path directly.

### Group 3 — 16-bit immediate

| Mnemonic | Bytes | 8080A T |
|---|---|---|
| LXI B / D / H, nn | 3 | 10 |
| LXI SP, nn | 3 | 10 |

Two byte-wide writes into the halves of the target slot. `LXI SP` delivers its
two address bytes in separate cycles, so whatever holds SP must accept its low
and high halves independently rather than needing a single atomic 16-bit load.

### Group 4 — 8-bit ALU

| Mnemonic | Bytes | 8080A T | Notes |
|---|---|---|---|
| ADD / ADC / SUB / SBB r | 1 | 4 | Operand from port B, latched |
| ANA / ORA / XRA / CMP r | 1 | 4 | CMP suppresses the A write |
| …M forms | 1 | 7 | Operand from memory |
| ADI … CPI n | 2 | 7 | Operand from the immediate byte |

**No register-file write occurs in this entire group** — A is discrete and the
operand is read-only. The write port is idle across the bulk of the
instruction set.

### Group 5 — INR / DCR

| Mnemonic | Bytes | 8080A T | Notes |
|---|---|---|---|
| INR r / DCR r | 1 | 5 | C unchanged — flag group 1 only |
| INR M / DCR M | 1 | 10 | Read, ±1, write back |

Read-modify-write of a slot: the canonical write-through loop, handled by the
operand latch.

### Group 6 — 16-bit INX / DCX / DAD

| Mnemonic | Bytes | 8080A T | Notes |
|---|---|---|---|
| INX / DCX B, D, H | 1 | 5 | No flags |
| INX / DCX SP | 1 | 5 | Counter pulse — no ALU, no write port |
| DAD B / D / H / SP | 1 | 10 | HL ← HL + rp; **C only** |

DAD is the payoff for two read ports: HL on A, source on B, one 16-bit add,
one write. `DAD SP` reads the counter's output buffer, so the operand-B mux
needs an SP input.

### Group 7 — Direct load / store

| Mnemonic | Bytes | 8080A T |
|---|---|---|
| LDA / STA nn | 3 | 13 |
| LHLD / SHLD nn | 3 | 16 |
| LDAX / STAX B, D | 1 | 7 |

LHLD/SHLD increment WZ between the two memory cycles — another operand-latch
case. With timing unconstrained this takes its own cycle rather than squeezing
into the memory access.

### Group 8 — Stack

| Mnemonic | Bytes | 8080A T | Notes |
|---|---|---|---|
| PUSH B / D / H / PSW | 1 | 11 | SP−−, store high; SP−−, store low |
| POP B / D / H / PSW | 1 | 10 | Load low, SP++; load high, SP++ |
| SPHL | 1 | 5 | Parallel load SP from port B |
| XTHL | 1 | 18 | H ↔ [SP+1], L ↔ [SP], via WZ |

Byte order is software-visible: PUSH stores the high byte at SP−1 and the low
byte at SP−2. PUSH PSW stores A at SP−1 and F at SP−2, with F's constant bits
forced at the driver.

POP PSW is the only path that writes flags from the data bus — both enable
groups plus an input mux.

### Group 9 — Jumps

| Mnemonic | Bytes | 8080A T | Notes |
|---|---|---|---|
| JMP nn | 3 | 10 | Parallel load PC from WZ |
| Jcc nn | 3 | 10 | All three bytes always fetched; only the load is suppressed |
| PCHL | 1 | 5 | Parallel load PC from port B |

### Group 10 — Call / return

| Mnemonic | Bytes | 8080A T |
|---|---|---|
| CALL nn | 3 | 17 |
| Ccc nn | 3 | 11 / 17 |
| RET | 1 | 10 |
| Rcc | 1 | 5 / 11 |
| RST n | 1 | 11 |

The sequencer must **abort remaining work mid-instruction** on a false
condition — a not-taken Ccc fetches its operands and stops; a not-taken Rcc
ends immediately. Software-visible only in its effect, but a real structural
requirement on the sequencer.

### Group 11 — Exchange

| Mnemonic | Bytes | 8080A T | Notes |
|---|---|---|---|
| XCHG | 1 | 4 | `WZ←HL`, `HL←DE`, `DE←WZ` — three writes, no extra hardware |

With timing unconstrained this needs no hold register and no address-remap
trick. Three ordinary write cycles through the existing temp slot.

The remap trick — a flip-flop swapping DE/HL decoding — is rejected: it saves
nothing now, and it introduces hidden state that every implicit HL reference
(M addressing, DAD H, PCHL, SPHL, XTHL) would have to honour, while making a
front-panel register dump show mislabelled contents.

### Group 12 — Rotates

| Mnemonic | Bytes | 8080A T | Notes |
|---|---|---|---|
| RLC / RRC / RAL / RAR | 1 | 4 | C flag only |

`RAL` is `A + A + Cn` on the '181 natively, carry landing correctly at both
ends; `RLC` is the same with A7 into Cn. The right rotates are a fixed
permutation of the result bus — wiring — selected by 2 × '157, with one extra
mux bit choosing A0 or C for the incoming bit 7.

No shift register needed.

### Group 13 — Accumulator / flag specials

| Mnemonic | Bytes | 8080A T | Notes |
|---|---|---|---|
| CMA | 1 | 4 | '181 NOT; **no flags affected** |
| CMC | 1 | 4 | Complement C only |
| STC | 1 | 4 | Set C only |
| DAA | 1 | 4 | See the DAA section |

### Group 14 — I/O

| Mnemonic | Bytes | 8080A T | Notes |
|---|---|---|---|
| IN port | 2 | 10 | Port byte on **both** A15–A8 and A7–A0 |
| OUT port | 2 | 10 | Same |

256 ports. Duplicating the port byte across both address halves matches the
real part, and some decoders depend on it.

### Group 15 — Control

| Mnemonic | Bytes | 8080A T | Notes |
|---|---|---|---|
| NOP | 1 | 4 | |
| HLT | 1 | 7 | CPU-side spin state; exits on interrupt or reset |
| DI | 1 | 4 | IFF ← 0 |
| EI | 1 | 4 | IFF ← 1 **after the following instruction** |

---

## Reset state

| State | On reset |
|---|---|
| PC | 0x0000 (counter clear) |
| IFF | 0 |
| Flags | Clear them — costs nothing, removes a source of nondeterminism |
| A | Undefined |
| BC, DE, HL, WZ | Undefined (register file has no reset) |
| SP | Undefined on real silicon; boot code must `LXI SP` first |

The clock module releases RST synchronously two clock edges after the
supervisor, so the first fetch starts cleanly. RESET while in RUN restarts
free-running from 0x0000 — press STEP MODE first, or breakpoint the vector.

---

## Verification

Software compatibility is settled by running real 8080 code and checking
results, not by inspecting the schematic. The flag corner cases pass casual
tests and fail careful ones, so they need deliberate checking however the
testing is done:

- the AC sense after subtraction (line reference: Flags, "AC on subtraction"),
- the ANA / ANI half-carry rule (`AC = A3 OR operand3`, C = 0),
- DAA's set-only carry (it can force C but never clear it).

Read AC back through `PUSH PSW` when checking it — an AC error is invisible to
any test that only looks at the register or the other four flags.

---

## Hardware requirements summary

| Feature | Status | What's needed |
|---|---|---|
| 8-bit ALU ops | Easy | 16-bit '181 chain, low byte |
| 16-bit pair ops | Easy | Two read ports + 16-bit '181 |
| Operand latches | **Required** | 4 × '373 — fixes the write-through loop |
| Byte merge | Moderate | 6 × '157 |
| S, P flags | Easy | Result bit 7; 1 × '280 |
| Z flag | Moderate | Real all-zeros detect, **not** the '181 A=B pin |
| C flag | Moderate | Two inversions in opposite senses |
| AC flag | Moderate | ALU bit-3 carry, **plus** a gate for the ANA case |
| Flag masks | Easy | '377 + '74, two enables |
| Rotates | Easy | '181 for left, 2 × '157 + wiring for right |
| DAA | Easy | 1 × GAL22V10 |
| XCHG | Easy | Three writes through WZ, no extra parts |
| Stack | Easy | SP that decrements — counter or ALU path, per the SP decision |
| Conditional abort | Moderate | Sequencer terminate input |
| Undefined opcodes | Easy | 12 decode aliases + a debug trap switch |
| **HLT** | **Moderate** | CPU-side spin state, not the clock module's halt flop |
| **Interrupts** | **Hard, mandatory** | INT, INTA cycle, RST-from-bus, IFF, EI delay |
| **Memory** | **Mandatory** | Byte-wide RAM where code/data live; ROM/RAM split set at decode |

---

## I/O module integration

The Addy I/O module drops in as the parallel console with no rework — it is an
8-bit board on an 8-bit machine. `IN`/`OUT` map directly: the '541 input port
drives the read bus when `/INOE` is low, the '377 output register captures the
write bus on a strobed `CLK_A` edge with `/LEDCE` low.

Cautions carried over from that module's own document, none of them optional:

- **H1 power header is VCC-on-pin-1** — the *reverse* of the clock module's
  GND-on-pin-1 convention. A ribbon carried straight across from the clock
  module reverses polarity. Label the connector.
- **Never fit a '245 in the U1 socket.** Its data pins align with the '541's
  so it drops in mechanically, but pin 19 becomes a grounded `/OE` (always
  enabled) and pin 1 becomes DIR — the power-on default drives the switches
  onto the bus permanently, and pulling `/INOE` low reverses direction into
  contention. Label the socket.
- **`/INOE` has a 10 k pullup; `CLK_A` and `/LEDCE` do not.** The input port
  fails safe (quiet on the bus) when undriven, but the clock and load-enable
  must be driven whenever the board is powered — they have no safe standalone
  level. The strobes come from the control decode: `/INOE` low while `IN`
  needs the operand, `/LEDCE` low in the state that executes `OUT`, and
  `/LEDCE` is sampled *by* the edge, so it must be stable before `CLK_A` rises.
- The '377 powers up with random contents, so expect garbage LEDs until the
  first `OUT`. Boot code that wants a dark panel writes zero early.

One module gives one input port and one output port; multiple ports need
multiple boards (the two strobes are independent, so a single board can serve
one `IN` port number and one `OUT` port number at different addresses).

## Assembler note

No 8080 assembler exists yet; this is a from-scratch tool. Two points worth
fixing early:

- **Emit a flat byte image.** The 8080 fetches bytes, so the tool produces a
  plain byte stream at consecutive addresses — no byte-lane split. (The
  per-lane interleave belongs to Addy's 16-bit word fetch and does not apply.)
- **Origin/layout follows the eventual memory map.** Once the ROM/RAM split is
  decided at address-decode time, the assembler needs to know where code may be
  placed (ROM or RAM regions) and where the reset vector lives. Leave this
  parameterisable rather than assuming flat RAM from 0x0000.

## Open questions

1. **What holds SP, and how.** SP needs ±1 during PUSH/POP/CALL/RET and a
   two-cycle load for `LXI SP`. If a counter board with a down-count and a
   split load is available, SP lives there and those operations are free
   pulses. If not, SP moves into the register file and each adjustment becomes
   a read → ALU ±1 → write. This is genuinely open — no counter module has
   been specified yet — and it drives the slot map (an in-file SP wants a
   bank-0 slot to stay on the fast read path). PC stays wherever it can be
   cleared on reset and count on fetch, since the register file has no reset.
2. **The memory map.** Where ROM ends and RAM begins, where I/O decodes, and
   what physically provides each. This is the address-decode decision the rest
   of the document defers to; until it is made, which memory boards apply
   stays open.
3. **Console.** The I/O module covers a parallel console (switches in, LEDs
   out). A serial console needs a UART or bit-banging — and bit-banging is the
   one software class the free-running timing genuinely breaks.
