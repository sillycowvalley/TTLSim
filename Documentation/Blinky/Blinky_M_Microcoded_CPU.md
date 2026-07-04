# Blinky-M — A Microcoded Dual-Stack TTL CPU

**Proposal rev 02 — UNRATIFIED. Working title.** Nothing here is locked.

---

# 0. Thesis

An 8-bit dual-stack Forth machine built from 7400-series parts, designed for minimum
IC count. The instrument of that minimum is time-sharing: one memory, one 8-bit bus,
one ALU, one address path — visited in sequence by a microprogram, one transfer per
T-state. Instructions take as many T-states as they need; each microprogram simply
ends when it ends. Nothing in the machine exists twice.

Target: **~47 ICs core**, front panel and clock module extra.

# 1. Programmer model

- **Dual-stack Forth machine.** Data stack for operands, return stack for control flow
  and frames. TOS is the accumulator-in-spirit and lives in a register; everything
  deeper lives in RAM.
- **ALU ops** take TOS and NOS, result to TOS: ADD, ADC, SUB, AND, OR, XOR, NOT, TST,
  CMP (non-destructive), plus five in-place TOS shifts — SHL, SHR (zero fill), ASR
  (sign fill), ROL, ROR (wrap).
- **Flags N, Z, C**, with per-flag write masking: logic ops preserve C; arithmetic,
  shifts, and CMP write it.
- **Stack ops:** DUP, DROP, SWAP, OVER, NIP, TUCK, ROT, and the return-stack movers
  >R, R>, R@. ΔDSP per instruction is whatever the operation needs — a multi-cycle
  machine pops twice as cheaply as once.
- **Frames:** BP base pointer; ENTER k opens a frame, LOCAL@ off / LOCAL! off access
  BP-relative locals, RET restores BP automatically (no explicit frame-free op).
- **Memory/I-O:** absolute LOAD a16 / STORE a16; stack-form FETCH / STORE!
  (zero-page, address from the stack); IN #port / OUT #port and their stack forms.
- **Control flow:** JUMP a16, CALL a16, RET; conditionals BEQ/BNE/BCS/BCC/BMI/BPL,
  all absolute with full 16-bit reach — there is no short/far distinction.
- **Interrupts:** NMI (edge, unmaskable), IRQ (level, masked by I), software BRK,
  RTI restoring PC, BP, flags, and I. Entry pushes state automatically; a handler can
  use ENTER/locals with zero extra convention.

# 2. Top-level architecture

One 8-bit data bus. One 16-bit address bus. One memory. One ALU.

```
                    ┌────────────────────────────────────────────┐
  ADDRESS BUS (16)  │  sources: PC │ ADR latch │ {page-const, SP} │
                    └───────┬────────────────────────────────────┘
                            │
                    ┌───────┴───────┐
                    │  RAM 32K×8    │  + 8K boot EEPROM + I/O (memory map §3)
                    └───────┬───────┘
                            │
  DATA BUS (8) ═════════════╪══════════════════════════════════════════
     ║        ║        ║    ║       ║        ║        ║         ║
   ┌─┴─┐    ┌─┴─┐    ┌─┴─┐  ║     ┌─┴─┐    ┌─┴──┐   ┌─┴──┐    ┌─┴──┐
   │IR │    │ A │    │ B │  ║     │TOS│    │PC  │   │ADR │    │BP  │
   └───┘    └─┬─┘    └─┬─┘  ║     └───┘    │byte│   │lat.│    └────┘
              └──┬─────┘    ║              │sel │   └────┘
              ┌──┴──┐       ║              └────┘
              │ ALU ├───────╝  ('181 ×2; result drives the bus)
              └─────┘
```

Bus discipline: exactly one source driver enabled per T-state — a '138 decodes a
3-bit microcode field, so mutual exclusion is structural, not conventional.
Destination load strobes come from a second decoder whose enable is gated by /CLK, so
nothing loads or writes until the bus has settled: every write in the machine is
confined to the second half-cycle by one gate.

# 3. Memory map (proposal)

| Range | Contents |
|---|---|
| 0x0000–0x7FFF | SRAM 32K×8 (code + data + stacks) |
| 0x7E00–0x7EFF | Data stack page (grows up, DSP = low address byte) |
| 0x7F00–0x7FFF | Return stack page (grows up, RSP = low address byte) |
| 0x8000–0xBFFF | I/O window ('139 decode; ports on low address bits) |
| 0xC000–0xDFFF | 8K boot EEPROM (28C64) — reset entry at 0xC000 |
| 0xE000–0xFFFF | spare / EEPROM mirror |

Reset: the PC high-byte '161 pair async-loads 0xC0 (parallel inputs strapped), the low
pair clears — PC = 0xC000, straight into EEPROM. The boot code copies itself (or the
application) into RAM with ordinary LOAD/STORE and jumps; because code and data share
one space, no external loader hardware exists at all. A DS1813 supervises reset.

Stacks are 256 deep each, page-anchored: during a stack access the address high byte
is a hardwired constant and the low byte is the SP counter. Pointers are free-running
mod-256 — wraparound is a visible front-panel diagnostic, not a trapped error.

# 4. Instruction encoding — byte stream

One opcode byte, then 0–2 operand bytes taken from the instruction stream by the
microprogram (PC increments through them; no separate operand register).

| Length | Instructions |
|---|---|
| 1 byte | all ALU ops, all stack/return-stack ops, NOP, HALT, RET, RTI, BRK, CLI, SEI, FETCH, STORE!, IN, OUT (stack forms) |
| 2 bytes | PUSH #imm, IN #port, OUT #port, ENTER k, LOCAL@ off, LOCAL! off |
| 3 bytes | JUMP a16, CALL a16, LOAD a16, STORE a16, Bcc a16 (all conditionals) |

The opcode byte is a raw microcode ROM address. There is no decode logic to economise,
so opcodes are assigned for human readability and nothing else; any family grouping is
a mnemonic convention only. Condition select and polarity for the six conditionals
ride in opcode bits feeding a '151 (§6), so they cost zero decode.

16-bit operands and pushed addresses are little-endian (proposal — §12).

# 5. Datapath blocks

| Block | Parts | Notes |
|---|---|---|
| PC | '161 ×4 | 16-bit counter. Counts for every instruction-stream byte, opcode and operands alike. Parallel-loads by byte from the data bus for JUMP/CALL/RET/Bcc; async reset load = 0xC000 |
| PC → address bus | '541 ×2 | enabled on fetch/operand T-states |
| PC → data bus | '257 ×2 | byte select (lo/hi) for CALL's return-address push |
| ADR latch | '574 ×2 | data-address register for LOAD/STORE/FETCH/STORE!; loads by byte from the bus, drives the address bus tri-state |
| DSP, RSP | '191 ×4 | 8-bit each, mod-256, async /LOAD-low with inputs tied low as the reset idiom |
| SP → address low | '257 ×2 | selects DSP vs RSP, tri-state onto address low byte |
| Const driver | '541 ×1 | microcode-selected constants onto address high / data bus: data-stack page, return-stack page, vector bytes, 0x00 |
| ALU | '181 ×2 | one adder for everything: arithmetic, logic, and BP+offset address math |
| ALU A, B latches | '377 ×2 | loaded from the bus in earlier T-states; B doubles as the operand-assembly temp for JUMP/CALL |
| ALU → data bus | '541 ×1 | A and B are latched, so the ALU output is stable while driving |
| TOS | '194 ×2 + '153 + '541 | shifts act on TOS in place; the '153 on the '194 serial pins selects the fill (zero / sign / wrap) from raw opcode bits; '541 drives TOS onto the bus; LEDs off the '194 Qs |
| Flags | '377 ×1 + '157 ×1 + Z-detect glue ('30 + '04) | N/Z/C/I in one register; the '157 selects ALU-derived vs stack-restored (RTI) inputs; NZ_WE / C_WE give per-flag masking |
| Interrupt latches | '74 ×2 | IRQ level sample; NMI synchroniser + true-edge pend (pend = NMI AND NOT sync — one-shot, no retrigger while held) |
| IR | '377 ×1 | opcode byte only |
| BP | '377 ×1 + '541 ×1 | 8-bit; locals live in low RAM. LOCAL@ runs A ← BP, B ← offset, ALU → ADR-lo, const 0x00 → ADR-hi |
| Memory | SRAM 32K ×1, 28C64 boot ×1, '139 ×1 | '139: half memory-map decode, half I/O strobes |
| I/O | '574 out, '541 in, +1 decode | expandable; port number from operand byte or TOS via the normal bus paths |

NOS is not a register: it is RAM at DSP−1, fetched into the B latch when an ALU op
needs it. Popping is decrement-then-read — the SP counts down in the T-state before
the access, so the read is at the raw pointer and no address arithmetic exists.

# 6. Control — microcode ROM sequencer

All control is a lookup table.

**Sequencer:** one '161 T-counter (3 bits used, T0–T7). Every microword carries a
**TRST** bit; asserting it makes the next edge fetch T0 of the next instruction.
Variable T-states per instruction is literally this one bit.

**μROM address (13 bits — exactly one 28C64):**

```
A12..A5 = IR[7:0]     opcode byte, raw
A4..A2  = T[2:0]      T-state
A1      = COND        '151 output: flag selected by opcode bits, polarity likewise
A0      = INTP        interrupt pending (NMI_PEND OR (IRQ_PEND AND I=0))
```

**μROM data:** 4× 28C64 in parallel = 32 control lines. Field sketch:

| Field | Bits | Meaning |
|---|---|---|
| SRC | 3 | bus source → '138: RAM, ALU, TOS, PC-byte, IN-port, const, BP, (spare) |
| DST | 4 | bus destination → '154, phase-gated by /CLK: IR, A, B, TOS, PClo, PChi, ADRlo, ADRhi, BP, FLAGS, RAM-WE, OUT-port |
| ALU S,M,Cn | 6 | direct to the '181s |
| ASEL | 2 | address-bus source: PC / ADR / {page, SP} / none |
| PCINC, PCBYTE | 2 | PC count enable; lo/hi select for the PC-byte source |
| SP | 3 | encoded: none / DSP± / RSP±, drives the '191 /CTEN + D//U pairs |
| TOSMODE | 2 | '194 S1S0: hold / load / shift |
| NZ_WE, C_WE | 2 | per-flag write masking |
| ISET, ICLR | 2 | I flag control (SEI/CLI/entry/RTI paths) |
| CSEL | 2 | const-driver pattern: D-page / R-page / vector / 0x00 |
| TRST | 1 | end of instruction |
| spare | ~3 | |

**Glitch discipline:** EEPROM outputs wander while the T address changes. Every
edge-triggered load is inherently safe (settled long before the edge); every
level-sensitive strobe (RAM /WE, OUT) comes from the DST '154, whose enable is gated
by /CLK — asserted only in the settled second half-cycle. One gate covers every strobe
in the machine.

**Toolchain:** the microcode source is a table in a small **C# generator** — one row
per (opcode, T, COND, INTP) — emitting the four .bin images plus a human-readable
listing. TTLSim loads 28C-series parts, so the verification chain is: generate →
TTLSim sweep → burn. A microcode bug is a table edit and a re-burn.

# 7. T-state budgets (illustrative — final counts fall out of the microcode table)

T0 is always: address ← PC, RAM → IR, PC++. One T-state per additional
instruction-stream byte, plus the work.

| Instruction | T-states | Sketch |
|---|---|---|
| NOP | 1 | fetch, TRST |
| PUSH #imm | 3 | fetch; TOS → RAM[Dpage:DSP], DSP++; RAM[PC] → TOS, PC++ |
| DUP | 2 | fetch; TOS → RAM[Dpage:DSP], DSP++ |
| DROP | 3 | fetch; DSP−−; RAM[Dpage:DSP] → TOS |
| SWAP | 4 | fetch; DSP−−; RAM ↔ TOS via A (RAM → A; TOS → RAM; ALU F=A → TOS); DSP++ folded in |
| ROT | ~7 | three-cell rotate through A/B — an opcode here, not a macro |
| ADD (all binary ALU) | 5 | fetch; TOS → A; DSP−−; RAM[Dpage:DSP] → B; ALU → TOS + flags |
| SHL/SHR/ASR/ROL/ROR | 2 | fetch; TOS shifts in place, C ← bit out |
| JUMP a16 | 5 | fetch; opnd-lo → B, PC++; opnd-hi → A, PC++; ALU(F=B) → PClo; ALU(F=A) → PChi |
| Bcc a16 | 3 / 5 | as JUMP when COND=1; TRST after the two operand skips when COND=0 |
| CALL a16 | 9 | JUMP's operand states + push PClo, PChi, BP (3 bytes to R-page) + 2 PC-load states |
| RET | 6 | fetch; pop BP, PChi, PClo (RSP−− then read, ×3) + loads |
| LOAD a16 | 6 | fetch; 2 operand bytes → ADR; spill TOS to stack; RAM[ADR] → TOS |
| STORE! (stack form) | 6 | fetch; TOS → ADRlo; const 0x00 → ADRhi; DSP−−; RAM[stack] → TOS; TOS → RAM[ADR]… (final order per microcode) |
| LOCAL@ off | 7 | fetch; off → B; BP → A; ALU → ADRlo; const 0 → ADRhi; spill TOS; RAM[ADR] → TOS |
| BRK / hardware entry | ~10 | push PClo, PChi, flags byte, BP; I ← 1; vector consts → PC |
| RTI | ~8 | pop in reverse; flags via the '157 restore path |

Average for stack-shaped code ≈ **4–5 T-states per instruction**.

**Throughput:** the critical path is μROM access + strobe decode + register setup.
With 28C64-150 parts the T-clock ceiling sits near 4 MHz (~0.8–1.0 MIPS); AT28C64B-45
parts open 6–8 MHz (~1.5 MIPS). Datapath elements never limit — every transfer is a
single register-to-register hop across one bus.

# 8. Interrupts

- **INTP is a μROM address bit.** When pending, T0 of the fetch microcode is a
  different row: instead of loading IR, the sequencer runs the entry microprogram —
  push PC-lo, PC-hi, flags byte, BP; set I; load PC from the vector constants. There
  is no jam hardware; the "forced instruction" is an address bit.
- **Raw PC vs PC+n falls out of ordering.** Hardware entry runs before the fetch
  increments PC, so the interrupted instruction's own address is pushed and it
  re-executes on return. BRK's microcode runs after its fetch, so the push resumes
  after it. The B distinction (software vs hardware) is one bit in the pushed flags
  byte.
- **Priority and masking:** NMI over IRQ — NMI_PEND steers the CSEL vector constant.
  INTP = NMI_PEND OR (IRQ_PEND AND NOT I): two gates. NMI also overrides HALT's clock
  stop, waking a halted machine.
- **I = 1 on reset** (interrupts masked until the init code executes CLI); the flag
  register's clear delivers it.
- Flags and I travel as one pushed byte; RTI pops it back through the '157 restore
  mux. RET pops BP and PC only, leaving flags alone.
- Round trip ≈ 18 T-states (~10 entry + ~8 RTI).

**Vectors** are microcode constants, so they are not memory locations at all — entry
loads PC directly with the handler-table address (proposal: IRQ/BRK → 0xC008,
NMI → 0xC010, each holding a JUMP placed by the boot EEPROM; RAM-resident handlers are
reached by rewriting those JUMP targets after the boot copy).

# 9. Chip count

| Block | Chips |
|---|---|
| Control: 28C64 μROM ×4, '161 T-counter, '151 COND, '138 SRC, '154 DST, '00 glue | 9 |
| PC: '161 ×4, '541 ×2 (addr), '257 ×2 (byte → data bus) | 8 |
| ADR latch: '574 ×2 | 2 |
| DSP/RSP: '191 ×4, '257 ×2 (SP → addr) | 6 |
| Const/page driver: '541 | 1 |
| ALU: '181 ×2, A/B '377 ×2, '541 bus driver | 5 |
| TOS: '194 ×2, '153 fill, '541 driver | 4 |
| Flags: '377, '157 restore, Z-detect glue ('30 + '04) | 4 |
| Interrupt latches: '74 ×2 | 2 |
| IR: '377 | 1 |
| BP: '377 + '541 | 2 |
| Memory: 32K SRAM, 28C64 boot, '139 map/IO decode | 3 |
| I/O: '574 out, '541 in, +1 decode/expansion | 3 |
| **Core total** | **~47** |

Two programmable part types in the whole machine (28C64, and nothing else — the μROM
is data, not logic). No adders exist outside the '181 pair. Clock, reset, and
single-step live on a separate module; a hardware breakpoint comparator on PC gates
its halt on T=0 so it stops on instruction boundaries.

# 10. Front panel

| Group | LEDs | Notes |
|---|---|---|
| PC | 16 | |
| IR | 8 | opcode byte |
| TOS | 8 | also the live shift display |
| A / B | 8 + 8 | ALU operands; during any binary ALU op, B is NOS |
| DSP / RSP | 8 + 8 | stack depths, wraparound visible |
| BP | 8 | frame base |
| ADR | 16 | current data address |
| FLAGS | 4 | N, Z, C, I |
| T-state | 3 (+1 decoded) | which microstep is executing |
| INT | 2 | IRQ pending, NMI pending |

Single-step is per-T-state by default — the panel shows the fetch, the operand reads,
and the ALU transfer as separate settled states. A step-instruction mode (run until
TRST) is a two-gate addition on the step path.

# 11. Reduction ladder — trading features for packages

Each rung is independent and unratified:

| Cut | Saves | Price | Running total |
|---|---|---|---|
| Baseline (§9) | — | — | ~47 |
| Drop the '194 shifter (TOS → '377 + '541; SHL = A plus A on the '181; SHR/ASR/ROL/ROR become subroutines) | −2 | shift-heavy code slows; the live shift display is gone | ~45 |
| Drop hardware BP (BP becomes a fixed RAM cell; frame ops microcoded through it) | −2 | +3–4 T per frame op; BP LEDs gone | ~43 |
| SPs into RAM cells (zero-page pointers) | −6 | every push/pop balloons to ~8 T; DSP/RSP LEDs gone — a heavy front-panel loss | ~37 |
| 8-bit machine throughout (256-byte space, 8-bit PC, 2-byte CALL frames) | −4 | a toy | ~33 |

Recommendation: take none of them. ~47 with the full programmer model is the design;
the ladder exists so each feature is priced, not assumed.

# 12. Open questions for ratification

1. Memory map (§3): stack page addresses; EEPROM at top; PC reset = 0xC000 via
   strapped '161 parallel inputs.
2. Byte order of 16-bit operands and pushed PC (proposal: little-endian throughout).
3. BP width: 8-bit with locals confined to low RAM (as specced) vs 16-bit
   (+1 '377, +1 '541, +2 T per frame op).
4. Vector scheme (§8): microcode-constant entry into an EEPROM JUMP table vs
   RAM-resident vector cells read as data (+2 T on entry, fully dynamic).
5. Stack-form op set: FETCH/STORE!/IN/OUT stack forms and ROT are in; anything else
   (PICK? two-byte 16-bit fetch?) waits for a workload.
6. μROM speed grade: 28C64-150 (≈4 MHz T-clock) vs AT28C64B-45 (6–8 MHz). A '377 ×4
   pipeline register on the μROM outputs (+4 chips) is the fallback, not the plan.
7. Opcode byte assignments (free choice; family-style grouping as mnemonic
   convention only).
8. T-state and phase LEDs on the panel (proposal: yes).
