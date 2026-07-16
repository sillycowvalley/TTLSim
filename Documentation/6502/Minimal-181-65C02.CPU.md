# Minimal 74LS181 CPU — Design Document

A minimal-IC-count, microcoded 8-bit CPU with 16-bit addressing, built around two
74LS181 ALU slices, designed to pair with the **Blinky Clock Module v3** (which
supplies the clock, reset supervision, halt/breakpoint policy, single-step and —
critically — the T-state counter, so the CPU carries no sequencer of its own).

The instruction set is a **65C02 subset with byte-identical encodings**,
including the full zero-page family and the complete Rockwell bit-manipulation
set (RMB/SMB/BBR/BBS) with exact flag fidelity: a stock 6502/65C02 assembler
emits directly runnable code for everything implemented. A handful of extra
instructions (ADD/SUB without carry, NOT/NEG/CLA/SET) occupy otherwise-unused
opcode slots.

**Headline numbers:** 33 logic ICs + 3 microcode ROMs on the CPU; ~38 ICs for
the complete machine with memory. 133 opcodes. Worst-case instruction: 8
T-states (of the module's 16 available). Zero-page, stack, and bit-manipulation
accesses match or beat 65C02 cycle counts.

---

## 1. Architecture overview

Single 8-bit data bus, 16-bit address bus, horizontal-ish microcode.

```
                         ┌──────────────── data bus (8) ────────────────┐
                         │            │           │          │          │
                      ┌──┴──┐      ┌──┴──┐     ┌──┴──┐    ┌──┴──┐    MEM/IO
                      │  A  │      │  X  │     │  B  │    │ IR  │
                      │'574 │      │'574 │     │'377 │    │'377 │
                      └──┬──┘      └──┬──┘     └──┬──┘    └──┬──┘
   A-side mini-bus ──────┴───────────┴──┬─────   │       opcode → μROM addr
   (one driver enabled per μstep:       │        │ IR6:4 → '238 → '541 = MASK
    A · X · $00 · PCL · PCH · MASK)  ┌──┴────────┴──┐
                                     │  2× 74LS181  │── Cn+8 → C flag / HCARRY
                                     │  M,S3-0,Cn   │
                                     └──────┬───────┘
                                        F → '541 → data bus
                                                │
        flags: Z ('688 on bus), N (bus bit 7), C ('181 carry), TEST (hidden)

   address bus high ←── '541 PAGE driver ($00 / $01 constant)
   address bus      ←── 2× '541 (PC)  ·  2× '574 MAR (tri-state)
                              ↑
   PC: 4× '161 ──────────────┘
        └──→ PCL '541, PCH '541 onto the A-side mini-bus (for branches/JSR)

   SP: 2× '193 (8-bit, up/down) ──→ '541 → data bus
```

Key structural decisions:

- **No sequencer on the CPU.** The clock module's '161 is *the* T counter;
  its T0–T3 outputs are microcode address bits, and the microcode asserts
  **/TRST** on each instruction's final cycle to reload T to 0. FETCH always
  happens at T = 0 — and because the microcode address still holds the *old*
  IR during T0, every opcode's T0 word is the identical generic FETCH; no
  opcode-specific work can hide there. (One deliberate exception: opcode
  $00's T0 does not fetch — safe because $00 only ever reaches IR via reset
  forcing or by being fetched as data, and in both cases the right next step
  is the RESET sequence, not a fetch.)
- **No address mux.** Address-bus sources take turns via output enables
  (ADDR field): the PC through '541s, the MAR through '574s, or — the page
  trick — **MARL with the PAGE driver** supplying the high byte.
- **The PAGE driver.** One '541 on the address-bus high half, inputs
  grounded except D0, which is wired to the ADDR field's low decode. It
  emits **$00 (zero page) or $01 (stack page)** as a constant. Consequence:
  zp and stack accesses never load MARH at all — the microsteps that would
  manufacture $00/$01 through the ALU simply don't exist.
- **A-side mini-bus.** The '181's A inputs are a six-driver tri-state bus
  (A, X, constant $00, PCL, PCH, MASK — one output bank enabled per
  microstep). This is what makes INX/DEX, indexed address arithmetic,
  relative branches, JSR pushes, and single-bit masks possible with zero
  mux ICs.
- **The MASK driver.** The Rockwell bit opcodes encode the bit number in the
  opcode itself (RMBn = $n7, SMBn = $(n+8)7, BBRn = $nF, BBSn = $(n+8)F), so
  **IR bits 6:4 are the bit index**. A '238 decodes them to a one-hot byte
  ($01<<n) behind a '541 as the sixth A-side driver — the mask materializes
  in zero cycles, and A and X are never involved.
- **PC counts itself** ('161s), so PC increment never touches the ALU.
  Low pair and high pair have separate /LOAD lines: PCL loads from a
  hardwired path off B while PCH loads from the data bus — a full 16-bit
  jump in one edge. (Synchronous load dominates count on the '161, so a
  jump and PCINC cannot share a cycle — visible in RTS.)
- **Reset is microcode, vectored at $FFFC/$FFFD — 6502-authentic, zero ICs.**
  RST drives the three microcode ROMs' /OE pins, and resistor networks pull
  the floating control word to a chosen constant: BUS=ALU, ASIDE=$00,
  F=pass($00), DST=IR. The module guarantees clocked edges during supervised
  reset, so **IR is forced to $00** before release. Opcode $00 — the 6502's
  BRK slot, otherwise unused — is the RESET pseudo-opcode: an 11-cycle
  microcode sequence that reads the vector at $FFFC/$FFFD and loads PC. The
  PC '161s' /CLR pins are tied inactive; the vector load defines PC. A and B
  are clobbered as scratch (registers are undefined after reset — also
  6502-authentic). One free consequence: a wild jump into $00-filled memory
  executes RESET and reboots cleanly through the vector.
- **SP counts itself** ('193s), so push/pop never touch the ALU. SP count
  pulses are folded into composite DST codes, since they always coincide
  with a specific bus destination (or none).
- **Flags watch the data bus, not the ALU.** Z ('688 comparing the bus to
  $00) and N (bus bit 7) are latched from the *data bus*; C is latched from
  the '181 carry chain. Because loads and transfers move data across the bus,
  **LDA/LDX/PLA/TAX etc. set N and Z with no extra cycle** — exactly the
  65C02 behaviour, and essential for real 6502 code (`LDA x / BEQ done`).
  C and N,Z have independent latch enables (FLGC / FLGNZ), matching the
  65C02 rule that logic ops and loads update N,Z but never C.
- **Hidden state:** SGN (branch offset bit 7), HCARRY (low-byte carry of
  address arithmetic, deliberately separate from the C flag so branches and
  indexing leave flags untouched), and **TEST** — the BBR/BBS bit-test
  result, latched from the '688 without touching Z. TEST is steered into
  the microcode address's Z bit line by the second (otherwise idle) section
  of the '153, under the TESTSEL control bit — so BBR/BBS fork on the
  hidden flop and **leave N, Z, and C untouched, exactly like the Rockwell
  parts**. Sign extension for branches XOR-steers the '181 S field with SGN;
  the steer condition is decoded (`ASIDE = PCH AND M = 0`), not a control bit.

### Why zero page earns its slots

Density first: every zp reference is 2 bytes instead of 3, and memory operands
dominate real code, so typical programs shrink 15–20% — the classic 6502 idiom
of zero page as a 256-byte pseudo-register file. With the PAGE driver the speed
argument returns too: **zp loads and stores match the 65C02 cycle-for-cycle**,
and the RMW and bit instructions beat it. And compatibility demands it:
assemblers emit the zp encodings automatically for any variable below $0100.

The Rockwell bit family exists precisely for zp-mapped I/O — `SMB3 led_port`,
`BBR7 acia_status, wait` — which is exactly how this machine's peripherals
will be addressed.

---

## 2. Microcode ROM

| | |
|---|---|
| Address | 15 bits = opcode (8) + T state (4, from clock module) + C + Z/TEST + N |
| Word | 24 bits — 3 × 28C256, **fully allocated, no spares** |
| Store | 32K × 24 — every opcode slot × 16 T states × 8 flag combinations |

Flags in the address make conditional branches free: taken and not-taken are
just different ROM contents at different flag addresses. The Z address line is
muxed ('153 section 2, TESTSEL): normally the Z flag, but the hidden TEST flop
during BBR/BBS tails. ROM outputs drive the control lines directly (no
pipeline register): the word changes just after the clock edge and settles
long before the next one — safe at these speeds behind the module's clean
gated clock.

### Control word (24 bits)

| Field | Bits | Values |
|---|---|---|
| ADDR | 2 | address bus ← PC / MAR / **ZP** (MARL + PAGE=$00) / **STK** (MARL + PAGE=$01) |
| BUS | 2 | data bus ← MEM / ALU·F / SP |
| DST | 4 | none · A · X · B · B+latch SGN · B+latch HC · B+SP++ · IR · MARL · MARH · PC (PCH←bus, PCL←B, same edge) · MEM write · MEM+SP−− · SP (TXS) · SP++ alone · **none+latch TEST** |
| ASIDE | 3 | A-side ← A / X / $00 / PCL / PCH / **MASK** |
| ALU M | 1 | '181 mode |
| ALU S | 4 | '181 select (XOR-steered by SGN when ASIDE=PCH·M=0) |
| CSEL | 2 | '181 carry-in ← 0 / 1 / C flag / HCARRY ('153 section 1) |
| TESTSEL | 1 | microcode Z address line ← Z flag / TEST flop ('153 section 2) |
| PCINC | 1 | PC++ |
| FLGC | 1 | latch C from '181 carry-out |
| FLGNZ | 1 | latch Z ('688) and N (bit 7) from the data bus |
| TRST | 1 | assert /TRST — instruction ends, module reloads T to 0 |
| HREQ | 1 | assert /HALTREQ (STP) |

Composite DST codes carry the strobes that always travel together: SGN and
HCARRY ride on their B loads, SP pulses ride on the bus destinations they
accompany, and TEST rides on a no-destination ALU pass. The bit family
consumed the machine's last three spares — the 16th DST code, the '153's
second section, and the final control bit — simultaneously. The word is full;
the next feature pays for a 4th ROM.

*Build note:* in active-high data convention the physical '181 carry pin is
active-low; "carry-in = 1" throughout this document means the pin pulled low,
and the C-flag polarity is fixed with a spare inverter so that stored C follows
the 6502 convention (C = 1 means carry / no-borrow).

### ALU configurations used

Of the 64 possible M·S·carry combinations, these earn microcode slots:

| M | S3–S0 | Cn | A-side | F | Used by |
|---|---|---|---|---|---|
| 1 | 1111 | – | A / X / $00 | pass A-side | STA, STX, STZ, TAX, TXA, TXS, PHA, PHX, CLA |
| 1 | 1010 | – | – | pass B | PLA/PLX flag pass, MARL←B |
| 1 | 0000 | – | A | /A | NOT A |
| 1 | 0101 | – | – | /B | DEC-memory finish |
| 1 | 1011 | – | A / MASK | A·B | AND, **BBR/BBS test** |
| 1 | 0010 | – | MASK | /A·B | **RMB** (clear bit n) |
| 1 | 1110 | – | A / MASK | A∨B | ORA, **SMB** (set bit n) |
| 1 | 0110 | – | A | A⊕B | EOR |
| 1 | 1100 | – | – | $FF | SET A |
| 0 | 1001 | c | A | A+B+c | ADC (c=C), ADD (c=0) |
| 0 | 1001 | 0 | X | X+B | indexed address low byte (abs,X: latch HCARRY; zp,X: carry ignored → page-zero wrap, the 6502-correct behaviour) |
| 0 | 1001 | c | $00 | B+c | abs,X high byte +HCARRY, INC mem |
| 0 | 0110 | c | A / X | side−B−1+c | SBC (c=C), SUB/CMP/CPX (c=1) |
| 0 | 0110 | 1 | $00 | −B | NEG, DEC-memory step 1 |
| 0 | 0000 | 1 | A / X | side+1 | INC A, INX |
| 0 | 1111 | c | A / X / $00 | side−1+c | DEC A, DEX, **CLC** (c=0 → borrow) / **SEC** (c=1 → no borrow) |
| 0 | 1100 | c | A | A+A+c | ASL (c=0), ROL (c=C) |
| 0 | 0000/1111 | HC | PCH | PCH ± sign + HCARRY | branch high-byte fix (SGN steers 0000↔1111) |

The CLC/SEC trick deserves a note: with FLGC and FLGNZ independent, CLC and
SEC are simply "compute something with a known borrow / no-borrow and latch
only C" — S=1111 on the $00 driver with carry-in 0 or 1. No flag-set hardware,
no operand fetch, 2 cycles each, N and Z untouched. Exactly 65C02 semantics.

---

## 3. Instruction set

T0 is always FETCH (IR ← M(PC), PC++) and is included in every cycle count.
**Cyc** = this CPU; **'C02** = 65C02/Rockwell cycle count for the same
encoding. All listed opcodes are **byte-identical to the 65C02** unless marked
✚ (ours only, parked in unused slots).

### Loads and stores

| Instr | Op | Cyc | 'C02 | Notes |
|---|---|---|---|---|
| LDA # | $A9 | 2 | 2 | N,Z set from bus — free |
| LDA zp | $A5 | 3 | 3 | **match** — PAGE driver |
| LDA zp,X | $B5 | 4 | 4 | **match**; wraps within page zero, 6502-correct |
| LDA abs | $AD | 4 | 4 | |
| LDA abs,X | $BD | 6 | 4(+1) | 'C02 adds 1 on page cross; we always pay the high-byte fix |
| LDX # | $A2 | 2 | 2 | |
| LDX zp | $A6 | 3 | 3 | (LDX zp,Y / abs,Y omitted — no Y) |
| LDX abs | $AE | 4 | 4 | |
| STA zp | $85 | 3 | 3 | |
| STA zp,X | $95 | 4 | 4 | |
| STA abs | $8D | 4 | 4 | A → ALU pass → bus |
| STA abs,X | $9D | 6 | 5 | |
| STX zp | $86 | 3 | 3 | |
| STX abs | $8E | 4 | 4 | |
| STZ zp | $64 | 3 | 3 | 65C02 extension; $00 driver → pass |
| STZ zp,X | $74 | 4 | 4 | |
| STZ abs | $9C | 4 | 4 | |
| STZ abs,X | $9E | 6 | 5 | |

### ALU operations (A ∘ operand → A, N,Z; arithmetic also C)

| Instr | # | zp | zp,X | abs | abs,X | Cyc (#/zp/zp,X/abs/abs,X) | 'C02 |
|---|---|---|---|---|---|---|---|
| ADC | $69 | $65 | $75 | $6D | $7D | 3 / 4 / 5 / 5 / 7 | 2 / 3 / 4 / 4 / 4(+1) |
| SBC | $E9 | $E5 | $F5 | $ED | $FD | 3 / 4 / 5 / 5 / 7 | 2 / 3 / 4 / 4 / 4(+1) |
| AND | $29 | $25 | $35 | $2D | $3D | 3 / 4 / 5 / 5 / 7 | 2 / 3 / 4 / 4 / 4(+1) |
| ORA | $09 | $05 | $15 | $0D | $1D | 3 / 4 / 5 / 5 / 7 | 2 / 3 / 4 / 4 / 4(+1) |
| EOR | $49 | $45 | $55 | $4D | $5D | 3 / 4 / 5 / 5 / 7 | 2 / 3 / 4 / 4 / 4(+1) |
| CMP | $C9 | $C5 | $D5 | $CD | $DD | 3 / 4 / 5 / 5 / 7 | 2 / 3 / 4 / 4 / 4(+1) |
| ADD ✚ | slot | slot | slot | slot | slot | 3 / 4 / 5 / 5 / 7 | — (idiom: CLC+ADC) |
| SUB ✚ | slot | slot | slot | slot | slot | 3 / 4 / 5 / 5 / 7 | — (idiom: SEC+SBC) |
| CPX | $E0 | $E4 | — | $EC | — | 3 / 4 / – / 5 / – | 2 / 3 / – / 3 / – |

The uniform +1 across this family is the B-staging cost: the 6502 completes
the ALU operation under the *next* instruction's fetch (its one-deep pipeline);
we must land the operand in B, then compute — and T0 is a dedicated fetch.

### Single-operand and register ops (all 2 cycles)

| Instr | Op | 'C02 | Notes |
|---|---|---|---|
| INC A | $1A | 2 | 65C02 extension opcode |
| DEC A | $3A | 2 | |
| ASL A | $0A | 2 | A+A, C out |
| ROL A | $2A | 2 | A+A+C |
| INX | $E8 | 2 | X on A-side, +1, writeback via bus |
| DEX | $CA | 2 | |
| TAX | $AA | 2 | N,Z via bus |
| TXA | $8A | 2 | |
| TXS | $9A | 2 | no flags (FLGNZ off) — 6502-correct |
| TSX | $BA | 2 | N,Z |
| CLC | $18 | 2 | FLGC-only borrow trick (see ALU table) |
| SEC | $38 | 2 | |
| NOT A ✚ | slot | — | idiom: EOR #$FF |
| NEG A ✚ | slot | — | idiom: EOR #$FF + ADC #1 |
| CLA ✚ | slot | — | idiom: LDA #0; sets Z |
| SET A ✚ | slot | — | A ← $FF |

### Memory read-modify-write

| Instr | Op | Cyc | 'C02 | Notes |
|---|---|---|---|---|
| INC zp | $E6 | 4 | 5 | **faster** — B+1 via $00 driver, PAGE high byte |
| INC zp,X | $F6 | 5 | 6 | **faster** |
| INC abs | $EE | 5 | 6 | **faster** |
| INC abs,X | $FE | 7 | 7 | |
| DEC zp | $C6 | 5 | 5 | B−1 = /(−B): NEG then NOT, two ALU passes |
| DEC zp,X | $D6 | 6 | 6 | |
| DEC abs | $CE | 6 | 6 | |
| DEC abs,X | $DE | 8 | 7 | |

### Bit manipulation (Rockwell/WDC set — full flag fidelity)

| Instr | Op | Cyc | 'C02 | Notes |
|---|---|---|---|---|
| RMB0–7 zp | $07…$77 | 4 | 5 | **faster**; M ← M·/mask; no flags — Rockwell-correct |
| SMB0–7 zp | $87…$F7 | 4 | 5 | **faster**; M ← M∨mask; no flags |
| BBR0–7 zp,rel | $0F…$7F | 5 nt / 7 t | 5 / 6(+1) | forks on hidden TEST flop; **N, Z, C untouched** |
| BBS0–7 zp,rel | $8F…$FF | 5 nt / 7 t | 5 / 6(+1) | |

The bit index rides in IR6:4 (the Rockwell encoding is perfectly regular), so
all 32 opcodes share **two** microcode shapes — the '238 mask decode makes
RMB3 and RMB6 literally the same ROM contents.

### Control flow

| Instr | Op | Cyc | 'C02 | Notes |
|---|---|---|---|---|
| JMP abs | $4C | 3 | 3 | PCL←B, PCH←bus, one edge |
| BRA | $80 | 4 | 3(+1) | 65C02 extension |
| BEQ / BNE | $F0 / $D0 | 2 nt / 4 t | 2 / 3(+1) | true relative, flag-preserving |
| BCS / BCC | $B0 / $90 | 2 / 4 | 2 / 3(+1) | |
| BMI / BPL | $30 / $10 | 2 / 4 | 2 / 3(+1) | N flag from bus bit 7 |
| JSR abs | $20 | 7 | 6 | pushes return−1, **byte-identical stack frame** |
| RTS | $60 | 7 | 6 | pop-then-increment via PCINC; load dominates count on the '161, so the final PCINC can't merge into the PC load |

### Stack

| Instr | Op | Cyc | 'C02 | Notes |
|---|---|---|---|---|
| PHA | $48 | 3 | 3 | **match** — PAGE driver |
| PLA | $68 | 4 | 4 | SP++ needs its own T1 (T0 is opcode-agnostic) |
| PHX | $DA | 3 | 3 | |
| PLX | $FA | 4 | 4 | |

### Miscellaneous

| Instr | Op | Cyc | 'C02 | Notes |
|---|---|---|---|---|
| NOP | $EA | 1 | 2 | END at T0 |
| STP | $DB | 2 | 3 | asserts /HALTREQ — machine freezes on the module's panel |
| RESET | $00 | 11 | 7 | pseudo-opcode in the BRK slot; forced into IR by reset, reads $FFFC/$FFFD, loads PC; clobbers A, B; identical ROM contents across all 8 flag combinations (flags are undefined at reset) |

**Total: 133 opcodes** from roughly 22 distinct microcode shapes. 123 opcode
slots remain free. (Audit: loads/stores 18 · ALU family 43 · register ops 16 ·
memory RMW 8 · control flow 10 · stack 4 · bit family 32 · misc 2.)

### Cycle-count scoreboard vs the 65C02

| | |
|---|---|
| **Faster** | INC zp/zp,X/abs, **RMB and SMB (all 16)**, NOP, STP |
| **Match** | all zp and zp,X loads/stores, all abs loads/stores, STZ, JMP, branches not-taken, **BBR/BBS not-taken**, PHA/PHX, PLA/PLX, DEC-memory except abs,X, all 2-cycle register ops, CLC/SEC |
| **+1** | all ALU ops in #/zp/zp,X/abs (B staging), JSR, RTS, branches taken, BBR/BBS taken, STA/STZ abs,X, DEC abs,X |
| **+2** | ALU abs,X, LDA abs,X (we always pay the page-cross fix the 'C02 usually skips) |

---

## 4. Microcode listings

Notation: `↑` = PCINC · `M(x)` = memory at address x · `ZP:`/`STK:` = ADDR
selects MARL + PAGE driver ($00/$01) · `END` = /TRST asserted · `flgC/flgNZ`
= latch enables · `HC` = HCARRY. Every instruction begins `T0: IR←M(PC) ↑`
(FETCH) — identical for all opcodes, since IR is stale during T0.

**LDA zp ($A5)** — no MARH load ever happens; the PAGE driver *is* the high byte
```
T1: MARL ← M(PC) ↑
T2: A ← M(ZP:MARL)        flgNZ  END
```

**LDA zp,X ($B5)** — indexed, carry deliberately discarded (page-zero wrap)
```
T1: B ← M(PC) ↑
T2: MARL ← X+B            ; HCARRY NOT latched — wraps in page zero
T3: A ← M(ZP:MARL)        flgNZ  END
```

**LDA abs ($AD)** — the abs template
```
T1: MARL ← M(PC) ↑
T2: MARH ← M(PC) ↑
T3: A ← M(MAR)            flgNZ  END
```

**ADC abs,X ($7D)** — the abs,X template, including the carry chain
```
T1: B ← M(PC) ↑                        ; base lo
T2: MARL ← X+B  (CSEL=0)   latch HC    ; indexed lo
T3: B ← M(PC) ↑                        ; base hi
T4: MARH ← $00+B (CSEL=HC)             ; hi + page-cross carry
T5: B ← M(MAR)                         ; operand
T6: A ← A+B (CSEL=C)       flgC flgNZ  END
```

**RMB n zp ($n7)** — one shape covers all eight; the '238 reads n from IR6:4.
SMB is identical with F = MASK∨B (S=1110). No flags — Rockwell-correct
```
T1: MARL ← M(PC) ↑
T2: B ← M(ZP:MARL)
T3: M(ZP:MARL) ← /MASK·B  (M=1 S=0010, ASIDE=MASK)   END
```

**BBR n zp,rel ($nF)** — the machine's only mid-instruction fork: the test
latches the hidden TEST flop (never Z), and TESTSEL steers it into the
microcode address from T4 on. BBS is the same shape with the fork sense
inverted in ROM contents
```
T1: MARL ← M(PC) ↑
T2: B ← M(ZP:MARL)
T3: bus ← MASK·B          latch TEST   ; N,Z,C untouched
    ── T4 onward: TESTSEL=1, Z address line = TEST ──
taken (bit clear):                     not taken:
T4: B ← M(PC) ↑  latch SGN             T4: ↑   END        ; skip offset
T5: B ← PCL+B    latch HC              ; 5 cycles
T6: bus ← PCH ±sign +HC (CSEL=HC)
    PC ← bus:B                    END  ; 7 cycles
```

**Bxx rel (e.g. BNE $D0)** — flags in the ROM address fork the paths at T1
```
taken:                                 not taken:
T1: B ← M(PC) ↑  latch SGN             T1: B ← M(PC) ↑   END
T2: B ← PCL+B    latch HC              ; 2 cycles
T3: bus ← PCH ±sign +HC (CSEL=HC)
    PC ← bus:B                    END  ; 4 cycles
```

**JSR abs ($20)** — pushing before the second operand fetch makes the stack
image byte-identical to a real 6502 (return address = opcode+2)
```
T1: B ← M(PC) ↑                        ; ADL parked in B
T2: MARL ← SP    (BUS=SP)
T3: M(STK:MARL) ← PCH (A-side pass)  SP--
T4: MARL ← SP
T5: M(STK:MARL) ← PCL                SP--
T6: bus ← M(PC); PC ← bus:B            END
```

**RTS ($60)** — pop-then-increment; PCINC needs its own cycle (load dominates
count on the '161)
```
T1: SP++
T2: MARL ← SP
T3: B ← M(STK:MARL)             SP++
T4: MARL ← SP
T5: bus ← M(STK:MARL); PC ← bus:B
T6: ↑                             END
```

**PHA ($48) / PLA ($68)**
```
PHA:                                   PLA:
T1: MARL ← SP                          T1: SP++
T2: M(STK:MARL) ← A   SP--   END       T2: MARL ← SP
                                       T3: A ← M(STK:MARL)  flgNZ  END
```

**DEC zp ($C6)** — B−1 synthesized as /(−B), since the '181 has no B−1
```
T1: MARL ← M(PC) ↑
T2: B ← M(ZP:MARL)
T3: B ← −B       ($00−B, CSEL=1)
T4: M(ZP:MARL) ← /B  (M=1 S=0101)  flgNZ  END
```

**CLC ($18) / SEC ($38)** — carry-only latch, nothing else moves
```
T1: F = $00−1+c  (S=1111, CSEL=0 for CLC / 1 for SEC)   flgC   END
```

**RESET ($00)** — the vector addresses are *computed*, since the only
conjurable constants are $00, $01, $FF and their ALU derivatives. A stashes
$FD; B carries the counting and ends holding the vector low byte
```
T0: no fetch                           ; the one exception to generic T0
T1: MARH ← $FF        (M=1 S=1100)
T2: B ← $01           ($00 + c)
T3: B ← B+1  = $02
T4: A ← /B   = $FD                     ; stash MARL for the high byte
T5: B ← B+1  = $03
T6: MARL ← /B = $FC
T7: B ← M(MAR)                         ; vector low  ($FFFC)
T8: MARL ← A                           ; $FD (A-side pass)
T9: bus ← M(MAR); PC ← bus:B           ; vector high ($FFFD)
T10: END                               ; next T0 fetches from the vector
```

---

## 5. Deliberate omissions vs the 65C02

| Missing | Why / workaround |
|---|---|
| Y register, all ,Y modes | one index register; X covers loops and tables |
| (zp), (zp,X), (zp),Y indirect | pointer walks need a scratch byte; A/X are architecturally off-limits mid-instruction. **+1 '377 hidden temp** unlocks (zp) — see growth path |
| JMP (abs) $6C | same scratch-byte problem, same fix |
| LSR / ROR | the '181 cannot shift right. **+1 IC** (a '541 with crossed bits on the F→bus path) would add both |
| BIT, TRB, TSB | no V flag, and the write combinations aren't worth the slots (TRB/TSB partially covered by RMB/SMB) |
| PHP / PLP | flag register can't drive or load the bus; fixable with a '574 flag register if ever needed |
| BRK / RTI / interrupts | no interrupt hardware — the clock module's halt/breakpoint machinery covers the debugging role. The $00 (BRK) slot is repurposed as the RESET pseudo-opcode |
| SEI / CLI / CLV / SED / CLD | no I, V, or D flags (no decimal mode) |
| WAI | no interrupts to wait for |

Practical consequence: straight-line 65C02 code that sticks to A, X, immediate,
zero page, zp,X, absolute, abs,X, the stack, the eight branches, and the
Rockwell bit instructions **assembles and runs unmodified** — which covers the
great majority of real programs, and essentially all bare-metal I/O code. The
remaining rewrites are Y-register usage and pointer indirection.

---

## 6. IC bill of materials

### CPU board — 33 logic ICs + 3 ROMs

| Qty | Part | Role |
|---|---|---|
| 2 | 74LS181 | ALU, two 4-bit slices, ripple carry |
| 4 | 74HC574 | A · X · MARL · MARH (tri-state: A/X onto the A-side mini-bus, MAR onto the address bus — the muxes that aren't there) |
| 4 | 74HC377 | B · IR · flag C · flags N,Z (independent latch enables) |
| 4 | 74HC161 | PC — self-counting, split /LOAD low/high pairs |
| 2 | 74HC193 | SP — self-counting up/down |
| 9 | 74HC541 | ALU F→bus · PC→addr ×2 · PAGE constant ($00/$01) → addr high · PCL→A-side · PCH→A-side · $00 constant (A-side) · **MASK → A-side** · SP→bus |
| 1 | 74HC238 | bit-mask decode: IR6:4 → one-hot $01<<n |
| 1 | 74HC688 | Z detect on the data bus |
| 2 | 74HC74 | SGN + HCARRY · **TEST** hidden flops |
| 1 | 74HC86 | SGN steering of the S field (sign extension) |
| 1 | 74HC153 | section 1: carry-in select (0 / 1 / C / HCARRY) · section 2: Z-address-line select (Z / TEST) |
| 2 | 74HC04 / 74HC00 | glue (carry polarity, ADDR decode, SGN-steer condition, /TRST & write strobes) |
| 3 | 28C256 | microcode — 24-bit control word, 15-bit address |

HCT (or HCT buffers) at the LS181 boundary as usual for input thresholds.

### Memory and decode — 2 ICs

| Qty | Part | Role |
|---|---|---|
| 1 | 28C256 | program ROM, $8000–$FFFF · reset vector at $FFFC/$FFFD |
| 1 | 62256 | RAM, $0000–$7FFF (zero page and stack page $0100 live here) |

A15 is the entire address decoder: A15 → ROM /CS, inverted (spare '04 gate) →
RAM /CS.

### External

Blinky Clock Module v3 (~16–17 ICs): clock generation and selection,
single-cycle / single-instruction stepping, halt and breakpoint policy,
supervised reset, **and the T counter** (T0–T3 into the microcode address,
/TRST from the control word, /HALTREQ from STP).

**Complete machine: ~38 ICs** plus the module.

### Performance

LS181 ripple carry plus ~150 ns EEPROM microcode access bounds the crystal leg
at roughly 1–2 MHz. Average ~3.5 cycles per instruction on zp-heavy code →
**~350–550k instructions/sec**, with the module's 555 leg providing the
0.5–5 Hz blinkenlights mode and NEXT INSTR walking the program fetch-to-fetch.

---

## 7. Build sequence — one working machine per stage

Following the clock module's own methodology: **every stage is a complete,
testable machine**, verified in simulation and on the bench before the next
layer goes on. Each stage lists the new ICs, the running logic-IC count, the
new instructions, and the milestone that proves it. Microcode is re-burned at
every stage; unimplemented opcodes microcode to STP so a wild jump halts
loudly instead of executing garbage ($00 is the exception from stage 5
onward — it runs the RESET sequence, so wild jumps into zeroed memory
reboot cleanly).

| Stage | Adds | +ICs | ICs | +Instr | Instr |
|---|---|---|---|---|---|
| 1 | Fetch loop (PC, IR, μROMs) | 9 | 9 | 2 | 2 |
| 2 | B + PC load | 1 | 10 | 1 | 3 |
| 3 | ALU + A | 4 | 14 | 11 | 14 |
| 4 | MAR + RAM | 2 | 16 | 7 | 21 |
| 5 | $00 driver | 1 | 17 | 5 | 26 |
| 6 | Flags | 4 | 21 | 9 | 35 |
| 7 | X | 1 | 22 | 9 | 44 |
| 8 | PAGE driver | 1 | 23 | 29 | 73 |
| 9 | Branches + abs,X | 4 | 27 | 20 | 93 |
| 10 | Stack | 3 | 30 | 8 | 101 |
| 11 | Bit engine | 3 | 33 | 32 | 133 |

### Stage 1 — the fetch loop (9 ICs)
**Add:** PC (4× '161) · PC→addr ('541 ×2) · IR ('377) · glue ('04, '00) ·
3× 28C256 microcode · program ROM.
**New instructions (2):** NOP, STP.
**Milestone:** ROM full of $EA ending in $DB. On the 555 leg the address LEDs
count; at the $DB the machine freezes and the module's HALT LED lights.
NEXT INSTR advances exactly one address. The entire fetch/END/T-counter
handshake with the module is proven before any datapath exists.
**Interim boot (stages 1–4):** PC /CLR is strapped to /RST and the memory
map is temporarily *inverted* (ROM low, RAM high) so clear-to-$0000 lands in
ROM. The vectored reset arrives at stage 5.

### Stage 2 — B and the PC load path (+1 → 10)
**Add:** B ('377); wire the dual PC load (PCL←B hardwire, PCH←bus).
**New (3 total):** JMP abs.
**Milestone:** replace the STP with JMP $8000 — the machine loops forever.
Single-cycle through the 3-cycle jump and watch PCL/PCH land on one edge.

### Stage 3 — the ALU (+4 → 14)
**Add:** 2× 74LS181 · A ('574, first A-side driver) · F→bus ('541).
Cn is temporarily strapped to the CSEL-low ROM bit (constant 0/1 carries
work; the '153 arrives with the flags — bench-discipline strap, removed at
stage 6).
**New (14):** LDA #, ADD #, SUB #, AND #, ORA #, EOR #, INC A, DEC A,
ASL A, NOT A, SET A.
**Milestone:** LEDs on A; single-step known arithmetic patterns. No flags
yet — results only.

### Stage 4 — MAR and RAM (+2 → 16, + 62256)
**Add:** MARL, MARH ('574 ×2) · RAM · A15 decode (spare '04 gate).
**New (21):** LDA abs, STA abs, ADD/SUB/AND/ORA/EOR abs.
**Milestone:** software walking-bit RAM test — the first program that can be
wrong in interesting ways. Breakpoint-comparator card gets useful here.

### Stage 5 — the $00 driver (+1 → 17)
**Add:** $00 constant '541 (second A-side driver).
**New (26):** CLA, NEG A, STZ abs, INC abs, DEC abs.
**Also — the reset vector goes live.** The RESET microcode needs $00, $01
and $FF, all of which now exist. Flip the memory map to final (ROM high,
RAM low), lift the PC /CLR strap, wire RST to the microcode ROMs' /OE pins,
fit the pull networks that define the reset control word (IR ← $00), and
burn the $00 RESET sequence. From here the machine boots through
$FFFC/$FFFD like a real 6502.

### Stage 6 — flags (+4 → 21)
**Add:** '688 (bus Z-detect) · C ('377) · N,Z ('377) · '153 (CSEL proper —
remove the stage-3 strap).
**New (35):** ADC #/abs, SBC #/abs, CMP #/abs, ROL A, CLC, SEC — and every
existing instruction now sets flags correctly.
**Also:** *temporary* absolute-target conditional jumps (JZ/JC flavour) in ✚
slots, purely as test instrumentation until relative branches exist —
retired at stage 9, same change-while-idle discipline as the strap.
**Milestone:** multi-byte ADC arithmetic; a real counted loop using the
interim jumps.

### Stage 7 — X (+1 → 22)
**Add:** X ('574, third A-side driver).
**New (44):** LDX #/abs, STX abs, TAX, TXA, INX, DEX, CPX #/abs.

### Stage 8 — the PAGE driver (+1 → 23)
**Add:** PAGE constant '541 on the address-bus high half ($00/$01 from the
ADDR decode).
**New (73):** the entire zero-page world in one stage — LDA/STA zp & zp,X,
LDX/STX zp, STZ zp/zp,X, ADC/SBC/AND/ORA/EOR/CMP zp & zp,X, ADD/SUB zp &
zp,X, CPX zp, INC/DEC zp & zp,X. **29 opcodes from one '541** — the best
IC-to-instruction ratio in the machine. Code density jumps 15–20%.

### Stage 9 — PC meets the ALU (+4 → 27)
**Add:** PCL→A-side, PCH→A-side ('541 ×2) · SGN+HCARRY ('74) · '86 sign
steer + decode glue.
**New (93):** all relative branches — BEQ, BNE, BCS, BCC, BMI, BPL, BRA —
and, HCARRY now existing, the complete abs,X family: LDA/STA/STZ abs,X, the
six ALU ops abs,X, ADD/SUB abs,X, INC/DEC abs,X. Retire the stage-6 interim
jumps.
**Milestone:** this is the moment a stock 6502 assembler's output starts
running unmodified.

### Stage 10 — the stack (+3 → 30)
**Add:** SP ('193 ×2) · SP→bus ('541). (The PCH/PCL pushes reuse the stage-9
A-side drivers; the $01 stack page reuses the stage-8 PAGE driver — this
stage is cheap *because* of the two before it.)
**New (101):** PHA, PLA, PHX, PLX, TXS, TSX, JSR, RTS.
**Milestone:** subroutines — the machine becomes something you structure
programs on. Verify the JSR frame byte-for-byte against a 65C02 reference.

### Stage 11 — the bit engine (+3 → 33)
**Add:** '238 mask decode (IR6:4) · MASK '541 (sixth A-side driver) · TEST
('74); wire the '153's second section (TESTSEL).
**New (133):** RMB0–7, SMB0–7, BBR0–7, BBS0–7 — 32 opcodes, two microcode
shapes.
**Milestone:** `SMB3 led_port` / `BBR7 acia_status, wait` — bare-metal I/O
idioms, complete. The machine as documented above.

### Beyond (each implies the 4th microcode ROM first — the word is full)

1. **Hidden temp '377** (+1): JMP (abs), LDA/STA (zp), synthesized (abs)
   indirection — the 6502's pointer idioms.
2. **Right-shift path** (+1 '541, bits crossed): LSR A, ROR A.
3. **'574 flag register** (net 0–1): PHP/PLP, and frees a '377.
4. **Y register '574** (+1): second index on the existing A-side mini-bus
   (ASIDE has two codes spare); re-opens the ,Y modes and (zp),Y.
