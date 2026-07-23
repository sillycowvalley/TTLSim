# 8080 Build — Rung 1 & Rung 2 Reference

Companion to `8080A_instruction_set.md`. Covers the first two bring-up
stages: the fetch engine (Rung 1) and the minimum executing machine
(Rung 2: NOP / JMP / HLT). Status: designed on paper, **not yet simulated
or built** — items marked ⚠ are reasoned but unverified.

---

## Shared datapath decisions

- **16-bit address bus** (A[15:0]), multi-source, three-state, exactly one
  driver at a time. A15 reaches the ROM header but is not decoded there —
  every address mirrors across 32 K. Harmless while code lives low; a wrong
  jump target may *appear* to work.
- **8-bit data bus** (D[7:0]). At these rungs it has one source (the ROM),
  which provides its own three-state gating — no '541 needed in front of
  it. Gating on the data side is destination load enables, not source
  output enables.
- **PC = two '273s.** Clocked on every rising CLK edge, no load enable.
  `/MR` comes straight from the clock module's `/RST`, giving reset to
  0x0000 for free. PC's D inputs come from the incrementer output — always.
- **Incrementer = four '283s.** All sixteen B inputs tied to one `DEC`
  line; Cin = `INC`. `INC=1, DEC=0` → +1; `INC=0, DEC=1` → −1;
  both low → pass-through. Both high is also pass (+0xFFFF+1), so no
  illegal combination exists. Its A input reads the **address bus**, not
  PC directly; its output feeds only PC's D inputs at these rungs.
  (Rung 4 note: give the output a tap toward the address bus for
  PUSH-to-SP−1 later.)
- **The PC trick:** because the incrementer reads the bus, PC needs no
  hold or load control. "Hold" = pass-through with PC driving the bus
  (PC ← PC). "Jump" = pass-through with WZ driving the bus (PC ← WZ).
  "Advance" = INC with PC driving the bus. The address-source select *is*
  the PC control.
- **ROM = Code & IR module, single lane** (U2 28C256 + U4), '373 fitment,
  LE on its pullup (transparent), `/OE` grounded — sole data-bus source,
  always driving. ZIF socket carries the program.
- **One T-state = one M-cycle.** One memory access per state; no
  sub-cycle sequencing. The T counter is the clock module's own '161,
  reloaded through `/TRST`.

### Timing budget (worst-case HC @ 5 V) ⚠ reasoned, not measured

Longest loop: PC '273 clock-to-Q (~35 ns) + '541 (~12 ns) + four-'283
ripple carry (~80 ns) + '273 setup (~12 ns) ≈ **140 ns**. ROM path:
'273 + '541 + EEPROM 150 ns + '377 setup ≈ **210 ns**. Both are orders
of magnitude inside the clock module's 2.5 kHz ceiling; the design never
approaches its own limits at panel speeds.

---

## Rung 1 — the fetch engine

**Boards/parts:** clock module · ROM (as above) · PC ('273 ×2) ·
incrementer ('283 ×4) · PC address buffers ('541 ×2) · data-bus probe.

No IR, no GAL, no WZ. Straps: `INC` = VCC, `DEC` = GND, `/PCOE` = GND
(PC permanently drives the bus).

**Observability:** fit the I/O module in its **'373 live-probe fitment**
(U2 = '373, H2.2/LE strapped high, H2.3//OE strapped low) on the data
bus — the LEDs track D combinationally, no clocking. This is a
documented fitment of that board, reused as-is.

**What it does:** free-runs (or single-steps) through memory; the LEDs
show each byte as PC walks the ROM. `RESET` forces 0x0000 and the walk
restarts.

**What it proves:** clock distribution, `/RST` polarity and release, the
PC → bus → incrementer → PC loop, ROM addressing, and the address
mirroring behaviour — all with a failure mode ("LEDs don't count") that
needs no instrument beyond eyes. The DMM-average trick from the clock
module's bring-up notes (~2.5 V = toggling) localises any stuck line.

---

## Rung 2 — NOP / JMP / HLT

**Adds:** IR ('377, CP = CLK, `/E` = `/IRCE`) · WZ (two '373s) ·
sequencer GAL (22V10) · the strapped Rung-1 controls move to GAL outputs.

**WZ:** both latches' D inputs sit on D[7:0]; `LE_Z` / `LE_W` select
which captures. Their three-state outputs drive the address bus directly
— Z → A[7:0], W → A[15:8] — under one shared `/WZOE`. No '541s on this
source; the '373s bring their own.

### State machine

Fetch happens in state 0 of every instruction, unconditionally. IR loads
at the edge ending state 0, so decode is live from state 1 onward. During
the first state 0 after reset IR holds garbage — harmless, because **no
state-0 control depends on IR**.

| State | NOP (and all undecoded opcodes) | JMP (0xC3) | HLT (0x76) |
|---|---|---|---|
| 0 | addr=PC · INC · fetch→IR | same | same |
| 1 | `/TRST` (done) | addr=PC · INC · D→Z (`LE_Z`) | `/HALTREQ` · `/TRST` |
| 2 | — | addr=PC · INC · D→W (`LE_W`) | — |
| 3 | — | addr=WZ (`/WZOE`) · pass · PC←WZ · `/TRST` | — |

NOP = 2 cycles, JMP = 4, HLT = 2 then frozen. Cycle counts are free
under the contract. All 253 opcodes other than 0xC3 and 0x76 execute as
NOP at this rung.

**HLT** asserts `/HALTREQ` to the clock module (edge-detected entry
there, so RUN always escapes) *and* `/TRST` in the same state: on
resume, the next CLK edge reloads T to 0 and execution continues with
the following instruction — 8080-like continue-after-halt.
⚠ Entry timing: the request rises mid-state and the halt flop samples on
free-running /OSC, so whether the edge ending state 1 occurs before the
clock gates off depends on phase. `/TRST` being already asserted makes
both orderings land in a fetch on resume, but this is the single
sharpest thing to watch in simulation.
⚠ This HLT is a stepping stone: the contract requires HLT to exit on
interrupt, which the clock module's halt flop cannot do. It gets rebuilt
as a CPU-side spin state later; build it knowing that.

**Structural bus safety:** `/PCOE` and `/WZOE` are complements by
construction (below) — exactly one address source enabled in every
state, with no reachable contention.

### GAL equations (22V10, combinational)

Inputs (11): `CLK`, `IR7..IR0`, `T1`, `T0` — T1/T0 are bits 1:0 of the
clock module's T counter. Outputs (8): `/PCOE`, `/WZOE`, `/IRCE`,
`/TRST`, `/HALTREQ`, `INC`, `LE_Z`, `LE_W`. `DEC` stays a board strap to
GND at this rung.

Notation: `&` AND, `#` OR, `!` NOT. Each output is written as its
**assertion condition** with the active level stated — translate
polarity per your compiler's convention rather than trusting mine.

```
/* --- instruction decode --- */
JMP =  IR7 &  IR6 & !IR5 & !IR4 & !IR3 & !IR2 &  IR1 &  IR0 ;  /* 0xC3 */
HLT = !IR7 &  IR6 &  IR5 &  IR4 & !IR3 &  IR2 &  IR1 & !IR0 ;  /* 0x76 */

/* --- state decode (T counter bits 1:0) --- */
S0 = !T1 & !T0 ;
S1 = !T1 &  T0 ;
S2 =  T1 & !T0 ;
S3 =  T1 &  T0 ;

/* --- outputs: condition under which each is ASSERTED --- */
/PCOE    (low  when) :  !(JMP & S3)                    /* default source  */
/WZOE    (low  when) :   JMP & S3                      /* complement      */
/IRCE    (low  when) :   S0                            /* fetch window    */
/TRST    (low  when) :  (!JMP & S1) # (JMP & S3)       /* NOP,HLT end @1; JMP @3 */
INC      (high when) :   S0 # (JMP & S1) # (JMP & S2)
LE_Z     (high when) :   JMP & S1 & CLK                /* closes on CLK fall */
LE_W     (high when) :   JMP & S2 & CLK
```

**`/HALTREQ` must be open-drain**, because the net is shared with the
clock module's panel button and its 4k7 pull-up — a totem-pole GAL
output driving high would fight a pressed button. Use the 22V10's
per-pin output-enable product term:

```
HALTREQ.OE = HLT & S1 ;    /* drive only while requesting */
HALTREQ    = 'b'0 ;        /* and drive low               */
```

Pin floats (pull-up wins) at all other times.

### Notes and traps

- **Latch-enable phasing.** `LE_Z`/`LE_W` are gated with CLK-high, so
  the '373s are transparent while D settles and close on the falling
  edge — the same "data settles in the high phase, the low phase belongs
  to the write" contract the register file module imposes. Keeping one
  phase discipline machine-wide is deliberate.
  ⚠ The relationship between latch closure (CLK fall) and T/PC advance
  (CLK rise) gives half a period of margin each way on paper; verify in
  simulation before copper.
- **State aliasing.** The equations use only T[1:0], so T counter values
  4–15 alias states 0–3. `/TRST` bounds every defined instruction at
  state 3, making those values unreachable in normal operation; if a
  glitch ever produced them, the aliasing degrades to harmless re-fetch.
  Paranoia option: AND `!T3 & !T2` into S0–S3 and add a trap output.
- **First fetch after power-up.** `/RST` clears T and PC but the '377 IR
  has no clear — it powers up random. Safe anyway: state 0 ignores IR,
  and the first edge replaces the garbage with a real opcode.
- **INR/DCR are not incrementer work** (recorded here so the Rung-5
  sequencer doesn't inherit the conflation): they are 8-bit ALU
  operations that set flags. Only INX/DCX belong to the incrementer.

### Suggested first test program

```
0000:  00          NOP
0001:  00          NOP
0002:  C3 00 00    JMP 0000h      ; tight loop — PC LEDs breathe
```

then

```
0000:  00          NOP
0001:  76          HLT            ; verify halt + RUN resume
0002:  C3 00 00    JMP 0000h
```

Single-step both on the clock module; NEXT INSTR should land on each
state-0 fetch (FETCH is decoded from the module's own T counter, which
is the same counter this design reloads through /TRST).
