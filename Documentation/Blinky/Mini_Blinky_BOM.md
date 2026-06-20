# Mini Blinky CPU — Consolidated BOM (W = 4)

Combines the core chip complement (`Mini_Blinky_CPU.md` §9), the clock
module (`blinky_clock_module_BOM.md`), and the hardware breakpoint
(`Blinky_Breakpoint.md`). The old §9 placeholder row **"Clock / reset /
step — 7414 + glue — ~2"** is removed; the clock module below replaces it
in full. Its **74HC14 (U2 in the clock capture)** *is* the '14 the
placeholder referred to — counted once, here.

Stock figures carried through as stated in the source docs; not
re-verified against `ChipInventory.md` (not on hand this turn).

> **Designator note.** Section A uses the master-document block names.
> Sections B/C use the standalone clock-module capture (U1–U7). Section D
> uses the `Blinky_PC_-_4Bit_-_CALL_-_Clock` capture (U9–U13, R9–R13,
> S4–S8). The U- and R-numbers in B and D are local to their own captures
> and overlap by coincidence — they are not the same parts.

---

## A. Core logic ICs

| Block | Part | Qty | Stock / sourcing |
|---|---|---|---|
| ALU | 74181 | 1 | 10 HC in transit (8 LS backup — LS needs HCT fence) |
| TOS shift register | 74194 | 1 | 10 in transit |
| TOS serial-fill mux | 74153 | 1 | ALU rev 2 |
| NOS latch | 74374 (or '273) | 1 | 16 HCT374 in transit (HC374 unobtainable) |
| Data-stack pointer | 74191 | 1 | 20 in transit (replaces unobtainable HC169) |
| Return-stack pointer | 74191 | 1 | same stock as DSP |
| Data / return / D-memory | 2114 SRAM | 3 | source / confirm |
| Program counter | 74161 | 1 | 8 on hand |
| Instruction register | 74374 (8-bit) | 1 | '374 stock as above |
| Address-source mux | 74157 | 1 | 8 on hand, 20 in transit |
| TOS source mux | 74151 | 4 | 8:1 × 4 bit; sel TOS_SRC0..2 (GAL 2). 20 on hand |
| NOS source mux | 74157 | 1 | quad 2:1; sel NOS_SRC (GAL 2). '157 stock as above |
| Return-stack data-in mux | 74257 | 1 | quad 2:1, 3-state; sel RDIN_SEL (GAL 3); /OE released on RET/R> reads. **Order — '257 not in stock** |
| NEXTPC mux | 74153 | 2 | 4:1 × 4 bit; sel PCSEL0/1 (GAL 4). 30 on hand |
| TOS write buffer | 74125 | 1 | quad 3-state buffer; TOS Q → shared data bus on STORE/OUT; /OE = DMEM_WE·IO_WR. 8 on hand |
| Data-stack write buffer | 74125 | 1 | quad 3-state buffer; NOS Q → data-stack bus on push; /OE = data-stack write. 8 on hand |
| I-memory | 8-bit EEPROM (28C16) | 1 | source / confirm |
| Flags N/Z/C | 7474 | 2 | 8 on hand |
| Decode / control | GAL16V8 | 4 | in stock; ATF16V8B as active-production alt |
| Stack-strobe glue | '04 / '00 | ~1 | derives stack /CS,/WE from the '191 pointers |

**Core logic subtotal: 30 ICs.**
(74374 ×2 = NOS + IR; 74191 ×2 = DSP + RSP; 2114 ×3; GAL16V8 ×4; 7474 ×2;
**data routing:** 74151 ×4, 74153 ×3 = serial-fill + 2× NEXTPC, 74157 ×2 =
address + NOS, 74257 ×1 = RDIN, 74125 ×2 = TOS + data-stack write buffers.)

> The breadboard bring-up board (`Blinky_PC_-_4Bit_-_CALL_-_Clock`) realises
> the PC as a '173 register + '283 adder rather than the '161 counter listed
> above — a load-NPC-each-cycle PC instead of a count-up PC. Its dual '153
> next-PC mux **is** the NEXTPC mux now itemised in this list. The
> '173 + '283 vs '161 PC choice remains a board-level realisation decision;
> the '283 adder it adds (for PC+1) is not yet folded into this core list.

---

## B. Clock module ICs

Matches `blinky_clock_module_BOM.md` (verified capture).

| Ref | Part | Qty | Stock |
|---|---|---|---|
| U1 | NE556 (bipolar) | 1 | on hand |
| U2 | 74HC14 | 1 | 12 on hand |
| U3, U6 | 74HC00 | 2 | *check stock* |
| U4 | 74HC273 | 1 | 8 on hand |
| U7 | 74HC08 | 1 | *check stock* |
| — | DS1813-5 (TO-92) | 1 | on hand |
| X1 | Can oscillator, ~1 MHz (optional, socketed) | 1 | *check stock* |

**Clock module subtotal: 6 ICs + 1 TO-92** (+ optional can).

---

## C. Clock module passives & front-panel parts

From the verified clock-module BOM. (Core logic has no passive list in the
source — only the clock module and the breakpoint are specified to this
level.)

**Resistors (¼ W):** R1 100k, R2 330R (power-LED limit), R3/R4 10k (×2),
R5–R8 330R (×4), R9/R10 4k7 (×2, 556 OUT pull-ups), R12 10k (step one-shot
clear node), RV1 100k multi-turn trimmer.
**= 11 resistors + 1 trimmer.**

**Capacitors:** C1 470n (step debounce), C3/C5/C6/C7/C8 100n (×5,
decoupling, one per IC), C4 47µF (rail bulk), C9 10µF (astable timing).
**= 8 capacitors.** (CONT pins left open — no 10n CV caps in this build.)

**LEDs:** D1/D2 green (STEP / RUN mode), D3 amber (CLK), D4 red (RST),
D8 red (supply present). **= 5 LEDs.**

**Switches / hardware:** SW1–SW4 6 mm tactile (STEP, mode-STEP, mode-RUN,
RESET); S1 supply on/off switch; J2 3-pin header + shunt (556 / CAN);
H1 2-pin power-input header; H2 4-pin output header (GND/VCC/CLK/RST).
**= 4 buttons, 1 switch, 1 jumper, 2 IO headers.**

---

## D. Hardware breakpoint (debug subsystem)

From `Blinky_Breakpoint.md`. Adds **3 ICs** + 5 switches + 5 pull-ups; the
force-STEP and run-leg gates reuse spare gates of the clock's '08, and the
matched-last sample reuses a spare '273 flip-flop — no extra package for
those.

| Ref | Part | Qty | Stock |
|---|---|---|---|
| U13 | 74HC688 (8-bit identity comparator) | 1 | 3 on hand |
| U10 | 74HC00 (edge detect + halt logic) | 1 | *check stock* |
| U11 | 74HC74 (HALT flip-flop; one FF spare) | 1 | 8 on hand (shared '74 stock) |
| S4–S8 | tactile / DIP switch (BP En + BP0–BP3) | 5 | general stock |
| R9–R13 | 10k pull-ups (for S4–S8) | 5 | general stock |

**Breakpoint subtotal: 3 ICs, 5 switches, 5 resistors.**
Reused (not added): U9 '08 gate-c (force STEP) + gate-d (gate run leg);
'273 FF6 (matched-last); PC '173 CLR rewired to the active-high RST net.

---

## E. Grand totals

| Category | Count |
|---|---|
| Core logic ICs | 30 |
| Clock module ICs | 6 |
| Breakpoint ICs (debug) | 3 |
| **Total DIP/IC** | **39** |
| Reset supervisor (TO-92) | 1 |
| Can oscillator (optional) | 1 |
| Resistors + trimmer (clock) | 11 + 1 |
| Resistors (breakpoint) | 5 |
| Capacitors (clock) | 8 |
| LEDs (clock front panel) | 5 |
| Pushbuttons (clock) | 4 + 1 switch |
| Switches (breakpoint address/enable) | 5 |
| Headers (clock) | 1 jumper + 2 IO |

The previous §9 sketch read "~18–20 core ICs," folding the clock into a
2-IC placeholder and omitting the data-path selectors entirely. Enumerated
properly — 3× 2114, 4× GAL, the doubled '374/'191, **and the data routing**
(TOS 8:1 source mux 4× '151, NOS and address quad 2:1s 2× '157, the
return-stack-in 3-state mux '257, the NEXTPC 4:1 2× '153, the serial-fill
'153, and the two '125 write-bus buffers) — the core alone is **30**; the
verified clock module adds 6 + a TO-92; the hardware breakpoint adds 3 more.
**No '14 is double-counted:** the placeholder's '14 is the clock's U2.

**Open / unverified:**
- `ChipInventory.md` not consulted this turn — stock figures are as-stated.
- The **74257** (3-state RDIN mux) is the one new part not in current stock —
  it must be ordered; the '125 write buffers are on hand (8 each).
- 74HC00, 2114, 28C16, can oscillator all flagged *source / confirm*.
- Stack-strobe glue (~1 IC) could partly draw on the clock module's spares
  (U7 gate-c/d AND, '273 FF7) but the source treats it as its own part;
  kept separate here.
- CPU front-panel LED drivers/latches not yet enumerated.
- The breakpoint is a debug aid on the bring-up board; whether it ships in
  a final Mini build is an open decision (it is cheap — 3 ICs — and works
  standalone, with no Mega).
