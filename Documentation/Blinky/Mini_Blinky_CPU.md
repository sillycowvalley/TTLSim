# Mini Blinky CPU — Master Reference (W = 4)

> **This is the single authoritative Mini Blinky document.** It supersedes and replaces
> `Mini_Blinky_CPU.md` (design summary), `Mini_Blinky_Control_Signals.md` (per-instruction
> control table), and the `Mini_Blinky_Reference.pdf` interim consolidation. All content from
> those three sources is merged here, with the PDF's instruction-set and decode refinements
> ratified (see Revision History at the end).

A half-scale instance of the full TTL Blinky CPU: same architecture, same programmer model,
narrower buses. Single-cycle (one clock tick = one instruction, no exceptions), Harvard,
dual-stack, Forth-style. The mini exists to be **functionally equivalent to the future 8-bit
machine** — every datapath and control path of the full design is exercised here; the only
thing that changes between them is a width knob, `W`.

---

## Relationship to the Full Blinky — the `W` Parameter

Define **`W`** = the machine word width (data bus, ALU, stack, address). The mini is `W = 4`;
the real machine is `W = 8`. The full design (`Blinky_TTL_CPU.md`) is the `W = 8` instance of
the same architecture.

**Equivalence lives at the programmer-model level, not the bit-encoding level.** A program
written and reasoned about on `W = 4` transfers to `W = 8` unchanged in *behavior* — same
operations, same stack discipline, same flags, same control-flow semantics. The *bit pattern*
of each instruction differs (the literal field is wider, opcodes may be renumbered), so the
assembler retargets `W = 4` → `W = 8`. What does **not** change is how a programmer thinks
about the machine.

The load-bearing invariant that makes this work:

> **Literal field width = `W`.**

That single rule is why `PUSH #imm`, `BRANCH addr`, and `CALL addr` are all single-word,
single-cycle, full-reach on *both* machines — the literal is exactly as wide as the bus it
feeds, so no high/low-byte sequence is ever needed.

### What scales with `W` vs. what is fixed

| Scales with `W` | Fixed regardless of `W` |
|---|---|
| Data bus / ALU width | The instruction *set* (same ops both machines) |
| D-memory address → 2^W cells | Three flags: N, Z, C |
| PC / I-memory address → 2^W instructions | ΔDSP ∈ {+1, 0, −1} per cycle |
| I/O port width | Single-port stack SRAM (one access per cycle) |
| TOS, NOS, data-stack width | Harvard, dual-stack structure |
| Both stack pointers → 2^W deep | Single-cycle: one tick = one instruction |
| Return-stack width (holds a PC) | Absolute branches and returns |
| **Literal / immediate field width (= W)** | |
| ALU = ⌈W/4⌉ × '181 (mini: 1, real: 2) | |
| TOS shift register = ⌈W/4⌉ × '194 (mini: 1, real: 2) | |

### Instruction width across `W` — why 8 bits here and 16 bits there

**1. Architecturally required width = opcode + literal.** Only the literal is pinned to `W`.
Opcode width is free — set by opcode *count*, not by `W` — and the curated set fits in 4 bits
at both scales.

| | opcode | literal (= W) | required |
|---|---|---|---|
| `W = 4` (mini) | 4 | 4 | **8-bit** |
| `W = 8` (real) | 4 | 8 | **12-bit** |

Pure scaling grows the instruction by exactly the literal growth (+4 bits): **12 bits at
`W = 8`**, not 16.

**2. Physical word width = memory granularity.** Memory chips are 8 bits wide:

- `W = 4`: 8-bit instruction → **one** 8-bit chip. Exact fit, zero waste.
- `W = 8`: 12 bits needed → **two** 8-bit chips → physical word is **16 bits**, ~4 bits
  spare. You cannot buy a 12-bit-wide memory, so the word *is* 16 whether or not all of it is
  used. (The full Blinky confirms this: 16-bit I-bus, two 8-bit EEPROMs.)

So `W = 8` is a 16-bit instruction word for a packaging reason, not an architectural one. The
architecture needs 12; the memory hands you 16.

**3. The spare 4 bits — the one place mini and full machine diverge by design.**

- **Mini, scaled faithfully:** keep the 4-bit opcode, reserve the top 4 bits. 12 used,
  4 dead. Maximally equivalent to `W = 4`.
- **Full Blinky:** *spends* those bits — its encoding is I-bit + 7-bit opcode + 8-bit
  literal. That is an enhancement the free headroom permits (larger opcode space, explicit
  immediate flag), not a requirement of equivalence.

Both are correct because equivalence is at the programmer-model level, not the bit encoding.

## Design Philosophy (inherited from the full Blinky)

- **Single-cycle execution** — one clock tick = one instruction, no exceptions.
- **Front-panel honesty** — every visible LED reflects real machine state; single-stepping
  shows complete, settled states.
- **Stack machine, not register machine** — operands are implicit (ALU A = TOS, B = NOS,
  result → TOS). No operand routing, no register-file decode.
- **Minimum chip count without architectural compromise.**

## `W = 4` Parameters

| Quantity | Value |
|---|---|
| Data path / ALU | 4-bit (one '181) |
| Program (I-memory) | 16 instructions |
| Data memory | 16 cells × 4-bit |
| Data stack depth | 16 |
| Return stack depth | 16 |
| Literal / immediate | 4-bit (full data range, full code reach) |
| Flags | N, Z, C |
| Instruction word | **8-bit** (4-bit opcode + 4-bit literal) |

The 16-instruction program ceiling is the accepted price of being a faithful half-scale
model: the `W = 8` machine tops out at 256 instructions *the same way*, because the PC width
equals the literal width equals `W` on both. We do **not** widen the PC past the literal
field — doing so would force multi-instruction address loads and break single-cycle
execution.

---

# 1. Programmer's Model

## Architecturally visible state

| Element | Width | Purpose |
|---|---|---|
| PC | W | program counter (I-memory address) |
| IR | 8 (mini) | instruction register |
| TOS | W | top of data stack ('194: parallel-loads on writes, shifts on SHL/SHR/ASR/ROL/ROR) |
| NOS | W | next on data stack |
| DSP | W | data-stack pointer |
| RTOS | W | top of return stack (return address) |
| RSP | W | return-stack pointer |
| N / Z / C | 1 each | flags, set by ALU ops only |

## Flags (written only by the ALU class)

Stack, memory, I/O and control instructions leave the flags untouched, so a `TST` or
arithmetic op can be separated from its consuming branch by any number of non-ALU
instructions. **Within the ALU class the write is per-flag (ALU rev 2):** N and Z latch on
every live ALU sub-op (`NZ_WE`); C latches only on the ops that define it — ADD/ADC/SUB/CMP
(carry/borrow) and the five shifts (the shifted-out bit) — via its own enable, `C_WE`. The
logic ops (XOR, NOT, TST, AND, OR) **preserve C**, so a carry or shifted-out bit survives
intervening masking ops until its consuming branch.

| Flag | Definition | Used by |
|---|---|---|
| N | bit `(W−1)` of the result (top bit) | `BMI` |
| Z | result is all-zero | `BEQ` |
| C | arith/CMP: carry / borrow out of the top bit. shift: the bit shifted out (old top on `SHL`/`ROL`, old bottom on `SHR`/`ASR`/`ROR`). Held by logic ops. | `BCS`, `ADC`, `SUB` |

N = bit `(W−1)` scales `W = 4` → `W = 8` with no logic change.

## Stack discipline — TOS / NOS update rules

Every instruction changes DSP by exactly +1, 0 or −1. **No instruction shifts the stack by
two in one cycle** — this keeps DSP a single up/down counter, stack memory single-port, and
the TOS/NOS muxes small.

| Operation | TOS ← | NOS ← | Stack memory |
|---|---|---|---|
| Push (+1) | new value (immediate / fetched / ALU) | old TOS | write old NOS at DSP+1 |
| Unary / shift (0) | ALU or '194-shifted result of old TOS | unchanged | idle |
| SWAP (0) | old NOS | old TOS | idle |
| Binary ALU (−1) | ALU result of (old TOS, old NOS) | stack[DSP−1] read | read only |
| DROP (−1) | old NOS | stack[DSP−1] read | read only |
| OUT (−1) | old NOS | stack[DSP−1] read | read only (I/O write is of old TOS) |

---

# 2. Instruction Encoding

Fixed **8-bit** instruction: a 4-bit opcode plus a 4-bit literal. The literal is **operand OR
address depending on the opcode — never both**; the opcode alone fixes its role, so decode is
purely positional and no immediate-flag bit is needed.

```
 7   6   5   4 | 3   2   1   0
+---------------+---------------+
|    opcode     |    literal    |
+---------------+---------------+
                 ↑ operand (data/op path) OR address (address mux), per opcode
```

Literal role by opcode:

- **operand role** (literal → data / op-select path): `SYS` (sub-op), `ALU` (op-select),
  `PUSH` (immediate value)
- **address role** (literal → address mux): `LOAD`, `STORE`, `OUT #port`, `BRANCH`, `BEQ`,
  `BCS`, `BMI`, `CALL`
- **unused**: `IN` (stack form — port comes from TOS, literal is don't-care)

## Top-Level Opcode Map

12 live opcodes, 4 reserved.

| Op | Bits | Mnemonic | Literal role | ΔDSP | Stack | Notes |
|---|---|---|---|---|---|---|
| 0 | 0000 | `SYS` | operand (sub-op) | varies | — | operand-less family — see SYS table |
| 1 | 0001 | `ALU` | operand (op-select) | −1 / 0 | sets N/Z/C | TOS,NOS → TOS; GAL expands the select |
| 2 | 0010 | `PUSH #imm` | operand (immediate) | +1 | `( -- n )` | literal → TOS |
| 3 | 0011 | `LOAD #addr` | address | +1 | `( -- m )` | push D-mem[addr] |
| 4 | 0100 | `STORE #addr` | address | −1 | `( a -- )` | pop → D-mem[addr] |
| 5 | 0101 | `IN` | — (port = TOS) | 0 | `( p -- v )` | stack-form port read; replaces port with value |
| 6 | 0110 | `OUT #port` | address | −1 | `( d -- )` | immediate-form; writes TOS to port = literal |
| 7 | 0111 | `BRANCH addr` | address | 0 | `( -- )` | PC ← addr |
| 8 | 1000 | `BEQ addr` | address | 0 | `( -- )` | branch if Z |
| 9 | 1001 | `BCS addr` | address | 0 | `( -- )` | branch if C |
| 10 | 1010 | `BMI addr` | address | 0 | `( -- )` | branch if N (top bit set) |
| 11 | 1011 | `CALL addr` | address | 0 | `( -- )` | push PC+1 to R-stack, PC ← addr |
| 12–15 | 11xx | — | — | — | — | reserved (indirect CALL / extension) |

`IN` is the stack form and `OUT` is the immediate form **on purpose**: together they cover
the TOS-vs-IR address-source mux (IN drives the TOS side, OUT #port the literal side) using
two opcodes instead of four. The full Blinky offers both forms of each; the mini carries one
of each because that is sufficient to exercise both mechanisms.

## SYS Sub-Ops (opcode 0)

The operand nibble selects one of the operand-less family; a small decode (one enable off
`opcode == SYS`) expands it. This grouping is what lets a 4-bit opcode carry the full shared
instruction set without cutting operations.

| Sub-op | ΔDSP | Stack effect | Mechanism exercised |
|---|---|---|---|
| `NOP` | 0 | `( -- )` | null single-cycle path |
| `HALT` | 0 | `( -- )` | clock stop |
| `RET` | 0 | `( -- )` R:`( a -- )` | PC ← RTOS, RSP −1 |
| `DUP` | +1 | `( a -- a a )` | push sourced from TOS latch; stack-mem write |
| `DROP` | −1 | `( a -- )` | pop; stack-mem read into NOS |
| `SWAP` | 0 | `( a b -- b a )` | TOS ↔ NOS cross path |
| `>R` | −1 (R +1) | `( a -- )` R:`( -- a )` | data → return stack |
| `R>` | +1 (R −1) | `( -- a )` R:`( a -- )` | return → data stack |

`ROT` is **not** encoded — it is not a single-cycle operation on a single-port stack.
Synthesize it as `>R SWAP R> SWAP` on both machines (§8.4).

## ALU Sub-Ops (opcode 1)

The operand nibble is a **curated op-select**, not the raw '181 function lines. The '181
needs ~6 control bits (S0–S3, mode M, carry-in source) plus this machine's per-op ΔDSP and
result-write-enable. Two of the selects (`SHL`, `SHR`) do not drive the '181 at all — they
drive the '194 shift-register TOS instead — so the GAL also carries the TOS mode and the
flag-C source. The decode GALs expand the 4-bit select into:

```
operand nibble  →  { S0, S1, S2, S3, M, Cn-source, ΔDSP, write-enable,
                     TOS-mode (load / shift-L / shift-R),
                     C-source ('181 carry / shifted-out bit) }
```

Same decode scheme on both machines; the mini simply populates fewer rows. "Pick fewer ALU
ops" therefore means "fewer GAL rows," nothing structural.

The '181 columns below are the decoded control the GAL emits (from the sim's `Hc181`
active-HIGH table), not the operand nibble:

| Sub-op | Nib | ΔDSP | Stack effect | '181 S3–S0 | Mode | Cn | N/Z | C |
|---|---|---|---|---|---|---|---|---|
| `ADD` | 0 | −1 | `( a b -- a+b )` | 1001 | arith | H | ✓ | ✓ Cn+4 |
| `ADC` | 1 | −1 | `( a b -- a+b+C )` | 1001 | arith | /C | ✓ | ✓ Cn+4 |
| `SUB` | 2 | −1 | `( a b -- b−a )` | 0110 | arith | L | ✓ | ✓ Cn+4 |
| `XOR` | 3 | −1 | `( a b -- a⊕b )` | 0110 | logic | — | ✓ | — held |
| `NOT` | 4 | 0 | `( a -- ¬a )` | 0000 | logic | — | ✓ | — held |
| `TST` | 5 | 0 | `( a -- a )` | 1111 | logic | — | ✓ | — held |
| `SHL` | 6 | 0 | `( a -- a«1 )` | — | — | — | ✓ | ✓ old top bit |
| `SHR` | 7 | 0 | `( a -- a»1 )` | — | — | — | ✓ | ✓ old bottom bit |
| `AND` | 8 | −1 | `( a b -- a∧b )` | 1011 | logic | — | ✓ | — held |
| `CMP` | 9 | 0 | `( a b -- a b )` | 0110 | arith | L | ✓ | ✓ Cn+4 |
| `ASR` | B | 0 | `( a -- a»1 )` sign | — | — | — | ✓ | ✓ old bottom bit |
| `OR` | C | −1 | `( a b -- a∨b )` | 1110 | logic | — | ✓ | — held |
| `ROL` | E | 0 | `( a -- rol a )` | — | — | — | ✓ | ✓ old top bit |
| `ROR` | F | 0 | `( a -- ror a )` | — | — | — | ✓ | ✓ old bottom bit |

Nibbles A and D are reserved and decode to the idle vector (`NZ_WE`, `C_WE`, `DSP_EN`,
`NOS_LD` and the '194 mode all exclude them explicitly).

**Direction:** A = TOS, B = NOS, so `SUB` and `CMP` compute TOS − NOS (C = 1 means no
borrow, i.e. TOS ≥ NOS). **Per-flag write masking (rev 2):** N/Z latch on every live sub-op
(`NZ_WE`); C latches only where the C column shows ✓ (`C_WE`). The logic ops XOR, NOT, TST,
AND, OR therefore *hold* C — the rev-1 "logic ops latch C = 1 from the held-high logic-mode
Cn+4" gotcha is retired. **`TST`** runs '181 select 1111 (F = A = TOS), latches N/Z, and
holds TOS — the row that proves the flag write is independent of any TOS write. **`CMP`** is
the non-destructive compare `( a b -- a b )`: SUB's '181 row and carry-in with the result
discarded — TOS and NOS hold, no stack access, ΔDSP 0. The old *popping* CMP `( a b -- )`
remains forbidden (−2). **Shifts:** SHL/SHR are logical (zero fill); ASR fills with the old
top bit (sign); ROL/ROR are plain rotates wrapping the falling-out bit. The fill source is a
'153 dual 4:1 mux on the '194 serial pins, selected by **raw IR literal bits L3/L2** — the
'194 ignores its serial inputs except while shifting, so the select needs no decode or
gating and costs no GAL output. Rotate-through-carry, if ever wanted, is a one-wire change
on that mux ('153 input C instead of Q0/Q7). **'181 code sharing:** `SUB`/`XOR`/`CMP` share
0110 (mode and write-mask differ); `ADD`/`ADC` share 1001 (Cn differs).

The rev-2 nibble placements (AND=8, CMP=9, ASR=B, OR=C, ROL=E, ROR=F) are **not aesthetic**:
the serial-fill select on raw L3/L2 pins ASR into 10xx and the rotates into 11xx, and the
chosen slots are what keep the '194-mode equations (`TOS_M1`/`TOS_M0`, already at the 16V8's
8-term ceiling in rev 1) on 7 product terms — see `Blinky_ALU_Rev2.md` and `GAL1_ALU.pld`.
`SHL`/`SHR` remain the proof ops for the '194 shifter; the five shift forms and AND/OR/CMP
exist on **both** machines.

## Full Byte Encoding (RATIFIED — ALU rev 2)

The top-level opcodes (0–11) are fixed. The sub-op nibbles below are **ratified**: the four
decode GAL fuse maps (`GAL1_ALU.pld` rev 02 … `GAL4_FLOW.pld` rev 03) are generated from
them, and the rev-2 ALU placements are load-bearing (serial-fill select on raw L3/L2; '194
mode equations at 7 of 8 terms — see `Blinky_ALU_Rev2.md`). SYS nibbles 8–F and ALU nibbles
A and D remain reserved and decode to the idle vector. Renumbering now means regenerating
and re-burning all four GALs and retargeting the assembler.

| Instr | Byte | Instr | Byte | Instr | Byte | Instr | Byte |
|---|---|---|---|---|---|---|---|
| **SYS** NOP | 0x00 | **ALU** ADD | 0x10 | AND | 0x18 | PUSH #n | 0x2n |
| HALT | 0x01 | ADC | 0x11 | CMP | 0x19 | LOAD #a | 0x3a |
| RET | 0x02 | SUB | 0x12 | ASR | 0x1B | STORE #a | 0x4a |
| DUP | 0x03 | XOR | 0x13 | OR | 0x1C | IN | 0x50\* |
| DROP | 0x04 | NOT | 0x14 | ROL | 0x1E | OUT #p | 0x6p |
| SWAP | 0x05 | TST | 0x15 | ROR | 0x1F | BRANCH a | 0x7a |
| >R | 0x06 | SHL | 0x16 | ALU rsvd | 0x1A, 0x1D | BEQ a | 0x8a |
| R> | 0x07 | SHR | 0x17 | | | BCS a | 0x9a |
| | | | | | | BMI a | 0xAa |
| | | | | | | CALL a | 0xBa |
| | | | | | | reserved | 0xC_–0xF_ |

n = 4-bit immediate, a = 4-bit address, p = 4-bit port.
\* `IN`'s literal is don't-care (port from TOS); 0x50 is the canonical form.
Instruction byte = `(opcode << 4) | literal`.

## What the Mini Exercises

The subset covers *mechanisms*, not operations — each instruction earns its slot only by
being the sole thing that lights a given datapath or control path:

- **ALU:** both '181 modes (arithmetic via ADD/SUB, logic via XOR/AND/OR), full carry chain,
  carry-*in* mux (ADC), subtract/borrow polarity (SUB), unary path (NOT), flag-only with
  suppressed write (TST one-operand, CMP two-operand non-destructive).
- **Shifter:** the '194 bidirectional shift-register TOS — a datapath the '181 never
  drives — in both directions and all three fill modes (`SHL`/`SHR` zero fill, `ASR` sign
  fill, `ROL`/`ROR` wrap via the '153 serial-fill mux), with C sourced from the bit shifted
  out rather than from the carry chain.
- **Stack:** push from literal (PUSH) and from TOS latch (DUP), pop with stack-mem read
  (DROP), TOS↔NOS cross (SWAP), inter-stack path (>R / R>, which also synthesizes ROT).
- **Memory / I/O:** IR-sourced address (LOAD / STORE / OUT #port), TOS-sourced address via
  the address mux (stack-form IN).
- **Control:** unconditional branch (BRANCH), all three flag→branch paths (BEQ/BCS/BMI),
  return-stack write and absolute return (CALL / RET), null and clock-stop (NOP / HALT).

---

# 3. Control Signals

Single-cycle: every signal below is the value the decode GALs assert **during the one cycle**
that executes the instruction. State changes happen on the rising edge that ends the cycle.
All address/operand paths come from the instruction currently in the IR.

The '181 select codes follow the simulator's `Hc181.cs` (datasheet "Active-HIGH operands"
function table). The '194/'191/2114 control polarities are datasheet-derived — those parts
are not yet modelled in the sim, so treat their exact pin senses as provisional until the
models land.

## Control Signal Dictionary

**Group A — ALU / shifter / stack-top / flags**

| Signal | Values | Meaning |
|---|---|---|
| `ALU_S3..S0` | 4-bit | '181 function select |
| `ALU_M` | arith / logic | '181 mode (M=L arith, M=H logic) |
| `ALU_Cn` | H / L / /C | carry-in pin: H = no inject, L = +1 inject, /C = inject the C flag |
| `TOS_MODE` | hold / load / shL / shR | '194 mode (load = parallel-load from `TOS_SRC`; shL on SHL/ROL, shR on SHR/ASR/ROR; serial fill from the '153 mux, selected by raw IR L3/L2) |
| `TOS_SRC` | LIT / DMEM / INP / RTOS / ALU / NOS | parallel-load source (only when `TOS_MODE = load`) |
| `NOS` | hold / ←TOS / ←stkR | NOS latch: hold, load old TOS, or load data-stack read |
| `NZ_WE` | ✓ / – | latch N and Z this cycle (every live ALU sub-op; reserved nibbles and non-ALU ops deassert it) |
| `C_WE` | ✓ / – | latch C this cycle (ADD ADC SUB CMP SHL SHR ASR ROL ROR only — logic ops hold C) |
| `C_SRC` | 181 / shf | C flip-flop source: '181 Cn+4 (arith/CMP), or the bit shifted out of the '194 (all five shifts) |

**Group B — sequencing / memory / I/O**

| Signal | Values | Meaning |
|---|---|---|
| `PCSEL` | +1 / LIT / RTOS | NEXTPC mux: PC+1, IR literal (branch/call target), or RTOS (return). Conditional forms select LIT only if the flag is set, else +1. |
| `DSP` | 0 / +1 / −1 | data-stack pointer '191: count enable (/CTEN) + direction (D//U) |
| `DSTK` | – / wr / rd | data-stack 2114 access (write = push spills old NOS, read = pop fills NOS). **Derived from DSP, not decoded** — see below. |
| `RSP` | 0 / +1 / −1 | return-stack pointer '191 |
| `RSTK` | – / wr(PC+1) / wr(TOS) | return-stack 2114. /CS, /WE derived from RSP; only the data-in select (PC+1 for CALL, TOS for >R) is decoded. Read implicit when RTOS consumed. |
| `DMEM` | – / rd@LIT / wr@LIT | data memory 2114, address = IR literal (**decoded** — no pointer to derive from) |
| `IO` | – / RD@TOS / WR@LIT | I/O strobe + address-source mux: IN reads port = TOS (stack form), OUT #port writes port = literal |
| `CLK` | run / **STOP** | clock-stop (HALT) |

**Idle / default vector** (what an instruction that "does nothing" asserts): `ALU` –,
`TOS` hold, `NOS` hold, `NZ_WE` –, `C_WE` –, `PCSEL` +1, `DSP` 0, `DSTK` –, `RSP` 0, `RSTK` –,
`DMEM` –, `IO` –, `CLK` run. A blank/`–` cell below means this default.

## Table A — per-instruction Group A (ALU / shifter / stack-top / flags)

| Instr | ALU_S | ALU_M | ALU_Cn | TOS_MODE | TOS_SRC | NOS | NZ_WE | C_WE | C_SRC |
|---|---|---|---|---|---|---|---|---|---|
| **SYS** NOP | – | – | – | hold | – | hold | – | – | – |
| HALT | – | – | – | hold | – | hold | – | – | – |
| RET | – | – | – | hold | – | hold | – | – | – |
| DUP | – | – | – | hold | – | ←TOS | – | – | – |
| DROP | – | – | – | load | NOS | ←stkR | – | – | – |
| SWAP | – | – | – | load | NOS | ←TOS | – | – | – |
| >R | – | – | – | load | NOS | ←stkR | – | – | – |
| R> | – | – | – | load | RTOS | ←TOS | – | – | – |
| **ALU** ADD | 1001 | arith | H | load | ALU | ←stkR | ✓ | ✓ | 181 |
| ADC | 1001 | arith | /C | load | ALU | ←stkR | ✓ | ✓ | 181 |
| SUB | 0110 | arith | L | load | ALU | ←stkR | ✓ | ✓ | 181 |
| XOR | 0110 | logic | – | load | ALU | ←stkR | ✓ | – | – |
| NOT | 0000 | logic | – | load | ALU | hold | ✓ | – | – |
| TST | 1111 | logic | – | hold | – | hold | ✓ | – | – |
| SHL | – | – | – | shL | – | hold | ✓ | ✓ | shf |
| SHR | – | – | – | shR | – | hold | ✓ | ✓ | shf |
| AND | 1011 | logic | – | load | ALU | ←stkR | ✓ | – | – |
| CMP | 0110 | arith | L | hold | – | hold | ✓ | ✓ | 181 |
| ASR | – | – | – | shR | – | hold | ✓ | ✓ | shf |
| OR | 1110 | logic | – | load | ALU | ←stkR | ✓ | – | – |
| ROL | – | – | – | shL | – | hold | ✓ | ✓ | shf |
| ROR | – | – | – | shR | – | hold | ✓ | ✓ | shf |
| PUSH #imm | – | – | – | load | LIT | ←TOS | – | – | – |
| LOAD #addr | – | – | – | load | DMEM | ←TOS | – | – | – |
| STORE #addr | – | – | – | load | NOS | ←stkR | – | – | – |
| IN | – | – | – | load | INP | hold | – | – | – |
| OUT #port | – | – | – | load | NOS | ←stkR | – | – | – |
| BRANCH addr | – | – | – | hold | – | hold | – | – | – |
| BEQ addr | – | – | – | hold | – | hold | – | – | – |
| BCS addr | – | – | – | hold | – | hold | – | – | – |
| BMI addr | – | – | – | hold | – | hold | – | – | – |
| CALL addr | – | – | – | hold | – | hold | – | – | – |

## Table B — per-instruction Group B (sequencing / memory / I/O)

| Instr | PCSEL | DSP | DSTK | RSP | RSTK | DMEM | IO | CLK |
|---|---|---|---|---|---|---|---|---|
| **SYS** NOP | +1 | 0 | – | 0 | – | – | – | run |
| HALT | +1 | 0 | – | 0 | – | – | – | **STOP** |
| RET | RTOS | 0 | – | −1 | – (rd) | – | – | run |
| DUP | +1 | +1 | wr | 0 | – | – | – | run |
| DROP | +1 | −1 | rd | 0 | – | – | – | run |
| SWAP | +1 | 0 | – | 0 | – | – | – | run |
| >R | +1 | −1 | rd | +1 | wr(TOS) | – | – | run |
| R> | +1 | +1 | wr | −1 | – (rd) | – | – | run |
| **ALU** ADD | +1 | −1 | rd | 0 | – | – | – | run |
| ADC | +1 | −1 | rd | 0 | – | – | – | run |
| SUB | +1 | −1 | rd | 0 | – | – | – | run |
| XOR | +1 | −1 | rd | 0 | – | – | – | run |
| NOT | +1 | 0 | – | 0 | – | – | – | run |
| TST | +1 | 0 | – | 0 | – | – | – | run |
| SHL | +1 | 0 | – | 0 | – | – | – | run |
| SHR | +1 | 0 | – | 0 | – | – | – | run |
| AND | +1 | −1 | rd | 0 | – | – | – | run |
| CMP | +1 | 0 | – | 0 | – | – | – | run |
| ASR | +1 | 0 | – | 0 | – | – | – | run |
| OR | +1 | −1 | rd | 0 | – | – | – | run |
| ROL | +1 | 0 | – | 0 | – | – | – | run |
| ROR | +1 | 0 | – | 0 | – | – | – | run |
| PUSH #imm | +1 | +1 | wr | 0 | – | – | – | run |
| LOAD #addr | +1 | +1 | wr | 0 | – | rd@LIT | – | run |
| STORE #addr | +1 | −1 | rd | 0 | – | wr@LIT | – | run |
| IN | +1 | 0 | – | 0 | – | – | RD@TOS | run |
| OUT #port | +1 | −1 | rd | 0 | – | – | WR@LIT | run |
| BRANCH addr | LIT | 0 | – | 0 | – | – | – | run |
| BEQ addr | LIT if Z else +1 | 0 | – | 0 | – | – | – | run |
| BCS addr | LIT if C else +1 | 0 | – | 0 | – | – | – | run |
| BMI addr | LIT if N else +1 | 0 | – | 0 | – | – | – | run |
| CALL addr | LIT | 0 | – | +1 | wr(PC+1) | – | – | run |

Conditional branches are the only place a flag feeds control: `PCSEL` is a function of opcode
**and** the selected flag. `BRANCH`/`CALL` force `LIT` unconditionally. `HALT` asserts
`CLK = STOP`; with the clock stopped, PC and all state freeze, so `PCSEL` is don't-care. The
`DSTK` and `RSTK` cells are **not independent controls** — they are read off the `DSP` /
`RSP` columns (0 → `–`, +1 → `wr`, −1 → `rd`); see next section.

## Stack-RAM strobes are derived, not decoded

The data- and return-stack 2114s need no dedicated /CS or /WE control lines. Each strobe is a
pure function of its pointer's action, which the GAL already emits:

- **/CS** = the pointer is counting (asserted on any push or pop, deasserted on hold — the
  '191 count-enable /CTEN, itself active-low).
- **/WE** = the pointer is counting up (write on push, read on pop — the '191 direction line
  D//U, which is LOW for up).

A held pointer leaves /CS deasserted, so the 2114's bidirectional I/O is high-Z off the
shared bus on every idle cycle — the same isolation an explicit decode would give, at zero
macrocells. The '191's senses land directly on the 2114's: /CTEN is active-low like /CS, and
D//U is LOW on a push exactly when the active-low /WE must assert — the alignment inverters
the '169 would have required drop out (exact pin senses remain provisional until the '191 is
modelled).

**Write timing.** The 2114 is async and captures on /WE rising, so the write strobe must
deassert before the '191 advances the address on the cycle-ending edge. Qualify the derived
/WE with the clock phase (one AND/NAND slot per stack RAM) so the write pulse lives inside
the address-stable window and commits ahead of the count. This is independent of the strobe's
source — a decoded /WE would need it too. A series buffer/inverter does **not** solve this:
it shifts the edge but doesn't bound the pulse to the stable window.

**D-memory is the exception:** its /CS, /WE come from the LOAD/STORE opcode and its address
from the IR literal — there is no pointer to derive them from — so `DMEM_CS` / `DMEM_WE`
remain decoded GAL outputs.

## GAL Partition — 4× 16V8 (no 20V8)

Deriving the four stack strobes from the pointer lines removes `DSTK_CS/WE` and `RSTK_CS/WE`
from the decode, shrinking the control vector **32 → 28** and dissolving the pressure that
previously forced a 20V8 (ALU rev 2's per-flag split adds `C_WE` back, landing at 29 decoded
lines + 4 derived strobes). With `DMEM_CS/WE` moved onto the stack-top device, the
flag-reading device carries only 6 outputs against its 11 inputs (17 pins ≤ 18), so it fits
a 16V8. The whole decode is **four 16V8s** — a single part type, which matches the 16V8
stock.

| GAL | Part | Outputs | Inputs |
|---|---|---|---|
| **1 — ALU / shift select** | 16V8 | `ALU_S0..S3`, `ALU_M`, `C_SRC`, `TOS_M0`, `TOS_M1` (8) | opcode + sub-op = 8 |
| **2 — stack-top + D-mem RAM** | 16V8 | `TOS_SRC0..2`, `NOS_LD`, `NOS_SRC`, `NZ_WE`, `DMEM_CS`, `DMEM_WE` (8) | opcode + sub-op = 8 |
| **3 — pointers + ret data mux + clk + C enable** | 16V8 | `DSP_CNT`, `DSP_UD`, `RSP_CNT`, `RSP_UD`, `RDIN_SEL`, `CLK_RUN`, `C_WE` (7) | opcode + sub-op = 8 |
| **4 — control flow + I/O + Cn** | 16V8 | `PCSEL0`, `PCSEL1`, `ALU_Cn`, `IO_RD`, `IO_WR`, `IOADDR_SEL` (6) | opcode + sub-op + **N, Z, C** = 11 |

The stack /CS, /WE strobes are generated externally from GAL 3's `DSP` / `RSP` outputs
(inverter + clock-phase gate per RAM, in the existing glue budget). GAL 3 keeps one spare
macrocell (pin 12) and GAL 4 keeps two — the remaining headroom for an immediate-ALU family
later (§9 open decisions) without adding a device. The '194 serial-fill mux consumes **no**
GAL output: its '153 selects are raw IR literal bits L3/L2, don't-care except while
shifting.

Devices: GAL16V8 (18 signal pins on the 20-pin package) throughout. The governing constraint
is unchanged: **all three flag inputs (N, Z, C) belong on one device** — only `PCSEL` (reads
N, Z, C) and `ALU_Cn` (reads C) consume flags, so co-locating them pays the flag-input cost
once. Every other output decodes from instruction bits alone (opcode + sub-op nibble =
8 inputs).

---

# 4. Datapath Block Diagram

Fetch spine on the left (NEXTPC → PC → I-MEM → IR); the IR splits into the opcode (to the
decode GALs) and the 4-bit literal (to TOS, D-mem and the I/O address mux). Execute cluster:
TOS ('194) and NOS feed the '181; the result returns to TOS, flags latch to the '74 pair and
feed back into `PCSEL` / `ALU_Cn`. ALU rev 2 hangs a '153 dual 4:1 mux on the '194 serial
pins (fill source: GND / Q-top / Q-bottom, selected by raw IR literal bits L3/L2) and splits
the flag-latch enable into `NZ_WE` (GAL 2) and `C_WE` (GAL 3) — neither changes the diagram's
topology. The two single-port stacks sit on the bottom row with their '191 pointers; RTOS
feeds both the NEXTPC mux (RET) and TOS (R>).

```
                 +-------------+      +-----------+      +-------------------+
   +------------>| NEXTPC mux  |----->| PC  '161  |----->| I-MEM             |
   |  +--------->| +1/LIT/RTOS |      | W-bit     |      | 16 x 8 (Harvard)  |
   |  |          +-------------+      +-----------+      +---------+---------+
   |  |             ^      ^                                       |
   |  | RTOS        |      | PC+1 (to RSTK on CALL)                v
   |  |             |      |                             +-------------------+
   |  |          PCSEL  (from GAL 4, flag-gated)         | IR '374 (8)       |
   |  |                                                  | op[7:4]|lit[3:0]  |
   |  |                                                  +----+---------+----+
   |  |                                              opcode  |         | literal
   |  |                                                      v         v
   |  |   +--------------------------------------+   +--------------+  LIT -> TOS_SRC,
   |  |   | CONTROL / DECODE: GAL x4 (all 16V8)   |<--+              |  D-MEM addr,
   |  |   | in: opcode + sub-op (+ N,Z,C on GAL4) |                  |  I/O addr mux
   |  |   | out: 28 lines (+4 stack strobes       |                  |
   |  |   |      derived from DSP/RSP)            |                  |
   |  |   +--------------------------------------+                   |
   |  |                                                              |
   |  |   +----------------+        +----------------+               |
   |  |   | TOS  '194      |<------>| ALU  '181      |               |
   |  +---| load/shL/shR   |        | A=TOS  B=NOS   |               |
   |      +----+-----------+        +-------+--------+               |
   |           ^   ^                        |  result -> TOS         |
   |           |   |                        v                        |
   |           |   |                +----------------+               |
   |           |   |                | FLAGS  N Z C   |--- N/Z/C ---> GAL 4
   |           |   |                | ('74 x2)       |    (PCSEL, ALU_Cn)
   |           |   |                +----------------+               |
   |           |   +-- INP (I/O read)                                |
   |      +----+-----------+                                         |
   |      | NOS latch      |                                         |
   |      | <-TOS / <-stkR |                                         |
   |      +----+-----------+                                         |
   |           |                                                     |
   |   +-------+--------+   +--------------+   +-----------------+   |
   |   | Data-stack RAM |   | DSP '191 ±1  |   | D-MEM 2114      |<--+ addr = LIT
   |   | 2114, 16-deep  |<--| (/CS,/WE     |   | (CS/WE decoded) |   |
   |   +----------------+   |  derived)    |   +-----------------+   |
   |                        +--------------+                         |
   |   +----------------+   +--------------+   +-----------------+   |
   +---| Ret-stack RAM  |<--| RSP '191 ±1  |   | I/O addr mux    |<--+ LIT (OUT)
 RTOS  | 2114, 16-deep  |   | (/CS,/WE     |   | '157            |<--- TOS (IN)
       +----------------+   |  derived)    |   +--------+--------+
        data-in: PC+1/TOS   +--------------+            |
        (RDIN_SEL)                                      v
                                               +-----------------+
   CLOCK / RESET / SINGLE-STEP                 | I/O ports       |
   one tick = one instruction                  | W-bit, separate |
   CLK gate <- HALT (CLK = STOP)               | space           |
                                               +-----------------+
```

---

# 5. Full 74181 Function Table (as modelled)

Active-HIGH operands and outputs, exactly as the simulator's `Hc181` computes them (A = TOS,
B = NOS). M = H selects logic (carry chain inhibited, Cn+4 held HIGH); M = L selects
arithmetic with carry-in. "plus" is the arithmetic sum; arithmetic rows are shown with no
carry in (Cn = H) — an incoming carry (Cn = L) adds 1. The rows the mini uses are **bold**.

| S3–S0 | Logic (M = H) | Arithmetic (M = L, Cn = H) |
|---|---|---|
| **0000** | **/A** (NOT) | A |
| 0001 | /A + /B | A + B |
| 0010 | /A · B | A + /B |
| 0011 | 0 | minus 1 |
| 0100 | /(A·B) | A plus A·/B |
| 0101 | /B | (A+B) plus A·/B |
| **0110** | **A ⊕ B** (XOR) | **A minus B minus 1** (SUB) |
| 0111 | A · /B | A·/B minus 1 |
| 1000 | /A + B | A plus A·B |
| **1001** | /(A⊕B) (XNOR) | **A plus B** (ADD / ADC) |
| 1010 | B | (A+/B) plus A·B |
| **1011** | **A · B** (AND) | A·B minus 1 |
| 1100 | 1 (all ones) | A plus A (left shift) |
| 1101 | A + /B | (A+B) plus A |
| **1110** | **A + B** (OR) | (A+/B) plus A |
| **1111** | **A** (TST) | A minus 1 |

Usage (ALU rev 2): 1001 arith = `ADD` / `ADC`; 0110 arith = `SUB` and `CMP` (A minus B
minus 1, with Cn = L → A − B = TOS − NOS; CMP discards the result); 0110 logic = `XOR`;
0000 logic = `NOT`; 1111 logic = `TST` (F = A = TOS, flags only); 1011 logic = `AND`;
1110 logic = `OR`. Cn+4 is the active-HIGH carry-out: on subtract, Cn+4 = 1 means no borrow
(A ≥ B) — and with the rev-2 per-flag masking it is only latched on the ops that define C.

---

# 6. Detailed Descriptions — Instructions

## SYS family (opcode 0) — operand-less

**NOP** — Null single-cycle path: asserts the idle/default vector, advances PC+1. Proves a
cycle can pass changing nothing but the program counter.

**HALT** — Asserts `CLK = STOP`. PC and all architectural state freeze; `PCSEL` is
don't-care. The front panel stays lit and settled for inspection.

**RET** — Absolute return: PC ← RTOS, RSP −1; the return-stack read is implicit. All control
flow is absolute, so RET reaches the entire code space.

**DUP** — Push a copy of the top. The '194 holds (new TOS = old TOS) while NOS ← old TOS;
old NOS spills to the data-stack 2114 (`DSTK = wr`) as DSP counts up (+1).

**DROP** — Pop. New TOS = old NOS; NOS refilled from the data-stack read at DSP−1
(`DSTK = rd`), DSP −1.

**SWAP** — Exchange the top two: TOS ← old NOS, NOS ← old TOS. ΔDSP = 0, no stack memory — a
pure cross path between the two top latches.

**>R** — Move the data top to the return stack. Data pops (−1, NOS refilled from stack
read); return writes TOS and counts up (+1). With R> it synthesises ROT.

**R>** — Move the return top to the data stack. Data pushes (+1, NOS ← old TOS, old NOS
spills); return reads and counts down (−1).

## ALU family (opcode 1) — sets N / Z / C

ALU result → TOS, second operand from NOS via the data-stack read; binary ops are −1,
unary/shift are 0. Flags are written only by this class.

**ADD** — TOS + NOS. '181 arith (M=0), select 1001, Cn = H (no inject). Result → TOS,
NOS ← stack read, ΔDSP −1; all three flags from the '181 (C = Cn+4).

**ADC** — Add with carry: as ADD but Cn ← the C flag. The path a narrow `W` uses to build
wide-`W` arithmetic across words.

**SUB** — '181 subtract — A minus B minus 1 with Cn = L, i.e. TOS − NOS (select 0110,
arith). C = 1 means no borrow (TOS ≥ NOS).

**XOR** — Logic mode (M=1), select 0110 — the other '181 mode. Shares its select with SUB;
only the mode bit differs. N/Z latch; **C held** (rev 2, `C_WE` deasserted).

**NOT** — Unary, select 0000 logic: one operand, no NOS read. ΔDSP = 0, NOS held. N/Z latch;
C held.

**TST** — Non-destructive flags-only test of TOS: '181 select 1111 logic (F = A = TOS),
`NZ_WE` asserted, `TOS_MODE = hold`, no stack or memory access (ΔDSP = 0). N = top bit of
TOS, Z = TOS is zero; C held. This is the row that proves the flag write is independent of
the TOS write — the result is computed and discarded with no write at all.

**AND** — Binary pop `( a b -- a∧b )`, select 1011 logic. Result → TOS, NOS ← stack read,
ΔDSP −1. N/Z latch; C held. The bit-mask workhorse.

**OR** — Binary pop `( a b -- a∨b )`, select 1110 logic. Same control pattern as AND.

**CMP** — Non-destructive compare `( a b -- a b )`: '181 select 0110 **arith** with
Cn = L — exactly SUB — but the result is discarded: TOS holds, NOS holds, no stack access,
ΔDSP 0. Flags of TOS − NOS latch (N/Z via `NZ_WE`, C via `C_WE`): Z = equal, C = 1 = TOS ≥
NOS. Replaces a SUB-and-restore dance when both operands are still needed; the *popping*
CMP `( a b -- )` remains a forbidden −2.

**SHL** — Left single-bit shift on the '194 — a datapath the '181 never drives. ΔDSP = 0;
zero fill; C ← old top bit (`C_SRC = shf`); N/Z from the shifted result.

**SHR** — Right single-bit logical shift on the '194 — toward the LSB. Zero fill; C ← old
bottom bit.

**ASR** — Right single-bit **arithmetic** shift: the '194 serial input is fed the old top
bit (sign) by the '153 fill mux. C ← old bottom bit. Signed halving.

**ROL / ROR** — Single-bit plain rotates: the falling-out bit re-enters through the fill
mux (ROL: top → bottom serial-in, C ← old top bit; ROR: bottom → top serial-in, C ← old
bottom bit). Not through-carry — RCL/RCR would be a one-wire change on the '153 inputs.

## Stack / memory / I/O / control (opcodes 2–11)

**PUSH #imm** — Literal → TOS (`TOS_SRC = LIT`). NOS ← old TOS, old NOS spills, DSP +1. The
immediate is exactly `W` wide — full data range in one word.

**LOAD #addr** — Push D-mem[addr]: TOS ← DMEM (`rd@LIT`), NOS ← old TOS, DSP +1.

**STORE #addr** — Pop → D-mem[addr]: data-in is the old TOS, the literal supplies the
address (`wr@LIT`). The pop refills NOS from the stack read, DSP −1.

**IN** — Stack-form port read `( port -- value )`: the port number on TOS selects the input
port via the address mux (`IO = RD@TOS`), and the value read replaces it on TOS. NOS held,
ΔDSP = 0. This is the only instruction that drives the I/O address mux from TOS, so it is
what exercises that path; a computed port number (e.g. looping over input ports) works
directly.

**OUT #port** — Immediate-form port write `( data -- )`: writes TOS to the port named by the
literal (`IO = WR@LIT`), then pops — TOS ← old NOS, NOS ← stack read, ΔDSP −1. **Replaces the
old stack-form OUT** (whose `( data port -- data )` left a dangling value because a clean
`( data port -- )` is a forbidden −2). Writing one value to several fixed ports is DUP then
OUT #port; a run-time-computed output port is no longer single-instruction (unroll, or map
targets to fixed ports).

**BRANCH addr** — Unconditional: PC ← addr (`PCSEL = LIT`). No stack or flag interaction.

**BEQ / BCS / BMI addr** — Conditional: `PCSEL = LIT` if (Z / C / N) else +1. Flags
untouched. With N = bit `(W−1)`, BMI is the cheap "the light hit the top" test for a shifting
bit.

**CALL addr** — Push absolute PC+1 to the return stack (RSP +1, `RSTK = wr(PC+1)`), then
PC ← addr. PC+1 is the pre-incremented counter value, available before the next edge — no
adder needed.

---

# 7. Detailed Descriptions — Control Signals

## Group A — ALU / shifter / stack-top / flags

**ALU_S3..S0** — The four '181 function-select lines, emitted by the GAL from the curated
4-bit ALU operand nibble (not the nibble itself). SUB/XOR share 0110; ADD/ADC share 1001;
TST = 1111.

**ALU_M** — '181 mode: M = L arithmetic (ADD, ADC, SUB), M = H logic (XOR, NOT, TST). The
single bit separating SUB from XOR at select 0110.

**ALU_Cn** — The '181 carry-in. H = no inject (ADD), L = +1 inject (SUB), /C = inject the C
flag (ADC). One of only two flag-consuming signals.

**TOS_MODE** — '194 mode: hold (NOP, DUP, TST, CMP), load (parallel-load from `TOS_SRC` —
most ops), shL (SHL, ROL) / shR (SHR, ASR, ROR). Load is the ordinary TOS write; the shift
modes make the TOS a shifter at no extra chip, with the serial fill (0 / old top / old
bottom) chosen by the '153 mux off raw IR literal bits L3/L2.

**TOS_SRC** — Parallel-load source when `TOS_MODE = load`: LIT (PUSH), DMEM (LOAD), INP
(IN), RTOS (R>), ALU (arith/logic results), NOS (DROP, SWAP, STORE, OUT #port). Don't-care
while holding/shifting.

**NOS** — The NOS latch: hold (also TST, CMP, IN), ←TOS (DUP, SWAP, PUSH, LOAD, R>), or
←stkR (every pop).

**NZ_WE / C_WE** — Per-flag latch enables (rev 2). `NZ_WE` asserts on every live ALU
sub-op; `C_WE` only on the ops that define C — the arithmetic group, the five shifts, and
CMP. Splitting the enables is what lets logic ops preserve C and what makes the reserved
nibbles A/D truly inert. Both decode from instruction bits alone. TST and CMP assert their
enables while TOS holds — flags latch with no result write at all.

**C_SRC** — Chooses the C flip-flop source: 181 (Cn+4 — ADD, ADC, SUB, CMP) or shf (the bit
shifted out of the '194 — old top on SHL/ROL, old bottom on SHR/ASR/ROR). A one-bit mux the
GAL selects; don't-care wherever `C_WE` is deasserted.

## Group B — sequencing / memory / I/O

**PCSEL** — NEXTPC mux: +1 (sequential), LIT (branch/call target from the IR literal), RTOS
(return). Conditional branches make it a function of opcode and the selected flag — the only
place a flag feeds control.

**DSP** — Data-stack pointer '191: count enable (/CTEN) + direction (D//U, LOW = up). 0 hold,
+1 push, −1 pop. Every
instruction moves it by exactly +1/0/−1 — single up/down counter, single-port RAM.

**DSTK** — Data-stack 2114: – idle, wr (push spills old NOS), rd (pop fills NOS). **Not an
independent control** — /CS = the DSP count-enable, /WE = the DSP direction line (phase-gated
for writes). A held DSP deasserts /CS, so an idle RAM is fully high-Z.

**RSP** — Return-stack pointer '191: 0 / +1 / −1. +1 on CALL and >R, −1 on RET and R>.

**RSTK** — Return-stack 2114: – idle, wr(PC+1) on CALL, wr(TOS) on >R. /CS, /WE derived from
RSP exactly as DSTK is from DSP; only the data-in select (`RDIN_SEL`: PC+1 vs TOS) is a
decoded control. Read implicit when RTOS is consumed by RET or R>.

**DMEM** — Data-memory 2114, address = IR literal: – idle, rd@LIT (LOAD), wr@LIT (STORE).
Decoded (no pointer to derive from).

**IO** — I/O strobe + address-source mux: – idle, RD@TOS (IN, port = TOS), WR@LIT
(OUT #port, port = literal). IN and OUT #port together drive both mux inputs — the TOS side
and the IR side.

**CLK** — run, or STOP on HALT — freezing PC and all state for a settled front-panel
snapshot.

---

# 8. Worked Examples

Programs use only mini opcodes. Bytes use the ratified encoding in §2.

## 8.1 Walking bit on the TOS LEDs

A single lit bit walks left across the 4-bit TOS LED field and wraps. SHL sets C from the bit
shifted out of the top; BCS re-seeds when it falls off bit 3.

```
        PUSH #1       ; 0x21  seed bit 0          TOS = 0001
loop:   SHL           ; 0x16  walk left; C = old top bit
        BCS reseed    ; 0x9-  fell off the top -> reseed
        BRANCH loop   ; 0x7-
reseed: DROP          ; 0x04  discard the now-zero TOS
        PUSH #1       ; 0x21  re-seed at bit 0
        BRANCH loop   ; 0x7-
```

For a true bounce (Knight Rider), test BMI after SHL to reverse at the top bit (N = bit 3)
and shift right with SHR until the bit reaches bit 0 — the intended use of BMI, giving the N
LED real work.

## 8.2 8-bit add on the 4-bit machine (ADC)

Add A = (Ah:Al) and B = (Bh:Bl), nibbles in D-mem 0–3, result to 4–5. The carry survives the
STORE because only ALU ops write flags — the point of ADC.

```
        LOAD #1       ; Al                  TOS = Al
        LOAD #3       ; Bl                  TOS = Bl, NOS = Al
        ADD           ; Al + Bl, sets C;    TOS = low nibble
        STORE #5      ; -> Rl  (C preserved: STORE writes no flags)
        LOAD #0       ; Ah
        LOAD #2       ; Bh                  TOS = Bh, NOS = Ah
        ADC           ; Ah + Bh + C         TOS = high nibble
        STORE #4      ; -> Rh
        HALT          ; 0x01
```

## 8.3 Read two ports, write their sum (I/O forms)

Stack-form IN takes its port from TOS and replaces it with the value read (ΔDSP 0);
immediate OUT #port writes TOS to a fixed port and pops. Between them they drive both sides
of the I/O address mux — IN from TOS, OUT #port from the literal.

```
        PUSH #3       ; port number 3        stack: 3
        IN            ; read port 3          stack: v3   (port from TOS)
        PUSH #4       ; port number 4        stack: v3 4
        IN            ; read port 4          stack: v3 v4
        ADD           ; v3 + v4, sets flags  stack: sum
        OUT #7        ; write sum to port 7; pop   stack: (empty)

        ; one value to several FIXED ports needs DUP (OUT #port pops):
        PUSH #V
        DUP           ; V V
        OUT #1        ; write port 1; pop    stack: V
        DUP           ; V V
        OUT #2        ; write port 2; pop    stack: V
        OUT #3        ; write port 3; pop    stack: (empty)
```

## 8.4 ROT as a macro

ROT `( a b c -- b c a )` is never a single opcode (not single-cycle on a single-port stack).
The shared macro on both machines, with the stack shown bottom→top:

```
        ; ( a b c -- b c a )      start: a b c
        >R            ; R: c            a b
        SWAP          ;                 b a
        R>            ; R: --           b a c
        SWAP          ;                 b c a
```

---

# 9. Memory, I/O & Front Panel

## Memory and I/O map

| Space | Size | Notes |
|---|---|---|
| I-memory | 16 instr × 8-bit | one 8-bit chip (only 4 address lines used); Harvard, parallel with data access |
| D-memory | 16 cells × 4-bit | 2114; address = IR literal; /CS and /WE decoded from LOAD/STORE |
| Data stack | 16 deep × W | 2114, single-port; free-running modulo-16 '191 pointer (no overflow detect); /CS, /WE derived from the pointer |
| Return stack | 16 deep × W | 2114, single-port; holds a PC; '191 pointer; separate from data stack; /CS, /WE derived from the pointer |
| I/O | W-bit ports | separate I/O space (Z80-style); IN port = TOS (stack form), OUT #port port = literal |

Stack pointers are free-running mod-16 counters — wraparound is a visible front-panel
diagnostic, not a trapped error, the same philosophy as the full machine.

## Front-panel LEDs (scaled to W = 4)

| Field | LEDs | Meaning |
|---|---|---|
| PC | 4 | position in program |
| IR | 8 | current instruction, fully visible |
| TOS | 4 | top of data stack — the "accumulator" in spirit; also the SHL/SHR display |
| NOS | 4 | next on stack |
| DSP | 4 | data-stack depth |
| RTOS | 4 | top of return stack (where we'll return to) |
| RSP | 4 | return-stack depth |
| N / Z / C | 3 | flags, set by every ALU op |

Narrower fields are *easier* to read at a glance than 8-bit ones — arguably a better blinky
for teaching the architecture.

## Core chip complement (W = 4)

On-hand counts are from `ChipInventory.md` (all-HC baseline, counts verified 11 June 2026).
"In transit" quantities from that inventory are ordered and counted as available here.

| Block | Part | Qty | On-hand / sourcing |
|---|---|---|---|
| ALU | 74181 | 1 | 10 HC in transit (8 LS as backup — LS build needs the HCT fence; see Sourcing notes) |
| TOS shift register | 74194 | 1 | 10 in transit |
| TOS serial-fill mux | 74153 | 1 | ALU rev 2 (fill = 0 / old top / old bottom, select = raw IR L3/L2) — check `ChipInventory.md` |
| NOS latch | 74374 (or '273) | 1 | 4 HC on hand; 16 HCT374 in transit (HC374 unobtainable; HCT is a drop-in) |
| Data-stack pointer | 74191 | 1 | 20 in transit — replaces the unobtainable HC169 (see Sourcing notes) |
| Return-stack pointer | 74191 | 1 | (same stock as DSP) |
| Data / return / D-memory | 2114 SRAM | 3 | not in 74-series inventory — source / confirm |
| Program counter | 74161 | 1 | 8 on hand |
| Instruction register | 74374 (8-bit) | 1 | '374 stock as above |
| Address-source mux | 74157 | 1 | 8 on hand, 20 in transit |
| I-memory | 8-bit EEPROM (e.g. 28C16) | 1 | TTLSim supports 28C-series; source / confirm |
| Flags N/Z/C | 7474 | 2 | 8 on hand |
| Decode / control | GAL16V8 × 4 | 4 | 16V8 confirmed in stock; ATF16V8B as active-production alt; **no 20V8 needed** |
| Stack-strobe glue | '04 / '00 (inverter + phase gate) | ~1 | derives stack /CS, /WE from the pointers; folds into existing glue |
| Clock / reset / step | 7414 + glue | ~2 | '14: 12 HC on hand |

**Core total: ~18–20 ICs.** (The earlier ~14–16 sketch assumed 1–2 decode GALs; the detailed
control-signal partition lands at four 16V8s, adding ~2–3, and ALU rev 2 adds the '153
serial-fill mux.) Front-panel LED drivers/latches are additional to this core list.

**Sourcing notes (June 2026 inventory):**

- **'169 → '191.** The 74HC169 is unobtainable; both stack pointers are 74HC191s. Same role
  (synchronous up/down count with hold), three wiring-level differences: the direction sense
  inverts ('169 U//D high = up; '191 D//U LOW = up — the GAL direction equations flip), the
  enable is the single active-low /CTEN, and load is asynchronous — /LOAD with the data
  inputs tied low doubles as the reset-to-zero, sharing the PC's reset net (the '191 has no
  clear pin). The inverted senses land on the 2114 strobes directly — see "Stack-RAM strobes
  are derived".
- **'181 family.** Primary part is the 74HC181; the 74LS181 backups may only be used with
  HCT receivers at every '181 output boundary (LS VOH ≥ 2.7 V cannot reliably drive plain-HC
  VIH ≈ 3.5 V) — HCT04 / HCT245 / HCT374 are stocked as the fence parts. The all-HC build
  needs no fence.
- **'374.** The HC374 is unobtainable; new stock is HCT374, a drop-in for every '374 slot
  here.

## Open decisions / notes

- **Dropped forms.** The *popping* two-operand CMP, stack-form OUT, and stack-form STORE are
  removed **on both machines** — each was the −1 residue of a forbidden −2 (single-port
  stack, one access per cycle), leaving a dangling operand. TST (one-operand) and the rev-2
  non-destructive CMP `( a b -- a b )` cover comparisons; OUT #port and STORE #addr replace
  the stack forms; stack-form IN survives because it is genuinely 0-delta.
- **Computed I/O.** Stack-form IN still reads a run-time port (port from TOS). Output to a
  run-time-computed port is no longer single-instruction — unroll to fixed OUT #port, or map
  targets to fixed ports. Fixed-port fan-out of one value is DUP + OUT #port.
- **Immediate-ALU family (deferred).** CMP #imm / ADD #imm / SUB #imm / AND #imm would all be
  clean 0-delta ops, but each needs a 2:1 mux on the ALU B input (NOS vs IR literal) — one
  '157, breaking the "B is always NOS" simplicity. Reserved opcodes 12–15 and the remaining
  spare GAL macrocells (one on GAL 3, two on GAL 4 — ALU rev 2 spent one of GAL 3's on
  `C_WE`) can host them later; B stays wired to NOS for now.
- **Reserved opcodes 12–15.** Other candidates: indirect CALL (target from a small address
  register) for reach beyond the 16-instruction direct space; a second ALU bank.
- **Branch set is shared.** The mini carries BEQ/BCS/BMI (one per flag). Any branch the mini
  *uses* must also exist on the full machine, or programs stop transferring. Keep the shared
  set at three unless the full design is updated to match. (Signed branches would need a V
  flag — parked: GAL 4 is an exact 18-pin fit and a V input would force a 20V8.)
- **ROT is a macro** on both machines (`>R SWAP R> SWAP`), never an opcode. Rotates ROL/ROR
  **exist as of ALU rev 2** on both machines — single-bit plain rotates through the '153
  serial-fill mux, alongside ASR. The shift unit remains single-bit per cycle;
  rotate-through-carry would be a one-wire change on the '153 inputs.
- **Stack write timing is pinned:** the derived /WE is qualified by the clock phase so the
  write commits before the '191 advances the address. Stack read addressing (pre- vs
  post-increment) remains a board-level wiring choice.
- **Sub-op nibble codes (§2) are ratified (ALU rev 2)** — the four GAL fuse maps are
  generated from them. Renumbering means re-burning all four devices and retargeting the
  assembler.

---

# Revision History

**ALU rev 2 (June 2026):** per-flag write masking (`FLAG_WE` → `NZ_WE` + new `C_WE` on
GAL 3 pin 13 — logic ops now preserve C, retiring the "logic ops latch C = 1" gotcha); new
sub-ops AND (8), CMP (9, non-destructive `( a b -- a b )`), ASR (B), OR (C), ROL (E),
ROR (F), with nibbles A/D reserved; one '153 serial-fill mux on the '194 serial pins,
selected by raw IR L3/L2. Sub-op encoding **ratified**; GAL fuse maps regenerated
(`GAL1`–`GAL3` rev 02, `GAL4` rev 03), brute-force verified over all 256 IR values. V flag,
GAL-182 lookahead and barrel shifter evaluated and rejected — see `Blinky_ALU_Rev2.md`.

**June 2026 update:** stack-pointer part is now the 74HC191 (HC169 unobtainable): direction
equations inverted, /CTEN enable, asynchronous /LOAD-low as reset. '181 sourcing updated (HC
primary; LS backup behind an HCT fence). Chip-complement counts refreshed against the all-HC
`ChipInventory.md` of 11 June 2026.

**This revision (master document)** consolidates the two previous `.md` docs and the interim
PDF into one file, and **ratifies** the PDF's refinements as the current design:

- `TST` replaces two-operand `CMP` (flags-only, non-destructive, '181 select 1111 logic).
- I/O forms swapped: `OUT` is now immediate (`OUT #port`, pops), `IN` is now stack-form
  (port from TOS, ΔDSP = 0). Stack-form OUT and stack-form STORE are dropped on both
  machines.
- Stack-RAM /CS, /WE are **derived from the '191 pointer lines** (count-enable and up/down),
  not decoded — control vector 28 decoded lines + 4 derived strobes.
- GAL partition is **four 16V8s — no 20V8** (supersedes the earlier 3× 16V8 + 1× 20V8
  partition; the flag-reading device now fits a 16V8 at 6 outputs / 11 inputs).
- Stack writes are phase-gated: derived /WE qualified by clock phase so the write commits
  inside the address-stable window, before the pointer advances.
- Proposed full byte encoding added (sub-op nibbles still open).

**Sources:** previous `Mini_Blinky_CPU.md`, `Mini_Blinky_Control_Signals.md`, `Hc181.cs`,
`ChipInventory.md`. '194 / '191 / 2114 control polarities are datasheet-derived and
provisional until those parts are modelled in the simulator; '181 codes and the function
table follow the sim's `Hc181` active-HIGH implementation.
