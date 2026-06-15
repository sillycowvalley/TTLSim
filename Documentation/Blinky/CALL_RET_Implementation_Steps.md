# Mini Blinky — CALL/RET Return-Stack Implementation Steps (W = 4)

Scope: add the return stack to `Blinky_PC_-_4_Bit` so `CALL` and `RET` execute.
Organised one IC at a time, in build order. Each entry is marked **NEW** (add the
part) or **MODIFY** (rework an existing part). Pin facts taken from the current
schematic are stated plainly; anything not yet confirmed is flagged
**PROVISIONAL**.

The PC datapath and decode front-end are in place and reused unchanged: U1 '173 (PC),
U3 '283 (+1), U5/U6 '153 (NPC mux), U4 '541 (instruction buffer), U7 GAL (Flow). Both
instruction nibbles are broken out as nets off the '541 — OP0–OP3 on pins 14/13/12/11
and L0–L3 on pins 18/17/16/15 — and the Flow GAL already decodes RET to the RTOS mux
code (`10`). What remains is the return-stack subsystem and bringing RTOS into the
NEXTPC mux.

---

## 1. U? — GAL16V8 "Pointers" / GAL3_POINTERS (NEW)

The return-stack/clock decode, a second GAL. Pinout from the `.pld`:

- Inputs: pin 2–5 = OP0–OP3, pin 6–9 = L0–L3. Tap the '541 outputs already on the
  board:
  - pin 2 ← '541 pin 14 (OP0)
  - pin 3 ← '541 pin 13 (OP1)
  - pin 4 ← '541 pin 12 (OP2)
  - pin 5 ← '541 pin 11 (OP3)
  - pin 6 ← '541 pin 18 (L0)
  - pin 7 ← '541 pin 17 (L1)
  - pin 8 ← '541 pin 16 (L2)
  - pin 9 ← '541 pin 15 (L3)
- Power: pin 20 = VCC, pin 10 = GND.
- Outputs used on this board:
  - pin 17 `RSP_EN` (active-LOW count enable) → RSP `/CTEN`
  - pin 16 `RSP_UD` (HIGH = up) → RSP direction
  - pin 15 `RDIN_SEL` (1 = PC+1, 0 = TOS) → RDIN mux select
  - pin 14 `CLK_RUN` (HIGH = run) → optional HALT gate
- Outputs unused here (no data stack / no flags on this board): pin 19 `DSP_EN`,
  pin 18 `DSP_UD`, pin 13 `C_WE`. Leave open.

Decode it implements (already verified): RSP counts **up on CALL**, **down on RET**;
`RDIN_SEL` = 1 only on CALL. The `>R`/`R>` cubes never fire here (no data stack), which
is harmless.

---

## 2. U? — 74'191 return-stack pointer "RSP" (NEW)

One '191 covers W = 4. Wire per the project's `return_stack_circuit.svg`:

- pin 4 `/CTEN` ← `RSP_EN` (Pointers GAL pin 17)
- pin 5 `D//U` ← `RSP_UD` (Pointers GAL pin 16) — see polarity note
- pin 11 `/LOAD` ← system reset net (the same `/RESET` that drives U1 '173 CLR via
  U10 pin 6); parallel inputs A/B/C/D (pins 15/1/10/9) tied to GND → load-of-zero is
  the reset
- pin 14 `CLK` ← system clock
- Q0–Q3 (pins 3/2/6/7) → return-stack RAM address `R_ADDR0–3` (step 3) and the RSP LEDs
- pins 12 `MAX/MIN`, 13 `/RCO` = NC; pin 16 VCC, pin 8 GND
- **PROVISIONAL:** the GAL defines `RSP_UD` HIGH = up, but the '191 `D//U` is LOW = up.
  Either insert an inverter on this line or flip the term and recompile the GAL.
  Resolve against the '191 datasheet before layout.
- **TTLSim:** the '191 is not modelled yet (2114 and '153 already are). The model must
  be added (five-touchpoint registration) before CALL/RET can be simulated end-to-end.

---

## 3. U? — 2114 return-stack RAM (NEW)

1K×4 SRAM, used 16-deep.

- Address: `R_ADDR0–3` from the RSP Q-outputs; A4–A9 tied to GND.
- I/O (4): the **RTOS bus** — read data out on RET, write data in on CALL.
- `/CS` and `/WE`: **derived**, not decoded — formed from `RSP_EN` / `RSP_UD` by the
  strobe glue (step 5), phase-gated so the CALL write lands mid-cycle.
- 2114 is async, no `/OE`.
- **PROVISIONAL:** exact `/CS` / `/WE` timing and the read/write bus arbitration with
  the RDIN mux (step 4) — pin senses are datasheet-derived and the project doc marks
  them provisional. Lock down against the 2114 datasheet. Pin-level reference:
  `return_stack_circuit.svg`.

---

## 4. U? — 74'157 RDIN mux (NEW)

Selects the return-stack write data.

- Select ← `RDIN_SEL` (Pointers GAL pin 15).
- Input selected on CALL (`RDIN_SEL` = 1) ← **PC+1** = U3 '283 sum outputs
  (pins 4/1/13/10 = bits 0–3).
- Other input (TOS, for a future `>R`) ← bring to a labelled stub; no data stack yet.
- Output → 2114 I/O bus during the write phase only.
- **PROVISIONAL:** the '157 has no high-Z state, so its drive onto the shared RTOS bus
  must be gated off during reads. Settle the gating with step 3/5.

---

## 5. U? — 74'00 or 74'04 stack-strobe glue (NEW)

Derives the return-stack RAM control from the pointer signals (one package, per BOM).

- Form phase-gated `/CS` (from `RSP_EN`) and `/WE` (from `RSP_UD`) for the 2114.
- Provide any inversion the RSP direction fix (step 2) or the RDIN bus gating (step 4)
  needs.

---

## 6. U5 / U6 — 74'153 NPC mux (MODIFY)

Bring RTOS into the one free mux input so RET can load it.

- The sel-`10` input (C2) on all four PC bits is tied to GND today: U5 pin 4, U5 pin 12,
  U6 pin 4, U6 pin 12.
- Rewire those four from GND to `RTOS[0..3]` off the 2114 I/O bus.
- Select lines unchanged (A = pin 14 ← Flow GAL pin 19; B = pin 2 ← Flow GAL pin 18).

After this, RET (PCSEL `10`) loads the PC from RTOS; CALL/BRANCH (PCSEL `01`) still load
the literal; sequential (`00`) and hold (`11`) are untouched.

---

## 7. U3 — 74'283 "+1" adder (NO CHANGE)

Listed for completeness. The '283 is unmodified; its sum outputs (pins 4/1/13/10) are
**tapped** as the PC+1 source for the RDIN mux (step 4). PC+1 is already present every
cycle, so CALL needs no extra arithmetic.

---

## 8. Validation

Load `calls.hex` into the EEPROM and single-step:

- `02 CALL 06` → RSP pushes 3, RSP = 1
- `06 CALL 0A` → RSP pushes 7, RSP = 2
- `0A NOP`, `0B RET` → pop 7, PC ← 7, RSP = 1
- `07 NOP`, `08 RET` → pop 3, PC ← 3, RSP = 0
- `03 NOP`, `04 BRA 02` → back to loop

Watch the RSP LEDs reach 2 and unwind to 0 each pass, and the PC LEDs follow the
push/pop addresses. Reset must show RSP = 0.
