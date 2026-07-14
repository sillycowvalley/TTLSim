# Thumby 16-bit Instruction Set — User Reference

Architecture reference for programmers and compiler writers. This document is
derived from the ratified ISA; where it describes hardware behaviour it is
normative. Section 12 (C ABI) is a software convention for the C toolchain and
is marked as such.

---

## 1. Machine model

| Property | Value |
|---|---|
| Word size | 16 bits |
| Addressing | **Word-addressable** — every address names a 16-bit word. There is no byte addressing. |
| Address space | 64K words (128 KB equivalent), Von Neumann: code, data, and literal pools share one memory and one bus |
| I/O | Memory-mapped only; no IN/OUT instructions |
| Registers | r0–r7, 16 bits each, general purpose |
| PC | Dedicated counter hardware, **not** in the register file, not addressable as a register |
| Flags | N Z C V — consumed only by conditional branches and the carry-in of ADC/SBC; no predication |
| Endianness | Not applicable at the ISA level — memory transfers are whole words |
| Instruction width | 16 bits, one word per instruction, always |
| Reset | PC = 0x0000; registers and flags power up **undefined**; interrupts disabled (IE = 0). The reset stub must establish everything. |

`PC+1` throughout this document denotes the pre-incremented counter value — the
address of the next instruction — which the hardware provides without an adder.

### Register conventions (software, not enforced by hardware)

| Register | Role |
|---|---|
| r0–r5 | General |
| r6 | Stack pointer (SP) |
| r7 | Link register (LR) — written by `JAL`/`JALR` |

### Execution model

Fixed short sequencer: FETCH → EXECUTE, with a MEM state appended for memory
access (class `10` instructions and the PC-relative literal load), and an INT
state for interrupt entry. No microcode. Every instruction is one word; timing
is uniform within a class.

---

## 2. The inline barrel shifter

The defining feature of the ISA: a barrel shifter sits permanently **in series
on the B operand path**, in front of the ALU, on every instruction. There are
no dedicated shift instructions — the four MOVs *are* the shift instructions,
and every two-operand ALU op carries a free `LSL #i` on its source operand.

**Shifter contract:**

- Types: LSL, LSR, ASR, ROR. There is no ROL (see aliases, §11).
- Amount sources: `imm4` (class 00), B-port bits [3:0] (SHF), hardwired 8
  (ORH, JA/JAL), hardwired 0 (everything else), or `ss` (scaled memory form).
- **Shift-by-zero is identity and leaves C unchanged.** There is no
  "shift #0 means #16" trick.
- **Variable amounts are taken mod 16** (x86 behaviour, diverges from ARM).
- Shifter carry: C receives the last bit shifted out, only when the effective
  amount ≠ 0.

---

## 3. Format map

The top two bits of every instruction select the class:

| Bits 15:14 | Class | Contents |
|---|---|---|
| `00` | ALU | op4, inline-shifted register operand |
| `01` | Immediate | op3 + Rd + imm8 |
| `10` | Memory | LDR/STR, immediate or scaled-register offset |
| `11` | Control | branches, JA/JAL, JR/JALR, flag & interrupt ops, PUSH/POP (reserved) |

---

## 4. Class 00 — ALU

```
 15 14 | 13 12 11 10 | 9  8  7  6 | 5  4  3 | 2  1  0
  0  0 |     op4     |    imm4    |    Rm   |    Rd
```

General form: **Rd = Rd ⊙ shift(Rm, #imm4)**. All operations except MOV/MVN are
destructive (Rd is both source and destination). MOV and MVN are
non-destructive — Rd and Rm are independent — so "shift into a temp" is a
single instruction.

| op4 | Syntax | Operation | Flags |
|---|---|---|---|
| 0000 | `MOV.LSL Rd, Rm, #i` | Rd = Rm << i | N Z C\* |
| 0001 | `MOV.LSR Rd, Rm, #i` | Rd = Rm >> i, zero fill | N Z C\* |
| 0010 | `MOV.ASR Rd, Rm, #i` | Rd = Rm >> i, sign fill | N Z C\* |
| 0011 | `MOV.ROR Rd, Rm, #i` | Rd = Rm ror i | N Z C\* |
| 0100 | `ADD Rd, Rm, LSL #i` | Rd = Rd + (Rm << i) | N Z C V |
| 0101 | `ADC Rd, Rm, LSL #i` | Rd = Rd + (Rm << i) + C | N Z C V |
| 0110 | `ORN Rd, Rm, LSL #i` | Rd = Rd ∨ ¬(Rm << i) | N Z |
| 0111 | `SBC Rd, Rm, LSL #i` | Rd = Rd − (Rm << i) − ¬C | N Z C V |
| 1000 | `AND Rd, Rm, LSL #i` | Rd = Rd ∧ (Rm << i) | N Z |
| 1001 | `ORR Rd, Rm, LSL #i` | Rd = Rd ∨ (Rm << i) | N Z |
| 1010 | `EOR Rd, Rm, LSL #i` | Rd = Rd ⊕ (Rm << i) | N Z |
| 1011 | `BIC Rd, Rm, LSL #i` | Rd = Rd ∧ ¬(Rm << i) | N Z |
| 1100 | `MVN Rd, Rm, LSL #i` | Rd = ¬(Rm << i) | N Z |
| 1101 | `CMP Rd, Rm, LSL #i` | Rd − (Rm << i), result discarded | N Z C V |
| 1110 | `TST Rd, Rm, LSL #i` | Rd ∧ (Rm << i), result discarded | N Z |
| 1111 | `SHF.tt Rd, Rm` | Rd = shift_tt(Rd, Rm[3:0]) | N Z C\* |

C\* = C takes the last bit shifted out when the amount ≠ 0, otherwise C is
unchanged.

Notes:

- `#i` is 0–15. Omitting `LSL #i` in assembly means amount 0.
- **SHF** is the variable-amount shift, destructive: the value is Rd, the
  amount is Rm[3:0] (mod 16). The type `tt` ∈ {LSL, LSR, ASR, ROR} is encoded
  in imm4[3:2]; imm4[1:0] are reserved-zero.
- **There is no SUB, register or immediate.** Subtraction is `SEC` followed by
  `SBC` — C is active-high no-borrow (6502 discipline). CMP is unaffected: its
  −1 inject is hardwired, so comparisons never depend on prior flag state.
- **There is no RSB** (reverse subtract) — the ALU offers exactly one subtract
  orientation. ×(2ⁿ−1) therefore costs three instructions:
  `MOV.LSL temp, x, #n` ; `SEC` ; `SBC temp, x`.
- **There is no CMN** — synthesize when needed.

---

## 5. Class 01 — Immediate

```
 15 14 | 13 12 11 | 10  9  8 | 7  6  5  4  3  2  1  0
  0  1 |   op3    |    Rd    |          imm8
```

imm8 is zero-extended (0–255) in every case.

| op3 | Syntax | Operation | Flags |
|---|---|---|---|
| 000 | `MOV Rd, #imm8` | Rd = imm8 | N Z |
| 001 | `CMP Rd, #imm8` | Rd − imm8, result discarded | N Z C V |
| 010 | `ADD Rd, #imm8` | Rd = Rd + imm8 | N Z C V |
| 011 | `SBC Rd, #imm8` | Rd = Rd − imm8 − ¬C | N Z C V |
| 100 | `LDR Rd, [PC, #imm8]` | Rd = mem[PC+1 + imm8] | — |
| 101 | `ADR Rd, #imm8` | Rd = PC+1 + imm8 | — |
| 110 | `ORH Rd, #imm8` | Rd = Rd ∨ (imm8 << 8) | N Z |
| 111 | `ADC Rd, #imm8` | Rd = Rd + imm8 + C | N Z C V |

Notes:

- **16-bit constants:** `MOV Rd, #lo8` ; `ORH Rd, #hi8` — two instructions, no
  memory traffic. Alternatively, the literal pool: `LDR Rd, [PC, #imm8]` reads
  a pool word interleaved with code (base = next-instruction address, forward
  reach 256 words). The literal load is a memory access and takes the MEM
  state, exactly like class 10.
- Immediate subtraction is `SEC` + `SBC Rd, #imm8`.
- `ADC Rd, #0` is the multi-precision carry ripple — no zero register needed.
- `ADR` forms PC-relative addresses for jump tables and pool-free address
  computation; its result feeds `JR`.

---

## 6. Class 10 — Memory

All memory access is whole-word. Flags are never touched by memory
instructions. Two forms, selected by bit 13.

**Immediate offset** (bit 13 = 0):

```
 15 14 | 13 | 12 | 11 10  9  8  7  6 | 5  4  3 | 2  1  0
  1  0 |  0 | L  |        imm6       |    Rb   |    Rd
```

| L | Syntax | Operation |
|---|---|---|
| 0 | `STR Rd, [Rb, #imm6]` | mem[Rb + imm6] = Rd |
| 1 | `LDR Rd, [Rb, #imm6]` | Rd = mem[Rb + imm6] |

imm6 is a **word** offset, 0–63 — direct reach for struct fields and stack
slots.

**Scaled register offset** (bit 13 = 1):

```
 15 14 | 13 | 12 | 11 | 10  9 | 8  7  6 | 5  4  3 | 2  1  0
  1  0 |  1 | L  | 0  |  ss   |   Ro    |    Rb   |    Rd
```

| L | Syntax | Operation |
|---|---|---|
| 0 | `STR Rd, [Rb, Ro, LSL #ss]` | mem[Rb + (Ro << ss)] = Rd |
| 1 | `LDR Rd, [Rb, Ro, LSL #ss]` | Rd = mem[Rb + (Ro << ss)] |

ss = 0–3. `LSL #0` is plain register-indexed addressing (word arrays);
#1–3 scale for 2/4/8-word records. Bit 11 is reserved-zero.

---

## 7. Class 11 — Control

### Conditional branch (`1100`)

```
 15 14 13 12 | 11 10  9  8 | 7  6  5  4  3  2  1  0
  1  1  0  0 |    cond     |     offset8 (signed)
```

`B<cond> label` — if the condition holds, PC = PC+1 + offset8. Offsets are in
**words**, sign-extended, reach ±128. `B label` assembles as `BAL` (cond 1110).
Anything farther than ±128 words is a `JA`.

| cond | Test | cond | Test | cond | Test | cond | Test |
|---|---|---|---|---|---|---|---|
| 0000 EQ | Z | 0100 MI | N | 1000 HI | C ∧ ¬Z | 1100 GT | ¬Z ∧ N=V |
| 0001 NE | ¬Z | 0101 PL | ¬N | 1001 LS | ¬C ∨ Z | 1101 LE | Z ∨ N≠V |
| 0010 CS | C | 0110 VS | V | 1010 GE | N=V | 1110 AL | always |
| 0011 CC | ¬C | 0111 VC | ¬V | 1011 LT | N≠V | 1111 | **Reserved** (SWI slot) |

Signedness after `CMP a, b`:

| Relation | Unsigned | Signed |
|---|---|---|
| a == b | EQ | EQ |
| a ≠ b | NE | NE |
| a < b | CC | LT |
| a ≤ b | LS | LE |
| a > b | HI | GT |
| a ≥ b | CS | GE |

### Absolute jump / jump-and-link (`1101`)

```
 15 14 13 12 | 11 | 10  9  8 | 7  6  5  4  3  2  1  0
  1  1  0  1 | L  |    Rs    |          imm8
```

| L | Syntax | Operation |
|---|---|---|
| 0 | `JA Rs, #imm8` | PC = (Rs << 8) ∨ imm8 |
| 1 | `JAL Rs, #imm8` | r7 = PC+1 ; PC = (Rs << 8) ∨ imm8 |

Rs holds the target's **high byte** (page); the immediate supplies the low
byte. `MOV rX, #hi8` then `JA rX, #lo8` reaches anywhere in 64K in two
instructions — and because Rs carries the page, a cluster of calls into one
256-word page pays its `MOV` once and one instruction per call thereafter.

### Register branches, flag & interrupt ops (`1110`)

```
 15 14 13 12 | 11 10  9 | 8  7  6 | 5  4  3 | 2  1  0
  1  1  1  0 |   sub3   | 0  0  0 |    Rs   | 0  0  0
```

| sub3 | Syntax | Operation | Flags |
|---|---|---|---|
| 000 | `JR Rs` | PC = Rs | — |
| 001 | `JALR Rs` | r7 = PC+1 ; PC = Rs | — |
| 010 | `CLC` | C = 0 | C |
| 011 | `SEC` | C = 1 | C |
| 100 | `RETI` | NZCV ← shadow ; IE = 1 ; PC ← IPC | N Z C V |
| 101 | `EI` | IE = 1 | — |
| 110 | `DI` | IE = 0 | — |
| 111 | — | **Reserved** | |

The Rs field is reserved-zero for CLC/SEC/RETI/EI/DI. `RET` is an alias for
`JR r7`.

### Class `1111` — Reserved

Reserved for a future PUSH/POP register-list format:

```
 15 14 13 12 | 11 | 10  9  8 | 7  6  5  4  3  2  1  0
  1  1  1  1 | L  | reserved |         rlist8
```

Not implemented. Compilers must not emit it; frames are built with explicit
SP arithmetic and LDR/STR (see §13).

---

## 8. Flag rules (consolidated)

- **N, Z** — written by every class-00 op and by MOV/CMP/ADD/ADC/SBC/ORH in
  class 01, from the 16-bit result. Never written by memory or control
  instructions (CLC/SEC write C only).
- **C** — one flag, five sources:
  - **adder carry-out** for ADD/ADC/SBC/CMP (register and immediate forms).
    Active-high **no-borrow** on subtract: after `CMP a, b` or a subtract,
    C = 1 means a ≥ b unsigned.
  - **shifter last-bit-out** for the MOV.x shifts and SHF, when the amount ≠ 0.
  - **constant 0 / 1** for CLC / SEC.
  - **held** in every other case — all logic ops (including ORH), amount-0
    shifts, memory, control.
- **V** — signed overflow, written only by ADD/ADC/SBC/CMP (register and
  immediate). The operand for the V computation is the **post-shift** B value.
- **CMP/TST** write flags with the register write-back suppressed.
- **Subtract discipline:** there is no SUB. A free-standing subtract is
  `SEC` ; `SBC`. Multi-precision continues with further `SBC`s. The add side
  offers both disciplines (ADD and ADC).
- Flag state is a **multi-instruction protocol** (`SEC`…`SBC`, `ADD`…`ADC`);
  the interrupt hardware shadows NZCV so these sequences are interrupt-safe
  (§9). A compiler need not disable interrupts around them.

---

## 9. Interrupts

Single level, one active-low IRQ line (wire-OR across peripherals), sampled at
instruction boundaries when IE is set. Dispatch is polled — the ISR reads
device status registers to find the source.

**Entry** (the INT state, all in parallel):

- IPC ← PC (return address, dedicated latch — not in the register file, not
  addressable)
- shadow ← NZCV
- IE ← 0 (no nesting; the shadows are one deep)
- PC ← 0x0002 (hardwired vector, no vector table)

**Return:** `RETI` restores NZCV from the shadow, sets IE, and loads PC from
IPC.

Because flags are shadowed, an ISR may clobber them immediately on entry — an
ISR arriving with zero free registers can open a stack frame at once:

```
isr:    SEC                 ; flags are shadowed — clobber freely
        SBC  r6, #4         ; open a frame
        STR  r0, [r6, #0]
        STR  r1, [r6, #1]
        ...
        RETI
```

The only standing software contract is that **r6 always holds a valid stack
pointer**. No register is sacrificed to interrupt linkage.

**Vector map:**

| Address | Contents |
|---|---|
| 0x0000–0x0001 | Reset stub: `MOV rX, #hi8` ; `JA rX, #lo8` — register clobber is legal only here |
| 0x0002 | IRQ entry: inline ISR or a `B isr` trampoline — must clobber nothing |
| 0x0003 | Reserved (SWI) |
| 0x0004 | General code begins |

---

## 10. What the hardware does not provide

A compiler must synthesize or call runtime routines for all of the following:

| Missing | Strategy |
|---|---|
| SUB / RSB / CMN | `SEC` + `SBC`; reverse operands or synthesize |
| Multiply | Shift-add sequences for constants (§11); runtime routine for variable × variable (ADD/ADC + shifts) |
| Divide / modulo | Runtime routine (shift-subtract restoring division on SEC/SBC/BCS) |
| Byte load/store | None — the machine is word-addressable. Packed bytes require explicit shift/mask sequences |
| PUSH/POP | Explicit SP arithmetic + LDR/STR (the register-list format is reserved, not implemented) |
| Predication | Branch around |
| Three-operand arithmetic | Copy first with a non-destructive MOV, then operate |

---

## 11. Idioms and aliases

### Aliases

| Write | Assembles as |
|---|---|
| `MOV Rd, Rm` | `MOV.LSL Rd, Rm, #0` |
| `RET` | `JR r7` |
| `B label` | `BAL label` |
| `ROL Rd, Rm, #n` | `MOV.ROR Rd, Rm, #(16−n)` — **C differs**: bit out the bottom, not the top |
| subtract | `SEC` ; `SBC` |
| 16-bit constant | `MOV Rd, #lo8` ; `ORH Rd, #hi8` |
| far jump / call | `MOV rX, #hi8` ; `JA rX, #lo8` (or `JAL`) |
| carry ripple | `ADC Rd, #0` |

Register-amount ROL: negate the amount, then `SHF.ROR` — the mod-16 wrap makes
the negate sufficient. Same C caveat.

### Codegen idiom gallery

| Task | Code |
|---|---|
| ×5 | `ADD r0, r0, LSL #2` |
| ×10 | `ADD r0, r0, LSL #2` ; `MOV.LSL r0, r0, #1` |
| ×7 | `MOV.LSL r1, r0, #3` ; `SEC` ; `SBC r1, r0` |
| Absolute value | `MOV.ASR r1, r0, #15` ; `EOR r0, r1` ; `SEC` ; `SBC r0, r1` |
| Signed ÷2ⁿ, round toward zero | `MOV.ASR r1, r0, #15` ; `MOV.LSR r1, r1, #(16−n)` ; `ADD r0, r1` ; `MOV.ASR r0, r0, #n` |
| Extract bits [h:l] | `MOV.LSL Rd, Rm, #(15−h)` ; `MOV.LSR Rd, Rd, #(15−h+l)` |
| Sign-extend low byte | `MOV.LSL Rd, Rm, #8` ; `MOV.ASR Rd, Rd, #8` |
| Byte swap | `MOV.ROR Rd, Rm, #8` |
| Test bit b | `MOV.LSL r1, r0, #(15−b)` ; `BMI` (or #(16−b) ; `BCS`) |
| Odd/even | `MOV.LSR r1, r0, #1` ; `BCS odd` |
| Bitfield insert at l | `BIC Rd, Rmask, LSL #l` ; `ORR Rd, Rs, LSL #l` |
| Word-array index | `LDR Rd, [Rb, Ro, LSL #0]` |
| 32-bit left shift by 1 | `ADD lo, lo` ; `ADC hi, hi` |
| 32-bit add | `ADD lo0, lo1` ; `ADC hi0, hi1` |
| 32-bit subtract | `SEC` ; `SBC lo0, lo1` ; `SBC hi0, hi1` |
| Add constant, 32-bit | `ADD lo, #imm8` ; `ADC hi, #0` |
| Galois LFSR step | `MOV.LSR r0, r0, #1` ; `BCC skip` ; `EOR r0, taps` |

---

## 12. C type mapping and ABI

Type mapping follows directly from word addressability and is effectively
dictated by the hardware:

| C type | Size (words) | Representation |
|---|---|---|
| `char`, `short`, `int` | 1 | 16 bits; `CHAR_BIT` = 16, `sizeof` everything counts words |
| `long` | 2 | Register pair / two consecutive words, low word first |
| Pointers | 1 | 16-bit word address |
| `char*` = `int*` | 1 | All pointers are word pointers — no distinction |

The calling convention below is the **toolchain convention** for the C
compiler; the hardware enforces none of it except the behaviour of JAL/JALR/JR
and the ISR's r6 contract.

| Item | Convention |
|---|---|
| Arguments | r0–r3, left to right; overflow on the stack, pushed right to left |
| Return value | r0 (32-bit values in r1:r0, r0 low) |
| Caller-saved | r0–r3 |
| Callee-saved | r4, r5 |
| SP | r6 — full-descending, word-aligned by construction |
| LR | r7 — clobbered by every call; non-leaf functions spill it |
| Flags | Caller-clobbered; never live across a call |

---

## 13. Compiler codegen notes

**Prologue / epilogue.** No PUSH/POP — frames are explicit SP arithmetic plus
imm6-offset stores, which reach 63 words without recomputation:

```
func:   SEC
        SBC  r6, #frameSize      ; open frame (≤ 255 words per SBC)
        STR  r7, [r6, #0]        ; spill LR (non-leaf)
        STR  r4, [r6, #1]        ; spill callee-saved as used
        ...
        LDR  r4, [r6, #1]
        LDR  r7, [r6, #0]
        ADD  r6, #frameSize      ; close frame
        RET                      ; JR r7
```

Leaf functions that need no locals are just body + `RET`.

**Calls.** Three shapes:

- Near (target within ±128 words): none — `B` is not a call. All calls are
  `JAL`/`JALR`.
- Direct: `MOV rX, #hi8` ; `JAL rX, #lo8`. Consecutive calls into the same
  256-word page reuse the page register — a peephole worth having.
- Computed / function pointers: address in a register, `JALR Rs`.

**Constants.** 0–255: one `MOV`. Anything else: `MOV` + `ORH` (two
instructions, always) or a literal-pool `LDR [PC, #imm8]` (one instruction +
one pool word + a MEM cycle; forward reach 256 words, pools interleaved with
code). Prefer MOV+ORH by default; the pool wins when the constant is shared or
the value is a relocated address.

**Comparisons.** `CMP` then the condition from the signedness table in §7.
CMP never needs a preceding SEC. Equality against 0 falls out of any flag-
setting op's Z — most ALU results make an explicit `CMP Rd, #0` redundant.

**Multiplication by constants.** Decompose into shift-add/shift-sub chains
using the free inline shift (`×5` is one instruction). Fall back to the
runtime multiply above a chain-length threshold.

**Switch statements.** Dense: bounds-check with `CMP`/`BHI`, then
`ADR rT, #table` ; `LDR rT, [rT, rIdx, LSL #0]` ; `JR rT` with a table of code
addresses. Sparse: compare chains.

**Struct access.** Fields at word offsets 0–63 are one instruction via the
imm6 forms. Larger structs re-base with `ADD`.

**Arrays.** `LDR/STR Rd, [Rb, Ro, LSL #ss]` covers element sizes 1, 2, 4, 8
words with no separate index arithmetic — a `long` array indexes with
`LSL #1` directly.

**Bytes and strings.** There are no bytes. The natural `char` is 16 bits and
strings are one character per word. Packed byte buffers (I/O, protocols) need
explicit `MOV.LSR`/masks to unpack and `BIC`/`ORR` to insert — treat packing
as a library concern, not a codegen concern.

**Interrupt safety.** The `SEC`…`SBC` and `ADD`…`ADC` protocols are
interrupt-safe by hardware (flag shadow). The compiler never needs `DI`/`EI`
fences around flag sequences.

**Volatile / MMIO.** Device registers are ordinary word loads and stores to
their mapped addresses; `volatile` semantics require only that the compiler
not cache or reorder those accesses — no special instructions exist or are
needed.
