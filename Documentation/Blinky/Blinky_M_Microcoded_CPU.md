# Blinky-M — A Microcoded Dual-Stack TTL CPU

**Rev 14 — GAL control ratified. Working title.**

---

# 0. Thesis

An 8-bit dual-stack Forth machine built from 7400-series parts, designed for minimum
IC count and — from rev 14 — for speed, via a two-stage GAL control path with no
EEPROM in it. The datapath is time-sharing: one memory, one 8-bit bus, one ALU, one
address path, visited in sequence by a microprogram, one transfer per T-state.
Instructions take as many T-states as they need; each microprogram simply ends when it
ends. Nothing in the machine exists twice.

**~57 ICs core**, front panel and clock module extra.

# 1. Programmer model

- **Dual-stack Forth machine.** Data stack for operands, return stack for control flow
  and frames. TOS is the accumulator-in-spirit and lives in a register; everything
  deeper lives in RAM.
- **ALU ops** take TOS and NOS, result to TOS: ADD, ADC, SUB, AND, OR, XOR, NOT, TST,
  CMP (non-destructive), plus the five in-place TOS shifts SHL, SHR, ASR, ROL, ROR.
  The '181 function is carried in the opcode itself (§4), so the full '181 table is
  reachable — the named ops are the useful subset, the rest available as `ALU #n`.
- **Flags N, Z, C**, with per-flag write masking: logic ops preserve C; arithmetic and
  CMP write it; shifts write C only (N/Z held).
- **Stack ops:** DUP, DROP, SWAP, OVER, NIP, TUCK, ROT, and the return-stack movers
  >R, R>, R@. ΔDSP per instruction is whatever the operation needs.
- **Frames:** BP base pointer (8-bit); ENTER k opens a frame, LOCAL@ off / LOCAL! off
  access BP-relative locals, RET restores BP automatically. Frames and globals live in
  page 0 (§3).
- **Memory/I-O:** absolute LOAD a16 / STORE a16; stack-form FETCH / STORE!
  (zero-page, address from the stack); IN #port / OUT #port (8-bit port number) and
  their stack forms. I/O is memory-mapped onto one page (§3), so LOAD/STORE a16 reach
  every port too.
- **Control flow:** JUMP a16, CALL a16, RET; conditionals BEQ/BNE/BCS/BCC/BMI/BPL, all
  absolute with full 16-bit reach — there is no short/far distinction.
- **Interrupts:** NMI (edge, unmaskable), IRQ (level, masked by I), software BRK, RTI
  restoring PC, BP, flags, and I. Entry pushes state automatically; a handler can use
  ENTER/locals with zero extra convention.

The complete opcode map with encodings and cycle counts is **Appendix A**.

# 2. Top-level architecture

One 8-bit data bus. One 16-bit address bus. One memory. One ALU.

```
                    ┌─────────────────────────────────────────────────────┐
  ADDRESS BUS (16)  │  sources: PC │ ADR │ {page, SP-low} │ {page, ADRlo}  │
                    └───────┬─────────────────────────────────────────────┘
                            │
                    ┌───────┴───────┐
                    │ RAM 64K×8     │  56K enabled + 8K boot EEPROM
                    │ (W24512)      │  + I/O page (memory map §3)
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

**Bus law: one source, one destination per T-state.** A '138 decodes a 3-bit
microcode field into the source enables, so mutual exclusion is structural, not
conventional; the destination strobe comes from a second decoder whose enable is
gated by /CLK, so nothing loads or writes until the bus has settled — every write in
the machine is confined to the second half-cycle by one gate. Edge-actions are the
exception: counter enables, TOS shifts, flag writes, and /TRST touch no bus, so any
number of them may ride any state.

The non-bus paths are deliberately few: A→ALU and B→ALU (hardwired operand inputs),
the '194's internal shift feedback and its '153 fill, ALU→flag inputs, and the
counters' count enables. Every other register-to-register arrow in §7 is a data-bus
transfer, one per T-state.

**Same-edge counter rule.** Every counter (PC '161s, DSP/RSP '191s) counts on the
clock edge that *ends* a T-state — the same edge on which destination registers latch
and the phase-gated write strobes close. Throughout the state the counter's outputs
are the old value, so any address it drives is stable; the new value exists only in
the following state (counter propagation delay after the edge provides the address
hold margin for the closing /WE). Consequences:

- Post-increments ride the state that used the pointer (`RAM[PC] → TOS, PC++`).
- A pop's pre-decrement rides the *preceding* state: assert DSP−− during fetch and
  the decremented pointer is already the valid read address in the next state.
  Decrement-then-read costs zero T-states.
- DSP and RSP have independent microword fields (§6), so both may count on one edge
  (`>R` moves a byte between stacks in a single write state).

**Opcode bypass — instruction-specific T0 (ratified).** The μROM is addressed by IR,
but during T0 the new opcode is still in flight: it is on the data bus (RAM driving
it into IR) and only latches on T0's closing edge. A '257 pair steers the μROM's
opcode address bits between the IR outputs (T ≠ 0) and the live data bus (T = 0), so
T0's microword already belongs to the incoming instruction. This is what legalises
the fetch-state folds above. Cost: two packages, and T0 becomes the machine's
critical path (SRAM → '257 → μROM → strobe decode) — see §7 throughput.

# 3. Memory map (ratified)

**56K SRAM + 8K EEPROM + one memory-mapped I/O page.** The SRAM part is a **W24512
(64K×8)**, enabled everywhere except the EEPROM window and the I/O page — no stranded
capacity. All machine pages sit contiguously at the bottom.

| Range | Contents |
|---|---|
| 0x0000–0x00FF | **Frame/globals page** — BP-addressed (page 0x00). Globals at the bottom; init lifts BP above them; ENTER k advances BP per frame. Ceiling: globals + sum of live frames ≤ 256 bytes |
| 0x0100–0x01FF | **I/O page** — 256 memory-mapped ports (page 0x01). '688 decode carves it out of SRAM |
| 0x0200–0x02FF | **Data stack** (page 0x02; grows up, DSP = low byte) |
| 0x0300–0x03FF | **Return stack, PC-lo lane** (page 0x03) |
| 0x0400–0x04FF | **Return stack, PC-hi lane** (page 0x04) |
| 0x0500–0x05FF | **Return stack, BP lane** (page 0x05) |
| 0x0600–0x06FF | **Return stack, flags lane** (page 0x06; written on interrupt/BRK frames only) |
| 0x0700–0xDFFF | SRAM — code + data (contiguous, unbroken, ~54.25K) |
| 0xE000–0xFFFF | 8K boot EEPROM (28C64) — reset entry at 0xE000; vector JUMP slots at 0xFEFE / 0xFDFD (§8) |

**Lane-structured return stack.** One RSP index spans the four lane pages: a call
frame is the three bytes {0x03:RSP, 0x04:RSP, 0x05:RSP} = {PC-lo, PC-hi, BP}, an
interrupt frame adds {0x06:RSP} = flags. RSP counts **once per frame**, not per byte:
RSP is call depth, readable directly off its LEDs, and the stack holds a full 256
nested frames. `>R`/`R>`/`R@` move data through the PC-lo lane at the same index —
the classic discipline hazard stands: a RET through `>R`'d data vectors PC into
garbage, visibly.

Address decode — two terms, total:

- **/EEPROM_CS = NAND(A15, A14, A13)** — one 3-input gate selects the top 8K.
- **/SRAM_CS = NAND(/EEPROM_CS, /IO-match)** — SRAM answers everywhere neither the
  EEPROM nor the '688's I/O-page match claims.

No dedicated decoder package: both gates fold into the glue ('10 + spare '00).

Reset: the PC high-byte '161 pair async-loads 0xE0 (parallel inputs strapped), the
low pair clears — PC = 0xE000, straight into EEPROM. The boot code copies itself (or
the application) into RAM with ordinary LOAD/STORE and jumps; because code and data
share one space, no external loader hardware exists at all. A DS1813 supervises
reset.

Stacks are 256 deep, page-anchored: the page driver supplies the high byte, the SP
counter the low byte. Pointers are free-running mod-256 — wraparound is a visible
front-panel diagnostic, not a trapped error.

# 4. Instruction encoding — byte stream

One opcode byte, then 0–2 operand bytes taken from the instruction stream by the
microprogram (PC increments through them; no separate operand register).

| Length | Instructions |
|---|---|
| 1 byte | all ALU ops, all stack/return-stack ops, NOP, HALT, RET, RTI, BRK, CLI, SEI, FETCH, STORE!, IN, OUT (stack forms) |
| 2 bytes | PUSH #imm, IN #port, OUT #port, ENTER k, LOCAL@ off, LOCAL! off |
| 3 bytes | JUMP a16, CALL a16, LOAD a16, STORE a16, Bcc a16 (all conditionals) |

The high nibble names the **family** and — because rev 14 banks the control sequencer
on it (§6) — is a genuine hardware boundary, not merely a mnemonic convention. Two
anchors are fixed: **NOP = 0x00** and **HALT = 0xFF**, both bus-idle patterns, so a
fetch from unprogrammed or floating memory halts the machine rather than running wild.

| Family | Nibble(s) | Contents |
|---|---|---|
| CTL | 0x0_ | NOP, RET, BRK, RTI, CLI, SEI |
| SHIFT | 0x1_ | SHL, SHR, ASR, ROL, ROR (not '181 ops — their own family) |
| STK | 0x2_ | DUP, OVER, TUCK, SWAP, DROP, NIP, ROT, >R, R>, R@ |
| MEM | 0x3_ | PUSH #, LOAD, STORE, FETCH, STORE!, IN #, OUT #, IN, OUT |
| ALU | 0x4_–0x7_ | the '181 quadrant (64 slots) |
| FRM | 0x8_ | ENTER, LOCAL@, LOCAL! |
| FLOW | 0x9_ | JUMP, CALL, BEQ/BNE/BCS/BCC/BMI/BPL |

Three member placements are load-bearing wiring, not aesthetics:

- **ALU quadrant (0x40–0x7F):** the opcode *is* the '181 control —
  `0 1 M CN S3 S2 S1 S0`. The six function bits wire (through the ALUFN mux, §5)
  straight to the '181 S/M/CN pins, so the entire '181 function table is a working
  instruction. The ~9 named ops occupy their datasheet patterns; every other slot is a
  valid `ALU #n` (Appendix A). This is what makes the ALU family's writeback a *single*
  micro-op instead of one per function — the decisive simplification behind the GAL
  control store.
- **SHIFT family (0x1_):** fill in IR[3:2] wires to the '153 shifter-fill mux;
  direction in IR[0] wires to the '194 shift direction and the C-source tap.
- **Branch members (0x9_, 8–F):** IR[2:1] select the flag (01 Z, 10 C, 11 N), IR[0]
  the polarity, wired raw to the COND mux — six conditionals, zero decode.

16-bit operands and pushed addresses are **little-endian** throughout.

IN #port / OUT #port carry an 8-bit port number and microcode it as a paged access
(page 0x01, port number in the address low byte) — one form of each. The stack forms
take the port from TOS the same way.

# 5. Datapath blocks

| Block | Parts | Notes |
|---|---|---|
| PC | '161 ×4 | 16-bit counter. Counts for every instruction-stream byte, opcode and operands alike. Parallel-loads by byte from the data bus for JUMP/CALL/RET/Bcc; async reset load = 0xE000 |
| PC → address bus | '541 ×2 | enabled on fetch/operand T-states |
| PC → data bus | '257 ×2 | byte select (lo/hi) for CALL's return-address push |
| ADR latch | '574 ×2 | data-address register. ADRlo is the workhorse — every paged access uses {page, ADRlo}; ADRhi is written only by LOAD/STORE a16. Both load from the bus, drive the address bus tri-state |
| DSP, RSP | '191 ×4 | 8-bit each, mod-256, async /LOAD-low with inputs tied low as the reset idiom; independent microword fields — both may count on one edge. RSP counts per frame (§3) |
| SP → address low | '257 ×2 | selects DSP vs RSP, tri-state onto address low byte; select = the SPSEL microword bit |
| Page driver | '541 ×1 | drives the address high byte on every paged access. No mux: input bits 2..0 = PGSEL, bits 7..3 = GND — PGSEL 0–6 yields pages 0x00–0x06 directly; 7 spare |
| Vector driver | '541 ×1 | drives the vector byte onto the data bus: bit 0 = NMI_PEND, bit 1 = its complement, bits 7..2 = VCC — emits 0xFE (IRQ/BRK) or 0xFD (NMI) |
| ALU | '181 ×2 + '153 ×3 (ALUFN mux) | one adder for everything. The S/M/CN pins are driven by a 3× '153 mux: ALUFN = IR (the opcode's own function bits, for the ALU family), F=A, F=B, or A+B (the three patterns non-ALU instructions need) |
| ALU A, B latches | '377 ×2 | loaded from the bus in earlier T-states; B doubles as the operand-assembly temp for JUMP/CALL |
| ALU → data bus | '541 ×1 | A and B are latched, so the ALU output is stable while driving |
| TOS | '194 ×2 + '153 + '541 | shifts act on TOS in place; the '153 on the '194 serial pins selects the fill (zero / sign / wrap) from raw IR bits [3:2]; the S-pins derive from TOSMODE + IR[0] (direction) in glue; '541 drives TOS onto the bus; LEDs off the '194 Qs |
| Flags | '377 ×1 + '157 ×3 + '541 ×1 + Z-detect glue ('30 + '04) | N/Z/C/I in one register; '157 #1 selects ALU-derived vs stack-restored (RTI) inputs; '157 #2 taps the outgoing shift bit (Q7 vs Q0, steered by IR[0]) and selects the C input ('181 Cn+4 vs shift bit, C_SRC = TOSMODE-shift); '157 #3 is **hold-feedback** — the '377's clock enable is common, so per-flag masking is D = WE ? new : Q per flag (NZ_WE / C_WE / the I paths steer it). Shifts write C only — N/Z held. The '541 drives the flags byte onto the bus for the interrupt-entry push; its **B input is /INTP** (registered): BRK pushes B = 1, hardware entry pushes B = 0, zero decode |
| Interrupt latches | '74 ×2 | IRQ level sample; NMI synchroniser + true-edge pend (pend = NMI AND NOT sync — one-shot, no retrigger while held); the fourth flop **registers INTP**, sampled on the TRST edge entering T0, so the entry sequence cannot be torn when ISET clears the pending condition mid-sequence |
| IR | '377 ×1 | opcode byte only |
| BP | '377 ×1 + '541 ×1 | 8-bit (ratified); frames/globals in page 0. LOCAL@/LOCAL! run A ← BP, B ← offset, ALU → ADRlo, then a page-0x00 access |
| Memory | W24512 SRAM ×1, 28C64 boot ×1, '10 select glue ×1 | /EEPROM = NAND(A15,A14,A13); /SRAM = NAND(/EEPROM, /IO-match) — see §3 |
| I/O | '688 page decode, '574 out, '541 in | '688: address high byte = 0x01 → I/O strobes, SRAM /CS override. Ports are address-decoded, invisible to the microword. Ports 0 and 1 are the on-board out latch and in buffer; the rest of the page is expansion |

NOS is not a register: it is RAM at DSP−1, fetched into the B latch when an ALU op
needs it. Popping is decrement-then-read under the same-edge rule (§2): the SP counts
down at the end of the preceding T-state, so the read is at the raw pointer and no
address arithmetic exists — and the decrement itself costs nothing.

# 6. Control — two-stage GAL sequencer

There is no microcode EEPROM. Control is generated by programmable logic in two
stages: a **sequencer** that turns (opcode, T, COND, INTP) into a 7-bit micro-op
index, and a **decoder** that expands that index into the machine's control lines.
The full treatment, with the micro-instruction reference, is `Blinky_M_GAL_Control.md`;
this section is the summary the rest of the design leans on.

## Why an index, not a control word

Across the whole instruction set only **57 distinct control-line combinations** ever
occur — 57 micro-ops. Naming each with a small index splits control into two
tractable problems: *which* micro-op is this instruction-step (a sequencing question,
naturally per-family), and *what does a micro-op do* (a fixed decode, identical for
every instruction that uses it). Both fit GALs; the decode is a pure combinational
function of a 7-bit index, the ideal shape for a 22V10.

The rev 14 ISA changes exist to make this split clean. The IR-driven '181 (§4) means
the ALU family contributes *two* writeback micro-ops (arithmetic, logic) instead of
one per function — without it the dictionary would not close at 57. The fold set
below trims 32 nominal control lines to 20, so the decoder fits two 22V10s.

## Stage 1 — sequencer (per bank)

A '138 decodes opcode[7:4] into one bank enable. Each bank owns a **combinational
22V10** whose inputs are the opcode low bits, COND, and the T-state T[2:0] from an
external '161 counter; its outputs are IDX[6:0] plus TRSTN (active-low /TRST). /TRST
drives the counter's /LOAD, so asserting it reloads the counter to zero and ends the
instruction — the same synchronous-clear idiom the machine already uses, no inverter.

There are eight banks — CTL, SHIFT, STK, MEM, ALU, FRM, FLOW, and CTLX (the HALT anchor
at 0xFF) — plus an ENTRY bank for interrupt entry, enabled by INTP with T as its only
live input. The ALU quadrant (0x40–0x7F) is one merged bank: the '138 ORs its four
nibble enables, so its GAL sees opcode[5:0] and T (9 inputs) and every ALU opcode runs
the one shared sequence.

Keeping the T-counter external is what lets a single 22V10 hold a whole bank — seven
index outputs plus TRSTN is eight, well within the ten macrocells, with no registered
feedback or async-reset logic to fit. TRSTN sits on pin 23 (pin 13 is input-only on a
22V10). The '161 is a part the datapath already carries and was never on the critical
path, so the speed story is unchanged.

## Stage 2 — decoder (shared)

Two 22V10s, **UOPA** and **UOPB**, expand IDX[6:0] into the 20 folded control lines,
purely combinationally. This is the dictionary made of fuses — identical hardware
regardless of which instruction runs. Worst line is 12 product terms against the
22V10's 16-term maximum macrocell, so both parts fit with margin (final fits confirmed
by BlinkyJED / WinCUPL).

## The fold set (ratified)

Twelve nominal fields collapse to what two GALs can carry; each fold is structural, not
a loss of function:

- **/TRST** is a Stage-1 output (sequencing, not datapath), wired straight to the
  T-counter reload — active-low, no inverter.
- **TOS load** is implied by DST = TOS; no field of its own.
- **PCINC** is derived: SRC = RAM AND AMODE = PC. Instruction-stream reads match it;
  internal reads use AMODE = ADR and do not.
- **ICLR** rides as a DST code (CLI's step has no other destination).
- **PCBYTE** rides as two SRC codes, PCBlo / PCBhi.
- **ASEL + PGSEL + SPSEL** merge into a 4-bit **AMODE** (only 9 address modes exist,
  and the ADRlo-vs-SP choice is implied by page).
- **DSPCTL + RSPCTL** merge into a 3-bit **SPOP** (only ~7 combinations occur).

## Downstream expansion (unchanged)

A '138 fans SRC to bus-source enables; a '154 fans DST to destination strobes,
phase-gated by /CLK so every write lands in the settled half-cycle; an AMODE decoder
drives the address-bus source and page constants; a '138 drives the DSP/RSP controls
from SPOP; one AND gate derives PCINC.

## Toolchain

Both GAL stages are a generated target of the same C# source of truth (`BlinkyMGen`)
that defines the instruction table — a `--plds` output alongside the assembler's use of
the same tables. A change to an instruction re-fits the affected sequencer GAL; a
genuinely new micro-op re-fits the two decoders; neither can drift from the design.
Verification chain: BlinkyJED compile → WinCUPL fuse cross-check → TTLSim sweep.

# 7. T-state budgets

The per-instruction T-state counts are unchanged from the datapath's behaviour; only
the opcode encodings moved (Appendix A). The illustrative walk-throughs — fetch,
operand consume, the same-edge counter folds — are as before and are not repeated here;
the authoritative per-opcode cycle counts are the Appendix cycle column, and the
per-T-state micro-op decomposition is `Blinky_M_GAL_Control.md`.

Average for stack-shaped code ≈ 3–4 T-states per instruction.

**Throughput.** With no EEPROM in the control path, the T0 critical path is program-RAM
access + bypass '257 + the two GAL stages (~20 ns each) + strobe decode + setup. Code
runs from SRAM after the boot copy, so the control store no longer caps the clock; the
ceiling moves onto the HC datapath — 8–10 MHz territory, without the shadow-copy loader
the EEPROM design needed. Datapath elements never limit: every transfer is one
register-to-register hop across one bus.

# 8. Interrupts

- **INTP is a μROM address bit.** When pending, T0 of the fetch microcode is a
  different row: instead of loading IR, the sequencer runs the entry microprogram —
  push PC-lo, PC-hi, flags byte, BP into the frame lanes; set I; load PC from the
  vector driver. There is no jam hardware; the "forced instruction" is an address
  bit.
- **INTP is registered** — sampled into the spare interrupt-'74 flop on the TRST
  edge entering T0 — so the whole entry sequence runs from stable INTP=1 rows even after ISET
  clears the pending condition at T4.
- **Raw PC vs PC+n falls out of ordering.** Hardware entry runs before the fetch
  increments PC, so the interrupted instruction's own address is pushed and it
  re-executes on return. BRK's microcode runs after its fetch, so the push resumes
  after it. **B = /INTP**, wired to the flags driver: BRK (INTP = 0 path) pushes
  B = 1, hardware entry pushes B = 0 — no line, no gate.
- **Priority and masking:** NMI over IRQ — NMI_PEND steers the vector driver.
  INTP = NMI_PEND OR (IRQ_PEND AND NOT I): two gates. NMI also overrides HALT's clock
  stop, waking a halted machine.
- **I = 1 on reset** (interrupts masked until the init code executes CLI); the flag
  register's clear delivers it.
- Flags and I travel as one byte in the flags lane via the flags bus driver; RTI pops
  it back through the '157 restore mux. RET reads the BP and PC lanes only, leaving
  flags alone.
- Round trip = **11 T-states** (6 entry + 5 RTI).

**Vectors — repeated-byte addresses (ratified): 0xFEFE (IRQ/BRK), 0xFDFD (NMI).**
The vector driver is one '541: bit 0 = NMI_PEND, bit 1 = its complement, bits 7..2 =
VCC — it emits a single byte V (0xFE or 0xFD) and the entry microcode loads *both*
PC bytes from it. One driver, one input pattern, two load states, zero gating. Each
address holds a JUMP placed by the boot EEPROM; RAM-resident handlers are reached by
targeting the JUMPs at RAM after the boot copy.

# 9. Chip count

| Block | Chips |
|---|---|
| Control S1: '138 bank select, 9× 22V10 sequencer (8 banks + entry), '161 T-counter | 11 |
| Control S2: 22V10 UOPA, UOPB | 2 |
| Control expand: '138 SRC, '154 DST, AMODE decode, '138 SPOP, '00 PCINC/glue | 5 |
| Opcode bypass: '257 ×2 | 2 |
| PC: '161 ×4, '541 ×2 (addr), '257 ×2 (byte → data bus) | 8 |
| ADR latch: '574 ×2 | 2 |
| DSP/RSP: '191 ×4, '257 ×2 (SP → addr) | 6 |
| Page + vector drivers: '541 ×2 | 2 |
| ALU: '181 ×2, '153 ×3 (ALUFN), A/B '377 ×2, '541 bus driver | 8 |
| TOS: '194 ×2, '153 fill, '541 driver | 4 |
| Flags: '377, '157 ×3, '541 bus driver, Z-detect ('30 + '04) | 7 |
| Interrupt latches: '74 ×2 | 2 |
| IR: '377 | 1 |
| BP: '377 + '541 | 2 |
| Memory: W24512 SRAM, '10 select glue | 2 |
| I/O: '688 page decode, '574 out, '541 in | 3 |
| **Core total** | **67** |

The core is **EEPROM-free** — the boot loader copies from an external source into SRAM,
and the control store is fuses: eleven 22V10s (two decoders, nine sequencer banks)
behind a '138 bank selector, with the '161 T-counter driving the T-state. All eleven
GALs compile clean through BlinkyJED (5892 fuses each); the worst decoder line is 12
product terms and the worst sequencer line 7, both inside the 22V10's macrocells. One
programmable technology does all of control; the '181 ALU has no adder peers. Clock,
reset, and single-step live on a separate module; a PC breakpoint comparator gates its
halt on T=0 for instruction-boundary stops. One GAL per bank fits with margin — the
worst bank uses 7 of a macrocell's 8–16 terms — so no bank needed a second.

# 10. Front panel

| Group | LEDs | Notes |
|---|---|---|
| PC | 16 | |
| IR | 8 | opcode byte — family/member readable by nibble |
| TOS | 8 | also the live shift display |
| A / B | 8 + 8 | ALU operands; during any binary ALU op, B is NOS |
| DSP / RSP | 8 + 8 | DSP = operand depth; RSP = **call depth** (one count per frame) |
| BP | 8 | frame base |
| ABUS | 16 | the address bus — always driven (ASEL has no idle code), so it shows every access: fetches, stack traffic, data |
| FLAGS | 4 | N, Z, C, I |
| T-state | 3 + 1×8 decoded | which microstep is executing (ratified) |
| INT | 3 | IRQ pending, NMI pending, **INTP** — the live μROM address bit (ratified) |

Single-step is per-T-state by default — the panel shows the fetch, the operand reads,
and the ALU transfer as separate settled states. A step-instruction mode (run until
/TRST) is a two-gate addition on the step path.

# 11. Ratified — rev 14

Rev 14 ratifies the two-stage GAL control store as the machine's only microcode
solution. The EEPROM control store is retired; there is no fallback baseline.

**This revision ratifies:**

- **Two-stage GAL control** (§6): combinational 22V10 sequencer banks behind a '138,
  feeding two combinational 22V10 decoders; no control EEPROM. Eleven GALs total (nine
  sequencer banks, two decoders), all compiled clean through BlinkyJED.
- **External '161 T-counter.** The T-state comes from a '161 (as in stage 0); /TRST
  (TRSTN, active-low, GAL pin 23) drives its /LOAD to reload T=0 at end of instruction.
  Keeping the counter external keeps each bank in a single 22V10 (8 outputs of 10).
- **IR-driven '181** via the ALUFN '153 mux (§4, §5): the ALU family's function rides
  the opcode, collapsing the ALU writeback and exposing the full '181 table as `ALU #n`.
- **Opcode map** (§4): CTL 0x0_, SHIFT 0x1_, STK 0x2_, MEM 0x3_, ALU 0x40–0x7F, FRM
  0x8_, FLOW 0x9_; NOP = 0x00, HALT = 0xFF anchors; branch and shift member bits wired
  raw. The ALU quadrant is one merged sequencer bank (opcode[5:0] + T).
- **The fold set** (§6): /TRST as a sequencer output, TOS-load implied by DST,
  PCINC derived, ICLR-as-DST, PCBYTE-as-SRC, AMODE consolidation, SPOP consolidation.
- **57-entry micro-op dictionary** with a 7-bit index (71 slots spare). Constant index
  bits (IDX6 everywhere; low bits in SHIFT/CTLX) take no macrocell — tied to GND.

**Verified in the fitter:**

1. All eleven GALs compile through BlinkyJED at 5892 fuses each.
2. Worst decoder line 12 terms, worst sequencer line 7 — both inside the 22V10's
   graduated macrocells (8–16). One GAL per bank; none overflowed.
3. `BlinkyMGen` emits both GAL stages, the HTML control reference, and the assembler
   opcode table from the one C# instruction table — done; the fuse maps cannot drift.

**Remaining downstream (not control-store design):** run the JEDEC maps through the
TTLSim sweep against the datapath, then cut the board.

# Appendix A — Instruction Set Reference

Opcode = the instruction's first byte. Families by high nibble: 0x0_ CTL, 0x1_ SHIFT,
0x2_ STK, 0x3_ MEM, 0x40–0x7F ALU, 0x8_ FRM, 0x9_ FLOW; HALT = 0xFF. Cycles are
T-states, fetch included.

Operand notation (also fixes length: `—` = 1 byte, letter-pair = 2, `aaaa` = 3):
**ii** immediate · **pp** port · **kk** count · **oo** signed offset (wraps in page 0)
· **aaaa** 16-bit absolute, little-endian. Stack effects in Forth notation; R: is the
return stack.

**Control (0x0_)**

| Mnemonic | Opcode | Operand | Cycles | Operation |
|---|---|---|---|---|
| NOP | 0x00 | — | 1 | No operation; PC advances |
| RET | 0x01 | — | 4 | Pop {PC, BP} frame; flags untouched |
| BRK | 0x02 | — | 7 | Software trap: push {PC+1, flags·B=1, BP}; I ← 1; PC ← 0xFEFE |
| RTI | 0x03 | — | 5 | Pop interrupt frame; PC, BP, N/Z/C, I restored |
| CLI | 0x04 | — | 2 | I ← 0 — IRQs enabled |
| SEI | 0x05 | — | 2 | I ← 1 — IRQs masked |
| HALT | 0xFF | — | 2 | Stop the clock; NMI wakes |

**Shift (0x1_)** — in-place on TOS; C only, N/Z held. Fill = IR[3:2], direction = IR[0].

| Mnemonic | Opcode | Operand | Cycles | Operation |
|---|---|---|---|---|
| SHL | 0x10 | — | 2 | TOS ≪ 1, zero fill; C ← old bit 7 |
| SHR | 0x11 | — | 2 | TOS ≫ 1, zero fill; C ← old bit 0 |
| ASR | 0x13 | — | 2 | TOS ≫ 1, sign fill; C ← old bit 0 |
| ROL | 0x14 | — | 2 | TOS rotate left (wrap); C ← bit out |
| ROR | 0x15 | — | 2 | TOS rotate right (wrap); C ← bit out |

**Stack (0x2_)** — no flags.

| Mnemonic | Opcode | Operand | Cycles | Operation |
|---|---|---|---|---|
| DUP | 0x20 | — | 2 | `( a — a a )` |
| OVER | 0x21 | — | 4 | `( a b — a b a )` |
| TUCK | 0x22 | — | 4 | `( a b — b a b )` |
| SWAP | 0x23 | — | 4 | `( a b — b a )` |
| DROP | 0x24 | — | 2 | `( a — )` |
| NIP | 0x25 | — | 1 | `( a b — b )` — pure pointer move |
| ROT | 0x26 | — | 6 | `( a b c — b c a )` |
| >R | 0x28 | — | 3 | `( a — )` R:`( — a )` |
| R> | 0x29 | — | 3 | `( — a )` R:`( a — )` |
| R@ | 0x2A | — | 3 | `( — a )` R:`( a — a )` |

**Memory / I-O (0x3_)** — no flags. Ports are page 0x01; LOAD/STORE aaaa reach them too.

| Mnemonic | Opcode | Operand | Cycles | Operation |
|---|---|---|---|---|
| PUSH # | 0x30 | ii | 3 | `( — ii )` push immediate |
| LOAD | 0x31 | aaaa | 5 | `( — m )` push mem[aaaa] |
| STORE | 0x32 | aaaa | 5 | `( d — )` pop to mem[aaaa] |
| FETCH | 0x33 | — | 3 | `( a — m )` read page 0 at TOS |
| STORE! | 0x34 | — | 5 | `( d a — )` page 0 at a ← d |
| IN # | 0x35 | pp | 4 | `( — v )` read port pp |
| OUT # | 0x36 | pp | 4 | `( d — )` write TOS to port pp; pops |
| INS | 0x37 | — | 3 | `( p — v )` stack-form: port from TOS |
| OUTS | 0x38 | — | 5 | `( d p — )` stack-form: port from TOS; pops both |

**ALU (0x40–0x7F)** — opcode = `01 M CN S3 S2 S1 S0`; the '181 function rides the
opcode. Binary ops `( a b — r )` pop, result to TOS. Named subset below; every other
slot is a valid `ALU #n` realising that '181 function (M, CN, S3–S0). N/Z written
always; C per class (arithmetic and CMP write it, logic preserves it). Cycles: 4
binary, 3 unary, 4 CMP.

The opcode of each named op is fixed by its '181 function code in the low six bits
(active-high operand convention), so the assembler and the generator derive them from
one table rather than a hand-assigned map. The rule:
`opcode = 0x40 | (M << 5) | (CN << 4) | S[3:0]`.

| Mnemonic | '181 M,CN,S3–S0 | Operand | Cycles | Operation |
|---|---|---|---|---|
| ADD | M=0 CN=0 S=1001 | — | 4 | `( a b — a+b )`; N/Z/C |
| ADC | M=0 CN=1 S=1001 | — | 4 | `( a b — a+b+C )`; reads and writes C |
| SUB | M=0 CN=1 S=0110 | — | 4 | `( a b — b−a )`; C = no borrow |
| AND | M=1 S=1011 | — | 4 | `( a b — a&b )`; preserves C |
| OR | M=1 S=1110 | — | 4 | `( a b — a\|b )`; preserves C |
| XOR | M=1 S=0110 | — | 4 | `( a b — a^b )`; preserves C |
| NOT | M=1 S=0000 | — | 3 | `( a — ~a )`; preserves C |
| CMP | M=0 CN=1 S=0110 | — | 4 | flags of TOS−NOS, operands held; C = TOS ≥ NOS |
| TST | M=1 S=1111 | — | 3 | flags of TOS, TOS held; preserves C |
| ALU #n | any {M,CN,S3–S0} | — | 3 / 4 | the '181 function n directly; the assembler exposes the whole table |

CMP and SUB share a '181 pattern and differ only in whether TOS is written — a
member-level distinction the assembler encodes and the generator resolves in the
dictionary. The concrete hex opcodes and the exact CMP/SUB and TST slots are emitted
by BlinkyMGen from this table; the design document states the rule, not a hand-copied
map, so the two can never disagree.

**Frame (0x8_)** — no flags.

| Mnemonic | Opcode | Operand | Cycles | Operation |
|---|---|---|---|---|
| ENTER | 0x80 | kk | 4 | BP ← BP + kk |
| LOCAL@ | 0x81 | oo | 6 | `( — m )` push mem[0x00 : BP+oo] |
| LOCAL! | 0x82 | oo | 6 | `( d — )` pop to mem[0x00 : BP+oo] |

**Flow (0x9_)** — branch members 8–F: IR[2:1] = flag (01 Z, 10 C, 11 N), IR[0] =
polarity. Targets absolute, little-endian. Conditionals 3 cycles not taken, 5 taken.

| Mnemonic | Opcode | Operand | Cycles | Operation |
|---|---|---|---|---|
| JUMP | 0x90 | aaaa | 5 | PC ← aaaa |
| CALL | 0x91 | aaaa | 8 | Push {PC-next, BP} (RSP++ once); PC ← aaaa |
| BEQ | 0x9A | aaaa | 3 / 5 | Branch if Z = 1 |
| BNE | 0x9B | aaaa | 3 / 5 | Branch if Z = 0 |
| BCS | 0x9C | aaaa | 3 / 5 | Branch if C = 1 |
| BCC | 0x9D | aaaa | 3 / 5 | Branch if C = 0 |
| BMI | 0x9E | aaaa | 3 / 5 | Branch if N = 1 |
| BPL | 0x9F | aaaa | 3 / 5 | Branch if N = 0 |

Unused slots in the non-ALU families decode to a HALT-equivalent (the machine stops on
an illegal instruction — same philosophy as the 0xFF anchor). The ALU quadrant has no
illegal slots: every one of its 64 codes is a valid '181 function.

