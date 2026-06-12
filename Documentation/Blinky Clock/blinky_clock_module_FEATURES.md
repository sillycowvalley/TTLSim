# Blinky Clock Module — Features

Companion to `blinky_clock_module.svg` and `blinky_clock_module_BOM.md`.
Five ICs plus a TO-92: NE556, 2× 74HC00, 74HC14, 74HC273, DS1813 — and an
optional socketed can oscillator.

## Summary

The module is the front panel for Blinky's clock: a pushbutton-selected
RUN / SINGLE-STEP clock source with a supervised reset and a jumpered
external-clock option. Its features are:

1. Pushbutton mode select (RUN / STEP) with an SR latch
2. Adjustable oscillator, ~3.2 Hz to ~330 Hz
3. External can clock via one jumper, conditioned to HC levels
4. Hardware-debounced single-step button
5. One single clock domain — every flip-flop on /OSC
6. Exactly-one-cycle step pulse
7. Glitch-free clock gating
8. Supervised reset: power-on + pushbutton, synchronous release
9. Clock silenced during reset
10. Four-LED front panel
11. Bipolar-556 friendly
12. Built-in expansion headroom

Outputs: **CLK** (the machine clock), **/RST** and **RST** (both reset
polarities, released synchronously).

## 1. Pushbutton mode select

Two momentary buttons (S2 = STEP, S3 = RUN) drive a cross-coupled NAND SR
latch (U2A/U2B). Pushbuttons hold their state in the latch, so there is no
slide switch to lever on a breadboard, and the latch itself absorbs contact
bounce — a bouncing press just re-asserts the state it already set. The
latch outputs also drive the mode LEDs directly, so the panel responds the
instant a button is pressed.

## 2. Adjustable oscillator

Timer 2 of the NE556 runs as an output-feedback astable: OUT2 charges and
discharges C2 through RV1 + R2, giving a ~50 % duty square wave with no
discharge-pin asymmetry. RV1 (100k trimmer) sweeps roughly 3.2 Hz to
330 Hz with C2 = 2.2 µ; R2 (1k) floors the resistance so the trimmer can
never short the timer's output into the capacitor.

## 3. External can clock — one jumper

J1 is a 3-pin header cut into the OSC line: the shunt selects the 556
astable or X1, a socketed can oscillator (e.g. 1 MHz). The cut sits on the
distribution side of the astable's feedback loop, so the 556 keeps
free-running either way and switchover is instant in both directions. The
can is hardwired through spare Schmitt inverter U4E, which squares any can
— TTL or HCMOS output — to clean rail-to-rail HC levels (the single-stage
inversion is harmless: all timing in the module is edge-relative). R11
(10k pull-up) keeps U4E's input defined when the socket is empty.
Everything downstream is frequency-agnostic; the tightest internal path
(the step-pulse settle chain) is guaranteed by datasheet maximums to about
3.5 MHz, with the breadboard itself the practical ceiling beyond that.
Habit: move J1 while holding reset.

## 4. Debounced single-step

The STEP button (S1) feeds timer 1 of the 556 wired as a pure Schmitt
trigger — THR and TRG tied, no timing parts — behind a 100k/470n RC. The
RC slews through the button's bounce; the timer's ⅓/⅔-VCC ratiometric
hysteresis (a 1.67 V band) turns the slow ramp into one clean edge per
press and per release. This is the Schuiki trick: a 555 as a debouncer
with better hysteresis than a logic-gate Schmitt.

## 5. Single clock domain

Every flip-flop in the module — reset synchronizer, step synchronizer,
and mode-select resampler — lives in one 74HC273 clocked on /OSC, the
inverted clock source. All state therefore changes only on OSC's falling
edge, while OSC is low; in RUN mode the machine clock's active (rising)
edges sit a half period away from every control transition. This is what
eliminates the runt-pulse and deadlock hazards of the asynchronous
designs we compared: there is nothing to interlock because nothing ever
changes mid-high. Whichever source J1 selects, the whole core follows it
— there is never a second domain.

## 6. One-cycle step pulse

The debounced step level is sampled through two '273 stages (Q2, Q3); the
edge detector Q2·NOT(Q3) (U4B, U3A, U4C) is true for exactly the one OSC
period after the press is first sampled. Hold the button for a minute —
the machine still advances exactly one clock, whether that clock is a
300 ms half-cycle from the 556 or 500 ns from the can. The pulse starts
and ends on falling edges, so the step path obeys the same half-period
margin as RUN mode.

## 7. Glitch-free clock gating

CLK = OSC·SEL_RUN + SP·SEL_STEP, built from three NANDs (U2C, U2D, U3B).
Because the select signals are resampled versions of the latch (FF4/FF5 of
the '273), they can only change while OSC is low — a mode change can never
slice a high clock pulse into a runt. The select resampler also removes the
mutual-interlock gates of the textbook design, and with them its deadlock
when the step source is idle.

## 8. Supervised reset

A DS1813 supplies power-on reset, and S4 hangs directly on its /RST pin —
the supervisor debounces the button and stretches every assertion to
~150 ms, with its own internal pull-up, no external parts. The raw reset
drives the '273's /MR asynchronously; FF0/FF1 (D0 = VCC daisy chain) then
release /RST synchronously, two falling edges after the button clears.
Asynchronous assert, synchronous release: the machine can be yanked into
reset at any time, but always leaves it cleanly aligned to the clock. U4D
provides the active-high RST for parts that want it.

## 9. Clock silenced during reset

Because /MR clears the select flip-flops along with everything else, both
mux legs deselect and CLK parks low for the whole of reset. Any in-flight
step pulse dies with FF2/FF3. On release, the selects reload from the
latch (the mode itself survives reset) on the first falling edge, the
clock resumes, and /RST lifts one edge later — the CPU's async-clear
registers sit safely at zero throughout.

## 10. Front panel

D1 (green) STEP mode and D2 (green) RUN mode, driven from the latch for
instant feedback; D3 (yellow) on CLK, visible activity at panel speeds
(a steady half-brightness glow on the can); D4 (red) on RST, lit for the
full stretched reset.

## 11. Bipolar-556 friendly

The bipolar NE556's outputs top out ~1.5 V below the rail — marginal into
74HC. R9/R10 (4k7 pull-ups on OUT1/OUT2) finish the pull to 5 V after the
Darlington high side stalls, restoring full HC noise margin with no extra
ICs. With the 100k trimmer, the bipolar part's threshold bias current is
~1.5 % of the charge current, so timing accuracy is a non-issue.

## 12. Expansion headroom

Spare on the board: two NAND gates (U3C/D), one Schmitt inverter (U4F),
and two '273 flip-flops (FF6/FF7). One known, accepted corner: a step
press and a mode change sampled on the same falling edge can race by one
gate delay — unreachable by human fingers at panel frequencies.
