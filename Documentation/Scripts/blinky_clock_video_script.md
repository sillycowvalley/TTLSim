# Blinky Clock Module — Video Script

Terse, incremental. Each section adds one feature to the board. Spoken lines in plain text, on-screen cues in brackets.

---

## Cold open

This is the clock for Blinky, my TTL stack machine. It does three jobs — free-run, single-step, and reset — and unlike most 555-based clocks out there, it does every one of them without a single glitch or a dropped step. That clean behaviour is the whole point of the design. Let me build it up a piece at a time.

[On screen: finished board, then fade to bare oscillator]

---

## 1. The heartbeat — astable oscillator

Start with the oscillator. One half of an NE556 — timer 2 — wired as an output-feedback astable. It charges and discharges C9 through the timing resistor and swings a clean fifty-percent square wave. That's the raw clock.

[Show OSC on scope]

---

## 2. Make it adjustable

The timing resistor is RV1, a hundred-K trimmer. Crank it and the rate drops to under a hertz — slow enough to watch each cycle on the LEDs. Back it off and it climbs. One knob, full speed range.

[Trim RV1, scope slows down]

One caveat: RV1 is a bare rheostat into the timing node, so don't trim it all the way to zero or you short the 556 output into the cap. A 1K series floor resistor is the fix — noted, not yet fitted.

---

## 3. Single-step, debounced

Now the manual side. The Step button feeds the *other* half of the 556 — timer 1 — wired as a pure Schmitt trigger, no timing parts, behind an RC. The slow ramp plus the timer's third-to-two-thirds hysteresis turns a bouncing press into one clean edge. A 555 as a debouncer, with a wider hysteresis band than any logic-gate Schmitt.

[Show bouncy button vs clean output]

---

## 4. RUN or STEP — a latch, not a switch

How do we pick between free-run and single-step? Two buttons into a cross-coupled NAND latch — half of a 74HC00. Step sets it, Run resets it. It *holds* its state, so there's no slide switch to snap off a breadboard, and the latch eats its own contact bounce. The outputs drive the mode LEDs directly, so the LEDs react the instant you press.

[Press STEP / RUN, LEDs toggle]

---

## 5. Combine them — glitch-free gate

Now mux the two sources into one clock line: CLK equals OSC when RUN, step-pulse when STEP. Four NANDs — the second '00. The trick is the select signals are *resampled* versions of the latch, so they only ever change while the clock is low. A mode change can never slice a high pulse into a runt. No glitches, ever.

[Show clean switchover mid-run]

---

## 6. Meet the '273

Before we wire those resampling flip-flops up, look at the chip they live in — because it's the heart of the board, and it's worth knowing cold.

The 74HC273 is an octal D register: eight D-type flip-flops in one twenty-pin package, sharing two control lines and nothing else. Pin 11 is the common clock; pin 1 is the common clear. On every rising edge of the clock, all eight D inputs — pins 3, 4, 7, 8, 13, 14, 17 and 18 — are copied to the eight Q outputs and held there until the next edge. That's the whole behaviour: sample on the edge, hold between edges.

The clear, pin 1, is active-low and asynchronous. Pull it low and every Q drops to zero immediately — it doesn't wait for a clock edge, it overrides the clock entirely. Let it back high and the register clocks normally again.

Two things it deliberately *doesn't* have. There's no load-enable — if you clock it, it loads; you can't tell it to sit out an edge. The chip that adds that is the '377. And there's no tri-state — the Q outputs always drive, so you can't float them onto a shared bus. That's the '574. For a holding register where nothing else is fighting for the outputs, you want neither, so the '273 is exactly the right part — and the cheapest.

And here's why it's *the* chip for this clock: eight flip-flops on one shared clock is a single clock domain in a single package — and that shared edge is what the entire glitch-free story rests on. The common asynchronous clear is the reset. One chip gives us both.

[Walk through the 74HC273 diagram — pinout, the shared CLK and /CLR, the function table]

---

## 7. One clock domain — why this clock doesn't glitch

This is where the board parts company with the usual 555 clock you'll find online. Most stop a few steps back: an oscillator, a step button, and a mode switch wired straight at the machine. They mostly work — until a mode change clips a clock pulse in half, or a quick tap on Step lands between two edges and vanishes. The machine downstream is usually forgiving, so nobody notices.

This one doesn't glitch and doesn't drop taps, and the reason is a single decision: every flip-flop on the board — reset sync, step sync, mode resample — lives in that one '273, clocked on the *inverted* oscillator. So all state changes land on one edge, while the clock's active edge sits a half-period away. Nothing changes mid-pulse. There's nothing to interlock because nothing races.

[Diagram: all FFs on /OSC]

---

## 8. The part most designs miss — a latched step

Here's the detail. A plain single-step samples the button level. Tap it between two clock edges and the press vanishes. So before sampling, I catch the press in a one-shot latch — the other half of the '00. A brief tap is *held* until the clock can see it.

Then it's sampled through two '273 stages and edge-detected, true for exactly one clock period. The one-shot self-clears one cycle later. Hold the button down for a minute — the machine still advances exactly one clock. Tap it — still exactly one.

[Hold button, single LED step]

---

## 9. Supervised reset

Reset is a DS1813 supervisor — three pins. It gives power-on reset for free, and the Reset button hangs straight on its open-drain pin; the part debounces it and stretches every press to about a hundred and fifty milliseconds.

The raw reset clears the '273 asynchronously — you can yank the machine into reset any time. But it *releases* synchronously, two clock edges after the button clears. Asynchronous assert, synchronous release: always leaves reset cleanly aligned to the clock.

[Press reset, show LED + release]

---

## 10. Reset forces STEP

And reset doesn't just clear — it parks the machine safe. Clearing the select flip-flops deselects both mux legs, so the clock parks low for the whole reset. Any in-flight step dies. Reset also drives the mode latch to STEP. So the machine always leaves reset *halted in single-step* — never free-running into a program before you're ready.

[Reset, STEP LED on, clock silent]

---

## 11. External clock — one jumper

If I want a real crystal rate instead of the 556, J2 is a three-pin jumper cut into the clock line. Shunt one way: the 556. Shunt the other: a socketed can oscillator, squared through a spare Schmitt inverter to clean HC levels. The 556 keeps free-running either way, so switchover is instant. Everything downstream is frequency-agnostic.

[Move jumper, can clock takes over]

---

## 12. Off the breadboard, onto a board

No front panel yet — but this is where the clock graduates off the breadboard onto a PCB, with the buttons and LEDs lined up in a neat row. Four indicators: green for STEP, green for RUN, amber on the clock, red on reset. Three buttons in line: Step, Run, Reset. Power comes in through an on/off switch with a red supply-present LED. A four-pin output header carries ground, five volts, clock, and reset to the rest of the machine.

And that header is the whole point. Four pins — ground, five volts, a clean clock, a clean reset — is everything any TTL machine needs from a clock. This board doesn't know or care what's hanging off it.

[Show the bare breadboard, then the finished PCB; pan across the buttons and LEDs]

---

## 13. One note on the 556

I used a bipolar NE556, and its outputs sit a volt and a half below the rail — marginal into 74HC. Two 4K7 pull-ups finish the pull to five volts after the output stalls. Full noise margin back, no extra chips. If you build this, that's the gotcha.

[Close on the pull-ups]

---

## Outro

So: six chips and a supervisor. Adjustable free-run, never-drop-a-tap single-step, glitch-free switching, and a reset that always lands you safe in step mode. The architecture owes its shape to Ben Eater's clock module — the engineering past that is mine.

And none of this is specific to Blinky. The board has no opinion about what it drives — it just puts a clean square wave and a clean reset on four pins. Drop it on the 4-bit testbed, the full 8-bit machine, or whatever register machine comes next. Build the clock once; reuse it on every design.

There's even spare logic on the board, which the hardware breakpoint claims on the integrated build — but that's the next video.

[End card]
