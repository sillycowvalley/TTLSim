# Addy CPU v0 — Minimal Executing Machine

**Contract:** given the Clock module, the Register File module (Thumby build)
and the Code & IR module already working, v0 is the smallest board that turns
them into a CPU that fetches, increments, decodes and writes back — **ten ICs
and one GAL.**

v0 is not a reduced v1. It is v1 with everything deleted that is not required
to close the fetch–execute loop. Every encoding it uses is bit-identical to
v1, so nothing burnt or written for v0 is thrown away later.

---

## What v0 executes

| Encoding | Mnemonic | Action |
|---|---|---|
| `4nii` | `ADDI rd, imm8` | rd ← rd + imm8 |
| `5nii` | `LDI rd, imm8` | rd ← imm8 |
| `FFFF` | `HLT` | freeze the clock module |

`n` = rd in IR[10:8], `ii` = imm8 in IR[7:0], zero-extended.

That is the whole instruction set, and it is enough to be a computer:

- **rd = 7 is a jump.** `LDI r7, addr` is an absolute jump into page 0.
  `ADDI r7, n` is a forward relative jump to PC+1+n (0…255).
- **`ADDI r0, 0` is a NOP** (0x4000).
- **A blank EEPROM reads 0xFFFF = HLT**, so an unprogrammed or half-programmed
  ROM stops the machine instead of running off into nonsense. Fails toward safe.

Backward relative jumps need `SUBI`, which needs the XOR stage — see
*Deliberate omissions*. Page-0 loops close with `LDI r7, addr`, which is all a
bring-up program needs.

---

## Block diagram

```
   Clock module ──CLK,RST,FETCH,T0,T1──►┌──────────┐
        ▲  /TRST, /HALTREQ  ◄───────────│  GAL1    │
        │                               │ CONTROL  │
        │                               └────┬─────┘
        │                       WE WSEL7 CIN │ /AOE /IMMOE /IRCE
        │                                    │
   ┌────┴───────────────────────────┐        │
   │      Register File (8×16)      │◄───WADDR = rd OR WSEL7  ('32)
   │                                │◄───AADDR = rd OR FETCH  ('32)
   └──QA──────────────────┬─────────┘
      │                   │  D[15:0] ◄────────────┐
      ▼                   ▼                       │
 Code & IR module      A register ('574 ×2)       │
 (ROM ×2 + IR ×2)          │ /AOE                 │
      │ IR[15:0]           ▼                      │
      └──────────► imm8 ──►ADDER ('283 ×4)────────┘
                  ('541)   Cin
```

No B register. No B-side port of the register file is used. No mux on AADDR.
No flags. No subtract.

---

## Parts — the v0 board

| Function | Parts |
|---|---|
| Adder | 4× 74HC283 |
| A operand register | 2× 74HC574 |
| imm8 buffer | 1× 74HC541 |
| WADDR / AADDR force-to-7 | 2× 74HC32 (6 gates used) |
| Control | 1× ATF22V10C |
| **Total** | **10 ICs** |

Plus: 10 k pulldown networks on the A bus and on the imm/B bus (the A bus must
read solid zero when /AOE is off — that is how `LDI` becomes `0 + imm`), one
0.1 µF per socket, 16 LEDs + resistors on the **D result bus** for
observability.

---

## Deliberate omissions, and what each costs to add back

| Omitted | Why v0 survives without it | Cost to add |
|---|---|---|
| B operand register (2× '574) | No class-00 register-register ops | +2 IC, +1 GAL output (BOE) |
| XOR subtract stage (4× '86) | No SUB/SUBI; no backward relative jumps | +4 IC, +1 GAL output (SUBTRACT) |
| Flags ('688 ×2, '74, '157) | No conditional branches | +4 IC, +1 GAL output (FLAGEN) |
| ASEL mux ('157) | AADDR is always rd — class 01 is read-modify-write | +1 IC, +1 GAL output |
| OUT register + IN buffer | LEDs sit directly on D | +2 IC, +2 GAL outputs |
| imm-high '541 (ADDIH) | Constants and jump targets limited to 0…255 | +1 IC, +1 GAL output |
| Second GAL | 8 of 10 OLMCs used; 2 spare | — |

Adding all of it back **is v1**. The v0 board is v1's PCB with sockets left
empty, if the layout is drawn that way from the start.

---

## Sequencing — unchanged from v1

Three T-states, opcode-independent.

| Signal | T0 | T1 | T2 |
|---|---|---|---|
| AADDR | 7 (FETCH forces) | rd | rd |
| WADDR | don't care | 7 (WSEL7) | rd |
| IR /CE | **active** | — | — |
| A clock | every edge | every edge | every edge |
| /AOE | off | **on** | on for ADDI, off for LDI |
| /IMMOE | off | off | **on** (class 01) |
| Cin | 0 | **1** | 0 |
| WE | 0 | **1** | class 01 |
| /TRST | — | — | **asserted** |
| /HALTREQ | — | — | HLT only |

- **T0** — AADDR forced to 7, QA = PC addresses the ROM directly. Ending edge:
  IR ← ROM, A ← PC.
- **T1** — A drives the adder with Cin = 1, D = PC+1, WADDR forced to 7,
  WE high; the write commits in CLK-low. Meanwhile AADDR shows rd; ending edge
  latches A ← rd's current value (or, if rd = 7, the in-flight PC+1 by
  write-through — which is exactly what makes `ADDI r7, n` land at PC+1+n).
- **T2** — imm8 on the B side, A on for ADDI / off for LDI, D = result,
  WADDR = rd, WE high in CLK-low. /TRST returns T to 0.

**Reset** is the v1 vector verbatim: RST forces WE = 1 and WSEL7 = 1 with all
bus sources off, so D = 0 through the pulldowns and r7 ← 0 on any CLK-low
inside the reset window.

---

## Bring-up ladder

Each rung is observable on the panel alone.

1. **Dry sequencer.** GAL + clock module only. Strap the GAL's IR inputs to
   0x4000 (ADDI). STEP and scope the grid: WE on T1 and T2, /TRST on T2,
   /IRCE on T0, a three-state lap.
2. **PC alive.** Add register file, A register, adder, both '32s. Strap the
   GAL IR inputs to 0xFFFF-minus-HLT (any class-11 pattern that is not HLT, e.g.
   0xE000) so T2 never writes. Hold RESET, release, then step: QA counts
   0, 1, 2, 3… once per lap. This is the single most valuable rung — it proves
   the register-file write contract, the force-to-7 gating, and Cin in one go.
3. **First real fetch.** Connect the Code & IR module. Burn both EEPROMs to
   all 0x4000. The machine now executes NOPs forever, three CLK flashes per
   address, with the ROM address LEDs counting.
4. **Blank-ROM safety.** Erase one EEPROM (or just pull it and let the bus
   pullups read 0xFF). Confirm the machine halts on the first instruction.
5. **First program.**

   ```
   0000  5000   LDI  r0, 0
   0001  4001   ADDI r0, 1
   0002  5701   LDI  r7, 1     ; jump back to 0001
   ```

   A binary counter on the D-bus LEDs, one count per three instructions.
   This exercises fetch, PC writeback, the immediate path, register write,
   and a jump — the entire machine.
6. **Forward relative jump and halt.**

   ```
   0000  5000   LDI  r0, 0
   0001  4001   ADDI r0, 1
   0002  4703   ADDI r7, 3     ; PC+1+3 = 0006
   0003  FFFF   HLT            ; must NOT be reached
   0004  FFFF   HLT
   0005  FFFF   HLT
   0006  FFFF   HLT            ; lands here, HALT lights
   ```

   Proves the r7-reads-as-PC+1 rule and the /HALTREQ path. NEXT INSTR walks
   it one row at a time.

After rung 6 you have a genuinely functional CPU. Everything after that is
adding instructions, and every one of them is a socket plus a GAL re-burn.

---

## Absolute maximums / cautions

- **/TRST and /HALTREQ are wired-AND nets** on the clock module — pulled up,
  drivers pull low only. The GAL drives both as tri-state (assert = drive low,
  otherwise Hi-Z). Do **not** convert these to push-pull; the clock module's
  own HALT REQ button shares /HALTREQ.
- **A-bus pulldowns are load-bearing.** Without them `LDI` computes
  `float + imm`, which will look intermittently correct and waste a day.
- Nothing edge-sensitive on QA or QB — QA feeds only the ROM address pins and
  the A register's D inputs, both sampled on CLK.
- Clock ceiling is the same ~2.5–3 MHz as v1; the ROM fetch path is unchanged.
  At panel rates none of it matters.
