# Blinky Clock Module v2 — Features

Companion to `blinky_clock_module_v2.ttlproj` and `blinky_clock_module_v2_BOM.md`.
Supersedes the v1 module (`blinky_clock_module.svg`). Designators: U1 NE556
(captured as U1A/U1B, two NE555s — TTLSim's library has no 556; the build
part is one NE556), U2 74HC14, U3/U6/U10/U14/U15 74HC00, U4 74HC273, U5 DS1813, U7/U12/U16 74HC08,
U8/U18 74HC161, U9 74HC151, U11/U13 74HC74, U17 74HC02, plus X1, a socketed can
oscillator. v2 is not just a clock: it is the machine's front-panel debug
module — clock source, run control, halt, breakpoint plumbing, instruction
step, and T-state display in one board.

## Summary

1. Three-button radio mode select: STEP / RUN-555 / RUN-CAN
2. Adjustable 556 oscillator (unchanged from v1)
3. Can oscillator with a DIP-selected synchronous divider — J2 retired
4. Glitch-free clock-source mux: 555 ↔ CAN hot-switchable
5. Hardware-debounced single-step (unchanged)
6. One clock domain — every board flip-flop on /OSC
7. Exactly-one-cycle step pulse, latched (unchanged)
8. Glitch-free mode gating → CLK (unchanged)
9. **CLKG = CLK · /HALT** — halt gates the machine clock, never the mode
10. One consolidated HALT: breakpoint, CPU HALT, and instruction step
11. Instruction step — run to the next fetch and freeze
12. T counter as a board service — FETCH output, /TRST input
13. Supervised reset; interaction with HALT and the new latches
14. Front panel: eleven LEDs
15. Bipolar-556 friendly (unchanged)
16. Gate ledger — zero spares

**Outputs:** GND, VCC, **CLK** (free-running panel clock), **CLKG** (the
machine clock, halt-gated), **RST** (active-high, synchronous release),
**HALT**, **FETCH**, and **T0–T2** (T3 as a board net).
**Handshake inputs** (each with a 10k pull-up so an absent source is
inert): **/TRST** from the CPU's sequencer, **/HALTDST** from the CPU's DST
decoder, **/BPMATCH** from the breakpoint card.

## 1. Three-button radio mode select

Three momentary buttons behave as radio buttons without any 3-way latch
cleverness: two ordinary cross-coupled NAND SR latches with a shared set.
SW2 (STEP) sets the mode latch (U3 a/b) to STEP as in v1. SW3 (RUN-555) and
SW5 (RUN-CAN) each pull **two** latch inputs: both drive the mode latch's
RUN side — combined through U12 gate-a (`/BRUN = /B555 · /BCAN`), a gate
rather than a diode-OR per the board rule — and each sets its own side of
the new **source latch** (U10 a/b, SEL555/SELCAN). One press lands the
complete state: mode and source together. R3/R4/R11 are the 10k button
pull-ups. Mode LEDs come straight off the latches as before; source LEDs
(D6 555, D7 CAN) come off the source latch, so even while single-stepping
the panel shows which source RUN is armed to use.

## 2. Adjustable oscillator

Unchanged from v1: 556 timer 2 as an output-feedback astable, RV1 + C9,
~0.7 Hz floor. The proposed 1k floor resistor between OUT2 and RV1 remains
open — keep RV1 off the bottom stop until it's fitted.

## 3. Can oscillator with a switched divider

J2 is gone. X1 is squared through U2 inv6 (CANC) as before, then feeds
U8, a '161 wired as a **synchronous** divider — clocked directly by CANC,
ENP/ENT high, so every tap switches on the same can edge with no ripple
skew and 50% duty throughout. Taps /2 /4 /8 /16 plus raw /1 enter U9, a
'151 whose select lines come from a 3-position DIP (SW7–SW9, 10k pull-ups,
closed = 0):

| DIV2 DIV1 DIV0 | Source |
|---|---|
| 0 0 0 | /1 (raw can) |
| 0 0 1 | /2 |
| 0 1 0 | /4 |
| 0 1 1 | /8 |
| 1 0 0 | /16 |
| 1 0 1 – 1 1 1 | GND — CAN leg parked (off) |

Codes 5–7 select a grounded input, an intentional "off" position — and the
all-open (unconfigured) DIP lands there, so a fresh board can't free-run on
a surprise MHz can. The switches set a code and the mux does the selecting,
so no DIP combination can ever short two counter outputs together.

**Rule:** change the DIP in STEP or during reset. The glitch-free mux (§4)
protects the 555↔CAN handoff, not edits inside the CAN source itself; the
divide ratio is bench configuration, the same habit the old J2 carried.

## 4. Glitch-free clock-source mux

The 555 and the can are asynchronous to each other, so the source switch is
a textbook glitch-free clock mux. U11 ('74) holds one enable per domain,
each clocked on its own source's **falling** edge — FF-A on /OSC555 (U17
gate-d as inverter), FF-B on /OSCCAN (the '151's complementary W output,
free):

- `ENA.d = SEL555 · /ENB` (U12 gate-b)
- `ENB.d = SELCAN · /ENA` (U12 gate-c)
- `OSC = OSC555·ENA + OSCCAN·ENB` (NAND-NAND: U10 c/d, U14 gate-a)

The old leg is disabled on its own clean edge before the new leg enables on
its own clean edge; during the handoff both enables are low and OSC parks
low for a cycle or two — the '273 simply pauses. The mux FFs are **not**
cleared by reset: OSC must keep running through reset so the '273 can
release it synchronously (§13). Single-stage enables, ratified: each FF has
a full half-period of its own clock to resolve, and the failure class is
the same finger-speed race v1 already accepts.

## 5–8. Step, clock domain, step pulse, mode gating

All carried over from v1 unchanged: the 556-Schmitt debounced step button
into the U3 c/d one-shot latch; every flip-flop on /OSC (U2 inv1, now
downstream of the source mux — whichever source wins, there is still only
one domain); the '273 (U4) FF2/FF3 step synchronizer with the U6 gate-c
edge detector; FF4/FF5 select resampling; and
`CLK = OSC·SEL_RUN_S + SP·SEL_STEP_S` in U6. CLK is now the **free-running
panel clock** output — alive even while the machine is halted — for
anything that must observe or wake a halted machine (NMI sampling, a probe
timebase, peripherals).

## 9. CLKG — halt gates the clock, never the mode

`CLKG = CLK · /HALT` (U7 gate-c) is the machine clock, the Stage-0 gating
shape moved onto this board: HALT freezes the machine exactly where it is
and touches nothing else. The mode latch, the mode LEDs, and CLK all keep
running. Because /HALT is a registered output (§10) it cuts CLKG before
the next rising edge — the on-time-halt guarantee holds with the gate in
the post-mux position; the design rule's real target was the mode-resample
path, not gate position. Stage 0's on-CPU halt gating retires: this flop
is the single owner of the machine clock, and the CPU core sees only CLKG.

## 10. One consolidated HALT

One '74 flop (U13 FF-A), clocked on /OSC, cleared by /RST, is the halt for
the whole machine. Three match sources merge ahead of one edge detector:

- **/BPMATCH** — the breakpoint card's comparator match
- **/HALTDST** — the CPU DST decoder's Halt line (micro-op `Ram→Halt`);
  tap the **raw '138 decode**, not the /CLK-phase-gated strobe — the edge
  detector needs a full-T-state level, not a half-cycle pulse
- **/INSTRMATCH** — the instruction-step fetch match (§11)

`/ANYMATCH = /BPMATCH · /HALTDST · /INSTRMATCH` (U7 gate-d + U16 gate-b),
inverted to MATCHNOW (U17 gate-c). U13 FF-B samples MATCHNOW one cycle
behind (matched-last), and `/ENTRY = NAND(MATCHNOW, /MATCHEDLAST)` fires
for exactly the first cycle of any match. The edge trigger is not optional:
once the clock stops, the DST line (or a matched address) is frozen
asserted, and a level-triggered halt could never be escaped.

The flop's next-state is `DHALT = ENTRY + HALT·KEEP`, where KEEP is the
no-clear condition `/BRUN · /STEPCLR · /BINSTR` (U12 gate-d, U16 gate-a).
Clears are therefore:

- **Run** — either RUN button: HALT drops on the next /OSC edge and the
  machine free-runs to the next breakpoint or HALT.
- **Step, in STEP mode** — `/STEPCLR = NAND(SREQ, SEL_STEP_S)` (U15
  gate-d) uses the step one-shot, which sets **on the press**, so the halt
  clears on the same falling edge the step pulse begins and the machine
  advances exactly one cycle — the first press after a halt is never
  swallowed. In RUN mode the Step button stays inert, as in v1.
- **INSTR** — §11.
- **Reset** — §13.
- **NMI wake** — reserved: one more AND folding the NMI-entry strobe into
  KEEP, the ISA's "NMI wakes" made real when interrupt bring-up arrives.
  The gate ledger is full (§16); it costs one package.

Because HALT is registered, everything downstream (CLKG, the ARM clear)
sees single clean edges — the v1 breakpoint's design rules 1–3 all still
hold, and rule 4 (no diode-ORs) is obeyed everywhere new.

## 11. Instruction step

INSTR (SW6) is a breakpoint on FETCH, nearly free once §10 and §12 exist.
Pressing it sets the ARM latch (U15 a/b) and clears HALT through KEEP; the
machine runs to the next T0, where `/INSTRMATCH = NAND(FETCH, ARM)` (U15
gate-c) feeds the common entry path and the machine freezes on the first
cycle of the next instruction. The ARM latch's clear input is
`ARMCLR = /HALT · /RST` (U16 gate-d): the halt landing disarms it, so it
fires once per press. Arming *while already halted* works because the set
side dominates the transient — HALT clears on the next edge, the clear
releases, and the latch holds until the next fetch. Hold INSTR down and
the machine walks fetch-to-fetch, pausing one cycle at each — the same
"run while held" semantic the breakpoint's Run-held behavior established.
Step advances one cycle, INSTR one instruction, Run to the next
breakpoint/HALT: a complete panel debugger from one flop.

## 12. T counter and FETCH — a board service

U18 is the module's T counter, offered to whatever machine is attached: a
'161 clocked by CLKG, /LOAD = /TRST, /CLR = /RST. **/TRST is the entire
interface contract**: a full-cycle level, asserted during the instruction's
final T state, sampled synchronously on the CLKG rising edge — the counter
reloads to zero exactly as the instruction boundary passes. FETCH is
decoded from all four bits — `FETCH = NOR(T0,T1) · NOR(T2,T3)` (U17 gates
a/b, U16 gate-c), active high during T0 and unambiguous for machines of up
to 16 T states. It drives the INSTR match, the T-state LEDs D9–D11, and is
a committed output alongside T0–T2 (T3 rides the board as a net for a
16-state machine's taking).

A machine uses the service at whatever depth suits it:

- **Multi-cycle with its own T counter** (Blinky-M): supply /TRST and keep
  the internal counter — T[2:0] into nine sequencer GALs is the
  control-store critical path, and no header hop belongs in it. The board
  counter shadows it in lockstep by construction: same clock, same reload
  idiom, same reset.
- **Single-cycle** (Blinky W=8): strap /TRST to ground. /LOAD held low
  loads zero on every CLKG edge — T pins at 0, FETCH sits true, INSTR
  becomes an exact synonym for Step, and a fetch-qualified breakpoint
  collapses into a plain address breakpoint. All three degenerate
  semantics are true statements about a single-cycle machine.
- **A future machine without its own sequencer state** could consume the
  board's T0–T3 directly as its T bus, driving only /TRST back.

Don't leave /TRST floating on a machine that has instruction boundaries:
unconnected (pull-up R17), the counter free-runs mod 16 and FETCH pulses
meaninglessly. Strap it or drive it.

## 13. Reset

Unchanged core: DS1813 + SW4, asynchronous assert into the '273's /MR,
synchronous release two falling edges later via FF0/FF1, RST out through
U2 inv5. Reset still forces the **mode** latch to STEP (U7 gate-a) and
kills any in-flight step pulse — the machine always leaves reset halted in
single-step, never free-running.

Interactions with the new state: /RST asynchronously clears the HALT flop,
the matched-last flop, the ARM latch (via ARMCLR), and the T
counter — the machine always leaves reset un-halted, disarmed, and with T
agreeing at zero. The **source latch deliberately holds through reset**:
which oscillator RUN would use is inert state while halted, and your bench
selection surviving a reset is the behavior you actually want. The mux
enable FFs are untouched by reset so OSC keeps running for the synchronous
release. One deliberate consequence carried from v1's breakpoint: a
breakpoint armed on the reset vector re-fires on the first cycle out of
reset — break-at-boot is a feature.

## 14. Front panel

D1 (green) STEP, D2 (green) RUN — mode latch, instant feedback. D3 (amber)
on **CLKG**: activity means the *machine* is clocking; frozen means
halted. D4 (red) RST. D5 (red) **HALT** — required now that a halt no
longer changes the mode LEDs. D6/D7 (green) 555/CAN source. D9–D11 (amber)
T0–T2 — exactly where in the instruction the machine is frozen. D8 (red)
supply present. Power arrives on H1 through S1 as before.

## 15. Bipolar-556 friendly

Unchanged: R9/R10 4k7 pull-ups restore OUT1/OUT2 to the rail for HC
inputs. R12 (the v1 one-shot clear-node pull-up) is **omitted** in v2 — it
was flagged redundant with U7 gate-b's push-pull drive, and this respin
retires it.

## 16. Gate ledger — zero spares

Every gate on the board is in service: U3, U6, U10, U14, U15 ('00s ×5),
U7, U12, U16 ('08s ×3), U17 ('02), all 4/4; U2 6/6; both '74s 2/2; '273
6 of 8 (D6/D7 tied, FF6/FF7 genuinely free — the only spare state on the
board). The v1 breakpoint card's '00 and '74 are absorbed here: the card
slims to its two '688s, switches, and pull-ups, sending /BPMATCH over the
handshake — `Blinky_Breakpoint.md` needs a follow-up pass to match. Known
corners, all carried or accepted: the v1 step/mode same-edge finger race;
the ARM latch's transient both-outputs-high while INSTR is held during a
halt (its /ARM consumer is only the latch itself); the source latch powers
up in a random but harmless state; and NMI wake costs the next '08.
