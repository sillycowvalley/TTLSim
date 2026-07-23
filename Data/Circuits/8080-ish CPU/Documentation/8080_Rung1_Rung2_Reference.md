# 8080 Build — Rung 1 & Rung 2 Reference

Companion to `8080A_instruction_set.md`. Covers the first two bring-up
stages: the fetch engine (Rung 1) and the minimum executing machine
(Rung 2: NOP / JMP / HLT). Status: designed on paper, **not yet simulated
or built** — items marked ⚠ are reasoned but unverified.

Revision note: PC is now a **write-enabled register ('377 pair)** behind
the shared incrementer, with reset jammed through the ordinary write
path by a zero driver. This replaces an earlier '273 scheme whose
"PC needs no load control" property was an artefact of Rung 2's source
set — it broke the moment a non-PC-target source (HL for `MOV A,M`)
would own the address bus. The '377 scheme matches real 8080 structure:
one shared incrementer, PC as a plain register with a write select, and
RESET forcing PC through the write path rather than a clear pin.

---

## Shared datapath decisions

- **16-bit address bus** (A[15:0]), multi-source, three-state, exactly one
  driver at a time, **never floating** — the ROM is transparent and the
  incrementer's A inputs hang on it, so every state must have an owner.
  A15 reaches the ROM header but is not decoded there — every address
  mirrors across 32 K. Harmless while code lives low; a wrong jump target
  may *appear* to work.
- **8-bit data bus** (D[7:0]). At these rungs it has one source (the ROM),
  which provides its own three-state gating — no '541 needed in front of
  it. Gating on the data side is destination load enables, not source
  output enables.
- **PC = two '377s.** CP = CLK; D inputs permanently wired to the
  incrementer's SUM outputs; synchronous `/E` (= `/PCLE`) asserted only
  in states that intend a PC update. Hold = `/E` high. No clear pin —
  see reset.
- **Reset = a write, not a clear.** A **zero driver** (two '541s, all
  inputs grounded) owns the address bus while reset is asserted:
  its enables come from **/RST** (low during reset → driving), while the
  PC address buffers' enable input takes **RST** (high during reset →
  off). The incrementer passes (INC forced low during reset), so
  SUM = 0x0000 and PC captures it on every reset-held edge. This is how
  the die does it — RESET jams zero into PC through the ordinary write
  path. The zero/PC handover on the bus is driven by the clock module's
  own complementary RST//RST pair, with no gate in between, and works
  with the GAL absent.
- **Incrementer = four '283s.** All sixteen B inputs tied to one `DEC`
  line; Cin = `INC`. `INC=1, DEC=0` → +1; `INC=0, DEC=1` → −1;
  both low → pass-through. Both high is also pass (+0xFFFF+1), so no
  illegal combination exists. Its A input reads the **address bus**;
  its output feeds only PC's D inputs at these rungs. It is the
  machine's *only* counting mechanism — PC now, SP and INX/DCX later —
  one INC line, one set of bus-ownership rules, every counter a
  customer. (Rung 4 note: give SUM a tap toward the address bus for
  PUSH-to-SP−1.)
- **PC transfers ride the bus.** "Advance" = PC drives, INC. "Hold" =
  `/PCLE` high (bus ownership now irrelevant to PC). "Jump" = WZ drives,
  pass, `/PCLE` low → PC ← WZ. Load-with-offset composes for free
  (PC ← source+1 = drive + INC + `/PCLE`), though no 8080 flow needs it.
- **ROM = Code & IR module, single lane** (U2 28C256 + U4), '373 fitment,
  LE on its pullup (transparent), `/OE` grounded — sole data-bus source,
  always driving. ZIF socket carries the program.
- **One T-state = one M-cycle.** One memory access per state; no
  sub-cycle sequencing. The T counter is the clock module's own '161
  (cleared by /RST at the source), reloaded through `/TRST`.

### Timing budget (worst-case HC @ 5 V) ⚠ reasoned, not measured

Longest loop: PC '377 clock-to-Q (~35 ns) + '541 (~12 ns) + four-'283
ripple carry (~80 ns) + '377 setup (~12 ns) ≈ **140 ns**. ROM path:
'377 + '541 + EEPROM 150 ns + '377 setup ≈ **210 ns**. Both are orders
of magnitude inside the clock module's 2.5 kHz ceiling; the design never
approaches its own limits at panel speeds.

---

## Rung 1 — the fetch engine

**ICs (10):** PC 2 × '377 · incrementer 4 × '283 · PC address buffers
2 × '541 · zero driver 2 × '541. Plus the clock module and ROM as
existing boards.

No IR, no GAL, no WZ. **Straps:**

| Pin | Strap | Effect |
|---|---|---|
| PC `/E` (both '377s) | GND | loads every edge — sound while only PC/zero own the bus |
| `INC` | **/RST** (module H2.4) | counts in run, forced pass during reset |
| `DEC` | GND | |
| PC buffer enables | **RST** (module H2.3) | PC off the bus during reset |
| Zero driver enables | **/RST** | zero owns the bus during reset |

**Observability:** fit the I/O module in its **'373 live-probe fitment**
(U2 = '373, H2.2/LE strapped high, H2.3//OE strapped low) on the data
bus — the LEDs track D combinationally, no clocking. A documented
fitment of that board, reused as-is. (Or a bare LED bank on D for zero
ICs of board-tying.)

**What it does:** free-runs (or single-steps) through memory; the LEDs
show each byte as PC walks the ROM. RESET forces the bus to 0x0000, PC
captures it for the duration, and the walk restarts on release.

**What it proves:** clock distribution, both reset polarities *and* the
reset-as-a-write mechanism (watch the address LEDs, if fitted, snap to
zero while RESET is held), the PC → bus → incrementer → PC loop, ROM
addressing, and the mirroring behaviour — with a failure mode ("LEDs
don't count") needing no instrument beyond eyes. The DMM-average trick
from the clock module's bring-up notes (~2.5 V = toggling) localises any
stuck line.

---

## Rung 2 — NOP / JMP / HLT

**Adds (4, total 14):** IR ('377, CP = CLK, `/E` = `/IRCE`) · WZ (two
'373s) · sequencer GAL (22V10). The Rung-1 straps on PC `/E` and `INC`
move to GAL outputs; the reset-path straps (PC buffers ← RST, zero
driver ← /RST) **stay hardwired** — reset must not depend on the GAL.

**WZ:** both latches' D inputs sit on D[7:0]; `LE_Z` / `LE_W` select
which captures. Their three-state outputs drive the address bus directly
— Z → A[7:0], W → A[15:8] — under one shared `/WZOE`. No '541s on this
source; the '373s bring their own.

### State machine

Fetch happens in state 0 of every instruction, unconditionally. IR loads
at the edge ending state 0, so decode is live from state 1 onward.
**No state-0 control depends on IR** — during the first state 0 after
reset IR holds whatever the reset-held fetches left there (0x00 = NOP,
since the zero driver put the bus at address 0… in fact IR loads
harmlessly throughout reset), and the first post-reset edge replaces it
with the real opcode at 0x0000 regardless.

| State | NOP (and all undecoded opcodes) | JMP (0xC3) | HLT (0x76) |
|---|---|---|---|
| 0 | addr=PC · INC · PC←+1 · fetch→IR | same | same |
| 1 | `/TRST` (done; PC holds) | addr=PC · INC · PC←+1 · D→Z (`LE_Z`) | `/HALTREQ` · `/TRST` (PC holds) |
| 2 | — | addr=PC · INC · PC←+1 · D→W (`LE_W`) | — |
| 3 | — | addr=WZ (`/WZOE`) · pass · PC←WZ (`/PCLE`) · `/TRST` | — |

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

**Structural bus safety:** in run, `/PCOE` and `/WZOE` are complements
by construction; in reset, the RST//RST pair swaps the zero driver for
the PC buffers below the GAL's feet. Exactly one address-bus owner in
every reachable state.

### GAL equations (22V10, combinational)

Inputs (12): `CLK`, `IR7..IR0`, `T1`, `T0`, `RST` — T1/T0 are bits 1:0
of the clock module's T counter; RST is the module's active-high reset.
Outputs (9): `/PCOE`, `/WZOE`, `/IRCE`, `/TRST`, `/HALTREQ`, `INC`,
`LE_Z`, `LE_W`, `/PCLE`. `DEC` stays a board strap to GND at this rung.

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
/PCOE    (low  when) :  !(JMP & S3) & !RST          /* default source; off in reset */
/WZOE    (low  when) :   JMP & S3 & !RST            /* complement of /PCOE in run   */
/PCLE    (low  when) :   RST # S0 # JMP             /* PC writes: reset, fetch, all of JMP */
/IRCE    (low  when) :   S0                          /* fetch window (reset-time loads harmless) */
/TRST    (low  when) :  (!JMP & S1) # (JMP & S3)     /* NOP,HLT end @1; JMP @3 */
INC      (high when) :  !RST & (S0 # (JMP & S1) # (JMP & S2))
LE_Z     (high when) :   JMP & S1 & CLK              /* closes on CLK fall */
LE_W     (high when) :   JMP & S2 & CLK
```

`/PCLE = RST # S0 # JMP` collapses because JMP's states 1–3 all write
PC and state 0 is covered by S0 — NOP's and HLT's state 1 are the only
run-mode holds, and they fall out as the complement.

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

- **The reset path bypasses the GAL — keep it that way.** The zero
  driver's enables and the PC buffers' disable are wired to /RST and RST
  directly. The GAL's `!RST` guards on `/PCOE`, `/WZOE`, `INC` and its
  `RST` term in `/PCLE` *agree* with the hardwired path; they are not
  what makes reset work. A blank or absent GAL still resets PC to
  0x0000 (with PC `/E` strapped low, Rung-1 style, for GAL-less bench
  work).
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
- **INR/DCR are not incrementer work** (recorded here so the Rung-5
  sequencer doesn't inherit the conflation): they are 8-bit ALU
  operations that set flags. Only INX/DCX belong to the incrementer —
  and PC/SP, its address-path customers.
- **Design-history note.** The '273 PC (no write enable, capture every
  edge) is recorded as rejected: sound only while every bus owner is a
  legitimate next-PC value, which stops being true at `MOV A,M`. The
  '161-counter PC was also considered and rejected — it solves the
  hazard but imports a second counting mechanism foreign to the 8080's
  structure. The '377-behind-shared-incrementer scheme is both the
  faithful topology and the one-counting-story design.

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
is the same counter this design reloads through /TRST). While RESET is
held, the address LEDs (if fitted) must show 0x0000 — that observation
is the zero-driver handover working.
