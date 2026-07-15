# Blinky Clock Module v3 — Functional Summary

A **generic front-panel clock and debug module** for homebrew TTL CPUs. One
board provides clock generation, source selection, single-cycle stepping,
instruction stepping, halt/breakpoint policy, T-state display, and supervised
reset — for **any** attached machine. The module assumes nothing about the
CPU: every machine-facing input is optional (10k pulled up, absent = inert),
and the module owns clocking and debug *policy* only, never machine internals.

Capture: `blinky_clock_module_v3.ttlproj` (TTLSim). ~16–17 ICs, 74HC logic,
5 V. Developed and verified stage-by-stage in simulation and on hardware.

---

## Panel

**Buttons**

| Button | Function |
|---|---|
| STEP MODE | Select single-cycle mode: the clock advances only on NEXT CYCLE |
| RUN 555 | Select RUN mode, clocked from the adjustable astable (~0.5–5 Hz, trimmer) |
| RUN XTAL | Select RUN mode, clocked from the socketed can via the divider |
| NEXT CYCLE | Advance exactly one clock cycle (live only in STEP MODE) |
| NEXT INSTR | Run to the start of the next instruction, freeze there |
| RESET | Assert supervised reset (does **not** change the mode) |

STEP MODE / RUN 555 / RUN XTAL are radio buttons (two NAND SR latches: mode
and source). One press lands the full state; the source latch remembers the
last RUN flavour while stepping. Buttons debounce through the latches
themselves; only NEXT CYCLE needs true debounce (556 Schmitt + RC) because
its one-shot is machine-cleared mid-press.

**Switches**: 3-bit DIP, closed = 1: code 0 parked (off), 1–5 = ÷1 ÷2 ÷4 ÷8
÷16, 6–7 parked. Factory all-open = parked — an unconfigured board can never
free-run on a surprise-MHz can.

**LEDs**: STEP MODE · RUN 555 · RUN XTAL (radio, exactly one lit) · RESET ·
HALT · CLK activity · STEP RAW (debouncer output, bench diagnostic) ·
T0–T3 · PWR.

## Headers

GND is pin 1 of every header; VCC appears on H1 only. All nine outbound
signals leave through 100R series resistors; both machine inputs are pulled
up with 4k7 (absent source = inert).

### H1 — Power (2-pin)

| Pin | Signal |
|---|---|
| 1 | GND |
| 2 | VCC |

### H2 — Machine control (8-pin)

| Pin | Signal | Dir | Description |
|---|---|---|---|
| 1 | GND | — | return |
| 2 | CLK | out | the machine clock — mode-gated and halt-gated; the only clock exported |
| 3 | RST | out | active-high reset: async assert, released synchronously two clock edges after the supervisor |
| 4 | /RST | out | active-low reset, straight from the supervisor net |
| 5 | HALT | out | high while the machine is frozen by the halt flop |
| 6 | /HALTREQ | **in** | the single halt request line; any source, any reason — multiple sources combine on the machine side (shares the net with the panel's HALT REQ button) |
| 7–8 | NC | | |

### H3 — T service (8-pin)

| Pin | Signal | Dir | Description |
|---|---|---|---|
| 1 | GND | — | return |
| 2–5 | T0–T3 | out | the T-state count |
| 6 | FETCH | out | high during T = 0, the first cycle of each instruction — 6502-SYNC-like but an *output*, decoded from the on-board (authoritative) T counter |
| 7 | /TRST | **in** | machine asserts during an instruction's final T state; synchronous reload to 0. Strap to pin 1 for single-cycle machines |
| 8 | NC | | |

### H4 — CANIN (2-pin) + J1 jumper

External clock injection for the XTAL leg without wearing the can socket:
0–5 V squarewave in (bench function generator, external oscillator board).
J1 (3-pin jumper, pin 2 common) selects the source: X1 socket side or H4.
Default shunt position: X1.

| Pin | Signal |
|---|---|
| 1 | GND |
| 2 | CANIN |

### H5 — RV1 EXT (2-pin)

Parallels the RV1 trimmer's two terminals for an external/panel pot. Fit the
trimmer *or* the external pot; both fitted puts them in parallel and speeds
the astable.

### TP1 — Test points (8-pin strip)

The five nets bring-up proved most diagnostic, plus a scope-clip ground.

| Pin | Signal | Why it's here |
|---|---|---|
| 1 | GND | scope/DMM clip |
| 2 | /OSC | the free-running board clock — everything stateful samples on it, including while halted |
| 3 | OSC | the mux output, pre-gating: proves source selection and handoff |
| 4 | /RST | reset domain: supervisor, /MR, halt clears |
| 5 | SREQ | the NEXT CYCLE one-shot: press storage and the Q3 clear |
| 6 | CLK | the machine clock as exported (post mode- and halt-gating) |
| 7–8 | NC | |

## Machine integration

- **Multi-cycle machine**: supply /TRST; consume CLK, RST/(/RST); optionally
  consume T0–T3/FETCH as its sequencer state or run its own counter.
- **Single-cycle machine**: strap /TRST low (H3.7 → H3.1 on the plug) — T
  pins at 0, FETCH is permanently true, NEXT INSTR degenerates (correctly)
  to NEXT CYCLE.
- **Breakpoints**: an external comparator card asserts /HALTREQ on match;
  qualify with FETCH for true PC breakpoints vs plain watchpoints.
- **HALT instruction**: the machine decodes it and asserts /HALTREQ.

## Functional blocks

**Sources.** NE556: timer 2 is an output-feedback astable (~50% duty, trimmer
adjustable); timer 1 is the NEXT CYCLE Schmitt debouncer. Socketed DIP-14 can
→ '14 conditioner → '161 synchronous divider (all taps switch on the same can
edge, 50% duty) → '151 tap select ('151 W output supplies the inverted clock
free).

**Glitch-free source mux.** The 555 and can are asynchronous, so selection
uses the two-flip-flop clock mux: cross-qualified D-terms (each enable
requires the other leg's enable off), each enable FF clocked on **its own
source's falling edge**. Break-before-make at any press timing: the leaving
leg completes its final full cycle, OSC parks low one–two cycles, the
arriving leg starts with a full-width pulse. No runt pulses, ever. The mux is
what makes source switching safe; the DIP (ratio changes inside the can leg)
remains change-while-idle bench discipline.

**Sync core.** Everything stateful clocks on one edge of free-running /OSC.
A '273 (/MR = /RST) synchronizes the step request (FF pair + edge detector →
SP, exactly one OSC period per press) and resamples the mode latch (gating
changes only between clocks). `CLK_pre = OSC·SEL_RUN + SP·SEL_STEP`: in RUN
the selected oscillator flows through; in STEP MODE exactly one clean cycle
per press regardless of hold time.

**T counter and FETCH.** A '161 **on this board** is *the* T counter — not a
shadow: clock = CLK, /LOAD = /TRST (synchronous reload-to-zero at each
instruction boundary), /CLR = /RST. FETCH = NOR(T0,T1)·NOR(T2,T3), authoritative
and valid to 16 T states.

**HALT.** One '74: the halt flop plus a request-last flop, both clocked on
**free-running /OSC** (they must keep sampling while the machine is frozen).
Entry is **edge-detected**: `ENTRY = HALTREQ·/HALTREQ_last` fires for exactly
one cycle on a request's rising edge. This solves the level-trigger trap — a
halted machine keeps asserting the request (the matched address is still on
the bus), but a level is old news one cycle later, so RUN always escapes, and
returning to the same breakpoint re-arms naturally (the request went low in
between). `DHALT = ENTRY + HALT·KEEP`. HALT gates the clock (`CLK =
CLK_pre·/HALT`) and touches nothing else: the CPU freezes in place, state
held by the stopped clock, and resuming is just letting the clock run.

Clears: **RUN** (either button — free-run to the next request), **NEXT CYCLE
in STEP MODE** (via the set-on-press one-shot, so the first press after a
halt advances exactly one cycle — never swallowed; inert in RUN mode, where
clearing would free-run, the wrong surprise), **NEXT INSTR**, **RESET** (async).

**NEXT INSTR.** A breakpoint on FETCH: the button sets an ARM latch and
clears HALT through KEEP; `NAND(FETCH, ARM)` joins the request path; the
machine runs to the next T0 and freezes on the instruction's first cycle; the
landing halt clears ARM (one press = one instruction). Held down, it walks
fetch-to-fetch. Set-dominance in the latch makes arming-while-halted work.
Granularity ladder: NEXT CYCLE = one cycle, NEXT INSTR = one instruction,
RUN = to the next request.

**Reset.** DS1813 supervisor + button: asynchronous assert, released
synchronously two clock edges later through the '273. /RST clears the step
pipeline, the T counter, the HALT/request-last flops, and the ARM latch. It
does **not** touch the mode/source latches (power-up presets STEP MODE via a
100n cap across the STEP MODE button; RESET leaves the mode alone — a
deliberate v3 semantic, reversed from v2), the mux enable FFs (OSC must run
through reset for the synchronous release), or the oscillators. Consequence:
RESET while in RUN restarts the machine free-running from the vector — press
STEP MODE first to inspect the first instruction, or put a breakpoint on the
vector (break-at-boot works: reset re-arms the request edge detector).

## Design rules embodied

- One clock domain; every board flip-flop on one edge of /OSC.
- Halt entry edge-triggered, never level; halt gates the clock, never the mode.
- Registered outputs cross clock-gating boundaries; combinational glitches
  never reach a clock or an async input.
- No diode-ORs; combining is gates. One driver per net; constants driven.
- Buttons active-low with pull-ups (button-4 footprints, wired diagonally
  pins 1/4 so either internal pairing works).
- Power-on state by RC preset where needed; reset is machine state, not
  panel-configuration state.
