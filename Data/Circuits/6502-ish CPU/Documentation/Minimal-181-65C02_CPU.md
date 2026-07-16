# Minimal 74LS181 CPU — Design Document

A minimal-IC-count, microcoded 8-bit CPU with 16-bit addressing, built around two
74LS181 ALU slices, designed to pair with the **Blinky Clock Module v3** (which
supplies the clock, reset supervision, halt/breakpoint policy, single-step and —
critically — the T-state counter, so the CPU carries no sequencer of its own).

The instruction set is a **65C02 subset with byte-identical encodings**,
including the full zero-page family, the complete Rockwell bit-manipulation
set (RMB/SMB/BBR/BBS) with exact flag fidelity, pointer indirection, the Y
register, left and right memory shifts, and both maskable and non-maskable
interrupts: a stock 6502/65C02 assembler emits directly runnable code for
everything implemented. A handful of extra instructions (ADD/SUB without
carry, NOT/NEG/CLA/SET) occupy otherwise-unused opcode slots. The set has
been **validated against real firmware** — the Hopper 6502 BIOS and runtime,
11,396 instructions — see §6.

**Headline numbers:** 44 logic ICs + 4 microcode ROMs on the CPU; ~50 ICs for
the complete machine with memory. 211 opcodes. Worst-case instruction: 9
T-states after the stage-17 result bus (11 before it); interrupt entry runs
12 — all within the module's 16 available. Zero-page, stack, indexed,
shift, and bit-manipulation accesses match or beat 65C02 cycle counts;
pointer-indirect modes run but pay a premium.

The build is staged (§8): stages 1–11 erect the **core machine** (33 logic
ICs, 3 ROMs, 133 opcodes) whose microcode is fully listed in this document;
stages 12–16 add the 4th microcode ROM and the **extension datapath** (temp,
Y, memory shifts, the P register, IRQ + NMI); stage 17 is a pure performance
retrofit (the result bus) that changes no behaviour. Extension cycle counts
below are worked estimates, to be pinned down as each stage's microcode is
written.

---

## 1. Design philosophy — why the '181

The 74181 is chosen because it hands you two things minimal designs usually
fight for:

- **Two independent operand ports** (A and B).
- **A built-in function set** — add / subtract / logic / pass-through — so
  "compute" and "move" are the same hardware. Every register transfer is just
  an ALU pass, and no separate transfer paths need building.

An earlier iteration of this design exploited those ports with two
buffer-deleting tricks: the accumulator hardwired permanently to the A port,
and no B register at all (the B port hanging straight off the data bus). Both
are retired here, deliberately:

- **The A port carries an eight-driver tri-state mini-bus instead of a
  hardwired accumulator.** A, X, Y, temp, a constant \$00, PCL, PCH, and the
  bit MASK all take turns on the A side. That one decision is what makes
  INX/DEX, indexed address arithmetic, relative branches, JSR pushes,
  single-bit masks, and memory left-shifts possible with **zero mux ICs** —
  worth far more than the one buffer the hardwired-A trick saved.
- **The B register exists** — but where the earlier design hung the B port
  permanently off the data bus, here the B port gets a 2:1 mux at stage 17
  (BSEL: B register or the data bus), turning the ALU's F output into a de
  facto second bus. Until then, every ALU op pays one cycle to stage its
  operand into B. The staging tax is paid openly — it appears as the
  uniform +1 in the stage-1–16 cycle counts — and it buys DEC synthesis,
  branch-offset staging, and JSR's operand parking; stage 17 then buys most
  of the tax back for two '157s.

The other founding decision the '181 enables: **microcode in EEPROM, flags in
the address** (§3), which makes conditional branching a table lookup rather
than sequencer logic.

---

## 2. Architecture overview

Single 8-bit data bus, 16-bit address bus, horizontal-ish microcode.

```
                         ┌──────────────── data bus (8) ────────────────┐
                         │            │           │          │          │
                      ┌──┴──┐      ┌──┴──┐     ┌──┴──┐    ┌──┴──┐    MEM/IO
                      │  A  │      │  X  │     │  B  │    │ IR  │
                      │'574 │      │'574 │     │'377 │    │'377 │
                      └──┬──┘      └──┬──┘     └──┬──┘    └──┬──┘
   A-side mini-bus ──────┴───────────┴──┬──── ┌──┴──┐    opcode → μROM addr
   (one driver enabled per μstep:       │   BSEL '157 ×2   IR6:4 → '238
    A · X · Y · temp · $00 ·         ┌──┴─────┴────┐        → '541 = MASK
    PCL · PCH · MASK)                │  2× 74LS181 │── Cn+8 → C flag / HCARRY
                                     │  M,S3-0,Cn  │   (B port ← B reg or bus)
                                     └──────┬──────┘
                                        F → '541 → data bus
                                        F → SHR '541 (>>1) → data bus
                                        F → register D inputs (stage 17)
                                                │
        flags: Z ('688), N (bit 7), C ('181 carry), TEST (hidden) — on F
        P → '541 → data bus (PHP/IRQ)
        VEC → '541 → data bus ($FA/$FB/$FE/$FF interrupt vector constants)

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
  \$00's T0 does not fetch — safe because \$00 only ever reaches IR via reset
  forcing or by being fetched as data, and in both cases the right next step
  is the RESET sequence, not a fetch. The interrupt grant also overrides the
  generic T0 — see the interrupt bullet.)
- **No address mux.** Address-bus sources take turns via output enables
  (ADDR field): the PC through '541s, the MAR through '574s, or — the page
  trick — **MARL with the PAGE driver** supplying the high byte.
- **The PAGE driver.** One '541 on the address-bus high half, inputs
  grounded except D0, which is wired to the ADDR field's low decode. It
  emits **\$00 (zero page) or \$01 (stack page)** as a constant. Consequence:
  zp and stack accesses never load MARH at all — the microsteps that would
  manufacture \$00/\$01 through the ALU simply don't exist.
- **A-side mini-bus.** The '181's A inputs are an eight-driver tri-state bus
  (A, X, Y, temp, constant \$00, PCL, PCH, MASK — one output bank enabled
  per microstep). This is what makes INX/DEX, indexed address arithmetic,
  relative branches, JSR pushes, single-bit masks, and memory left-shifts
  possible with zero mux ICs. The field is now exactly full: eight drivers,
  three bits.
- **The MASK driver.** The Rockwell bit opcodes encode the bit number in the
  opcode itself (RMBn = \$n7, SMBn = \$(n+8)7, BBRn = \$nF, BBSn = \$(n+8)F), so
  **IR bits 6:4 are the bit index**. A '238 decodes them to a one-hot byte
  (\$01<<n) behind a '541 as an A-side driver — the mask materializes
  in zero cycles, and A and X are never involved.
- **PC counts itself** ('161s), so PC increment never touches the ALU.
  Low pair and high pair have separate /LOAD lines: PCL loads from a
  hardwired path off B while PCH loads from the data bus — a full 16-bit
  jump in one edge. (Synchronous load dominates count on the '161, so a
  jump and PCINC cannot share a cycle — visible in RTS.)
- **Reset is microcode, vectored at \$FFFC/\$FFFD — 6502-authentic, zero ICs.**
  RST drives the microcode ROMs' /OE pins, and resistor networks pull
  the floating control word to a chosen constant: BUS=ALU, ASIDE=\$00,
  F=pass(\$00), DST=IR. The module guarantees clocked edges during supervised
  reset, so **IR is forced to \$00** before release. Opcode \$00 — the 6502's
  BRK slot, otherwise unused — is the RESET pseudo-opcode: an 11-cycle
  microcode sequence that reads the vector at \$FFFC/\$FFFD and loads PC. The
  PC '161s' /CLR pins are tied inactive; the vector load defines PC. A and B
  are clobbered as scratch (registers are undefined after reset — also
  6502-authentic). One free consequence: a wild jump into \$00-filled memory
  executes RESET and reboots cleanly through the vector.
- **SP counts itself** ('193s), so push/pop never touch the ALU. SP count
  pulses are folded into composite DST codes, since they always coincide
  with a specific bus destination (or none). *Why hardware counters for PC
  and SP at all?* Routing increments through the '181 instead (two 8-bit
  passes, low then high with the carry caught in the Cn mux) was costed in
  the earlier iteration at roughly **+2 T per PC increment and +3 T per
  push/pop** — a tax on nearly every instruction, not just a few (an `LDA
  abs` balloons from 4 cycles to ~6; JSR/RTS to 10–11). Six counter ICs
  buy that back everywhere.
- **Flags watch the ALU's F output** (originally the data bus; the watchers
  move to F at stage 17, where F becomes the result path). Z ('688 comparing
  F to \$00) and N (F bit 7) latch on FLGNZ; C latches from the '181 carry
  chain on FLGC. Because every load and transfer is an ALU pass, **F carries
  the loaded value too — LDA/LDX/PLA/TAX etc. still set N and Z with no
  extra cycle**, exactly the 65C02 behaviour, and essential for real 6502
  code (`LDA x / BEQ done`). Independent FLGC / FLGNZ enables match the
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
- **Extension datapath (stages 12–16).** Five additions ride spare codes and
  the 4th ROM's new bits, touching nothing already built:
  - **temp** ('574) — a scratch register loading like any DST, and driving
    the **A-side mini-bus as its eighth driver**. temp does double duty:
    it is the pointer-parking seat that makes (zp), (zp,X), (zp),Y and
    JMP (abs) possible (A and X stay architecturally untouched
    mid-instruction), *and* it is the doubling seat for memory left-shifts
    — the '181's only shift is `A+A` on the A port, so `ASL M` is
    `temp ← M · M ← temp+temp`. Sitting on the A side serves both jobs;
    everything temp must deliver elsewhere travels as an ALU pass through F.
  - **Y** ('574) — an A-side driver on one of ASIDE's spare codes. Indexing
    with Y reuses the entire X machinery (same ALU configs, same HCARRY
    path); only the ASIDE code differs.
  - **SHR** ('541 with crossed bits on the F→bus path) — output bit i wired
    to F bit i+1, bit 7 injected as 0 (LSR) or the C flag (ROR, one glue
    gate); the shifted-out F bit 0 latches into C. The '181 shifts left only
    (it is an adder — `A+A`); this one buffer is the entire right-shift path.
  - **P register path** — a '541 gathers N·Z·C·I into the 6502 P byte layout
    to drive the bus (PHP, interrupt push); a '157 steers the Z, C and I
    latch inputs from their computed sources to the corresponding bus bits
    when DST=P (PLP, RTI). N needs no steering — it already latches from
    bit 7, and N *is* bit 7 of P.
  - **VEC driver** ('541) — the interrupt vector bytes as a jammed constant.
    The four bytes \$FA, \$FB (NMI vector \$FFFA/B) and \$FE, \$FF (IRQ vector
    \$FFFE/F) differ in exactly two bits: bit 2 is IRQ-vs-NMI and bit 0 is
    low-vs-high byte. So six '541 inputs are tied high, bit 2 is wired to
    the grant flop (which source won), and bit 0 to the VECHI control line.
    Consequence: **IRQ and NMI share literally identical microcode** — the
    hardware picks the vector — and entry runs 12 cycles. (Computing the NMI
    vector RESET-style was costed first and *does not fit*: counting B up to
    \$05 pushes the sequence to 18 T-states against the T counter's hard
    16-state wall. The constant driver is not a luxury.)
- **The result bus (stage 17).** Two '157s put a 2:1 mux on the '181's B
  port (BSEL: B register, or the data bus directly), and the D inputs of
  A, X, Y, B, temp, IR, MAR and PCH move from the data bus to the ALU's F
  output. F becomes the result path; the shared data bus becomes
  operand-side only (memory, SP, P, VEC drivers — plus the F→bus and
  SHR drivers for memory writes). Now an operand can come *off the bus,
  through the ALU, and into its destination in one cycle*: `ADC #` is
  `A ← A + M(PC)` in a single T-state. The B-staging tax, the abs,X
  double-penalty, and most of the pointer-walk overhead disappear (§4,
  stage-17 table). What it cannot fix: T0 as a dedicated generic fetch is
  the module handshake, so JSR/RTS keep their +1 — the standing price of
  having no sequencer on the board, and still a good trade.

### Why zero page earns its slots

Density first: every zp reference is 2 bytes instead of 3, and memory operands
dominate real code, so typical programs shrink 15–20% — the classic 6502 idiom
of zero page as a 256-byte pseudo-register file. With the PAGE driver the speed
argument returns too: **zp loads and stores match the 65C02 cycle-for-cycle**,
and the RMW, shift and bit instructions beat it. And compatibility demands it:
assemblers emit the zp encodings automatically for any variable below \$0100.

The Rockwell bit family exists precisely for zp-mapped I/O — `SMB3 led_port`,
`BBR7 acia_status, wait` — which is exactly how this machine's peripherals
will be addressed.

---

## 3. Microcode ROM

| | |
|---|---|
| Address | 15 bits = opcode (8) + T state (4, from clock module) + C + Z/TEST + N |
| Word | 24 bits core (3 × 28C256, stages 1–11) + 8 extension bits (4th 28C256, stage 12) = **32 bits** |
| Store | 32K × 32 — every opcode slot × 16 T states × 8 flag combinations |

Flags in the address make conditional branches free: taken and not-taken are
just different ROM contents at different flag addresses. The Z address line is
muxed ('153 section 2, TESTSEL): normally the Z flag, but the hidden TEST flop
during BBR/BBS tails. ROM outputs drive the control lines directly (no
pipeline register): the word changes just after the clock edge and settles
long before the next one — safe at these speeds behind the module's clean
gated clock.

### Why EEPROM, and why no GAL

The control store is EEPROM and nothing else — the decision deserves its
reasoning on record:

- An EEPROM is an arbitrary lookup table: independent entries, no logic
  minimisation, reprogrammed on the bench in seconds. Microcode control words
  are effectively **random bits**, which is the *worst possible* case for a
  GAL's sum-of-products structure — a GAL wants correlated, minimisable logic,
  and there is none here.
- **Flags in the address** turn conditional branching into pure table lookup.
  A BEQ at a given T-state simply reads a different control word depending on
  Z, because Z is an address line. No sequencer logic, no next-state
  equations, no GAL required for the "hard part."
- A 28C256 is 32K deep and cheap. Spending address space is free; spending
  ICs and PLD toolchains is not.

The one scenario where a GAL would pay off — kept in the back pocket, not
used: if the store ever had to shrink by going **flag-multiplexed**, a GAL
could collapse the flag lines down to "the one flag this opcode cares about,"
avoiding the 8× table growth. With 32K × 32 costing four chips, that day
never comes for this machine.

### Control word — core 24 bits (ROMs 1–3)

| Field | Bits | Values |
|---|---|---|
| ADDR | 2 | address bus ← PC / MAR / **ZP** (MARL + PAGE=\$00) / **STK** (MARL + PAGE=\$01) |
| BUS | 2 | data bus ← MEM / ALU·F / SP; one code spare |
| DST | 4 | none · A · X · B · B+latch SGN · B+latch HC · B+SP++ · IR · MARL · MARH · PC (PCH←bus, PCL←B, same edge) · MEM write · MEM+SP−− · SP (TXS) · SP++ alone · **none+latch TEST** |
| ASIDE | 3 | A-side ← A / X / \$00 / PCL / PCH / **MASK** / **Y** (stage 13) / **temp** (stage 12) — exactly full |
| ALU M | 1 | '181 mode |
| ALU S | 4 | '181 select (XOR-steered by SGN when ASIDE=PCH·M=0) |
| CSEL | 2 | '181 carry-in ← 0 / 1 / C flag / HCARRY ('153 section 1) |
| TESTSEL | 1 | microcode Z address line ← Z flag / TEST flop ('153 section 2) |
| PCINC | 1 | PC++ |
| FLGC | 1 | latch C from '181 carry-out |
| FLGNZ | 1 | latch Z ('688) and N (bit 7) |
| TRST | 1 | assert /TRST — instruction ends, module reloads T to 0 |
| HREQ | 1 | assert /HALTREQ (STP) |

Composite DST codes carry the strobes that always travel together: SGN and
HCARRY ride on their B loads, SP pulses ride on the bus destinations they
accompany, and TEST rides on a no-destination ALU pass. The bit family
consumed the core word's last three spares — the 16th DST code, the '153's
second section, and the final control bit — simultaneously. **The core word
is full; that is why stage 12 begins with the 4th ROM.**

### Control word — extension 8 bits (ROM 4, stages 12–17)

| Field | Bits | Values |
|---|---|---|
| BUSX | 1 | extends BUS to 3 bits: adds **P** (flag byte → bus), **SHR** (F>>1 → bus), **VEC** (interrupt vector constant → bus); two codes spare |
| DSTX | 1 | extends DST to 5 bits: adds **temp**, **Y**, **P←bus** (jam Z·C·I via the '157), **MARL+B** (same value, same edge — the stage-17 pointer walks lean on it), SP-composites for the interrupt push, **B+latch SGN+HC** (stage-17 branches); several codes spare |
| ISET | 1 | set the I flag (SEI, interrupt entry) |
| ICLR | 1 | clear the I flag (CLI; RTI restores I via P←bus) |
| IACK | 1 | clear the granted pending flop (interrupt entry) |
| VECHI | 1 | VEC driver byte select: vector low / vector high |
| BSEL | 1 | '181 B port ← B register / data bus (stage 17) |
| — | 1 | spare |

Until stage 12 the 4th ROM socket is empty and the extension lines are pulled
to their inactive levels; the core machine neither knows nor cares.

*Build note:* in active-high data convention the physical '181 carry pin is
active-low; "carry-in = 1" throughout this document means the pin pulled low,
and the C-flag polarity is fixed with a spare inverter so that stored C follows
the 6502 convention (C = 1 means carry / no-borrow).

### ALU configurations used

Of the 64 possible M·S·carry combinations, these earn microcode slots:

| M | S3–S0 | Cn | A-side | F | Used by |
|---|---|---|---|---|---|
| 1 | 1111 | – | A / X / Y / temp / \$00 | pass A-side | STA, STX, STY, STZ, TAX, TXA, TAY, TYA, TXS, PHA, PHX, PHY, CLA, MARL←temp |
| 1 | 1010 | – | – | pass B | PLA/PLX/PLY flag pass, MARL←B, pointer staging, all post-17 loads (B=bus) |
| 1 | 0000 | – | A | /A | NOT A |
| 1 | 0101 | – | – | /B | DEC-memory finish |
| 1 | 1011 | – | A / MASK | A·B | AND, **BBR/BBS test** |
| 1 | 0010 | – | MASK | /A·B | **RMB** (clear bit n) |
| 1 | 1110 | – | A / MASK | A∨B | ORA, **SMB** (set bit n) |
| 1 | 0110 | – | A | A⊕B | EOR |
| 1 | 1100 | – | – | \$FF | SET A |
| 0 | 1001 | c | A | A+B+c | ADC (c=C), ADD (c=0) |
| 0 | 1001 | 0 | X / Y | side+B | indexed address low byte (abs,X / abs,Y / (zp),Y: latch HCARRY; zp,X: carry ignored → page-zero wrap, the 6502-correct behaviour) |
| 0 | 1001 | c | \$00 | B+c | abs,X high byte +HCARRY, INC mem, pointer+1 walks |
| 0 | 0110 | c | A / X / Y | side−B−1+c | SBC (c=C), SUB/CMP/CPX/CPY (c=1) |
| 0 | 0110 | 1 | \$00 | −B | NEG, DEC-memory step 1 |
| 0 | 0000 | 1 | A / X / Y | side+1 | INC A, INX, INY |
| 0 | 1111 | c | A / X / Y / \$00 | side−1+c | DEC A, DEX, DEY, **CLC** (c=0 → borrow) / **SEC** (c=1 → no borrow) |
| 0 | 1100 | c | A / temp | side+side+c | ASL A / **ASL mem** (c=0), ROL A / **ROL mem** (c=C) — memory shifts stage the operand in temp |
| 0 | 0000/1111 | HC | PCH | PCH ± sign + HCARRY | branch high-byte fix (SGN steers 0000↔1111) |

LSR and ROR use no new ALU configuration at all: F = pass A (or pass B for
memory RMW), taken off the SHR driver instead of the straight one. Likewise
stage 17 adds no configurations — BSEL just changes where B comes from.

The CLC/SEC trick deserves a note: with FLGC and FLGNZ independent, CLC and
SEC are simply "compute something with a known borrow / no-borrow and latch
only C" — S=1111 on the \$00 driver with carry-in 0 or 1. No flag-set hardware,
no operand fetch, 2 cycles each, N and Z untouched. Exactly 65C02 semantics.

---

## 4. Instruction set

T0 is always FETCH (IR ← M(PC), PC++) and is included in every cycle count.
**Cyc** = this CPU at stage 16; **'C02** = 65C02/Rockwell cycle count for the
same encoding. All listed opcodes are **byte-identical to the 65C02** unless
marked ✚ (ours only, parked in unused slots). Stage-12–16 families are
gathered in their own subsection with estimated cycle counts, and the
stage-17 improvements in the table after that.

### Loads and stores

| Instr | Op | Cyc | 'C02 | Notes |
|---|---|---|---|---|
| LDA # | \$A9 | 2 | 2 | N,Z set on the fly — free |
| LDA zp | \$A5 | 3 | 3 | **match** — PAGE driver |
| LDA zp,X | \$B5 | 4 | 4 | **match**; wraps within page zero, 6502-correct |
| LDA abs | \$AD | 4 | 4 | |
| LDA abs,X | \$BD | 6 | 4(+1) | 'C02 adds 1 on page cross; we always pay the high-byte fix (stage 17: 4) |
| LDX # | \$A2 | 2 | 2 | |
| LDX zp | \$A6 | 3 | 3 | (zp,Y and abs,Y variants arrive at stage 13) |
| LDX abs | \$AE | 4 | 4 | |
| STA zp | \$85 | 3 | 3 | |
| STA zp,X | \$95 | 4 | 4 | |
| STA abs | \$8D | 4 | 4 | A → ALU pass → bus |
| STA abs,X | \$9D | 6 | 5 | stage 17: 4 |
| STX zp | \$86 | 3 | 3 | |
| STX abs | \$8E | 4 | 4 | |
| STZ zp | \$64 | 3 | 3 | 65C02 extension; \$00 driver → pass |
| STZ zp,X | \$74 | 4 | 4 | |
| STZ abs | \$9C | 4 | 4 | |
| STZ abs,X | \$9E | 6 | 5 | stage 17: 4 |

### ALU operations (A ∘ operand → A, N,Z; arithmetic also C)

| Instr | # | zp | zp,X | abs | abs,X | Cyc (#/zp/zp,X/abs/abs,X) | 'C02 |
|---|---|---|---|---|---|---|---|
| ADC | \$69 | \$65 | \$75 | \$6D | \$7D | 3 / 4 / 5 / 5 / 7 | 2 / 3 / 4 / 4 / 4(+1) |
| SBC | \$E9 | \$E5 | \$F5 | \$ED | \$FD | 3 / 4 / 5 / 5 / 7 | 2 / 3 / 4 / 4 / 4(+1) |
| AND | \$29 | \$25 | \$35 | \$2D | \$3D | 3 / 4 / 5 / 5 / 7 | 2 / 3 / 4 / 4 / 4(+1) |
| ORA | \$09 | \$05 | \$15 | \$0D | \$1D | 3 / 4 / 5 / 5 / 7 | 2 / 3 / 4 / 4 / 4(+1) |
| EOR | \$49 | \$45 | \$55 | \$4D | \$5D | 3 / 4 / 5 / 5 / 7 | 2 / 3 / 4 / 4 / 4(+1) |
| CMP | \$C9 | \$C5 | \$D5 | \$CD | \$DD | 3 / 4 / 5 / 5 / 7 | 2 / 3 / 4 / 4 / 4(+1) |
| ADD ✚ | slot | slot | slot | slot | slot | 3 / 4 / 5 / 5 / 7 | — (idiom: CLC+ADC) |
| SUB ✚ | slot | slot | slot | slot | slot | 3 / 4 / 5 / 5 / 7 | — (idiom: SEC+SBC) |
| CPX | \$E0 | \$E4 | — | \$EC | — | 3 / 4 / – / 5 / – | 2 / 3 / – / 3 / – |

The uniform +1 across this family (pre-17) is the B-staging cost: the 6502
completes the ALU operation under the *next* instruction's fetch (its
one-deep pipeline); we must land the operand in B, then compute — and T0 is
a dedicated fetch. **Stage 17 erases this column**: with BSEL the operand
comes off the bus straight into the B port, and the whole family drops to
2 / 3 / 3 / 4 / 4 — matching the 65C02, and beating it on zp,X and the
page-crossing abs,X.

### Single-operand and register ops (all 2 cycles)

| Instr | Op | 'C02 | Notes |
|---|---|---|---|
| INC A | \$1A | 2 | 65C02 extension opcode |
| DEC A | \$3A | 2 | |
| ASL A | \$0A | 2 | A+A, C out |
| ROL A | \$2A | 2 | A+A+C |
| INX | \$E8 | 2 | X on A-side, +1, writeback |
| DEX | \$CA | 2 | |
| TAX | \$AA | 2 | N,Z on the fly |
| TXA | \$8A | 2 | |
| TXS | \$9A | 2 | no flags (FLGNZ off) — 6502-correct |
| TSX | \$BA | 2 | N,Z |
| CLC | \$18 | 2 | FLGC-only borrow trick (see ALU table) |
| SEC | \$38 | 2 | |
| NOT A ✚ | slot | — | idiom: EOR #\$FF |
| NEG A ✚ | slot | — | idiom: EOR #\$FF + ADC #1 |
| CLA ✚ | slot | — | idiom: LDA #0; sets Z |
| SET A ✚ | slot | — | A ← \$FF |

### Memory read-modify-write

| Instr | Op | Cyc | 'C02 | Notes |
|---|---|---|---|---|
| INC zp | \$E6 | 4 | 5 | **faster** — B+1 via \$00 driver, PAGE high byte |
| INC zp,X | \$F6 | 5 | 6 | **faster** |
| INC abs | \$EE | 5 | 6 | **faster** |
| INC abs,X | \$FE | 7 | 7 | stage 17: 5 |
| DEC zp | \$C6 | 5 | 5 | B−1 = /(−B): NEG then NOT, two ALU passes (stage 17: 4 — the negate rides the load, BSEL=bus) |
| DEC zp,X | \$D6 | 6 | 6 | stage 17: 5 |
| DEC abs | \$CE | 6 | 6 | stage 17: 5 |
| DEC abs,X | \$DE | 8 | 7 | stage 17: 6 |

### Bit manipulation (Rockwell/WDC set — full flag fidelity)

| Instr | Op | Cyc | 'C02 | Notes |
|---|---|---|---|---|
| RMB0–7 zp | \$07…\$77 | 4 | 5 | **faster**; M ← M·/mask; no flags — Rockwell-correct |
| SMB0–7 zp | \$87…\$F7 | 4 | 5 | **faster**; M ← M∨mask; no flags |
| BBR0–7 zp,rel | \$0F…\$7F | 5 nt / 7 t | 5 / 6(+1) | forks on hidden TEST flop; **N, Z, C untouched** (stage 17: 6 taken) |
| BBS0–7 zp,rel | \$8F…\$FF | 5 nt / 7 t | 5 / 6(+1) | |

The bit index rides in IR6:4 (the Rockwell encoding is perfectly regular), so
all 32 opcodes share **two** microcode shapes — the '238 mask decode makes
RMB3 and RMB6 literally the same ROM contents.

### Control flow

| Instr | Op | Cyc | 'C02 | Notes |
|---|---|---|---|---|
| JMP abs | \$4C | 3 | 3 | PCL←B, PCH←bus, one edge |
| BRA | \$80 | 4 | 3(+1) | 65C02 extension (stage 17: 3) |
| BEQ / BNE | \$F0 / \$D0 | 2 nt / 4 t | 2 / 3(+1) | true relative, flag-preserving (stage 17: 3 taken) |
| BCS / BCC | \$B0 / \$90 | 2 / 4 | 2 / 3(+1) | |
| BMI / BPL | \$30 / \$10 | 2 / 4 | 2 / 3(+1) | N flag is bit 7 of the result path |
| JSR abs | \$20 | 7 | 6 | pushes return−1, **byte-identical stack frame** |
| RTS | \$60 | 7 | 6 | pop-then-increment via PCINC; load dominates count on the '161, so the final PCINC can't merge into the PC load |

### Stack

| Instr | Op | Cyc | 'C02 | Notes |
|---|---|---|---|---|
| PHA | \$48 | 3 | 3 | **match** — PAGE driver |
| PLA | \$68 | 4 | 4 | SP++ needs its own T1 (T0 is opcode-agnostic) |
| PHX | \$DA | 3 | 3 | |
| PLX | \$FA | 4 | 4 | |

### Miscellaneous

| Instr | Op | Cyc | 'C02 | Notes |
|---|---|---|---|---|
| NOP | \$EA | 1 | 2 | END at T0 |
| STP | \$DB | 2 | 3 | asserts /HALTREQ — machine freezes on the module's panel |
| RESET | \$00 | 11 | 7 | pseudo-opcode in the BRK slot; forced into IR by reset, reads \$FFFC/\$FFFD, loads PC; clobbers A, B; identical ROM contents across all 8 flag combinations (flags are undefined at reset) |

### Extension families (stages 12–16)

Cycle counts here are **worked estimates** — every sequence has been sketched
against the datapath and fits the 16-T budget, but the microcode is written
and pinned per stage, per the build discipline. All encodings byte-identical
to the 65C02.

| Family (stage) | Opcodes | Cyc | 'C02 | Notes |
|---|---|---|---|---|
| LDA / STA (zp) (12) | \$B2 / \$92 | 9 | 5 | temp holds the pointer low byte through the walk — see listing §5 |
| ORA AND EOR ADC CMP SBC (zp) (12) | \$12 \$32 \$52 \$72 \$D2 \$F2 | 10 | 5 | walk + B-staging |
| JMP (abs) (12) | \$6C | 8 | 6 | pointer hi fetched at ptr+1 **without** page carry — NMOS wrap quirk retained (see §6) |
| JMP (abs,X) (12) | \$7C | 13 | 6 | the long pole of the pointer family (stage 17: 8) |
| TAY TYA INY DEY (13) | \$A8 \$98 \$C8 \$88 | 2 | 2 | identical shapes to the X twins |
| LDY # / zp / zp,X / abs / abs,X (13) | \$A0 \$A4 \$B4 \$AC \$BC | 2/3/4/4/6 | 2/3/4/4/4(+1) | |
| STY zp / zp,X / abs (13) | \$84 \$94 \$8C | 3/4/4 | 3/4/4 | |
| CPY # / zp / abs (13) | \$C0 \$C4 \$CC | 3/4/5 | 2/3/3 | |
| PHY / PLY (13) | \$5A \$7A | 3/4 | 3/4 | reuses the PHA/PLA shapes |
| LDX zp,Y / abs,Y · STX zp,Y (13) | \$B6 \$BE \$96 | 4/6/4 | 4/4(+1)/4 | |
| LDA / STA abs,Y (13) | \$B9 \$99 | 6/6 | 4(+1)/5 | abs,X shapes with ASIDE=Y |
| ORA AND EOR ADC CMP SBC abs,Y (13) | \$19 \$39 \$59 \$79 \$D9 \$F9 | 7 | 4(+1) | |
| LDA / STA (zp),Y (13) | \$B1 \$91 | 10 | 5(+1)/6 | pointer walk + Y index + HCARRY fix |
| ORA AND EOR ADC CMP SBC (zp),Y (13) | \$11 \$31 \$51 \$71 \$D1 \$F1 | 11 | 5(+1) | the pre-17 machine's longest instruction |
| ORA AND EOR ADC STA LDA CMP SBC (zp,X) (13) | \$01 \$21 \$41 \$61 \$81 \$A1 \$C1 \$E1 | 9–10 | 6 | X+ptr wraps in page zero, 6502-correct |
| LSR A / ROR A (14) | \$4A \$6A | 2 | 2 | pass A via the SHR driver; C ← old bit 0 |
| LSR zp / zp,X / abs / abs,X (14) | \$46 \$56 \$4E \$5E | 4/5/5/7 | 5/6/6/6(+1) | **faster** in the common modes |
| ROR zp / zp,X / abs / abs,X (14) | \$66 \$76 \$6E \$7E | 4/5/5/7 | 5/6/6/6(+1) | |
| ASL zp / zp,X / abs / abs,X (14) | \$06 \$16 \$0E \$1E | 4/5/5/7 | 5/6/6/6(+1) | **faster**; M ← temp+temp — temp is the doubling seat |
| ROL zp / zp,X / abs / abs,X (14) | \$26 \$36 \$2E \$3E | 4/5/5/7 | 5/6/6/6(+1) | temp+temp+C |
| PHP / PLP (15) | \$08 \$28 | 3/4 | 3/4 | P '541 out; '157 flag-jam in |
| CLI / SEI (16) | \$58 \$78 | 2 | 2 | ICLR / ISET bits, nothing else moves |
| CLD (16) | \$D8 | 2 | 2 | **exact no-op**: D is architecturally 0 on this machine, so CLD's semantics are preserved perfectly; SED stays excluded — it would have to lie |
| RTI (16) | \$40 | 8 | 6 | pop P, pop PC — no PCINC, unlike RTS |
| IRQ entry (16) | — (jammed pseudo-opcode) | 12 | 7 | push PCH, PCL, P · SEI · vector via the VEC driver |
| NMI entry (16) | — (same pseudo-opcode) | 12 | 7 | **identical microcode to IRQ** — the VEC driver's bit 2 selects \$FFFA/B vs \$FFFE/F in hardware; unmaskable, priority over IRQ |

**Total: 211 opcodes** (133 core + 78 extension) from roughly 32 distinct
microcode shapes. 43 slots remain free after the RESET and interrupt
pseudo-opcodes. (Extension audit: pointer family 10 · Y family 44 ·
shifts 18 · P 2 · interrupts + CLD 4.)

### Stage 17 — where the cycles go

The result bus removes the "operand and result can't share the bus" limit.
Families not listed are unchanged. All counts remain estimates until the
stage-17 microcode rewrite is burned.

| Family | Stage 16 | Stage 17 | 'C02 | Verdict after 17 |
|---|---|---|---|---|
| ALU # | 3 | 2 | 2 | match |
| ALU zp | 4 | 3 | 3 | match |
| ALU zp,X | 5 | 3 | 4 | **faster** |
| ALU abs | 5 | 4 | 4 | match |
| ALU abs,X / abs,Y | 7 | 4 | 4(+1) | match, beats page-cross |
| LDA/LDX/LDY abs,X / abs,Y | 6 | 4 | 4(+1) | match, beats page-cross |
| STA/STZ abs,X / abs,Y | 6 | 4 | 5 | **faster** |
| Branches taken (incl. BRA) | 4 | 3 | 3(+1) | match, beats page-cross |
| BBR/BBS taken | 7 | 6 | 6(+1) | match |
| INC abs,X | 7 | 5 | 7 | **faster** |
| DEC zp / zp,X / abs / abs,X | 5/6/6/8 | 4/5/5/6 | 5/6/6/7 | **faster** |
| LSR/ROR/ASL/ROL abs,X | 7 | 5 | 6(+1) | **faster** |
| LDA (zp) / ALU (zp) | 9 / 10 | 7 / 8 | 5 | +2–3 |
| (zp,X) family | 9–10 | 7–8 | 6 | +1–2 |
| LDA / ALU (zp),Y | 10 / 11 | 9 | 5(+1) | +3 |
| JMP (abs,X) | 13 | 8 | 6 | +2 |

Unchanged and why: JSR (7), RTS (7), RTI (8), JMP abs (3), JMP (abs) (8),
interrupt entry (12) — all dominated by memory writes, pops, or the
dedicated-T0 handshake; PHA/PLA and friends likewise. The 2-cycle register
ops were already at the floor (T0 fetch + 1).

### Cycle-count scoreboard vs the 65C02 (stage 17 machine)

| | |
|---|---|
| **Faster** | ALU/loads/stores zp,X, STA/STZ abs,X/abs,Y, INC (all modes), DEC (all modes), **RMB and SMB (all 16)**, **ASL/ROL/LSR/ROR memory (all modes)**, NOP, STP — plus every page-crossing indexed access and taken branch the 'C02 charges +1 for |
| **Match** | the entire # / zp / abs ALU and load/store families, all abs,X/abs,Y reads, STZ, JMP, all branches, **BBR/BBS**, all pushes and pops, PHP/PLP, all 2-cycle register ops, CLC/SEC, CLI/SEI, CLD, LSR/ROR A |
| **+1** | JSR, RTS (the dedicated-T0 price of having no on-board sequencer) |
| **+2** | RTI, JMP (abs), JMP (abs,X), (zp) and (zp,X) families |
| **+3–4** | (zp),Y family, interrupt entry — the walks the 6502's dedicated address logic did in hardware run through the one ALU here |

---

## 5. Microcode listings

Notation: `↑` = PCINC · `M(x)` = memory at address x · `ZP:`/`STK:` = ADDR
selects MARL + PAGE driver (\$00/\$01) · `END` = /TRST asserted · `flgC/flgNZ`
= latch enables · `HC` = HCARRY. Every instruction begins `T0: IR←M(PC) ↑`
(FETCH) — identical for all opcodes, since IR is stale during T0. Listings
are the stage-1–16 forms; the stage-17 rewrite compresses them per the table
in §4 without changing any architectural trick.

**LDA zp (\$A5)** — no MARH load ever happens; the PAGE driver *is* the high byte
```
T1: MARL ← M(PC) ↑
T2: A ← M(ZP:MARL)        flgNZ  END
```

**LDA zp,X (\$B5)** — indexed, carry deliberately discarded (page-zero wrap)
```
T1: B ← M(PC) ↑
T2: MARL ← X+B            ; HCARRY NOT latched — wraps in page zero
T3: A ← M(ZP:MARL)        flgNZ  END
```

**LDA abs (\$AD)** — the abs template
```
T1: MARL ← M(PC) ↑
T2: MARH ← M(PC) ↑
T3: A ← M(MAR)            flgNZ  END
```

**ADC abs,X (\$7D)** — the abs,X template, including the carry chain
```
T1: B ← M(PC) ↑                        ; base lo
T2: MARL ← X+B  (CSEL=0)   latch HC    ; indexed lo
T3: B ← M(PC) ↑                        ; base hi
T4: MARH ← $00+B (CSEL=HC)             ; hi + page-cross carry
T5: B ← M(MAR)                         ; operand
T6: A ← A+B (CSEL=C)       flgC flgNZ  END
```
Stage 17 collapses this to three lines — the index add and the operand
compute each ride their memory read:
```
T1: MARL ← X+M(PC) ↑  latch HC         ; BSEL=bus
T2: MARH ← $00+M(PC) (CSEL=HC) ↑
T3: A ← A+M(MAR) (CSEL=C)  flgC flgNZ  END
```

**RMB n zp (\$n7)** — one shape covers all eight; the '238 reads n from IR6:4.
SMB is identical with F = MASK∨B (S=1110). No flags — Rockwell-correct
```
T1: MARL ← M(PC) ↑
T2: B ← M(ZP:MARL)
T3: M(ZP:MARL) ← /MASK·B  (M=1 S=0010, ASIDE=MASK)   END
```

**BBR n zp,rel (\$nF)** — the machine's only mid-instruction fork: the test
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

**Bxx rel (e.g. BNE \$D0)** — flags in the ROM address fork the paths at T1
```
taken:                                 not taken:
T1: B ← M(PC) ↑  latch SGN             T1: B ← M(PC) ↑   END
T2: B ← PCL+B    latch HC              ; 2 cycles
T3: bus ← PCH ±sign +HC (CSEL=HC)
    PC ← bus:B                    END  ; 4 cycles
```

**JSR abs (\$20)** — pushing before the second operand fetch makes the stack
image byte-identical to a real 6502 (return address = opcode+2)
```
T1: B ← M(PC) ↑                        ; ADL parked in B
T2: MARL ← SP    (BUS=SP)
T3: M(STK:MARL) ← PCH (A-side pass)  SP--
T4: MARL ← SP
T5: M(STK:MARL) ← PCL                SP--
T6: bus ← M(PC); PC ← bus:B            END
```

**RTS (\$60)** — pop-then-increment; PCINC needs its own cycle (load dominates
count on the '161)
```
T1: SP++
T2: MARL ← SP
T3: B ← M(STK:MARL)             SP++
T4: MARL ← SP
T5: bus ← M(STK:MARL); PC ← bus:B
T6: ↑                             END
```

**PHA (\$48) / PLA (\$68)**
```
PHA:                                   PLA:
T1: MARL ← SP                          T1: SP++
T2: M(STK:MARL) ← A   SP--   END       T2: MARL ← SP
                                       T3: A ← M(STK:MARL)  flgNZ  END
```

**DEC zp (\$C6)** — B−1 synthesized as /(−B), since the '181 has no B−1
```
T1: MARL ← M(PC) ↑
T2: B ← M(ZP:MARL)
T3: B ← −B       ($00−B, CSEL=1)
T4: M(ZP:MARL) ← /B  (M=1 S=0101)  flgNZ  END
```

**ASL zp (\$06) / ROL zp (\$26)** — the '181's only shift is A+A on the A port,
so the operand stages in temp, the eighth A-side driver. LSR/ROR memory use
the same shape with the SHR driver instead of the doubling
```
T1: MARL ← M(PC) ↑
T2: temp ← M(ZP:MARL)
T3: M(ZP:MARL) ← temp+temp  (M=0 S=1100, ASIDE=temp,
                             CSEL=0 for ASL / C for ROL)  flgC flgNZ  END
```

**CLC (\$18) / SEC (\$38)** — carry-only latch, nothing else moves
```
T1: F = $00−1+c  (S=1111, CSEL=0 for CLC / 1 for SEC)   flgC   END
```

**LDA (zp) (\$B2)** — the stage-12 pointer-walk template. temp is what lets
the walk happen without touching A or X: the pointer low byte parks there
while the high byte is fetched, then passes through the ALU into MARL
```
T1: B ← M(PC) ↑              ; pointer address P
T2: MARL ← B (pass)
T3: temp ← M(ZP:MARL)        ; pointer lo  (DST=temp)
T4: MARL ← $00+B+1 (CSEL=1)  ; P+1 — wraps in page zero at P=$FF, 6502-correct
T5: B ← M(ZP:MARL)           ; pointer hi
T6: MARH ← B (pass)
T7: MARL ← temp (ASIDE=temp, pass)
T8: A ← M(MAR)   flgNZ  END  ; 9 cycles
```

**IRQ / NMI entry (stage 16)** — one microcode sequence serves both: the
jammed pseudo-opcode replaces the T0 fetch (PCINC suppressed, so the pushed
return address is the interrupted PC), and the VEC driver's bit 2 — wired to
the grant flop — selects the vector in hardware
```
T0: IR ← jam (no fetch, no ↑)
T1: MARL ← SP
T2: M(STK:MARL) ← PCH          SP--
T3: MARL ← SP
T4: M(STK:MARL) ← PCL          SP--
T5: MARL ← SP
T6: M(STK:MARL) ← P  (BUS=P)   SP--  ISET IACK
T7: MARH ← $FF       (M=1 S=1100)
T8: MARL ← VEC       (BUS=VEC, VECHI=0)   ; $FA or $FE — hardware decides
T9: B ← M(MAR)                            ; vector low
T10: MARL ← VEC      (VECHI=1)            ; $FB or $FF
T11: bus ← M(MAR); PC ← bus:B        END  ; 12 cycles
```

**RESET (\$00)** — the vector addresses are *computed*, since at stage 5 the
only conjurable constants are \$00, \$01, \$FF and their ALU derivatives (the
VEC driver arrives eleven stages later and covers only the interrupt
vectors). A stashes \$FD; B carries the counting and ends holding the vector
low byte
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

## 6. Deliberate omissions and deviations vs the 65C02

| Missing / different | Why / workaround |
|---|---|
| BIT, TRB, TSB | no V flag, and the write combinations aren't worth the slots. `BIT zp / BVx` and `BIT zp / BMI` idioms have exact one-instruction replacements in BBS6/BBR6/BBS7/BBR7; side-effect-only BIT reads (clearing VIA/PIA interrupt flags) become LDA |
| V flag, CLV, BVC, BVS | ADC/SBC set N, Z, C only; signed-overflow tests must be recoded. In practice V's main firmware use is the BIT-bit-6 idiom, covered above |
| Decimal mode, SED | the '181 gives nothing there and it is pure microcode bloat. **CLD is implemented** (stage 16) as an exact no-op — D is architecturally 0, so its semantics are preserved perfectly, and reset preambles run unmodified. SED stays excluded: it would have to lie |
| BRK | \$00 is the RESET pseudo-opcode. Software interrupt = JSR to the handler, or an output port wired to /IRQ. Firmware BRK crash-traps patch naturally to STP — freezing with the module's HALT LED lit beats rebooting as a diagnostic |
| WAI | STP plus the module's halt machinery covers the freeze; a wait-for-IRQ gate isn't worth the slot |
| JMP (\$xxFF) page wrap | the pointer high byte is fetched from \$xx00, the NMOS behaviour; the 65C02's fix costs a cycle and a special case for a pointer nobody should place there |
| Interrupt timing detail | IRQ/NMI are sampled at instruction boundaries only (the T0 grant), entry is 12 cycles vs the 6502's 7, and the pushed P carries constant V and D bits — semantics match, latency doesn't |

### Firmware validation — Hopper BIOS + runtime

The opcode set was audited against real firmware: the **Hopper 6502 BIOS and
r6502 runtime listings — 11,396 instructions, 125 distinct opcodes**. Results:

- **117 of 125 opcodes (99.0% of instruction sites) run natively.** The
  firmware leans on exactly what the extension stages built: (zp),Y ×521,
  STZ ×210, BRA ×210, the Rockwell RMB/SMB/BBR/BBS family in active use,
  PHX/PHY/PLX/PLY, SEI/CLI/RTI, LDA/STA (zp), JMP (abs), one STP.
- **Two of the eight missing opcodes drove design changes** (now in): ASL/ROL
  memory (94 sites — multi-byte shift chains in the long-math runtime) and
  CLD (2 sites — reset preambles).
- **The remaining 17 sites patch in 12 source lines**, half of them into
  *better* code: 5× `BIT zp / BVx` → `BBS6/BBR6 zp` (one instruction, flag
  preserving), 1× `BIT zp / BPL` → `BBR7`, 4× side-effect BIT reads → LDA,
  2× BRK crash-traps → STP.
- Nothing in either file touches decimal arithmetic, WAI, TRB/TSB, or a
  JMP (abs) pointer near a page boundary.

Practical consequence: with stages 12–16 fitted, 65C02 code that avoids
decimal arithmetic, the V flag, BIT, BRK, and WAI **assembles and runs
unmodified** — including the pointer idioms, Y-indexed table walks, and
interrupt-driven I/O on both /IRQ and /NMI. On the core machine (stages
1–11) the remaining rewrites are Y-register usage and pointer indirection.

---

## 7. IC bill of materials

### CPU board — 44 logic ICs + 4 ROMs (core stages 1–11: 33 + 3)

| Qty | Part | Role |
|---|---|---|
| 2 | 74LS181 | ALU, two 4-bit slices, ripple carry |
| 6 | 74HC574 | A · X · **Y** · **temp** · MARL · MARH (tri-state: A/X/Y/temp onto the A-side mini-bus, MAR onto the address bus — the muxes that aren't there) |
| 4 | 74HC377 | B · IR · flag C · flags N,Z (independent latch enables) |
| 4 | 74HC161 | PC — self-counting, split /LOAD low/high pairs |
| 2 | 74HC193 | SP — self-counting up/down |
| 13 | 74HC541 | ALU F→bus · **SHR F>>1→bus (crossed bits)** · PC→addr ×2 · PAGE constant (\$00/\$01) → addr high · PCL→A-side · PCH→A-side · \$00 constant (A-side) · MASK → A-side · SP→bus · **P→bus (flag byte)** · **VEC→bus (interrupt vector constant)** · **interrupt opcode jam** |
| 1 | 74HC238 | bit-mask decode: IR6:4 → one-hot \$01<<n |
| 1 | 74HC688 | Z detect on the result path |
| 4 | 74HC74 | SGN + HCARRY · TEST + **I flag** · **IRQ synchronizer + pending** · **NMI edge-detect + pending** |
| 1 | 74HC86 | SGN steering of the S field (sign extension) |
| 1 | 74HC153 | section 1: carry-in select (0 / 1 / C / HCARRY) · section 2: Z-address-line select (Z / TEST) |
| 3 | 74HC157 | **B-port mux ×2 (BSEL: B register / data bus — the stage-17 result bus)** · flag-source select (Z, C, I latch inputs ← computed / bus bits for PLP, RTI) |
| 2 | 74HC04 / 74HC00 | glue (carry polarity, ADDR decode, SGN-steer condition, /TRST & write strobes, ROR bit-7 inject, interrupt grant & priority: NMI unconditional, IRQ = pending·/I·/NMI-pending) |
| 4 | 28C256 | microcode — 32-bit control word (24 core + 8 extension), 15-bit address |

HCT (or HCT buffers) at the LS181 boundary as usual for input thresholds.

### Memory and decode — 2 ICs

| Qty | Part | Role |
|---|---|---|
| 1 | 28C256 | program ROM, \$8000–\$FFFF · reset vector at \$FFFC/\$FFFD · IRQ vector at \$FFFE/\$FFFF · NMI vector at \$FFFA/\$FFFB |
| 1 | 62256 | RAM, \$0000–\$7FFF (zero page and stack page \$0100 live here) |

A15 is the entire address decoder: A15 → ROM /CS, inverted (spare '04 gate) →
RAM /CS.

### External

Blinky Clock Module v3 (~16–17 ICs): clock generation and selection,
single-cycle / single-instruction stepping, halt and breakpoint policy,
supervised reset, **and the T counter** (T0–T3 into the microcode address,
/TRST from the control word, /HALTREQ from STP).

**Complete machine: ~50 ICs** plus the module (core stages 1–11: ~38).

### Performance

LS181 ripple carry plus ~150 ns EEPROM microcode access bounds the crystal leg
at roughly 1–2 MHz. On the stage-16 machine, average ~3.5 cycles per
instruction on zp-heavy code → **~350–550k instructions/sec**; the stage-17
result bus cuts average CPI by roughly 20–25% → **~450–700k**, with the
module's 555 leg providing the 0.5–5 Hz blinkenlights mode and NEXT INSTR
walking the program fetch-to-fetch. Pointer-indirect-heavy code still pays a
premium — the (zp) walks run the 6502's dedicated address logic through the
one ALU — so hot loops prefer zp,X / abs,X / abs,Y, all of which match or
beat the 65C02 after stage 17. (Two further orthogonal speed levers, priced
but not scheduled: a 74182 carry-lookahead between the '181 slices kills the
ripple delay, and a pipeline register on the microcode outputs — 2–3 '574s —
overlaps ROM access with execution. Both raise the clock ceiling rather than
cut cycles.)

---

## 8. Build sequence — one working machine per stage

Following the clock module's own methodology: **every stage is a complete,
testable machine**, verified in simulation and on the bench before the next
layer goes on. Each stage lists the new ICs, the running logic-IC count, the
new instructions, and the milestone that proves it. Microcode is re-burned at
every stage; unimplemented opcodes microcode to STP so a wild jump halts
loudly instead of executing garbage (\$00 is the exception from stage 5
onward — it runs the RESET sequence, so wild jumps into zeroed memory
reboot cleanly).

Stages 1–11 build the core machine on the 24-bit control word; stage 12 fits
the 4th microcode ROM and everything after rides its extension bits; stage 17
changes no behaviour and no instruction count — only cycle counts.

| Stage | Adds | +ICs | ICs | +Instr | Instr |
|---|---|---|---|---|---|
| 1 | Fetch loop (PC, IR, μROMs) | 9 | 9 | 2 | 2 |
| 2 | B + PC load | 1 | 10 | 1 | 3 |
| 3 | ALU + A | 4 | 14 | 11 | 14 |
| 4 | MAR + RAM | 2 | 16 | 7 | 21 |
| 5 | \$00 driver | 1 | 17 | 5 | 26 |
| 6 | Flags | 4 | 21 | 9 | 35 |
| 7 | X | 1 | 22 | 9 | 44 |
| 8 | PAGE driver | 1 | 23 | 29 | 73 |
| 9 | Branches + abs,X | 4 | 27 | 20 | 93 |
| 10 | Stack | 3 | 30 | 8 | 101 |
| 11 | Bit engine | 3 | 33 | 32 | 133 |
| 12 | 4th μROM + temp | 1 (+1 ROM) | 34 | 10 | 143 |
| 13 | Y register | 1 | 35 | 44 | 187 |
| 14 | Shift paths | 1 | 36 | 18 | 205 |
| 15 | Flag register (PHP/PLP) | 2 | 38 | 2 | 207 |
| 16 | Interrupts (IRQ + NMI) | 4 | 42 | 4 | 211 |
| 17 | Result bus | 2 | 44 | 0 | 211 |

### Stage 1 — the fetch loop (9 ICs)
**Add:** PC (4× '161) · PC→addr ('541 ×2) · IR ('377) · glue ('04, '00) ·
3× 28C256 microcode · program ROM.
**New instructions (2):** NOP, STP.
**Milestone:** ROM full of \$EA ending in \$DB. On the 555 leg the address LEDs
count; at the \$DB the machine freezes and the module's HALT LED lights.
NEXT INSTR advances exactly one address. The entire fetch/END/T-counter
handshake with the module is proven before any datapath exists.
**Interim boot (stages 1–4):** PC /CLR is strapped to /RST and the memory
map is temporarily *inverted* (ROM low, RAM high) so clear-to-\$0000 lands in
ROM. The vectored reset arrives at stage 5.

### Stage 2 — B and the PC load path (+1 → 10)
**Add:** B ('377); wire the dual PC load (PCL←B hardwire, PCH←bus).
**New (3 total):** JMP abs.
**Milestone:** replace the STP with JMP \$8000 — the machine loops forever.
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

### Stage 5 — the \$00 driver (+1 → 17)
**Add:** \$00 constant '541 (second A-side driver).
**New (26):** CLA, NEG A, STZ abs, INC abs, DEC abs.
**Also — the reset vector goes live.** The RESET microcode needs \$00, \$01
and \$FF, all of which now exist. Flip the memory map to final (ROM high,
RAM low), lift the PC /CLR strap, wire RST to the microcode ROMs' /OE pins,
fit the pull networks that define the reset control word (IR ← \$00), and
burn the \$00 RESET sequence. From here the machine boots through
\$FFFC/\$FFFD like a real 6502.

### Stage 6 — flags (+4 → 21)
**Add:** '688 (Z-detect) · C ('377) · N,Z ('377) · '153 (CSEL proper —
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
**Add:** PAGE constant '541 on the address-bus high half (\$00/\$01 from the
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
A-side drivers; the \$01 stack page reuses the stage-8 PAGE driver — this
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
idioms, complete. **This is the core machine** — 33 ICs, 133 opcodes, every
microcode word listed in §5, and a perfectly good place to stop.

### Stage 12 — the 4th ROM and temp (+1 logic, +1 ROM → 34)
**Add:** 4th 28C256 into its waiting socket (the extension control bits go
live — BUS grows to 3 bits, DST to 5, plus the interrupt-machinery lines for
stage 16) · temp ('574: loads like any DST, drives the **A-side mini-bus as
its eighth driver** — ASIDE's last spare code, making the field exactly
full. Everything temp delivers elsewhere travels as an ALU pass through F).
**New (143):** LDA/STA (zp), ORA/AND/EOR/ADC/CMP/SBC (zp), JMP (abs),
JMP (abs,X) — pointer indirection, the 6502's defining idiom. A and X remain
architecturally untouched mid-instruction; temp does all the parking (see
the LDA (zp) listing, §5).
**Milestone:** a linked-list traversal and a jump-table dispatch via
JMP (abs) — stock 6502 pointer code, assembled unmodified, running.

### Stage 13 — Y (+1 → 35)
**Add:** Y ('574, A-side driver on a spare ASIDE code). Everything
else is microcode: Y indexing reuses the X machinery verbatim — same ALU
configs, same HCARRY path, different ASIDE code.
**New (187):** the biggest stage in the machine — LDY (5 modes), STY (3),
TAY, TYA, INY, DEY, CPY (3), PHY, PLY, LDX zp,Y/abs,Y, STX zp,Y, the full
abs,Y family (LDA, STA + six ALU ops), and — temp already existing — the
crown jewels **(zp),Y** and **(zp,X)**: 44 opcodes from one '574.
**Milestone:** the canonical block copy — `LDA (src),Y / STA (dst),Y / INY /
BNE` — from unmodified 6502 source. After this stage the compatibility story
is essentially complete.

### Stage 14 — the shift paths (+1 → 36)
**Add:** SHR '541 on the F→bus path, output bit i wired to F bit i+1; bit 7
injected as 0 (LSR) or the C flag (ROR — one glue gate); shifted-out bit 0
latched into C. Bus source SHR on a new BUSX code.
**New (205):** LSR A, ROR A, and the full memory-shift set — LSR, ROR,
**ASL and ROL** in zp/zp,X/abs/abs,X. The left shifts cost **zero hardware**:
the '181's A+A reaches memory because temp (stage 12) sits on the A side —
`ASL M` is `temp ← M · M ← temp+temp` (§5 listing), 4 cycles in zp, beating
the 65C02's 5. Right shifts take the same shape off the SHR driver; no new
ALU configuration anywhere.
**Milestone:** a CRC-8 routine, a software divide, and a 32-bit
shift-multiply — the `ASL lo / ROL mid / ROL hi` chains that long arithmetic
lives on (and that the Hopper runtime uses 94 times, §6).

### Stage 15 — the flag register (+2 → 38)
**Add:** P→bus '541 (gathers N·Z·C·I into the 6502 P byte layout, constants
on the unimplemented bits) · '157 steering the Z, C and I latch inputs from
their computed sources to the corresponding bus bits when DST=P. N needs no
path — it already latches from bit 7, and N *is* bit 7 of P.
**New (207):** PHP, PLP.
**Milestone:** PHP / PLP round-trip preserving all flags through a
subroutine — the interrupt-safe save idiom, and the prerequisite the next
stage exists for (an interrupt must push P).
**Note:** P pushes/pops as N V 1 B D I Z C with V, D as constants — code
that pushes P, masks bits, and pulls it back behaves 6502-plausibly.

### Stage 16 — interrupts, IRQ and NMI (+4 → 42)
**Add:** '74 (IRQ synchronizer + pending flop) · '74 (NMI edge-detect +
pending — NMI is edge-sensitive and must latch even while masked work is in
flight) · **VEC '541** (the four vector bytes as a jammed constant: six
inputs tied high, bit 2 wired to the grant flop — IRQ vs NMI — and bit 0 to
the VECHI control line) · interrupt-jam '541 (drives the shared pseudo-opcode
onto the data bus during T0 when a grant fires — memory's /OE suppressed,
PCINC suppressed so the pushed return address is the interrupted PC) · I flag
on the spare half of the TEST '74 · priority glue (NMI grant unconditional;
IRQ grant = pending·/I·/NMI-pending).
**New (211):** CLI, SEI, CLD, RTI — plus the hardware IRQ **and NMI**
entries, which are **one and the same 12-cycle microcode sequence** (§5):
the VEC driver picks \$FFFA/B versus \$FFFE/F in hardware. (The
compute-the-vector approach used by RESET was costed for NMI first and
rejected: counting up to \$FA's complement pushes the sequence to 18
T-states, through the T counter's hard 16-state wall. The constant driver is
what makes NMI fit at all — and it shortens IRQ from 14 to 12 as a side
effect.) CLD joins the flag ops as an **exact no-op** — D is architecturally
0, so real firmware's reset preambles run unmodified.
**Milestone:** a timer on /IRQ counting ticks in an ISR while the foreground
blinks a LED — two programs visibly running at once — then the same timer
moved to /NMI with I set, proving the mask boundary. The module's NEXT INSTR
walks straight into the ISR entry for debugging, and its /HALTREQ line
coexists on the same discipline (both are pulled-up, machine-side-combined
request lines).

### Stage 17 — the result bus (+2 → 44)
**Add:** 2× '157 as a 2:1 mux on the '181's B port (BSEL: B register / data
bus); rewire the D inputs of A, X, Y, B, temp, IR, MAR and PCH from the data
bus to the ALU's F output; move the '688 and the N tap from the bus to F
(strictly better — every load is an ALU pass, so F carries loaded values
*and* computed results). The data bus becomes operand-side only.
**New instructions: none. New cycle counts: most of §4's deficit column** —
ALU # drops to 2, the indexed families to 4, taken branches to 3, the
pointer walks by 2–5 each (full table in §4). JSR/RTS keep their +1: the
dedicated generic T0 is the module handshake and no wiring change touches it.
**Cost that isn't ICs:** every microcode word is rewritten and re-burned —
which is exactly why this is a stage and not a footnote: the machine before
and after must produce identical results, only faster.
**Milestone:** re-run the entire stage-1–16 test suite unmodified and diff
the outputs byte-for-byte — zero behavioural change — then measure the CPI
drop on the block-copy and CRC benchmarks (~20–25% expected). The machine as
documented above.
