# Blinky Clock Module — Features

Companion to `blinky_clock_module.svg` and `blinky_clock_module_BOM.md`.
Designators follow the schematic capture: U1 NE556, U2 74HC14, U3 and U6
74HC00, U4 74HC273, U7 74HC08, plus U5 DS1813 (TO-92) and an optional
socketed can oscillator.

## Summary

The module is the front panel for Blinky's clock: a pushbutton-selected
RUN / SINGLE-STEP clock source with a supervised reset and a jumpered
external-clock option. Its features are:

1. Pushbutton mode select (RUN / STEP) with an SR latch
2. Adjustable oscillator
3. External can clock via one jumper, conditioned to HC levels
4. Hardware-debounced single-step button
5. One single clock domain — every flip-flop on /OSC
6. Exactly-one-cycle step pulse, latched so a tap is never lost
7. Glitch-free clock gating
8. Supervised reset: power-on + pushbutton, synchronous release
9. Clock silenced during reset; reset forces STEP
10. Front-panel LEDs and a switched, indicated supply
11. Bipolar-556 friendly
12. Built-in expansion headroom

Outputs on header H2: **GND**, **VCC**, **CLK** (the machine clock), and
**RST** (active-high reset, released synchronously with the clock).

## 1. Pushbutton mode select

Two momentary buttons drive a cross-coupled NAND SR latch (U3 gate-a /
gate-b). SW2 (STEP) sets the latch, SW3 (RUN) resets it; R3/R4 are the
10k input pull-ups. The latch holds its state, so there is no slide switch
to lever on a breadboard, and the latch itself absorbs contact bounce — a
bouncing press just re-asserts the state it already set. Its outputs drive
the mode LEDs (D1 STEP, D2 RUN) directly, so the panel responds the instant
a button is pressed. The set input is OR'd with reset (see §8), so a reset
always lands the latch in a known mode.

## 2. Adjustable oscillator

Timer 2 of the NE556 runs as an output-feedback astable: OUT2 charges and
discharges C9 through RV1, giving a ~50 % duty square wave with no
discharge-pin asymmetry. RV1 (100k trimmer) sets the rate; with C9 = 10 µF
the floor is roughly **0.7 Hz** at full 100k, rising as RV1 is reduced —
slow enough to watch each step on the panel LEDs.

> PROPOSED — not yet ratified: add a 1k series resistor between OUT2 and
> RV1. As built, RV1 is a bare rheostat from OUT2 into the timing node, so
> trimming it toward zero shorts the 556 output into C9. Keep RV1 off the
> bottom stop until the floor resistor is fitted.

## 3. External can clock — one jumper

J2 is a 3-pin header cut into the OSC line: the shunt selects the 556
astable or X1, a socketed can oscillator. The cut sits on the distribution
side of the astable's feedback loop, so the 556 keeps free-running either
way and switchover is instant in both directions. The can is hardwired
through spare Schmitt inverter U2 inv6, which squares any can — TTL or
HCMOS output — to clean rail-to-rail HC levels (the single-stage inversion
is harmless: all timing in the module is edge-relative). Everything
downstream is frequency-agnostic. Habit: move J2 while holding reset.

> PROPOSED — not yet ratified: a 10k pull-up on the can-output node, to
> define U2 inv6's input when the socket is empty and to firm up a
> TTL-output can's VOH. Not fitted in the current build.

## 4. Debounced single-step

The STEP button (SW1) feeds timer 1 of the 556 wired as a pure Schmitt
trigger — THR and TRG tied, no timing parts — behind a 100k/470n RC
(R1/C1, ~47 ms). The RC slews through the button's bounce; the timer's
⅓/⅔-VCC ratiometric hysteresis (a 1.67 V band) turns the slow ramp into
one clean edge per press and per release — a 555 as a debouncer with
better hysteresis than a logic-gate Schmitt.

## 5. Single clock domain

Every flip-flop in the module — reset synchronizer, step synchronizer,
and mode-select resampler — lives in one 74HC273 (U4) clocked on /OSC, the
inverted clock source (U2 inv1). All state therefore changes only on OSC's
falling edge, while OSC is low; in RUN mode the machine clock's active
(rising) edges sit a half period away from every control transition. There
is nothing to interlock because nothing ever changes mid-high. Whichever
source J2 selects, the whole core follows it — there is never a second
domain.

## 6. One-cycle step pulse, latched

The debounced step edge is first captured by a one-shot latch — the second
cross-coupled NAND pair in U3 (gate-c / gate-d). Pressing SW1 sets it
immediately, so **even a brief tap is held** until the clock can sample it;
this is the element a plain "sample-the-level" design lacks, and without it
a press that falls entirely between two clock edges is silently dropped.

The latched request is then sampled through two '273 stages (Q2, Q3) and
the edge detector Q2·NOT(Q3) (U6 gate-c → U2 inv4 → U6 gate-b) is true for
exactly the one OSC period after the press is first sampled. The one-shot
self-clears one clock later: when Q3 rises, U2 inv3 drives the latch's
clear input low through U7 gate-b. Hold the button for a minute — the
machine still advances exactly one clock; tap it — it still advances
exactly one. The pulse starts and ends on falling edges, so the step path
obeys the same half-period margin as RUN mode.

## 7. Glitch-free clock gating

CLK = OSC·SEL_RUN + SP·SEL_STEP, built from the four NANDs of U6
(gate-a RUN leg, gate-b STEP leg, gate-c edge detect, gate-d combine).
Because the select signals are resampled versions of the latch (FF4/FF5 of
the '273), they can only change while OSC is low — a mode change can never
slice a high clock pulse into a runt. CLK leaves the board on H2 and drives
the D3 activity LED.

## 8. Supervised reset

A DS1813 (U5) supplies power-on reset, and SW4 hangs directly on its /RST
pin — the supervisor debounces the button and stretches every assertion to
~150 ms, with its own internal ~5.5k pull-up and an open-drain output, so
the button and the diode-free OR (below) share the node with no contention.
The raw /RST drives the '273's /MR asynchronously; FF0/FF1 (D0 = VCC daisy
chain) then release reset synchronously, two falling edges after the button
clears. Asynchronous assert, synchronous release: the machine can be yanked
into reset at any time but always leaves it cleanly aligned to the clock.
U2 inv5 provides the active-high RST (on H2 and the D4 LED) for parts that
want it.

Reset is folded into the latches by U7 (74HC08), used as an active-low OR:
gate-a = (STEP-set ∨ reset) → mode latch, gate-b = (Q3-clear ∨ reset) →
step one-shot. AND gates rather than a Schottky diode-OR on purpose — both
target nodes are high-impedance (a weak-pull-up latch input and the '273's
sense path), and a low-barrier Schottky's reverse leakage is enough to drag
them; a push-pull gate output isolates cleanly and its CMOS input draws
nothing. **Rule for this board: never diode-OR onto an open-drain or
weak-pull-up node.**

## 9. Clock silenced during reset; reset forces STEP

Because /MR clears the select flip-flops along with everything else, both
mux legs deselect and CLK parks low for the whole of reset. Any in-flight
step pulse dies with FF2/FF3. Reset also drives the mode latch to STEP
through U7 gate-a, so the machine always leaves reset **halted in
single-step, never free-running** — the safe power-on state. On release the
selects reload from the latch on the first falling edge, the clock resumes,
and RST lifts one edge later; the CPU's async-clear registers sit safely at
zero throughout.

## 10. Front panel and supply

D1 (green) STEP mode and D2 (green) RUN mode, driven from the latch for
instant feedback; D3 (amber) on CLK, visible activity at panel speeds;
D4 (red) on RST, lit for the full stretched reset. The board takes power on
the 2-pin header H1 through the S1 on/off switch, with D8 (red, via R2) as
a supply-present indicator. The 4-pin output header H2 carries GND, VCC,
CLK and RST to the rest of the machine.

## 11. Bipolar-556 friendly

The bipolar NE556's outputs top out ~1.5 V below the rail — marginal into
74HC. R9/R10 (4k7 pull-ups on OUT1/OUT2) finish the pull to 5 V after the
Darlington high side stalls, restoring full HC noise margin with no extra
ICs. With the 100k trimmer, the bipolar part's threshold bias current is a
fraction of the charge current, so timing accuracy is a non-issue.

## 12. Expansion headroom

The standalone module ships with spare logic: two AND gates (U7 gate-c /
gate-d, inputs parked low) and two '273 flip-flops (FF6 / FF7). On the
integrated bring-up board the hardware breakpoint claims most of it — U7
gate-c / gate-d become the force-STEP and run-leg gates and '273 FF6 becomes
the matched-last sample (see `Blinky_Breakpoint.md`), leaving '273 FF7 as
the only free flip-flop. Both 74HC00s are now fully used — the
mode latch and step one-shot fill U3, the mux fills U6 — and all six
Schmitt inverters of U2 are in use. One known, accepted corner: a step
press and a mode change sampled on the same falling edge can race by one
gate delay — unreachable by human fingers at panel frequencies.
