# Blinky-M — A Microcoded Dual-Stack TTL CPU

**Rev 12 — core decisions ratified; remaining items in §11. Working title.**

---

# 0. Thesis

An 8-bit dual-stack Forth machine built from 7400-series parts, designed for minimum
IC count. The instrument of that minimum is time-sharing: one memory, one 8-bit bus,
one ALU, one address path — visited in sequence by a microprogram, one transfer per
T-state. Instructions take as many T-states as they need; each microprogram simply
ends when it ends. Nothing in the machine exists twice.

**~56 ICs core**, front panel and clock module extra.

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
- **Frames:** BP base pointer (8-bit, ratified); ENTER k opens a frame, LOCAL@ off /
  LOCAL! off access BP-relative locals, RET restores BP automatically (no explicit
  frame-free op). Frames and globals live in page 0 (§3).
- **Memory/I-O:** absolute LOAD a16 / STORE a16; stack-form FETCH / STORE!
  (zero-page, address from the stack); IN #port / OUT #port (8-bit port number,
  one form of each) and their stack forms. I/O is memory-mapped onto one page (§3),
  so LOAD/STORE a16 reach every port as well. The op set is frozen at this list
  (ratified); additions are μROM re-burns, not respins.
- **Control flow:** JUMP a16, CALL a16, RET; conditionals BEQ/BNE/BCS/BCC/BMI/BPL,
  all absolute with full 16-bit reach — there is no short/far distinction.
- **Interrupts:** NMI (edge, unmaskable), IRQ (level, masked by I), software BRK,
  RTI restoring PC, BP, flags, and I. Entry pushes state automatically; a handler can
  use ENTER/locals with zero extra convention.

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
exception: counter enables, TOS shifts, flag writes, and TRST touch no bus, so any
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

The opcode byte is a raw microcode ROM address. Opcodes are assigned for human
readability: the high nibble is the family, the low nibble the member — a mnemonic
convention only, costing nothing in hardware (ratified). Two anchors are fixed
(ratified): **NOP = 0x00** and **HALT = 0xFF** — both bus-idle patterns, so a fetch
from unprogrammed or floating memory reads HALT and the machine stops instead of
running wild. The full map is **Appendix A**.

Two member placements are load-bearing wires, not aesthetics:

- **ALU family (0x1m):** the member nibble is the sub-op select, and its bits [3:2]
  are wired raw to the '153 shifter-fill mux — the nibble placement of
  SHL/SHR/ASR/ROL/ROR *is* the fill-select encoding.
- **Shift direction is IR[0]:** SHR/ASR/ROR sit at odd members, SHL/ROL at even —
  one raw bit steers both the '194 direction and the shifted-out-bit tap, zero
  decode.
- **Branch family (0x5m, members 8–F):** IR[2:1] select the flag (01 = Z, 10 = C,
  11 = N) and IR[0] the polarity (0 = branch if set, 1 = branch if clear), wired raw
  to the COND '151 — six conditionals, zero decode.

16-bit operands and pushed addresses are **little-endian** throughout.

IN #port / OUT #port carry an 8-bit port number and microcode them as a paged access
(page 0x01, port number in the address low byte) — one form of each, no 8/16-bit
split. The stack forms take the port from TOS the same way.

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
| ALU | '181 ×2 | one adder for everything: arithmetic, logic, and BP+offset address math |
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

# 6. Control — microcode ROM sequencer

All control is a lookup table.

**Sequencer:** one '161 T-counter (3 bits used, T0–T7). Every microword carries a
**TRST** bit; asserting it makes the next edge fetch T0 of the next instruction.
Variable T-states per instruction is literally this one bit.

**μROM address (13 bits — exactly one 28C64):**

```
A12..A5 = opcode      IR[7:0] when T ≠ 0; live data bus when T = 0 ('257 bypass, §2)
A4..A2  = T[2:0]      T-state
A1      = COND        '151 output: flag selected by IR[2:1], polarity by IR[0]
A0      = INTP        registered interrupt pending — sampled on the TRST edge
                      entering T0, from NMI_PEND OR (IRQ_PEND AND I=0)
```

COND is consumed only at T ≥ 1 (branch decisions), by which point IR is latched — the
bypass never races the condition mux.

**μROM data:** 4× 28C64 in parallel = 32 control lines, **all 32 assigned — zero
spare**. Any future field means a fifth ROM (trivial: same address wiring, one more
socket).

| Field | Bits | Meaning |
|---|---|---|
| SRC | 3 | bus source → '138: RAM, ALU, TOS, PC-byte, BP, FLAGS, VEC, (spare) |
| DST | 4 | bus destination → '154, phase-gated by /CLK: IR, A, B, TOS, PClo, PChi, ADRlo, ADRhi, BP, FLAGS, RAM-WE, HALT |
| ALU S,M,Cn | 6 | direct to the '181s; carry-in = **CN & (S0 ? C : 1)** — one AND + a '157 #2 spare section. SUB/CMP (S=0110): CN injects 1; ADC (S=1001): CN injects the C flag; ADD: CN = 0 |
| ASEL | 2 | address-bus source: PC / ADR / {page, SP} / {page, ADRlo} |
| PCINC, PCBYTE | 2 | PC count enable; lo/hi select for the PC-byte source |
| DSPCTL | 2 | hold / up / down |
| RSPCTL | 2 | hold / up / down — independent of DSPCTL; both stacks may move on one edge |
| SPSEL | 1 | DSP vs RSP onto the address low byte |
| TOSMODE | 2 | 00 hold / 01 load / 10 shift; bit 1 doubles as C_SRC ('181 carry vs '194 shift-out); shift direction = IR[0] |
| NZ_WE, C_WE | 2 | per-flag write masking |
| ISET, ICLR | 2 | I flag control (SEI/CLI/entry/RTI paths) |
| PGSEL | 3 | page: 0x00 frames / 0x01 I/O / 0x02 D-stack / 0x03 PC-lo / 0x04 PC-hi / 0x05 BP / 0x06 flags / (7 spare) |
| TRST | 1 | end of instruction |

Out-port writes need no DST slot: a write into the I/O page (RAM-WE with the '688
matched) strobes the port latch instead of the SRAM. Likewise in-port reads are just
reads at an I/O-page address — ports are address-decoded, invisible to the microword.

**Glitch discipline:** EEPROM outputs wander while the T address changes. Every
edge-triggered load is inherently safe (settled long before the edge); every
level-sensitive strobe (RAM /WE, OUT) comes from the DST '154, whose enable is gated
by /CLK — asserted only in the settled second half-cycle. One gate covers every strobe
in the machine.

**Toolchain:** the microcode source is a table in a small **C# generator** — one row
per (opcode, T, COND, INTP) — emitting the four images as **Intel HEX (.hex)** plus a
human-readable listing. TTLSim loads 28C-series parts, so the verification chain is:
generate → TTLSim sweep → burn. A microcode bug is a table edit and a re-burn.

# 7. T-state budgets (illustrative — final counts fall out of the microcode table)

One line per T-state. Notation: `ADDR=x` names the address-bus source for the state;
`{pg,·}` is a paged access (page driver on the high byte); everything after it is the
bus transfer; trailing `++`/`−−` are counts taken on the state's closing edge (§2
same-edge rule). T0 is always the fetch, made instruction-specific by the opcode
bypass (§2).

```
NOP — 1 T
  T0  ADDR=PC   RAM → IR              PC++            TRST

PUSH #imm — 3 T
  T0  ADDR=PC   RAM → IR              PC++
  T1  ADDR={02,DSP}  TOS → RAM        DSP++
  T2  ADDR=PC   RAM → TOS             PC++            TRST

DUP — 2 T
  T0  ADDR=PC   RAM → IR              PC++
  T1  ADDR={02,DSP}  TOS → RAM        DSP++           TRST

DROP — 2 T
  T0  ADDR=PC   RAM → IR              PC++  DSP−−
  T1  ADDR={02,DSP}  RAM → TOS                        TRST

SWAP — 4 T
  T0  ADDR=PC   RAM → IR              PC++  DSP−−
  T1  ADDR={02,DSP}  RAM → A
  T2  ADDR={02,DSP}  TOS → RAM        DSP++
  T3            ALU(F=A) → TOS                        TRST

ROT — 6 T   ( a b c — b c a )
  T0  ADDR=PC   RAM → IR              PC++  DSP−−
  T1  ADDR={02,DSP}  RAM → A   (b)          DSP−−
  T2  ADDR={02,DSP}  RAM → B   (a)
  T3  ADDR={02,DSP}  ALU(F=A) → RAM  (b)    DSP++
  T4  ADDR={02,DSP}  TOS → RAM       (c)    DSP++
  T5            ALU(F=B) → TOS   (a)                  TRST

ADD (all binary ALU) — 4 T
  T0  ADDR=PC   RAM → IR              PC++
  T1            TOS → A                     DSP−−
  T2  ADDR={02,DSP}  RAM → B
  T3            ALU → TOS, flags                      TRST

NOT / TST (unary) — 3 T
  T0  ADDR=PC   RAM → IR              PC++
  T1            TOS → A
  T2            ALU → TOS, flags   (TST: flags only)  TRST

CMP — 4 T   (non-destructive)
  T0  ADDR=PC   RAM → IR              PC++  DSP−−
  T1  ADDR={02,DSP}  RAM → B                DSP++
  T2            TOS → A
  T3            flags only                            TRST

SHL/SHR/ASR/ROL/ROR — 2 T
  T0  ADDR=PC   RAM → IR              PC++
  T1            TOS shifts in place, C ← bit out      TRST

>R — 3 T
  T0  ADDR=PC   RAM → IR              PC++
  T1  ADDR={03,RSP}  TOS → RAM        RSP++  DSP−−
  T2  ADDR={02,DSP}  RAM → TOS                        TRST

R> — 3 T
  T0  ADDR=PC   RAM → IR              PC++  RSP−−
  T1  ADDR={02,DSP}  TOS → RAM        DSP++
  T2  ADDR={03,RSP}  RAM → TOS                        TRST

R@ — 3 T
  T0  ADDR=PC   RAM → IR              PC++  RSP−−
  T1  ADDR={02,DSP}  TOS → RAM        DSP++
  T2  ADDR={03,RSP}  RAM → TOS        RSP++           TRST

JUMP a16 — 5 T
  T0  ADDR=PC   RAM → IR              PC++
  T1  ADDR=PC   RAM → B   (lo)        PC++
  T2  ADDR=PC   RAM → A   (hi)        PC++
  T3            ALU(F=B) → PClo
  T4            ALU(F=A) → PChi                       TRST

Bcc a16 — 3 T not taken / 5 T taken
  T0  ADDR=PC   RAM → IR              PC++
  taken (COND=1): as JUMP T1–T4
  not taken:
  T1                                  PC++   (skip lo)
  T2                                  PC++   (skip hi)  TRST

CALL a16 — 8 T
  T0  ADDR=PC   RAM → IR              PC++
  T1  ADDR=PC   RAM → B   (lo)        PC++
  T2  ADDR=PC   RAM → A   (hi)        PC++
  T3  ADDR={03,RSP}  PClo → RAM
  T4  ADDR={04,RSP}  PChi → RAM
  T5  ADDR={05,RSP}  BP → RAM         RSP++   (one count per frame)
  T6            ALU(F=B) → PClo
  T7            ALU(F=A) → PChi                       TRST

RET — 4 T
  T0  ADDR=PC   RAM → IR              PC++  RSP−−
  T1  ADDR={05,RSP}  RAM → BP
  T2  ADDR={04,RSP}  RAM → PChi
  T3  ADDR={03,RSP}  RAM → PClo                       TRST

LOAD a16 — 5 T
  T0  ADDR=PC   RAM → IR              PC++
  T1  ADDR=PC   RAM → ADRlo           PC++
  T2  ADDR=PC   RAM → ADRhi           PC++
  T3  ADDR={02,DSP}  TOS → RAM        DSP++   (spill)
  T4  ADDR=ADR  RAM → TOS                             TRST

STORE a16 — 5 T
  T0  ADDR=PC   RAM → IR              PC++
  T1  ADDR=PC   RAM → ADRlo           PC++
  T2  ADDR=PC   RAM → ADRhi           PC++
  T3  ADDR=ADR  TOS → RAM             DSP−−
  T4  ADDR={02,DSP}  RAM → TOS                        TRST

FETCH — 3 T   (stack form, zero-page: ( a — m ))
  T0  ADDR=PC   RAM → IR              PC++
  T1            TOS → ADRlo
  T2  ADDR={00,ADRlo}  RAM → TOS                      TRST

STORE! — 5 T   (stack form, zero-page: ( data addr — ))
  T0  ADDR=PC   RAM → IR              PC++  DSP−−
  T1            TOS → ADRlo
  T2  ADDR={02,DSP}  RAM → B   (data)       DSP−−
  T3  ADDR={00,ADRlo}  ALU(F=B) → RAM
  T4  ADDR={02,DSP}  RAM → TOS                        TRST

IN #port — 4 T
  T0  ADDR=PC   RAM → IR              PC++
  T1  ADDR=PC   RAM → ADRlo           PC++
  T2  ADDR={02,DSP}  TOS → RAM        DSP++   (spill)
  T3  ADDR={01,ADRlo}  RAM → TOS   (port read)        TRST

OUT #port — 4 T
  T0  ADDR=PC   RAM → IR              PC++
  T1  ADDR=PC   RAM → ADRlo           PC++
  T2  ADDR={01,ADRlo}  TOS → RAM   (port write)  DSP−−
  T3  ADDR={02,DSP}  RAM → TOS                        TRST

ENTER k — 4 T
  T0  ADDR=PC   RAM → IR              PC++
  T1  ADDR=PC   RAM → B   (k)         PC++
  T2            BP → A
  T3            ALU(A+B) → BP                         TRST

LOCAL@ off — 6 T
  T0  ADDR=PC   RAM → IR              PC++
  T1  ADDR=PC   RAM → B   (off)       PC++
  T2            BP → A
  T3  ADDR={02,DSP}  TOS → RAM        DSP++   (spill; ALU settles meanwhile)
  T4            ALU(A+B) → ADRlo
  T5  ADDR={00,ADRlo}  RAM → TOS                      TRST

LOCAL! off — 6 T
  T0  ADDR=PC   RAM → IR              PC++
  T1  ADDR=PC   RAM → B   (off)       PC++
  T2            BP → A
  T3            ALU(A+B) → ADRlo
  T4  ADDR={00,ADRlo}  TOS → RAM      DSP−−
  T5  ADDR={02,DSP}  RAM → TOS                        TRST

hardware entry — 6 T   (INTP row; BRK adds its fetch = 7 T)
  T0  ADDR={03,RSP}  PClo → RAM
  T1  ADDR={04,RSP}  PChi → RAM
  T2  ADDR={06,RSP}  FLAGS → RAM
  T3  ADDR={05,RSP}  BP → RAM         RSP++   (one count per frame)
  T4            VEC → PClo            ISET
  T5            VEC → PChi                            TRST

RTI — 5 T
  T0  ADDR=PC   RAM → IR              PC++  RSP−−
  T1  ADDR={05,RSP}  RAM → BP
  T2  ADDR={06,RSP}  RAM → FLAGS ('157 restore)
  T3  ADDR={04,RSP}  RAM → PChi
  T4  ADDR={03,RSP}  RAM → PClo                       TRST
```

Stack-form IN/OUT are the FETCH/STORE! sequences with page 0x01.

Average for stack-shaped code ≈ **3–4 T-states per instruction**.

**Throughput:** the critical path is T0 — PC drivers + SRAM access + bypass '257 +
μROM + strobe decode + setup. Build with on-hand 28C64-150 parts and run the T-clock
where that path closes (~2–2.5 MHz, ~0.6–0.8 MIPS); AT28C64B-45 parts lift the
ceiling to 4–6 MHz (~1.0–1.7 MIPS) as a drop-in. The long-run plan is a hardware
shadow-copy of the μROMs and boot EEPROM into fast SRAM at startup (mechanical, not
software) — future work, §11. Datapath elements never limit — every transfer is a
single register-to-register hop across one bus.

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
| Control: 28C64 μROM ×4, '161 T-counter, '257 ×2 opcode bypass, '151 COND, '138 SRC, '154 DST, '00 glue | 11 |
| PC: '161 ×4, '541 ×2 (addr), '257 ×2 (byte → data bus) | 8 |
| ADR latch: '574 ×2 | 2 |
| DSP/RSP: '191 ×4, '257 ×2 (SP → addr) | 6 |
| Page + vector drivers: '541 ×2 | 2 |
| ALU: '181 ×2, A/B '377 ×2, '541 bus driver | 5 |
| TOS: '194 ×2, '153 fill, '541 driver | 4 |
| Flags: '377, '157 ×3 (restore + C-source + hold-feedback), '541 bus driver, Z-detect glue ('30 + '04) | 7 |
| Interrupt latches: '74 ×2 | 2 |
| IR: '377 | 1 |
| BP: '377 + '541 | 2 |
| Memory: W24512 SRAM, 28C64 boot, '10 select glue | 3 |
| I/O: '688 page decode, '574 out, '541 in | 3 |
| **Core total** | **56** |

Two programmable part types in the whole machine (28C64, and nothing else — the μROM
is data, not logic). No adders exist outside the '181 pair. Clock, reset, and
single-step live on a separate module; a hardware breakpoint comparator on PC gates
its halt on T=0 so it stops on instruction boundaries.

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
TRST) is a two-gate addition on the step path.

# 11. Decisions and remaining items

**Ratified:** opcode bypass ('257 ×2, instruction-specific T0); memory map per §3 —
low contiguous pages 0x00–0x06, **full 56K SRAM from day one**, EEPROM at 0xE000;
lane-structured return stack (RSP counts frames); vectors 0xFEFE / 0xFDFD via the
repeated-byte driver; BP = 8-bit; op set frozen; opcode family-nibble convention with
NOP = 0x00 and HALT = 0xFF; T-state and INTP panel LEDs; μROM = on-hand 28C64-150 at
reduced T-clock for now; spare DST code = **HALT strobe** to the clock module's
halt flip-flop; **shift C-source** via the second flags '157 (direction from
IR[0], C_SRC from TOSMODE = shift — zero μword lines); **shifts write C only,
N/Z held** (divergence documented in Appendix A); **registered INTP** on the
spare interrupt-'74 flop; **B = /INTP** into the flags driver; **ADC carry-in =
CN & (S0 ? C : 1)**; **flags hold-feedback '157** — the '377's common clock enable
means per-flag masking is D = WE ? new : Q, made real in hardware; Appendix cycle
counts regenerated from the BlinkyMGen listing.

**Remaining / future:**

1. **Hardware shadow-copy loader** — mechanically copy the μROMs and boot EEPROM
   into fast SRAM at startup, then run at full T-clock. Deliberately deferred;
   nothing in the current design forecloses it.
2. μword headroom: all 32 lines assigned. The escape valve is a fifth 28C64 on the
   same address lines — note for the PCB: leave a footprint.
3. **PC-hi reset load mechanism.** §3/§5 say the high '161 pair "async-loads 0xE0
   with parallel inputs strapped" — but the '161 load is synchronous, and the same
   parallel inputs must carry the data bus for JUMP/CALL/RET byte loads, so they
   cannot be strapped. Candidate fix: 10k pull resistors patterning **0xE0 onto the
   data bus**, the SRC '138 disabled during reset (G1 = /RST) so the bus floats to
   the pattern, and both PC /LOADs asserted through the reset OR — the first clock
   edge under reset loads 0xE000. Unratified; the stage-0 bring-up sidesteps it by
   resetting PC to 0x0000 with the boot program assembled there.

---

# Appendix A — Instruction Set Reference

Opcode = the instruction's first byte and its μROM row. Families by high nibble:
0x0_ control, 0x1_ ALU, 0x2_ stack, 0x3_ memory/I-O, 0x4_ frame, 0x5_ flow;
HALT = 0xFF (bus-idle anchor). Cycles are T-states including the fetch.

Operand notation (also fixes instruction length: `—` = 1 byte, `ii pp kk oo` = 2,
`aaaa` = 3): **ii** immediate byte · **pp** port number · **kk** unsigned count ·
**oo** signed offset (arithmetic wraps within page 0) · **aaaa** 16-bit absolute
address, little-endian. Stack effects in Forth notation; R: is the return stack.

**Control (0x0_)**

| Mnemonic | Opcode | Operand | Cycles | Operation |
|---|---|---|---|---|
| NOP | 0x00 | — | 1 | No operation; PC advances |
| RET | 0x01 | — | 4 | Pop {PC, BP} frame; flags untouched |
| BRK | 0x02 | — | 7 | Software trap: push {PC+1, flags·B=1, BP} frame; I ← 1; PC ← 0xFEFE |
| RTI | 0x03 | — | 5 | Pop interrupt frame; PC, BP, N/Z/C, I all restored |
| CLI | 0x04 | — | 2 | I ← 0 — IRQs enabled |
| SEI | 0x05 | — | 2 | I ← 1 — IRQs masked |
| HALT | 0xFF | — | 2 | Stop the clock; NMI wakes |

**ALU (0x1_)** — member nibble is the sub-op; bits [3:2] wire the shifter fill mux.
N/Z written by every op except the five shifts (which write C only); otherwise C per the notes ("preserves C" = logic class).

| Mnemonic | Opcode | Operand | Cycles | Operation |
|---|---|---|---|---|
| ADD | 0x10 | — | 4 | `( a b — a+b )` pop; N/Z/C |
| ADC | 0x11 | — | 4 | `( a b — a+b+C )` pop; reads and writes C |
| SUB | 0x12 | — | 4 | `( a b — b−a )` pop; C = no borrow (6502 sense) |
| XOR | 0x13 | — | 4 | `( a b — a^b )` pop; preserves C |
| NOT | 0x14 | — | 3 | `( a — ~a )`; preserves C |
| TST | 0x15 | — | 3 | Flags of TOS; TOS held; preserves C |
| SHL | 0x16 | — | 2 | TOS ≪ 1, zero fill; C ← old bit 7; N/Z unchanged |
| SHR | 0x17 | — | 2 | TOS ≫ 1, zero fill; C ← old bit 0; N/Z unchanged |
| AND | 0x18 | — | 4 | `( a b — a&b )` pop; preserves C |
| CMP | 0x19 | — | 4 | `( a b — a b )` flags of TOS−NOS; C = TOS ≥ NOS; operands held |
| — | 0x1A | | | reserved |
| ASR | 0x1B | — | 2 | TOS ≫ 1, sign fill; C ← old bit 0; N/Z unchanged |
| OR | 0x1C | — | 4 | `( a b — a\|b )` pop; preserves C |
| — | 0x1D | | | reserved |
| ROL | 0x1E | — | 2 | TOS plain rotate left (wrap); C ← bit rotated out; N/Z unchanged |
| ROR | 0x1F | — | 2 | TOS plain rotate right (wrap); C ← bit rotated out; N/Z unchanged |

**Stack (0x2_)** — no flags.

| Mnemonic | Opcode | Operand | Cycles | Operation |
|---|---|---|---|---|
| DUP | 0x20 | — | 2 | `( a — a a )` |
| OVER | 0x21 | — | 4 | `( a b — a b a )` |
| TUCK | 0x22 | — | 4 | `( a b — b a b )` |
| SWAP | 0x23 | — | 4 | `( a b — b a )` |
| DROP | 0x24 | — | 2 | `( a — )` |
| NIP | 0x25 | — | 1 | `( a b — b )` — pure pointer move, touches no memory |
| ROT | 0x26 | — | 6 | `( a b c — b c a )` |
| >R | 0x28 | — | 3 | `( a — )` R:`( — a )` — data to the return stack's PC-lo lane |
| R> | 0x29 | — | 3 | `( — a )` R:`( a — )` — return stack to data stack |
| R@ | 0x2A | — | 3 | `( — a )` R:`( a — a )` — copy return TOS; no net RSP change |

**Memory / I-O (0x3_)** — no flags. Ports are page 0x01; LOAD/STORE aaaa reach
them too.

| Mnemonic | Opcode | Operand | Cycles | Operation |
|---|---|---|---|---|
| PUSH # | 0x30 | ii | 3 | `( — ii )` push immediate |
| LOAD | 0x31 | aaaa | 5 | `( — m )` push mem[aaaa] |
| STORE | 0x32 | aaaa | 5 | `( d — )` pop to mem[aaaa] |
| FETCH | 0x33 | — | 3 | `( a — m )` read page 0 at TOS; value replaces address |
| STORE! | 0x34 | — | 5 | `( d a — )` pop both; page 0 at a ← d |
| IN # | 0x35 | pp | 4 | `( — v )` read port pp |
| OUT # | 0x36 | pp | 4 | `( d — )` write TOS to port pp; pops |
| IN | 0x37 | — | 3 | `( p — v )` port from TOS; value replaces it |
| OUT | 0x38 | — | 5 | `( d p — )` port from TOS; pops both |

**Frame (0x4_)** — no flags.

| Mnemonic | Opcode | Operand | Cycles | Operation |
|---|---|---|---|---|
| ENTER | 0x40 | kk | 4 | BP ← BP + kk — open a kk-byte frame in page 0 |
| LOCAL@ | 0x41 | oo | 6 | `( — m )` push mem[0x00 : BP+oo] |
| LOCAL! | 0x42 | oo | 6 | `( d — )` pop to mem[0x00 : BP+oo] |

**Flow (0x5_)** — branch members 8–F: IR[2:1] = flag (01 Z, 10 C, 11 N),
IR[0] = polarity, wired raw to the COND '151. All targets absolute; every
conditional has full 16-bit reach. Conditionals: 3 cycles not taken, 5 taken.

| Mnemonic | Opcode | Operand | Cycles | Operation |
|---|---|---|---|---|
| JUMP | 0x50 | aaaa | 5 | PC ← aaaa |
| CALL | 0x51 | aaaa | 8 | Push {PC-next, BP} frame (RSP++ once); PC ← aaaa |
| BEQ | 0x5A | aaaa | 3 / 5 | Branch if Z = 1 (equal / zero) |
| BNE | 0x5B | aaaa | 3 / 5 | Branch if Z = 0 (not equal / non-zero) |
| BCS | 0x5C | aaaa | 3 / 5 | Branch if C = 1 (carry set / no borrow / TOS ≥ NOS after CMP) |
| BCC | 0x5D | aaaa | 3 / 5 | Branch if C = 0 (carry clear / borrow) |
| BMI | 0x5E | aaaa | 3 / 5 | Branch if N = 1 (negative — bit 7 set) |
| BPL | 0x5F | aaaa | 3 / 5 | Branch if N = 0 (positive or zero) |

All unassigned opcodes decode to a HALT-equivalent μROM row (machine stops on an
illegal instruction — same philosophy as the 0xFF anchor).
