# Blinky-M — A Microcoded Dual-Stack TTL CPU

**Proposal rev 04 — UNRATIFIED. Working title.** Nothing here is locked.

---

# 0. Thesis

An 8-bit dual-stack Forth machine built from 7400-series parts, designed for minimum
IC count. The instrument of that minimum is time-sharing: one memory, one 8-bit bus,
one ALU, one address path — visited in sequence by a microprogram, one transfer per
T-state. Instructions take as many T-states as they need; each microprogram simply
ends when it ends. Nothing in the machine exists twice.

Target: **~51 ICs core**, front panel and clock module extra.

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
  Frames and globals live in page 0 (§3).
- **Memory/I-O:** absolute LOAD a16 / STORE a16; stack-form FETCH / STORE!
  (zero-page, address from the stack); IN #port / OUT #port (8-bit port number,
  one form of each) and their stack forms. I/O is memory-mapped onto one page (§3),
  so LOAD/STORE a16 reach every port as well.
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
                    │ RAM 64K×8     │  48K enabled + 8K boot EEPROM
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

Bus discipline: exactly one source driver enabled per T-state — a '138 decodes a
3-bit microcode field, so mutual exclusion is structural, not conventional.
Destination load strobes come from a second decoder whose enable is gated by /CLK, so
nothing loads or writes until the bus has settled: every write in the machine is
confined to the second half-cycle by one gate.

**Same-edge counter rule.** Every counter (PC '161s, DSP/RSP '191s) counts on the
clock edge that *ends* a T-state — the same edge on which destination registers latch
and the phase-gated write strobes close. Throughout the state the counter's outputs
are the old value, so any address it drives is stable; the new value exists only in
the following state (counter propagation delay after the edge provides the address
hold margin for the closing /WE). Consequences:

- A count enable is free to share any T-state — it needs no bus. Post-increments ride
  the state that used the pointer (`RAM[PC] → TOS, PC++`).
- A pop's pre-decrement rides the *preceding* state: assert DSP−− during fetch and
  the decremented pointer is already the valid read address in the next state.
  Decrement-then-read costs zero T-states.
- DSP and RSP have independent microword fields (§6), so both may count on one edge
  (`>R` moves a byte between stacks in a single write state).

# 3. Memory map

48K SRAM + 8K EEPROM + one memory-mapped I/O page. The SRAM part is a **W24512
(64K×8)** for now, with the top 16K left unenabled — one chip, no width or depth
stacking, and the spare quarter is free expansion headroom.

| Range | Contents |
|---|---|
| 0x0000–0x00FF | **Frame/globals page** — BP-addressed (ADR-high = 0x00). Globals at the bottom; init lifts BP above them; ENTER k advances BP per frame. Ceiling: globals + sum of live frames ≤ 256 bytes |
| 0x0100–0x01FF | **I/O page** — 256 memory-mapped ports (ADR-high = 0x01). '688 decode carves it out of SRAM |
| 0x0200–0x7DFF | SRAM — code + data |
| 0x7E00–0x7EFF | Data stack page (grows up, DSP = low address byte) |
| 0x7F00–0x7FFF | Return stack page (grows up, RSP = low address byte) |
| 0x8000–0xBFFF | SRAM — code + data (continues) |
| 0xC000–0xDFFF | 8K boot EEPROM (28C64) — reset entry at 0xC000 |
| 0xE000–0xFFFF | spare (SRAM present but unenabled; future decode) |

Address decode: a '139 half on A15/A14 splits the space into quarters — Y0–Y2 enable
the SRAM (0x0000–0xBFFF = 48K), Y3 with A13 selects the EEPROM (0xC000–0xDFFF) and
leaves 0xE000–0xFFFF spare. A '688 comparing the address high byte against 0x01
overrides SRAM /CS and enables the I/O strobes.

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

16-bit operands and pushed addresses are **little-endian** throughout.

IN #port / OUT #port carry an 8-bit port number and microcode them as a plain access
with ADR-high forced to the I/O-page constant — one form of each, no 8/16-bit split.
The stack forms take the port from TOS the same way.

# 5. Datapath blocks

| Block | Parts | Notes |
|---|---|---|
| PC | '161 ×4 | 16-bit counter. Counts for every instruction-stream byte, opcode and operands alike. Parallel-loads by byte from the data bus for JUMP/CALL/RET/Bcc; async reset load = 0xC000 |
| PC → address bus | '541 ×2 | enabled on fetch/operand T-states |
| PC → data bus | '257 ×2 | byte select (lo/hi) for CALL's return-address push |
| ADR latch | '574 ×2 | data-address register for LOAD/STORE/FETCH/STORE!/IN/OUT; loads by byte from the bus, drives the address bus tri-state |
| DSP, RSP | '191 ×4 | 8-bit each, mod-256, async /LOAD-low with inputs tied low as the reset idiom; independent microword fields — both may count on one edge |
| SP → address low | '257 ×2 | selects DSP vs RSP, tri-state onto address low byte |
| Const driver | '541 ×1 | microcode-selected constants onto address high / data bus: 0x00 (frames), 0x01 (I/O page), data-stack page, return-stack page, vector bytes |
| ALU | '181 ×2 | one adder for everything: arithmetic, logic, and BP+offset address math |
| ALU A, B latches | '377 ×2 | loaded from the bus in earlier T-states; B doubles as the operand-assembly temp for JUMP/CALL |
| ALU → data bus | '541 ×1 | A and B are latched, so the ALU output is stable while driving |
| TOS | '194 ×2 + '153 + '541 | shifts act on TOS in place; the '153 on the '194 serial pins selects the fill (zero / sign / wrap) from raw opcode bits; '541 drives TOS onto the bus; LEDs off the '194 Qs |
| Flags | '377 ×1 + '157 ×1 + '541 ×1 + Z-detect glue ('30 + '04) | N/Z/C/I in one register; the '157 selects ALU-derived vs stack-restored (RTI) inputs; the '541 drives the flags byte onto the bus for the interrupt-entry push; NZ_WE / C_WE give per-flag masking |
| Interrupt latches | '74 ×2 | IRQ level sample; NMI synchroniser + true-edge pend (pend = NMI AND NOT sync — one-shot, no retrigger while held) |
| IR | '377 ×1 | opcode byte only |
| BP | '377 ×1 + '541 ×1 | 8-bit; frames/globals in page 0. LOCAL@ runs A ← BP, B ← offset, ALU → ADR-lo, const 0x00 → ADR-hi |
| Memory | W24512 SRAM ×1, 28C64 boot ×1, '139 ×1 | '139: A15/A14 quarter decode — SRAM enable ×3, EEPROM (with A13) |
| I/O | '688 page decode, '574 out, '541 in | '688: address high byte = 0x01 → I/O strobes, SRAM /CS override. The in-port buffer is enabled by page match + read — ports are address-decoded, not microcode-selected. Ports 0 and 1 are the on-board out latch and in buffer; the rest of the page is expansion |

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
A12..A5 = IR[7:0]     opcode byte, raw
A4..A2  = T[2:0]      T-state
A1      = COND        '151 output: flag selected by opcode bits, polarity likewise
A0      = INTP        interrupt pending (NMI_PEND OR (IRQ_PEND AND I=0))
```

**μROM data:** 4× 28C64 in parallel = 32 control lines. Field sketch:

| Field | Bits | Meaning |
|---|---|---|
| SRC | 3 | bus source → '138: RAM, ALU, TOS, PC-byte, const, BP, FLAGS, (spare) |
| DST | 4 | bus destination → '154, phase-gated by /CLK: IR, A, B, TOS, PClo, PChi, ADRlo, ADRhi, BP, FLAGS, RAM-WE, (spare) |
| ALU S,M,Cn | 6 | direct to the '181s |
| ASEL | 2 | address-bus source: PC / ADR / {page, SP} / none |
| PCINC, PCBYTE | 2 | PC count enable; lo/hi select for the PC-byte source |
| DSPCTL | 2 | hold / up / down |
| RSPCTL | 2 | hold / up / down — independent of DSPCTL; both stacks may move on one edge |
| TOSMODE | 2 | '194 S1S0: hold / load / shift |
| NZ_WE, C_WE | 2 | per-flag write masking |
| ISET, ICLR | 2 | I flag control (SEI/CLI/entry/RTI paths) |
| CSEL | 3 | const-driver pattern: 0x00 / 0x01 (I/O) / D-page / R-page / vector-lo / vector-hi |
| TRST | 1 | end of instruction |
| spare | ~1 | |

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
everything after it is the bus transfer; trailing `++`/`−−` are counts taken on the
state's closing edge (§2 same-edge rule). T0 is always the fetch.

```
NOP — 1 T
  T0  ADDR=PC   RAM → IR              PC++            TRST

PUSH #imm — 3 T
  T0  ADDR=PC   RAM → IR              PC++
  T1  ADDR=D:DSP  TOS → RAM           DSP++
  T2  ADDR=PC   RAM → TOS             PC++            TRST

DUP — 2 T
  T0  ADDR=PC   RAM → IR              PC++
  T1  ADDR=D:DSP  TOS → RAM           DSP++           TRST

DROP — 2 T
  T0  ADDR=PC   RAM → IR              PC++  DSP−−
  T1  ADDR=D:DSP  RAM → TOS                           TRST

SWAP — 4 T
  T0  ADDR=PC   RAM → IR              PC++  DSP−−
  T1  ADDR=D:DSP  RAM → A
  T2  ADDR=D:DSP  TOS → RAM           DSP++
  T3            ALU(F=A) → TOS                        TRST

ROT — 6 T   ( a b c — b c a )
  T0  ADDR=PC   RAM → IR              PC++  DSP−−
  T1  ADDR=D:DSP  RAM → A   (b)             DSP−−
  T2  ADDR=D:DSP  RAM → B   (a)
  T3  ADDR=D:DSP  ALU(F=A) → RAM  (b)       DSP++
  T4  ADDR=D:DSP  TOS → RAM       (c)       DSP++
  T5            ALU(F=B) → TOS   (a)                  TRST

ADD (all binary ALU) — 4 T
  T0  ADDR=PC   RAM → IR              PC++
  T1            TOS → A                     DSP−−
  T2  ADDR=D:DSP  RAM → B
  T3            ALU → TOS, flags                      TRST

NOT / TST (unary) — 3 T
  T0  ADDR=PC   RAM → IR              PC++
  T1            TOS → A
  T2            ALU → TOS, flags   (TST: flags only)  TRST

CMP — 4 T   (non-destructive)
  T0  ADDR=PC   RAM → IR              PC++  DSP−−
  T1  ADDR=D:DSP  RAM → B                   DSP++
  T2            TOS → A
  T3            flags only                            TRST

SHL/SHR/ASR/ROL/ROR — 2 T
  T0  ADDR=PC   RAM → IR              PC++
  T1            TOS shifts in place, C ← bit out      TRST

>R — 3 T
  T0  ADDR=PC   RAM → IR              PC++
  T1  ADDR=R:RSP  TOS → RAM           RSP++  DSP−−
  T2  ADDR=D:DSP  RAM → TOS                           TRST

R> — 3 T
  T0  ADDR=PC   RAM → IR              PC++  RSP−−
  T1  ADDR=D:DSP  TOS → RAM           DSP++
  T2  ADDR=R:RSP  RAM → TOS                           TRST

R@ — 3 T
  T0  ADDR=PC   RAM → IR              PC++  RSP−−
  T1  ADDR=D:DSP  TOS → RAM           DSP++
  T2  ADDR=R:RSP  RAM → TOS           RSP++           TRST

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
  T3  ADDR=R:RSP  PClo → RAM          RSP++
  T4  ADDR=R:RSP  PChi → RAM          RSP++
  T5  ADDR=R:RSP  BP → RAM            RSP++
  T6            ALU(F=B) → PClo
  T7            ALU(F=A) → PChi                       TRST

RET — 4 T
  T0  ADDR=PC   RAM → IR              PC++  RSP−−
  T1  ADDR=R:RSP  RAM → BP                  RSP−−
  T2  ADDR=R:RSP  RAM → PChi                RSP−−
  T3  ADDR=R:RSP  RAM → PClo                          TRST

LOAD a16 — 5 T
  T0  ADDR=PC   RAM → IR              PC++
  T1  ADDR=PC   RAM → ADRlo           PC++
  T2  ADDR=PC   RAM → ADRhi           PC++
  T3  ADDR=D:DSP  TOS → RAM           DSP++   (spill)
  T4  ADDR=ADR  RAM → TOS                             TRST

STORE a16 — 5 T
  T0  ADDR=PC   RAM → IR              PC++
  T1  ADDR=PC   RAM → ADRlo           PC++
  T2  ADDR=PC   RAM → ADRhi           PC++
  T3  ADDR=ADR  TOS → RAM             DSP−−
  T4  ADDR=D:DSP  RAM → TOS                           TRST

FETCH — 4 T   (stack form, zero-page: ( a — m ))
  T0  ADDR=PC   RAM → IR              PC++
  T1            TOS → ADRlo
  T2            const 0x00 → ADRhi
  T3  ADDR=ADR  RAM → TOS                             TRST

STORE! — 6 T   (stack form, zero-page: ( data addr — ))
  T0  ADDR=PC   RAM → IR              PC++
  T1            TOS → ADRlo           DSP−−
  T2            const 0x00 → ADRhi
  T3  ADDR=D:DSP  RAM → B   (data)          DSP−−
  T4  ADDR=ADR  ALU(F=B) → RAM
  T5  ADDR=D:DSP  RAM → TOS                           TRST

IN #port — 5 T
  T0  ADDR=PC   RAM → IR              PC++
  T1  ADDR=PC   RAM → ADRlo           PC++
  T2            const 0x01 → ADRhi
  T3  ADDR=D:DSP  TOS → RAM           DSP++   (spill)
  T4  ADDR=ADR  RAM → TOS   (port read)               TRST

OUT #port — 5 T
  T0  ADDR=PC   RAM → IR              PC++
  T1  ADDR=PC   RAM → ADRlo           PC++
  T2            const 0x01 → ADRhi
  T3  ADDR=ADR  TOS → RAM   (port write)  DSP−−
  T4  ADDR=D:DSP  RAM → TOS                           TRST

ENTER k — 4 T
  T0  ADDR=PC   RAM → IR              PC++
  T1  ADDR=PC   RAM → B   (k)         PC++
  T2            BP → A
  T3            ALU(A+B) → BP                         TRST

LOCAL@ off — 7 T
  T0  ADDR=PC   RAM → IR              PC++
  T1  ADDR=PC   RAM → B   (off)       PC++
  T2            BP → A
  T3            ALU(A+B) → ADRlo
  T4            const 0x00 → ADRhi
  T5  ADDR=D:DSP  TOS → RAM           DSP++   (spill)
  T6  ADDR=ADR  RAM → TOS                             TRST

LOCAL! off — 7 T
  T0  ADDR=PC   RAM → IR              PC++
  T1  ADDR=PC   RAM → B   (off)       PC++
  T2            BP → A
  T3            ALU(A+B) → ADRlo
  T4            const 0x00 → ADRhi
  T5  ADDR=ADR  TOS → RAM             DSP−−
  T6  ADDR=D:DSP  RAM → TOS                           TRST

hardware entry — 6 T   (INTP row; BRK adds its fetch = 7 T)
  T0  ADDR=R:RSP  PClo → RAM          RSP++
  T1  ADDR=R:RSP  PChi → RAM          RSP++
  T2  ADDR=R:RSP  FLAGS → RAM         RSP++
  T3  ADDR=R:RSP  BP → RAM            RSP++
  T4            const vec-lo → PClo   ISET
  T5            const vec-hi → PChi                   TRST

RTI — 5 T
  T0  ADDR=PC   RAM → IR              PC++  RSP−−
  T1  ADDR=R:RSP  RAM → BP                  RSP−−
  T2  ADDR=R:RSP  RAM → FLAGS ('157 restore)  RSP−−
  T3  ADDR=R:RSP  RAM → PChi                RSP−−
  T4  ADDR=R:RSP  RAM → PClo                          TRST
```

Average for stack-shaped code ≈ **3–4 T-states per instruction**.

**Throughput:** the critical path is μROM access + strobe decode + register setup.
With 28C64-150 parts the T-clock ceiling sits near 4 MHz (~1.0–1.3 MIPS);
AT28C64B-45 parts open 6–8 MHz (~2 MIPS). Datapath elements never limit — every
transfer is a single register-to-register hop across one bus.

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
- Flags and I travel as one pushed byte via the flags bus driver; RTI pops it back
  through the '157 restore mux. RET pops BP and PC only, leaving flags alone.
- Round trip = **11 T-states** (6 entry + 5 RTI).

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
| Flags: '377, '157 restore, '541 bus driver, Z-detect glue ('30 + '04) | 5 |
| Interrupt latches: '74 ×2 | 2 |
| IR: '377 | 1 |
| BP: '377 + '541 | 2 |
| Memory: W24512 SRAM, 28C64 boot, '139 map decode | 3 |
| I/O: '688 page decode, '574 out, '541 in | 3 |
| **Core total** | **51** |

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

# 11. Open questions for ratification

1. Memory map (§3): frame/globals page 0x0000, I/O page 0x0100, stack pages
   0x7E00/0x7F00, EEPROM at 0xC000; PC reset = 0xC000 via strapped '161 parallel
   inputs; W24512 as the SRAM part with the top 16K unenabled.
2. BP width: 8-bit with frames/globals confined to page 0 (as specced) vs 16-bit
   (+1 '377, +1 '541, +2 T per frame op).
3. Vector scheme (§8): microcode-constant entry into an EEPROM JUMP table vs
   RAM-resident vector cells read as data (+2 T on entry, fully dynamic).
4. Stack-form op set: FETCH/STORE!/IN/OUT stack forms and ROT are in; anything else
   (PICK? two-byte 16-bit fetch?) waits for a workload.
5. μROM speed grade: 28C64-150 (≈4 MHz T-clock) vs AT28C64B-45 (6–8 MHz). A '377 ×4
   pipeline register on the μROM outputs (+4 chips) is the fallback, not the plan.
6. Opcode byte assignments (free choice; family-style grouping as mnemonic
   convention only).
7. T-state and phase LEDs on the panel (proposal: yes).
