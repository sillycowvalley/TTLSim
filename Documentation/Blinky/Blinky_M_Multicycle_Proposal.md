# Blinky-M — Multi-Cycle Von Neumann Blinky

**Proposal rev 01 — UNRATIFIED. Working title.** Nothing here is locked. This is a
clean-sheet minimum-IC design that deliberately abandons the single-cycle mantra. The single-cycle W=8 Blinky (`Blinky_TTL_CPU.md`) is unaffected.

---

# 0. Thesis

Single-cycle execution is the most expensive sentence in the Blinky design document.
"One tick = one instruction, no exceptions" means every computation the machine can ever need must exist as parallel hardware in the same 250 ns: a dedicated PC adder, a
dedicated BP adder, a dedicated RSP−1 decrementer, a two-lane 16-bit-wide return stack, a selector-per-sink mux forest, and a 16-bit Harvard fetch path. Multi-cycle execution replaces *space* with *time*: one ALU, one bus, one memory, visited in sequence by a microprogram. Variable T-states per instruction come free — each microprogram simply ends when it ends.

Target: **~47 ICs core** vs the single-cycle machine's **~70–73**, same programmer model. A reduction ladder to ~33 is given in §11 for anyone willing to trade features.

# 1. What is kept, what changes

**Kept — the programmer model is the Blinky identity:**

- Dual-stack Forth machine: data stack + return stack, TOS as the accumulator-in-spirit
- The ratified instruction *set semantics*: same ALU ops (incl. all five shift forms),
  same stack ops, frames (BP, ENTER/LOCAL@/LOCAL!), CALL/RET with automatic BP
  save/restore, NMI/IRQ/BRK/RTI with the raw-PC vs PC+1 distinction and B flag
- Flags N, Z, C; per-flag write masking (logic ops preserve C)
- Writes immediate-form only; ΔDSP ∈ {+1, 0, −1} per instruction
- Front-panel honesty — every LED is real machine state (see §12 for what changes)

**Changed:**

| Single-cycle Blinky                       | Blinky-M                                      |
| ----------------------------------------- | --------------------------------------------- |
| Harvard: I-mem 2×8K×8 + separate D-mem    | Von Neumann: one 64K byte-addressed space     |
| 16-bit instruction word, fixed            | Byte-stream, variable length (1–3 bytes)      |
| One tick = one instruction                | 2–9 T-states per instruction, microcoded      |
| 5× 22V10 combinational decode             | Microcode EEPROM + 3-bit T-counter            |
| Dedicated stack SRAMs (2114 / dual-lane)  | Both stacks are pages of main RAM             |
| Selector-per-sink routing                 | One shared 8-bit tri-state data bus           |
| Relative branches + far-conditional idiom | All branches absolute 16-bit (§5)             |
| 13-bit PC, register + '283 adder          | 16-bit PC, '161 counter (increments for free) |

# 2. Where the chips go — the deletion table

Block-by-block against `Blinky_TTL_CPU.md` §11:

| Single-cycle block                                   | Chips | Blinky-M fate                                                                                                 |
| ---------------------------------------------------- | ----- | ------------------------------------------------------------------------------------------------------------- |
| PC adder '283 ×4 + offset mux/sign-ext '157 ×2       | 6     | **Deleted.** PC is a counter; branches absolute, no offset math                                               |
| BP adder '283 ×4 + BP source mux '157 ×4             | 8     | **Deleted.** BP+offset computed through the main ALU in a T-state                                             |
| RSP−1 decrementer '283 ×2 + RS addr mux '157 ×2      | 4     | **Deleted.** Decrement RSP in the T-state *before* the read; read at the raw pointer                          |
| Return stack SRAM ×4 (two 16-bit lanes) + RDIN '257s | ~7    | **Deleted.** Return frames are bytes pushed sequentially into main RAM                                        |
| Second I-mem EEPROM, IR '374 ×2, BRK-force '257 ×2   | 5     | IR is one '377; interrupt entry is a microcode address bit — no jam hardware                                  |
| NEXTPC 3-state drivers + vector '139                 | 3–4   | **Deleted.** PC loads from the bus; vectors are microcode constants                                           |
| TOS 8:1 source routing + NOS latch + TUCK '257s      | ~7    | **Deleted.** One bus. NOS is not a register — it lives in RAM and is fetched into the ALU B latch when needed |
| D-address 3:1 mux (13-bit)                           | ~4    | **Deleted.** One address bus, three tri-state sources, one active per T-state                                 |
| 22V10 ×5                                             | 5     | Replaced by 4× 28C64 μROM + sequencer (§7) — more packages here, far less engineering                         |

The multi-cycle *tax* is the new hardware time-sharing requires: ALU A/B latches, the
ADR latch pair, the T-counter, the μROMs, the strobe decoders — about +12 packages.
The deletions total about −37. Net ≈ −25.

# 3. Top-level architecture

One 8-bit data bus. One 16-bit address bus. One memory. One ALU. A microprogram walks
data across the bus one transfer per T-state.

```
                    ┌────────────────────────────────────────────┐
  ADDRESS BUS (16)  │  sources: PC │ ADR latch │ {page-const, SP} │
                    └───────┬────────────────────────────────────┘
                            │
                    ┌───────┴───────┐
                    │  RAM 32K×8    │  + 8K boot EEPROM + I/O (memory map §4)
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

Bus discipline: exactly one source driver enabled per T-state (a '138 decodes a 3-bit
microcode field — mutual exclusion is structural, not conventional). Destination load
strobes come from a second decoder, phase-gated by /CLK so nothing loads or writes
until the bus has settled — the WRPH lesson applied machine-wide by construction, with no per-strobe engineering.

# 4. Memory map (proposal)

| Range         | Contents                                             |
| ------------- | ---------------------------------------------------- |
| 0x0000–0x7FFF | SRAM 32K×8 (code + data + stacks)                    |
| 0x7E00–0x7EFF | Data stack page (grows up, DSP = low address byte)   |
| 0x7F00–0x7FFF | Return stack page (grows up, RSP = low address byte) |
| 0x8000–0xBFFF | I/O window ('139 decode; ports on low address bits)  |
| 0xC000–0xDFFF | 8K boot EEPROM (28C64) — reset entry at 0xC000       |
| 0xE000–0xFFFF | spare / EEPROM mirror                                |

Reset: PC async-clears... to 0x0000, which is RAM — so instead the PC's high-byte
'161 pair async-**loads** 0xC0 (parallel inputs strapped) while the low pair clears.
The boot loader copies to RAM and jumps, exactly the existing loader philosophy; the
EEPROM//OE + SRAM//OE split and DS1813 discipline carry over. Being von Neumann, the
loader is *simpler* than Thumby's problem in reverse: the CPU itself can copy EEPROM →
RAM as ordinary LOAD/STORE code. The external loader hardware can be deleted entirely.

Stacks are 256 deep each, page-anchored: the address high byte during a stack access is
a hardwired constant, the low byte is the SP counter. Free-running mod-256 pointers,
wraparound visible on the panel — unchanged philosophy.

# 5. Instruction encoding — byte stream

One opcode byte, then 0–2 operand bytes taken from the instruction stream by the
microprogram (PC increments through them; no separate operand register).

| Length  | Instructions                                                                                        |
| ------- | --------------------------------------------------------------------------------------------------- |
| 1 byte  | all ALU ops, all stack/return-stack ops, NOP, HALT, RET, RTI, BRK, CLI, SEI, FETCH, IN (stack form) |
| 2 bytes | PUSH #imm, IN #port, OUT #port, ENTER k, LOCAL@ off, LOCAL! off                                     |
| 3 bytes | JUMP a16, CALL a16, LOAD a16, STORE a16, Bcc a16 (all conditionals)                                 |

**The encoding cleverness of the single-cycle machine is retired, deliberately.** The
opcode byte is a raw microcode ROM address — there are no shared cubes to buy, no
family/member structure needed, no pop-bit placement, no term budgets. Opcodes are
assigned for human readability and nothing else. (A family grouping can be kept purely
as a mnemonic convention.)

**Relative branches are dropped.** Every conditional is absolute with full 16-bit
reach, so BEQ/BNE/BCS/BCC/BMI/BPL all survive but the inverse-branch far idiom is no
longer needed — every conditional is far. This deletes the sign-extend/offset hardware
and simplifies BlinkyASM. Condition select and polarity ride in opcode bits feeding a
'151 (§7), so the six conditionals cost zero decode.

`(INT)` remains a jam-only pseudo-opcode in spirit, but jamming is now just the
interrupt-pending bit steering the fetch microcode (§9) — no hardware pattern exists.

# 6. Datapath blocks

| Block             | Parts                                | Notes                                                                                                                                                                           |
| ----------------- | ------------------------------------ | ------------------------------------------------------------------------------------------------------------------------------------------------------------------------------- |
| PC                | '161 ×4                              | 16-bit counter. Counts for every instruction-stream byte (opcode and operands alike). Parallel-loads by byte from the data bus for JUMP/CALL/RET/Bcc; async reset load = 0xC000 |
| PC → address bus  | '541 ×2                              | enabled on fetch/operand T-states                                                                                                                                               |
| PC → data bus     | '257 ×2                              | byte select (lo/hi) for CALL's return-address push                                                                                                                              |
| ADR latch         | '574 ×2                              | data-address register for LOAD/STORE/FETCH; loads by byte from the bus, drives the address bus tri-state                                                                        |
| DSP, RSP          | '191 ×4                              | 8-bit each, mod-256, async /LOAD-low reset idiom unchanged                                                                                                                      |
| SP → address low  | '257 ×2                              | selects DSP vs RSP, tri-state onto address low byte                                                                                                                             |
| Const driver      | '541 ×1                              | microcode-selected constants onto address high / data bus: data-stack page, return-stack page, vector bytes                                                                     |
| ALU               | '181 ×2                              | one adder for everything: arithmetic, logic, BP+offset, and any future address math                                                                                             |
| ALU A, B latches  | '377 ×2                              | loaded from the bus in prior T-states; B doubles as the operand-assembly temp for JUMP/CALL                                                                                     |
| ALU → data bus    | '541 ×1                              | A/B are latched, so the ALU output is stable while driving                                                                                                                      |
| TOS               | '194 ×2 + '153 + '541                | the shifter survives intact: SHL/SHR/ASR/ROL/ROR act on TOS in place, same '153 fill mux, fill select from raw IR bits. '541 drives TOS onto the bus. LEDs off the '194 Qs      |
| Flags             | '377 ×1 + '157 ×1 + Z-detect glue ×2 | N/Z/C/I in one register; '157 selects ALU-derived vs stack-restored (RTI) inputs; NZ_WE / C_WE masking kept                                                                     |
| Interrupt latches | '74 ×2                               | IRQ level sample, NMI sync + true-edge pend — same three-flop pattern as PLD5, in discrete form                                                                                 |
| IR                | '377 ×1                              | opcode byte only                                                                                                                                                                |
| BP                | '377 ×1 + '541 ×1                    | 8-bit (locals live in low RAM); BP+off runs A ← BP, B ← offset byte, ALU → ADR low, const 0x00 → ADR high                                                                       |
| Memory            | SRAM 32K ×1, 28C64 boot ×1, '139 ×1  | '139: half = memory map decode, half = I/O strobes                                                                                                                              |
| I/O               | '574 ×1 out, '541 ×1 in, +1 decode   | expandable; port number from operand byte or TOS via the normal bus paths                                                                                                       |

Things that are **no longer registers at all**: NOS (it is RAM at DSP−1, fetched into B
when an ALU op needs it), RTOS (it is RAM), the D-address mux, every dedicated adder.

# 7. Control — microcode ROM sequencer

The five 22V10s and all of BlinkyJED's role in control are replaced by a lookup table.

**Sequencer:** one '161 T-counter (3 bits used, T0–T7 max). Every microword carries a
**TRST** bit; asserting it makes the next edge fetch T0 of the next instruction.
Variable T-states per instruction is literally this one bit.

**μROM address (13 bits — exactly one 28C64):**

```
A12..A5 = IR[7:0]     opcode byte, raw
A4..A2  = T[2:0]      T-state
A1      = COND        '151 output: flag selected by opcode bits, polarity by one more
A0      = INTP        interrupt pending (NMI_PEND OR (IRQ_PEND AND I=0))
```

**μROM data:** 4× 28C64 in parallel = 32 control lines. Field sketch:

| Field         | Bits | Meaning                                                                                                           |
| ------------- | ---- | ----------------------------------------------------------------------------------------------------------------- |
| SRC           | 3    | bus source → '138: RAM, ALU, TOS, PC-byte, IN-port, const, BP, (spare)                                            |
| DST           | 4    | bus destination → '154, phase-gated by /CLK: IR, A, B, TOS, PClo, PChi, ADRlo, ADRhi, BP, FLAGS, RAM-WE, OUT-port |
| ALU S,M,Cn    | 6    | direct to the '181s                                                                                               |
| ASEL          | 2    | address-bus source: PC / ADR / {page, SP} / none                                                                  |
| PCINC, PCBYTE | 2    | PC count enable; lo/hi select for PC-byte source                                                                  |
| SP            | 3    | encoded: none / DSP± / RSP±, drives the '191 /CTEN + D//U pairs                                                   |
| TOSMODE       | 2    | '194 S1S0: hold / load / shift (direction per S-code; fill from IR via the '153, unchanged)                       |
| NZ_WE, C_WE   | 2    | per-flag masking, as ratified for the ALU                                                                         |
| ISET, ICLR    | 2    | I flag control (SEI/CLI/entry/RTI paths)                                                                          |
| CSEL          | 2    | const-driver pattern: D-page / R-page / vector / 0x00                                                             |
| TRST          | 1    | end of instruction                                                                                                |
| spare         | ~3   |                                                                                                                   |

**Glitch discipline:** EEPROM outputs wander while the address (T) changes. Every
edge-triggered load is safe by nature (settled long before the edge at these access
times); every level strobe (RAM /WE, OUT) comes from the DST '154 whose enable is
gated by /CLK — asserted only in the settled second half. This is the single-cycle
machine's WRPH = /CLK rule, but enforced at one gate for every strobe in the machine.

**Toolchain:** the microcode source is a table in a small **C# generator** — one row
per (opcode, T, COND, INTP), emitting the four .bin images plus a human-readable
listing. TTLSim already loads 28C-series parts, so the full verification chain is:
generate → TTLSim sweep → burn. **BlinkyJED, WinCUPL cross-check, and JEDTester leave
the control path entirely.** A microcode bug is a table edit and a re-burn — no fuse
maps, no term budgets, no pin planning.

# 8. T-state budgets (illustrative — final counts fall out of the microcode table)

T0 is always: address ← PC, RAM → IR, PC++. One T-state per additional
instruction-stream byte, plus the work.

| Instruction          | T-states | Sketch                                                                               |
| -------------------- | -------- | ------------------------------------------------------------------------------------ |
| NOP                  | 1        | fetch, TRST                                                                          |
| PUSH #imm            | 3        | fetch; TOS → RAM[Dpage:DSP], DSP++; RAM[PC] → TOS, PC++                              |
| DUP                  | 2        | fetch; TOS → RAM[Dpage:DSP], DSP++                                                   |
| DROP                 | 3        | fetch; DSP−−; RAM[Dpage:DSP] → TOS                                                   |
| ADD (all binary ALU) | 5        | fetch; TOS → A; DSP−−; RAM[Dpage:DSP] → B; ALU → TOS + flags                         |
| SHL/SHR/ASR/ROL/ROR  | 2        | fetch; TOS shifts in place, C ← bit out                                              |
| JUMP a16             | 5        | fetch; opnd-lo → B, PC++; opnd-hi → A, PC++; ALU(F=B) → PClo; ALU(F=A) → PChi        |
| Bcc a16              | 3 / 5    | as JUMP when COND=1; TRST after the two operand skips when COND=0                    |
| CALL a16             | 9        | JUMP's 3 operand states + push PClo, PChi, BP (3 bytes to R-page) + 2 PC-load states |
| RET                  | 6        | fetch; pop BP, PChi, PClo (RSP−− then read, ×3) + loads                              |
| LOAD a16             | 6        | fetch; 2 operand bytes → ADR; spill TOS to stack; RAM[ADR] → TOS                     |
| LOCAL@ off           | 7        | fetch; off → B; BP → A; ALU → ADRlo; const 0 → ADRhi; spill TOS; RAM[ADR] → TOS      |
| BRK / hardware entry | ~10      | push PClo, PChi, flags byte, BP; I ← 1; vector consts → PC                           |
| RTI                  | ~8       | pop in reverse; flags via the '157 restore path                                      |

Average for stack-machine code ≈ **4–5 T**.

**Throughput honestly stated:** at a 4 MHz T-clock this is ~0.8–1.0 MIPS against the
single-cycle machine's 4 MIPS — roughly **5× slower**. Partial compensation: the
critical path shortens dramatically (the BP-adder write path, the machine's ceiling,
no longer exists — the longest path is μROM access + decode + setup), so a 6–8 MHz
T-clock is plausible with AT28C64B-45 parts, pulling it back to ~1.5 MIPS.

# 9. Interrupts

The pattern survives; the hardware almost entirely evaporates.

- **INTP is a μROM address bit.** When pending, T0 of the *fetch* microcode is a
  different row: instead of loading IR from RAM, the sequencer runs the entry
  microprogram (push PC-lo, PC-hi, flags byte, BP; set I; load PC from the vector
  constants). No BRK-force '257s, no vector driver, no '139 Y3 — the jam is an address
  bit.
- Raw-PC vs PC+1 falls out for free again, differently: hardware entry runs *before*
  the fetch increments PC, so raw PC is pushed; BRK's own microcode runs *after* its
  fetch, so PC+1 is pushed. The B distinction rides in the pushed flags byte.
- NMI-over-IRQ priority: NMI_PEND steers the CSEL vector constant (one input on the
  const path). Masking: INTP = NMI_PEND OR (IRQ_PEND AND NOT I) — two gates.
- Flags + I travel as one pushed byte; RTI pops it through the '157 restore mux. The
  saved-I lane, ILANE routing, and lane spare-bit engineering all disappear — a byte is
  a byte.
- Round trip is ~18 T-states (~10 entry + ~8 RTI) versus the single-cycle machine's 2.
  Still comfortably ahead of the 6502's 13 *instruction-length* cycles in wall-clock
  terms only at higher T-clock; this is a real regression and is accepted.

# 10. Chip count

| Block                                                                           | Chips   |
| ------------------------------------------------------------------------------- | ------- |
| Control: 28C64 μROM ×4, '161 T-counter, '151 COND, '138 SRC, '154 DST, '00 glue | 9       |
| PC: '161 ×4, '541 ×2 (addr), '257 ×2 (byte → data bus)                          | 8       |
| ADR latch: '574 ×2                                                              | 2       |
| DSP/RSP: '191 ×4, '257 ×2 (SP → addr)                                           | 6       |
| Const/page driver: '541                                                         | 1       |
| ALU: '181 ×2, A/B '377 ×2, '541 bus driver                                      | 5       |
| TOS: '194 ×2, '153 fill, '541 driver                                            | 4       |
| Flags: '377, '157 restore, Z-detect glue ('30 + '04)                            | 4       |
| Interrupt latches: '74 ×2                                                       | 2       |
| IR: '377                                                                        | 1       |
| BP: '377 + '541                                                                 | 2       |
| Memory: 32K SRAM, 28C64 boot, '139 map/IO decode                                | 3       |
| I/O: '574 out, '541 in, +1 decode/expansion                                     | 3       |
| **Core total**                                                                  | **~47** |

Versus the single-cycle machine's ~70–73: **≈ −25 packages (−35%)**, five fewer
programmable parts to verify, one memory chip type era instead of three, and zero
'283s in the building. The existing Clock and Reset board carries over unchanged (its
divider chain finally has a customer again if the quarter-phase gating ever wants one —
though §7's /CLK gating needs nothing extra). The Breakpoint board carries over: it
compares PC, and PC still exists; "matched" should additionally gate on T=0 so it halts
on instruction boundaries.

# 11. Reduction ladder — trading features for packages

Each rung is independent and unratified:

| Cut                                                                                                       | Saves | Price                                                                         | Running total |
| --------------------------------------------------------------------------------------------------------- | ----- | ----------------------------------------------------------------------------- | ------------- |
| Baseline (§10)                                                                                            | —     | —                                                                             | ~47           |
| Drop the '194 shifter (TOS → '377 + '541; SHL = A plus A on the '181; SHR/ASR/ROL/ROR become subroutines) | −2    | shift-heavy code gets slow; the SHL/SHR TOS LED show is gone                  | ~45           |
| Drop hardware BP (BP becomes a fixed RAM cell; ENTER/LOCAL@/LOCAL! microcoded through it)                 | −2    | +3–4 T per frame op; BP LEDs gone                                             | ~43           |
| SPs into RAM cells (PDP-8 style zero-page pointers)                                                       | −6    | every push/pop balloons to ~8 T; DSP/RSP LEDs gone — a heavy front-panel loss | ~37           |
| 8-bit machine throughout (256-byte space, 8-bit PC, 2-byte CALL frames)                                   | −4    | a toy; PC '161 ×2, one addr driver, one ADR latch                             | ~33           |

Recommendation: take none of them. ~47 with the full programmer model is the design;
the ladder exists so the cost of each feature is priced, not assumed.

# 12. What is lost — stated plainly

1. **Speed: ~5× fewer instructions per second** at the same T-clock (§8).
2. **Per-instruction front-panel states.** Single-step becomes per-T-state by default;
   a "step instruction" mode (run until TRST) restores the old behaviour and is a
   two-gate addition on the clock board's step path. Arguably a teaching *gain*: the
   panel now shows the fetch, the operand reads, and the ALU transfer as separate
   settled states.
3. **NOS LEDs.** There is no NOS register. The panel can show A and B latches instead —
   during any ALU op, B *is* NOS.
4. **The single-cycle interrupt story.** Entry+RTI goes from 2 cycles to ~18 T-states.
5. **The encoding elegance.** Shared cubes, the pop bit, B = IR[10] — all retired. Their
   replacement (a lookup table) is less clever and much easier to be right about.
6. **Relative branches / relocatable short hops.** Absolute-only control flow;
   BlinkyASM resolves everything at assembly time.

# 13. Open questions for ratification

1. Memory map (§4): page addresses for the two stacks; EEPROM at top vs bottom;
   PC reset value = 0xC000 via strapped '161 parallel inputs.
2. Byte order of 16-bit operands and pushed PC (proposal: little-endian throughout).
3. BP width: 8-bit with locals confined to low RAM (as specced) vs 16-bit (+1 '377,
   +1 '541, +2 T per frame op).
4. Drop the external boot-loader hardware in favour of CPU-executed copy from EEPROM
   (§4) — the von Neumann dividend.
5. μROM part speed: 28C64-150 caps the T-clock near 4 MHz; AT28C64B-45 opens 6–8 MHz.
   Alternatively one '377 ×4 pipeline register on the μROM outputs (+4 chips) — not
   recommended, contradicts the thesis.
6. Opcode byte assignments (free choice — propose keeping the family nibble structure
   as a mnemonic convention only).
7. Whether the T-state counter and decoded phase get front-panel LEDs (proposal: yes,
   3 + 1).
