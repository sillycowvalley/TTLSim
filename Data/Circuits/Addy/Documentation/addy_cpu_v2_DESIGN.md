# Addy CPU Module v2 — Design

A **16-bit TTL CPU** with a full integer ALU, data memory, and a stack —
capable of running code produced by a C subset compiler written in C#.

v2 builds directly on the v1 board stack (Blinky Clock Module v3 + Register
File Module in Thumby configuration) and replaces the v1 CPU board. The clock
module, register file, and all design principles carry forward unchanged.
What changes is the ALU, the flag set, the instruction set, and the addition
of SRAM and a data bus.

Working name still "Addy" — now everything it does, it does through the '181.

---

## What Changed from v1 and Why

**ALU: '283 + '86 → '181 + '182.**
The 74LS181 is a 4-bit ALU slice that performs add, subtract, AND, OR, XOR,
NOT, and pass — all from a 4-bit function select (S3–S0) and a mode pin (M).
Subtraction and all logic operations are built in; the separate '86 XOR
subtract stage disappears. Four '181 slices cover 16 bits. The 74LS182 carry
lookahead unit eliminates ripple carry across slices, cutting worst-case carry
propagation from ~100 ns to ~35 ns and opening up significantly higher clock
speeds.

The '181 is only available as 74LS, not 74HC. An **HCT fence** — 4× 74HCT541
buffers on the result bus — translates LS output levels (VOH ≥ 2.5 V) to
CMOS levels (VOH ≥ 4.4 V) before driving the HC register file and HC zero
detectors. HC→LS in the other direction (register file outputs into '181
inputs) is fine without buffering: HC VOH easily satisfies LS VIH.

**Flags: Z + C → Z + C + N + V.**
The '181/'182 combination makes carry and borrow available cleanly. Adding
a Negative flag (result bit 15) and an oVerflow flag (signed arithmetic
overflow) costs one additional '74 flip-flop and nothing in opcode space —
the flag register simply grows. All four flags open up a full complement of
signed and unsigned conditional branches.

**New instructions: logic ops, carry arithmetic, memory, carry/signed conditionals.**
The v1 spare opcode slots fill with AND, OR, XOR, carry-in arithmetic
(ADDC, SUBC), memory load/store (LD, ST), and carry/signed conditionals
(MOVC, MOVNC, MOVN). The class structure and instruction word format are
unchanged.

**SRAM: data memory.**
A 62256-compatible 32K×8 SRAM pair provides a 32K×16 data address space.
Combined with the 32K×16 ROM, the machine has a full **16-bit address space
of 64K words**. ROM occupies 0x0000–0x7FFF; RAM occupies 0x8000–0xFFFF.
Address bit 15 is the sole decode signal — one gate, or a spare GAL product
term. Reset vectors to 0x0000, which is always ROM, so the machine always
starts executing valid code.

**Net IC delta from v1:**
Remove 4× '283, 4× '86 (8 ICs). Add 4× '181, 1× '182, 4× HCT541 fence,
2× SRAM, 2× '374 MAR, 1× HCT245 data bus buffer, 1× '138 memory decode,
extended flag storage (≈ 15 ICs added, 8 removed). Net board grows by
roughly 7 ICs; total approximately 37 ICs.

---

## Programmer's Model

### Registers

| Reg | Role |
|-----|------|
| r0  | general purpose; return value by convention |
| r1–r4 | general purpose |
| r5  | frame pointer (FP) by convention |
| r6  | stack pointer (SP) by convention; points to top of stack |
| r7  | program counter — readable and writable like any register |

All conventions (SP, FP, return value) are software conventions enforced by
the compiler and calling convention, not by hardware. The hardware treats all
eight registers identically.

Reading r7 as an operand yields **PC + 1**, as in v1. This is the base for
relative jumps and for capturing return addresses.

### Flags

| Flag | Meaning | Set by |
|------|---------|--------|
| Z | result was zero | all class-00 and class-01 ops |
| C | carry-out / borrow-out | all class-00 and class-01 ops |
| N | result bit 15 was 1 (negative in two's complement) | all class-00 and class-01 ops |
| V | signed overflow | all class-00 and class-01 ops |

C on subtraction follows the borrow convention of the '181: C = 1 means no
borrow (result ≥ 0 unsigned), C = 0 means borrow (result < 0 unsigned). This
matches the convention used by ADDC/SUBC for multi-word arithmetic.

V is derived combinationally from carry-into and carry-out-of bit 15 via one
'86 gate, as is conventional: `V = Cn15 XOR Cout`.

All four flags are latched on the same CLK edge as the result, by the
existing '74 + '157 recirculating flag register, extended to 4 bits. Logic
ops set Z and N from the result; C is forced to 0 and V is forced to 0
(logic results are neither arithmetic carries nor signed overflows).

### Instruction Word

Unchanged from v1:

```
 15 14 | 13 12 11 | 10  9  8 |  7  6  5 |  4  3 |  2  1  0
 class |    op    |    rd    |    rs    | rsvd  |    rt
                             |           imm8 (IR[7:0])    |
```

IR[4:3] remain reserved. The 3-T-state uniform timing is unchanged for all
instructions except LD and ST, which require 4 T-states (see below).

---

## Opcode Table

### Class 00 — Register ALU (rs and rt operands)

| IR[15:11] | Mnemonic | Action | Flags |
|-----------|----------|--------|-------|
| 00000 | ADD rd, rs, rt | rd ← rs + rt | Z C N V |
| 00001 | SUB rd, rs, rt | rd ← rs − rt | Z C N V |
| 00010 | MOV rd, rs | rd ← rs | Z C N V |
| 00011 | AND rd, rs, rt | rd ← rs & rt | Z 0 N 0 |
| 00100 | CMN rs, rt | flags ← rs + rt, no write | Z C N V |
| 00101 | CMP rs, rt | flags ← rs − rt, no write | Z C N V |
| 00110 | TST rs | flags ← rs, no write | Z 0 N 0 |
| 00111 | OR rd, rs, rt | rd ← rs \| rt | Z 0 N 0 |

IR[13]=1 is suppress-write (CMN, CMP, TST); IR[12]=1 is B-kill (MOV, TST);
IR[11]=1 is subtract (SUB, CMP). AND and OR fill the two v1 spare slots
00011 and 00111, distinguished from arithmetic ops by the '181 mode pin M
which the GAL asserts for all logic opcodes.

### Class 01 — Immediate ALU (rd and imm8 operands)

| IR[15:11] | Mnemonic | Action | Flags |
|-----------|----------|--------|-------|
| 01000 | ADDI rd, imm8 | rd ← rd + imm | Z C N V |
| 01001 | SUBI rd, imm8 | rd ← rd − imm | Z C N V |
| 01010 | LDI rd, imm8 | rd ← imm | Z C N V |
| 01011 | ADDIH rd, imm8 | rd ← rd + (imm << 8) | Z C N V |
| 01100 | ADDC rd, rs, rt | rd ← rs + rt + C | Z C N V |
| 01101 | CMPI rd, imm8 | flags ← rd − imm, no write | Z C N V |
| 01110 | SUBC rd, rs, rt | rd ← rs − rt − (1−C) | Z C N V |
| 01111 | XOR rd, rs, rt | rd ← rs ^ rt | Z 0 N 0 |

ADDC and SUBC use the rs/rt register fields (not imm8) despite sitting in
class 01 address space — imm8 is ignored. The '181 Cn input is driven from
the C flag latch rather than the GAL's static subtract logic. XOR in slot
01111 also uses rs and rt operands, symmetrically with AND and OR.

NOT is not a separate opcode. The compiler synthesises `NOT rd, rs` by
loading 0xFFFF into a temporary and emitting `XOR rd, rs, tmp`. No register
is permanently reserved for this constant.

### Class 10 — Conditional Ops (unchanged from v1)

| IR[15:11] | Mnemonic | Action |
|-----------|----------|--------|
| 10000 | MOVZ rd, rs | rd ← rs if Z |
| 10001 | MOVNZ rd, rs | rd ← rs if !Z |
| 10010 | LDIZ rd, imm8 | rd ← imm if Z |
| 10011 | LDINZ rd, imm8 | rd ← imm if !Z |
| 10100 | ADDIZ rd, imm8 | rd ← rd + imm if Z |
| 10101 | ADDINZ rd, imm8 | rd ← rd + imm if !Z |
| 10110 | SUBIZ rd, imm8 | rd ← rd − imm if Z |
| 10111 | SUBINZ rd, imm8 | rd ← rd − imm if !Z |

With rd = r7, the lower four are conditional absolute jumps and the upper
four are conditional relative branches (±255, relocatable) — unchanged from
v1.

### Class 11 — Special, Memory, and Carry/Signed Conditionals

| IR[15:11] | Mnemonic | Action |
|-----------|----------|--------|
| 11000 | OUT rs | output register ← rs |
| 11001 | IN rd | rd ← input port (8 bits, zero-extended) |
| 11010 | LD rd, [rs] | rd ← SRAM[rs] — 4 T-states |
| 11011 | ST [rd], rs | SRAM[rd] ← rs — 4 T-states |
| 11100 | MOVC rd, rs | rd ← rs if C |
| 11101 | MOVNC rd, rs | rd ← rs if !C |
| 11110 | MOVN rd, rs | rd ← rs if N |
| 11111 | HLT | assert /HALTREQ |

HLT remains all-ones: blank EEPROM still freezes the machine safely.

MOVC/MOVNC with rd=r7 are conditional jumps on C, covering unsigned
comparisons after CMP (C=0 means borrow, i.e. A < B unsigned; C=1 means
A ≥ B). MOVN with rd=r7 is a conditional jump on N for signed comparisons.
The compiler combines N and V for full signed relation coverage: signed `<`
is `N != V`, emitted as a short two-instruction test sequence when V matters.

#### LD/ST Timing — 4 T-States

LD and ST are the only instructions that extend beyond 3 T-states. The GAL
suppresses /TRST at T2 when IR decodes as LD or ST, allowing the T-counter
to advance to T3. On T3 the memory cycle completes: SRAM /OE or /WE is
asserted, and on LD the data bus drives the register file D inputs directly.

The sequencer exception is exactly one additional GAL product term, fully
contained in GAL1. Nothing else changes behaviour.

---

## Memory Map

Address bit 15 is the sole ROM/RAM decode signal — one gate or a spare GAL
product term. Reset vectors to 0x0000, which is always ROM.

```
0x0000 – 0x7FFF   32K words   ROM (2× AT28C256)
                              program code + read-only constants
                              reset vector at 0x0000

0x8000 – 0xFFFF   32K words   RAM (2× 62256)
                              0x8000 upward  : globals and static data (BSS)
                              0xFFFF downward: stack (grows toward globals)
```

The compiler's startup code (emitted at 0x0000, before `main`) initialises
SP to 0xFFFF and zeroes the BSS segment before calling `main`. Stack and
heap grow toward each other; the full 32K RAM is available to both without
any reservation.

**Stack grows downward.** PUSH pre-decrements after store; POP
post-increments before load. This is the natural direction for a descending
stack starting at the top of RAM: SP starts at 0xFFFF and moves toward the
globals. The alternative (ascending stack) would require reserving the top of
RAM or doing extra work to detect collision; descending gives the full address
space naturally.

**Read-only data** (`const` arrays, string literals, lookup tables) is placed
by the compiler in the ROM half alongside code. The linker step assigns
addresses; the relative order of code and constants within ROM does not matter
to the hardware.

---

## Stack and Calling Convention

No hardware stack. The stack lives in RAM addressed by r6 (SP), growing
downward from 0xFFFF.

**PUSH rs:**
```asm
ST   [r6], rs     ; store at current SP
SUBI r6, 1        ; SP--
```

**POP rd:**
```asm
ADDI r6, 1        ; SP++
LD   rd, [r6]     ; load from new top
```

**CALL addr** (compiler-synthesised):
```asm
LDI  r_tmp, addr_low     ; compose target in a scratch register
ADDIH r_tmp, addr_high
ST   [r6], r7            ; push return address (r7 reads as PC+1 here)
SUBI r6, 1
MOV  r7, r_tmp           ; jump
```

Because reading r7 yields PC+1 (the next instruction), the value pushed is
exactly the correct resume point — no adjustment needed.

**RET:**
```asm
ADDI r6, 1        ; SP++
LD   r7, [r6]     ; pop into PC = return
```

**Calling convention:**
- Arguments: first four in r0–r3 (left to right); further arguments pushed
  right-to-left by caller
- Return value: r0 (or r0:r1 for `long`)
- Caller-saved: r0–r3
- Callee-saved: r4, r5 (FP)
- r5 (FP) points to the saved FP at function entry, enabling frame walking

**Startup sequence** (compiler-emitted preamble at 0x0000):
```asm
LDI  r6, 0xFF          ; SP low byte
ADDIH r6, 0xFF         ; SP = 0xFFFF
; zero BSS (loop over globals region)
LDI  r0, 0
LDI  r1, BSS_START_low
ADDIH r1, BSS_START_high
LDI  r2, BSS_END_low
ADDIH r2, BSS_END_high
; ... zero loop using ST ...
; call main
LDI  r7, main_low
ADDIH r7, main_high    ; jump to main (compose address then MOV to r7)
```

---

## Linker and Global Variables

The compiler operates in two passes over the whole program:

1. **Compile pass** — each source file is compiled to relocatable object
   records: instruction sequences with symbolic references to globals and
   functions.

2. **Link pass** — assigns fixed ROM addresses to all functions and `const`
   data (starting from 0x0000 + startup preamble size), and fixed RAM
   addresses to all globals and statics (starting from 0x8000). Resolves all
   symbolic references, patches forward branches, and emits the final binary.

The linker is part of the same C# tool. For a single-file program the two
passes collapse into one compile-then-assign step. Multi-file programs (if
supported) link object files in a second stage.

BSS (uninitialised globals) is placed after initialised globals. The startup
preamble zeroes the entire BSS region before calling `main`, matching C
semantics for uninitialised globals.

---

## Operators: Soft Multiply, Divide, and Shift

Multiply (`*`), divide (`/`), modulo (`%`), and variable shifts (`<<`, `>>`)
have no hardware instruction. The compiler handles them as follows:

**Constant folding** — evaluated at compile time with no emitted code:
```c
x = 3 * 4;       // emits: LDI rx, 12
```

**Constant power-of-two special cases** — inlined as shift loops or single
instructions:

| Expression | Emitted as | Cost |
|------------|------------|------|
| `x * 2` | `ADD rd, rs, rs` | 1 instruction |
| `x * 4` | `ADD rd, rs, rs` × 2 | 2 instructions |
| `x * 2^n` | unrolled `ADD rd,rd,rd` × n | n instructions |
| `x / 2` unsigned | 1-bit right-shift inline | ~5 instructions |
| `x << n` (constant) | unrolled `ADD rd,rd,rd` × n | n instructions |
| `x >> n` (constant, small) | short inline loop | ~4n instructions |
| `x * 0` | `LDI rd, 0` | 1 instruction |
| `x * 1` | `MOV rd, rs` | 1 instruction |

**General case — shared library routine:**
When the operand is a runtime variable and no constant case applies, the
compiler emits a call to a soft routine linked from the runtime library. The
library is compiled into the ROM image and only included if the relevant
operator is used anywhere in the program.

**Inline vs shared:**
- If a general-case `*`, `/`, or variable `<<`/`>>` appears **exactly once**
  in the translation unit, the routine body is inlined at the call site — no
  call overhead, no library entry point needed.
- If it appears **more than once**, a single shared routine is emitted and
  called each time.

The compiler makes this decision per operator after the full AST is built,
before code generation.

**Approximate soft routine costs** (at 5 MHz):

| Operation | Instructions | Time |
|-----------|-------------|------|
| 16×16 → 16 multiply | ~60 | ~12 µs |
| 16÷16 → 16 divide | ~80 | ~16 µs |
| Variable left shift | ~5 per bit | ~1 µs per bit |
| Variable right shift | ~6 per bit | ~1.2 µs per bit |

Acceptable for programs that are not arithmetic-bound in tight inner loops.

---

## Extended Idioms

| Intent | Sequence |
|--------|----------|
| 32-bit add (r1:r0 + r3:r2) | `ADD r0,r0,r2` then `ADDC r1,r1,r3` |
| 32-bit sub | `SUB r0,r0,r2` then `SUBC r1,r1,r3` |
| NOT rd, rs | load 0xFFFF into tmp, then `XOR rd, rs, tmp` |
| Unsigned branch ≥ (after CMP) | `MOVNC r7, addr` (C=1 means no borrow = A≥B) |
| Unsigned branch < (after CMP) | `MOVC r7, addr` (C=0 means borrow = A<B) |
| Signed branch < (after CMP) | `MOVN r7, addr` (plus V check if needed) |
| Arithmetic left shift ×2 | `ADD rd, rs, rs` |
| Array index | `ADD raddr, rbase, ridx` then `LD rd, [raddr]` |
| Bool to 0/1 | `CMP rs, r0` / `LDIZ rd, 0` / `LDINZ rd, 1` |
| 16-bit constant | `LDI r, low` then `ADDIH r, high` |
| NOP | `MOV r0, r0` (unchanged from v1) |

---

## Datapath

```
                 WADDR◄──┐  D[15:0]◄──────────────────────────────────────┐
              ┌──────────┴──────────┐                                      │
   AADDR ────►│    Register File    │◄──── BADDR = IR[2:0]                 │
              │  (Thumby 8×16 ×2)   │                                      │
              └────┬────────────┬───┘                                      │
                  QA            QB                                         │
         ┌─────────┤             │                                         │
         ▼         ▼             ▼                                         │
      EEPROM    A '574        B '574──┐                                    │
      addr        │ /OE (HC)    /OE   │                                    │
         │        ▼                   ▼                                    │
         ▼      A bus             B bus ◄── imm8 '541 (HC)                 │
      IR '377  (10k pulldowns)        ▲  ◄── imm-high '541 (HC)            │
         │       (HC→LS: ok)          │  ◄── input '541 (HC)               │
         └──►GALs                (HC→LS: ok)                               │
                  │                   │                                    │
                  ▼                   ▼                                    │
              ┌────────────────────────────────────────────┐               │
              │  4× 74LS181  +  1× 74LS182 lookahead       │               │
              │  S3–S0, M ◄── GAL1     Cn ◄── C flag/GAL  │               │
              └───────────────────┬────────────────────────┘               │
                                  │  result bus (LS levels)                │
                             4× 74HCT541  ← HCT fence                     │
                                  │  result bus (HC levels)                │
                                  ├──► 2× '688 zero detect → Z → flags     │
                                  ├──► result[15] → N → flags              │
                                  ├──► '86 Cn15 XOR Cout → V → flags       │
                                  ├──► '182 Cn+4 → C → flags               │
                                  ├──► flags '74 + '157 (Z/C/N/V)          │
                                  ├──► LED register 2× '377                │
                                  │                                        │
              MAR 2× '374 ◄───────┘ (latched on T1 of LD/ST from rs)      │
                                  │                                        │
              A15 decode ─────────┼──► /CE ROM or /CE RAM                  │
                                  │                                        │
              ┌───────────────────┴──────────────────┐                    │
              │  2× AT28C256 ROM  │  2× 62256 RAM     │                    │
              │  0x0000–0x7FFF    │  0x8000–0xFFFF    │                    │
              └───────────────────┴──────────────────┘                    │
                              74HCT245                                     │
                         (data bus buffer, LD path)                        │
                                   └───────────────────────────────────────┘
```

---

## ALU: '181 + '182 Function Select

| Operation | M | S3 S2 S1 S0 | Notes |
|-----------|---|-------------|-------|
| ADD, ADDC | 0 | 1 0 0 1 | F = A + B + Cn |
| SUB, SUBC | 0 | 0 1 1 0 | F = A − B − 1 + Cn |
| MOV / pass A | 0 | 1 1 1 1 | F = A (B bus pulled down = 0) |
| AND | 1 | 1 0 1 1 | F = A AND B |
| OR  | 1 | 1 1 1 0 | F = A OR B |
| XOR | 1 | 0 1 1 0 | F = A XOR B |
| NOT A | 1 | 0 0 0 0 | F = NOT A |

GAL1 drives S3–S0 and M from IR[15:11] and T-state. These five signals
consume the five spare output pins identified in the v1 GAL budget (GAL1 had
2 spare OLMCs, GAL2 had 3). Product term counts per signal are modest —
M is two to three literals, S0–S3 are four to six each — well within the
22V10's per-OLMC budgets.

---

## Control Decode — Two GAL22V10s

All v1 equations carry forward. Additions:

**GAL1 additions:**

```
; '181 mode and function select
M       = T2·LOGICOP·/RST
S3      = T2·(arithmetic-add group)·/RST
S2      = T2·(OR + NOT group)·/RST
S1      = T2·(subtract + XOR + OR group)·/RST
S0      = T2·(add + AND + MOV group)·/RST

; suppress /TRST at T2 for LD/ST (4-state extension)
/TRST   = /T2 + (LD + ST)          ; was: TRST = T2

; carry-in source: C flag for ADDC/SUBC, else static SUBTRACT
CIN_SEL = T2·(ADDC + SUBC)·/RST

; flags now Z/C/N/V; FLAGEN timing unchanged
FLAGEN  = T2·(c00 + c01)·/RST
```

**GAL2 additions:**

```
MAR_CLK = T1·(LD + ST)·/RST        ; latch rs into MAR
SRAM_OE = T3·LD·/RST               ; SRAM read enable (active low at pin)
SRAM_WE = T3·ST·/RST               ; SRAM write enable (active low at pin)
DBUS_OE = T3·LD·/RST               ; HCT245: SRAM → D bus
ROM_CE  = /A15                     ; ROM active when A15=0
RAM_CE  = A15                      ; RAM active when A15=1
```

---

## Timing Summary

| Path | v1 | v2 |
|------|----|----|
| ALU compute, 16-bit | ~100 ns (ripple '283) | ~35–40 ns ('181+'182) |
| HCT fence | — | ~8 ns |
| T1/T2 window needed | ~150–180 ns | ~55 ns |
| EEPROM fetch (T0) | ~250 ns | ~250 ns (unchanged) |
| Clock ceiling | ~3 MHz | ~5–8 MHz (EEPROM now the long pole) |
| LD/ST access time | — | 4 T-states; 62256 at 55 ns, well within margin |

---

## Parts List (v2 CPU Board)

| Function | Parts | vs v1 |
|----------|-------|-------|
| Instruction register | 2× 74HC377 | unchanged |
| A, B operand registers | 4× 74HC574 | unchanged |
| imm8 / imm-high / input buffers | 3× 74HC541 | unchanged |
| ALU slices | 4× 74LS181 | replaces 4× '283 + 4× '86 |
| Carry lookahead | 1× 74LS182 | new |
| HCT result bus fence | 4× 74HCT541 | new |
| Zero detect | 2× 74HC688 | unchanged |
| Flags (Z/C/N/V) | 1× 74HC74 + 1× 74HC157 | same ICs, extended to 4 bits |
| Address steering | 1× 74HC157 + 2× 74HC32 | unchanged |
| Memory address register (MAR) | 2× 74HC374 | new |
| Data bus buffer (LD path) | 1× 74HCT245 | new |
| Data SRAM | 2× 62256 (32K×8) | new |
| Memory decode (A15) | 1× 74HC138 or spare GAL term | new |
| Control | 2× GAL22V10 (ATF22V10C) | same devices, updated equations |
| Program ROM | 2× AT28C256 | unchanged |
| Output register + LEDs | 2× 74HC377 | unchanged |

**≈ 37 ICs.** Same house rules: 0.1 µF per socket, unused inputs tied,
10 k pulldowns on both operand buses.

---

## C Compiler Capability (C# Hosted, Targets Addy v2)

The compiler runs on a PC written in C# and produces Addy v2 assembly,
assembled to a binary image for burning to ROM. It is not self-hosted and
does not need to be.

### Type System

| C Type | Addy Representation | Notes |
|--------|---------------------|-------|
| `int` | 16-bit signed, one word | native |
| `unsigned int` | 16-bit unsigned, one word | native |
| `char` | 16-bit, one word | RAM is 16-bit wide; `sizeof(char)==1` word; no packing waste |
| `unsigned char` | 16-bit unsigned, masked to 0x00FF | mask on assignment |
| `long` | 32-bit signed, two consecutive words | ADDC/SUBC make this practical |
| `unsigned long` | 32-bit unsigned, two words | same |
| Pointers (`T*`) | 16-bit word address | full 64K-word space, 0x0000–0xFFFF |
| `struct` / `typedef` | fields at word offsets | compiler handles layout |
| `float` / `double` | not supported | not practical without hardware multiply |

Because RAM is 16 bits wide, a `char` occupies one word address — identical
to `int`. There is no byte packing, no odd/even address handling, no
unaligned access. `char*` and `int*` are the same size and granularity.
`sizeof(char) == sizeof(int) == 1` word, which is valid C and matches many
early 16-bit systems. String operations are natural and efficient.

`const` variables and arrays are placed by the compiler in the ROM half of
the address space. The compiler uses the `const` qualifier to determine ROM
vs RAM placement; the linker assigns the actual addresses.

### Operators

| Operator | Implementation | Cost |
|----------|----------------|------|
| `+`, `-` | ADD, SUB | 1 instruction |
| `&`, `\|`, `^` | AND, OR, XOR | 1 instruction |
| `~` | load 0xFFFF into tmp; XOR | 2 instructions |
| `++`, `--` | ADDI/SUBI 1 | 1 instruction |
| `+=`, `-=` small immediate | ADDI/SUBI | 1 instruction |
| Comparisons `==` `!=` | CMP + Z branch | 2 instructions |
| Unsigned `<`, `>=` (after CMP) | MOVC / MOVNC | 2 instructions |
| Signed `<`, `>=` (after CMP) | MOVN + V check | 2–4 instructions |
| `long` `+` `-` | ADD+ADDC / SUB+SUBC | 2 instructions |
| `*` constant power-of-two | unrolled ADD | n instructions |
| `*` general | soft routine (inline if used once) | ~60 instructions |
| `/`, `%` general | soft routine (inline if used once) | ~80 instructions |
| `<<` constant n | unrolled ADD | n instructions |
| `<<` variable | soft routine (inline if used once) | ~5 per bit |
| `>>` variable | soft routine (inline if used once) | ~6 per bit |

### Control Flow

| C construct | Implementation |
|-------------|----------------|
| `if` / `else` | CMP + conditional branch (relative ±255 or far) |
| `while`, `for`, `do` | conditional relative branch back |
| `switch` | chain of CMP+branch; jump table for dense cases |
| `break`, `continue`, `return` | unconditional branch or RET sequence |
| `goto` | unconditional jump |
| Function call | CALL sequence (compose address, push PC+1, MOV r7) |
| Recursion | fully supported — stack depth limited only by RAM |

### What Is Not Supported

- `float` / `double` — not included; a soft float library is theoretically
  possible but impractical without hardware multiply
- `long long` (64-bit) — not supported
- Variadic functions (`printf`-style) — not in the initial compiler
- Standard library beyond hand-written basics: `memcpy`, `memset`, `strlen`,
  `strcpy`, integer-to-string formatting for the LED output register
- `#include` of system headers — the compiler has a built-in header for Addy
  intrinsics (`in()`, `out()`, `hlt()` as inline functions/macros)

### Compiler Architecture (C#, ~6,000–8,000 lines)

Five phases:

1. **Lexer + preprocessor** (~800 lines) — tokenises C source; handles
   `#define`, `#ifdef`, `#include` of local files.

2. **Recursive-descent parser** (~1,500 lines) — produces an AST for the
   supported C subset; handles operator precedence and associativity correctly.

3. **Type checker and semantic analyser** (~800 lines) — resolves types,
   lvalues, implicit conversions, struct field offsets, function signatures;
   determines ROM vs RAM placement for each variable.

4. **IR generation and register allocator** (~1,500 lines) — three-address
   IR maps almost directly to Addy's 3-address ALU; linear-scan register
   allocator over r0–r5 with spill to stack frame; `long` values allocated
   as register pairs; soft-operator usage counted here to decide inline vs
   shared routine.

5. **Code generator + linker + assembler** (~1,500 lines) — emits Addy
   assembly; assigns ROM addresses to functions and `const` data, RAM
   addresses to globals; resolves all references; two-pass for forward branch
   fixup; emits the final binary and an annotated `.lst` file interleaving
   source lines with generated instructions.

Output: a `.bin` file (raw 16-bit words, big-endian) ready for an AT28C256
programmer, plus the `.lst` listing. A companion C# utility handles EEPROM
programming via a serial adapter.

---

## Bring-Up Sequence (v2 additions to v1 stages 1–6)

Run the v1 bring-up sequence first to verify the '181 is arithmetically
equivalent to the '283, then continue:

7. **HCT fence.** Run the v1 counter program. Probe the result bus downstream
   of the HCT541s and confirm VOH > 4.0 V at the register file D inputs.

8. **Logic ops.** `LDI r0, 0x0F / LDI r1, 0x33 / AND r2, r0, r1 / OUT r2`
   — LEDs should show 0x03. Repeat for OR (expect 0x3F) and XOR (expect 0x3C).

9. **Flag extension.** `LDI r0, 0x7FFF / ADDI r0, 1` — result 0x8000,
   N=1, V=1, C=0, Z=0. Inspect all four flag bits on the panel.

10. **ADDC chain.** r1:r0 = 0x0001FFFE (r1=1, r0=0xFFFE). Add r3:r2 =
    0x00000002. `ADD r4,r0,r2` → 0x0000, C=1. `ADDC r5,r1,r3` → 0x0002.
    Verify r5:r4 = 0x00020000.

11. **ROM/RAM decode.** Scope A15 and both /CE lines while running a program
    that does a fetch (A15=0, ROM /CE active) followed by a stack operation
    (A15=1, RAM /CE active). Verify no overlap.

12. **MAR and SRAM.** `LDI r1, 0x8200 / LDI r2, 0xABCD / ST [r1], r2 /
    LDI r3, 0 / LD r3, [r1] / OUT r3` — LEDs show 0xCD. Confirms MAR
    latching, SRAM write, SRAM read, and data bus path end-to-end.

13. **Stack round-trip.** Hand-assemble PUSH r0 / POP r1 with r0=0x1234.
    Step through; verify r1=0x1234 and SP returns to original value.

14. **First C program.** Compile a recursive factorial function. Burn and run.
    Verify `factorial(5)` = 120 on the LED output register.

---

## ISA Compatibility with v1

v2 is fully backward-compatible with v1. Every v1 opcode has the same
encoding and behaviour. A v1 ROM image runs on the v2 board without
modification — subject to the memory map change: v1 programs that assumed
all of 0x0000–0xFFFF was ROM will need their data sections moved to the RAM
half (0x8000–0xFFFF). v1 programs with no data memory are unaffected.

The new N and V flags are set silently by v1 arithmetic but never consumed
by v1 code — harmless. The new opcodes (AND, OR, XOR, ADDC, SUBC, LD, ST,
MOVC, MOVNC, MOVN) occupy slots that were spare in v1.

---

## Design Notes

**Why not hardware multiply in v2?** A 16×16 multiplier in TTL is a
significant addition — four '284/'285 array multiplier slices plus
partial-product accumulation, easily 10–15 ICs and a board of its own.
Software multiply at 5 MHz runs in roughly 12 µs — fast enough for the
programs v2 is intended to run. Hardware multiply, or a single-bit shift
instruction enabling faster soft multiply, is a natural v3 candidate.

**Why descending stack?** The stack grows downward from 0xFFFF toward the
globals at 0x8000. This gives the entire 32K RAM to both stack and heap
without any reservation, and SP initialisation is one two-instruction
constant load. An ascending stack would require partitioning RAM in advance
or adding runtime collision detection.

**Why no I/O expansion in v2?** IN and OUT as dedicated instructions cover
the single input port and LED output register that the hardware provides.
Memory-mapped I/O (additional ports decoded in the upper RAM addresses) is
the natural v3 expansion path and requires no ISA changes — ST to a
mapped address already does the right thing once the address decoder is
extended.
