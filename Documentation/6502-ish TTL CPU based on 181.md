# 74181-Centered 8-Bit TTL CPU — Design Spec

A low-IC-count but capable 8-bit TTL CPU built around the 74LS181 ALU.
The ISA is deliberately 6502-flavoured; the microarchitecture is single-bus
with EEPROM microcode. Two build variants are specified: **Lean** (minimum
chip count) and **Fast** (hardware counters for PC/SP).

---

## 1. Design Philosophy

The 74181 is chosen because it hands you two things minimal designs usually
fight for:

- **Two independent operand ports** (A and B).
- **A built-in function set** — add / subtract / logic / pass-through.

Two tricks fall out of that and drive the whole design:

1. **Accumulator A permanently drives the ALU A port.** A is never separately
   buffered onto the bus — to read A you just run the ALU in `F = A` mode and
   enable the output driver. Deletes one buffer.
2. **There is no B register.** The ALU B port hangs directly off the data bus,
   so every ALU op is "A (register) *op* whatever-is-on-the-bus." Deletes a
   whole register plus its buffer.

Everything else — flags, X/Y/temp, MAR, IR, microcode, memory — is common to
both variants. The **only** thing that changes between Lean and Fast is how
PC and SP are implemented.

---

## 2. The Two Variants

| Aspect | Lean | Fast |
|---|---|---|
| PC | Plain 2× 74LS374 (16-bit register) | 4× 74LS163 counter + 2× 74LS244 buffer |
| SP | Plain 1× 74LS374 | 4× 74LS193 counter + 2× 74LS244 buffer |
| PC/SP increment | Routed through the 181 in two 8-bit passes (low, then high with carry) | Self-incrementing in the counter, one T-state |
| Control word | 23–24 bits (3× 28C256) | 26–27 bits (4× 28C256) |
| Approx IC count | ~25 + osc | ~29–31 + osc |
| Character | Best learning machine, smaller board, cleaner single arithmetic path | Faster; cheap CALL/RET/interrupts; more silicon and wiring |

**Lean tax:** because PC/SP have no counting hardware, every increment consumes
ALU S-bits + Cn during fetch/address work. Rule of thumb: **+2 T per PC
increment, +3 T per push/pop**. It shows up as a tax on nearly every
instruction, not just a few — see cycle table §5.

---

## 3. Register Set

| Register | Width | Notes |
|---|---|---|
| A | 8 | Accumulator, wired permanently to ALU A port |
| X, Y | 8 | Index / general registers, and B-operand sources |
| temp | 8 | Scratch (microcode use) |
| PC | 16 | Program counter |
| SP | 16 | Stack pointer |
| IR | 8 | Instruction register |
| MAR | 16 | Memory address register (data/operand access) |
| P | 6 | Flags: N Z V C I (no decimal mode) |

No decimal mode — the 181 gives nothing there and it is pure microcode bloat.

---

## 4. Instruction Set Architecture

Trimmed 6502. ~45 instructions. Encoding is regular enough that the assembler
is essentially `opcode = (mode << 4) | aluOp`.

### 4.1 Addressing modes (4)

`#` immediate · `zp` zero-page · `abs` absolute · `abs,X` indexed.

### 4.2 Opcode map

High nibble = mode/group, low nibble = operation.

| Range | Meaning |
|---|---|
| `0x00–07` | ALU op, immediate |
| `0x10–17` | ALU op, zero-page |
| `0x20–27` | ALU op, absolute |
| `0x30–37` | ALU op, absolute,X |
| `0x40–46` | STA / STX / STY (zp, abs, abs,X) |
| `0x50–55` | LDX / LDY (#, zp, abs) |
| `0x60–67` | TAX TXA TAY TYA INX DEX INY DEY |
| `0x70–77` | ASL A, ROL A, INC A, DEC A, CLC SEC CLI SEI |
| `0x80–87` | BEQ BNE BCS BCC BMI BPL BVS BVC (signed 1-byte relative) |
| `0x90–97` | JMP abs, JSR abs, RTS, RTI, PHA PLA PHP PLP |
| `0xF0 / 0xFF` | NOP / HLT |

### 4.3 ALU-op field (low 3 bits of the 0x0–0x3 blocks)

`LDA, ADC, SBC, AND, ORA, EOR, CMP, BIT`

Examples: `LDA #` = 0x00, `ADC #` = 0x01, `LDA zp` = 0x10, `CMP abs,X` = 0x36.

### 4.4 Mapping to the 74181

| Instruction | 181 realisation |
|---|---|
| LDA | Pass-through, `F = B` |
| ADC / SBC | Arithmetic mode, Cn from carry flag |
| CMP | SBC that keeps flags, discards F |
| BIT | AND that keeps flags only |
| ASL A | `A + A`; carry out from Cn+4 |
| ROL A | `A + A + Cn` |
| INC A / DEC A | `A + 1` / `A − 1` |
| TAX etc. | Source routed through 181 in pass mode into target latch |

**No right shifts.** The 74181 shifts left only (it is an adder, `A+A`) and has
no right-shift path. LSR / ROR are deliberately excluded — no external mux
needed, and the opcode map has no holes left by them.

---

## 5. Cycle Counts

T-states include fetch. Baseline figures are for the **Fast** build. The Lean
penalty (§2) is applied afterward.

### ALU ops (LDA, ADC, SBC, AND, ORA, EOR, CMP, BIT — uniform)

| Mode | Encoding | Cycles |
|---|---|---|
| `#` immediate | `0x00–07` | 2 |
| `zp` | `0x10–17` | 3 |
| `abs` | `0x20–27` | 4 |
| `abs,X` | `0x30–37` | 4 |

### Stores

| Instr | Modes | Cycles |
|---|---|---|
| STA | zp / abs / abs,X | 3 / 4 / 5 |
| STX, STY | zp / abs | 3 / 4 |

### Loads (X/Y)

| Instr | Modes | Cycles |
|---|---|---|
| LDX, LDY | `#` / zp / abs | 2 / 3 / 4 |

### Register / implied

| Instr | Cycles |
|---|---|
| TAX TXA TAY TYA | 2 |
| INX DEX INY DEY | 2 |
| ASL A, ROL A | 2 |
| INC A, DEC A | 2 |
| CLC SEC CLI SEI | 2 |

### Branches (signed 1-byte relative)

| Instr | Cycles |
|---|---|
| BEQ BNE BCS BCC BMI BPL BVS BVC | 2 not taken / 3 taken |

### Control & stack

| Instr | Cycles |
|---|---|
| JMP abs | 3 |
| JSR abs | 6 |
| RTS | 6 |
| RTI | 6 |
| PHA / PHP | 3 |
| PLA / PLP | 4 |

### Misc

NOP = 2 · HLT = halts (no further cycles).

### Lean-build penalty

Every PC/SP touch routes through the 181 in two 8-bit passes. Practical rule:
**+~2 T per PC increment, +~3 T per push/pop.** Consequences:

- Lean `LDA abs` ≈ 6 (vs 4).
- Lean `JSR` / `RTS` ≈ 10–11 (they hammer SP).

---

## 6. Microcode Store: EEPROM vs GAL

**Decision: EEPROM control store; no GAL required for this machine.**

- EEPROM is an arbitrary lookup table — independent entries, no logic
  minimisation, reprogram with the T48. Microcode control words are effectively
  random bits, which is the *worst* case for a GAL's sum-of-products structure.
- **Flags go in the EEPROM address.** With address `{opcode, flags, T-state}`,
  a `BEQ` at a given T-state simply reads a different control word depending on
  Z, because Z is an address line. Conditional branching becomes table lookup —
  no sequencer logic, no next-state equations.
- A 28C256 is 32K deep and cheap, so spend the address space and skip the GAL.

**When a GAL would pay off (not used here):** if you later go flag-multiplexed
to shrink the store — a GAL can collapse the four flags down to "the one flag
this opcode cares about," avoiding the 4× table growth. Kept in back pocket only.

---

## 7. T-State Sequencer

No state machine — a counter plus a microcode reset bit:

- **74LS161** counts T0, T1, T2… driving the EEPROM low address lines.
- Each instruction's **last micro-step asserts `T_RESET`**, synchronously
  clearing the 161 to T0. This gives **variable-length instructions for free**:
  a 2-cycle `INX` asserts reset at T1; a 6-cycle `JSR` asserts it at T5.
- **Fetch (T0–T1) is shared by every opcode.** During those steps the
  opcode address bits are forced low (opcode-jam) until T1, so fetch microcode
  is one shared copy. After T1 the real opcode drives the address and decode
  begins at T2.

One counter chip and one control bit do the whole sequencer job. No ring
counter unless one-hot T-state wires are wanted for fan-out.

---

## 8. Control Word

### 8.1 Signal groups

| Group | Signals | Bits |
|---|---|---|
| Bus load enables (latch *from* bus) | A, X, Y, temp, IR, MAR-lo, MAR-hi, PC-lo, PC-hi, SP, FLAGS | 11 |
| Bus source select (drive *onto* bus, '138-encoded) | ALU / X / Y / temp / PC-lo / PC-hi / SP / Mem | 3 |
| ALU | S3 S2 S1 S0, M, Cn | 6 |
| Memory | MEM_WRITE, ADDR_SRC (PC vs MAR) | 2 |
| Counters (**Fast only**) | PC_COUNT, SP_UP, SP_DOWN | 3 |
| Sequencer | T_RESET | 1 |

### 8.2 Widths

| Variant | Bits | EEPROMs (28C256) |
|---|---|---|
| **Lean** (drop the 3 counter bits) | **23** (24 with Cn mux) | 3 |
| **Fast** | **26** (27 with Cn mux) | 4 |

The Lean version adds **no** new signals despite sequencing PC/SP through the
ALU — it reuses the ALU field, ADDR_SRC, the PC-lo/PC-hi/SP bus sources and
their existing load enables. Dropping the counters is a clean −3.

### 8.3 Two encoding decisions

- **Loads stay one-hot** (not encoded), because a single T-state often loads,
  updates flags, and resets the sequencer at once — e.g. `LDA #` final step
  asserts A-load + FLAGS-load + T_RESET together. Encoding would serialise
  these and cost cycles.
- **Cn wants a 2-bit mux, not a raw bit.** Selects `{0, 1, C-flag, /C-flag}`
  into the 181 Cn: ADC needs C-flag, ASL/ROL need 0 or flag, INC needs 1. The
  **Lean build needs this more** — the low→high carry on every PC/SP increment
  has to be caught and fed into the high pass. Implemented with a 74LS153.
  Adding it makes the ALU group 7 bits (Lean total 24, Fast total 27).

---

## 9. Lean-Build BOM

| Function | Part | Qty |
|---|---|---|
| **ALU** (4-bit slice ×2, rippled) | 74LS181 | 2 |
| Accumulator A | 74LS377 | 1 |
| ALU → bus driver | 74LS245 | 1 |
| X, Y, temp registers | 74LS374 | 3 |
| PC (plain reg, lo+hi) | 74LS374 | 2 |
| SP (plain reg) | 74LS374 | 1 |
| MAR (lo+hi) | 74LS374 | 2 |
| IR | 74LS374 | 1 |
| Flags — zero detect | 74LS30 | 1 |
| Flags — overflow (carry XOR) | 74LS86 | 1 |
| Flags — N Z V C latch | 74LS175 | 1 |
| Microcode store (23–24-bit word) | 28C256 | 3 |
| T-state counter | 74LS161 | 1 |
| Clock / single-step | 74LS74 | 1 |
| Bus-source decode (1-of-8) | 74LS138 | 1 |
| Cn select mux {0,1,C,/C} | 74LS153 | 1 |
| Opcode-jam + glue | 74LS08 | 1 |
| Program ROM | 28C256 | 1 |
| RAM | 62256 | 1 |
| Clock source | can oscillator module | 1 |
| | | **~25 ICs + osc** |

**Subtotals:** datapath 7 · address/IR registers 6 · flags 3 ·
control/sequencer 7 · memory 2.

Notes:

- The **nine 74LS374s** are the signature of the Lean build — every 16-bit
  thing (PC, MAR) and every register is the same jellybean latch, no counters.
  One part number to stock; PC/SP increment happens through the 181.
- The **74LS175** holds only N Z V C. If you want the **I (interrupt-disable)**
  flag later for a lean interrupt path, hang it off the spare half of the
  74LS74 rather than widening the latch.
- The **74LS153** Cn mux is not optional on Lean — the low→high carry on every
  PC/SP increment depends on it.
- The **74LS08** covers opcode-jam gating plus odd glue. Budget a few pull-down
  resistor packs (not counted as ICs).

---

## 10. Open Items / Next Steps

- Worked micro-programs (control-word bit patterns) for fetch, `LDA abs`,
  and `JSR` — Lean and Fast.
- Interrupt path (I flag, vector fetch, RTI sequencing).
- Optional: hardware stack niceties on the Fast build.
- Assembler opcode table (non-C# format if wanted — plain table / spec).
