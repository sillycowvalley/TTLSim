# Blinky Hardware Breakpoint — Design & Features

A standalone hardware address breakpoint — a small card that compares an
address bus against a switch-set value and, on a match, halts the machine
**at** that cycle and drops it into single-step. No host computer is
involved: it works on a bare board with no Mega attached, and it complements
rather than replaces the Mega-driven breakpoint of the development rig.

It is built as its own card. The address bus comes in on input headers; a
short clock-and-halt handshake runs back to the clock module. Because it
watches a *bus* rather than tapping one CPU's program-counter pins, it drops
onto any machine whose address — or instruction-address — bus you can wire to
the input header. A jumper sets whether it compares 8 or 16 bits, so the same
card serves a 4-bit testbed or a full 16-bit machine.

## Summary

1. Identity comparator: address bus vs switch-set value, switch-armed
2. 8- or 16-bit compare, selected by one jumper
3. Synchronous HALT flip-flop — the heart, and why it's glitch-free
4. Edge-triggered: one halt per entry, on the *first* cycle of the match
5. Halts **at** the matched cycle, not the one after
6. Drops the machine into STEP and lights the mode LED
7. Single-step and Run both continue cleanly
8. Reset clears the breakpoint state
9. Watches the bus, so it breaks on **data** access too — a watchpoint for free
10. First-cycle stop makes it a CPU **bring-up** tool, not just a program debugger

The hard-won design rules are collected at the end — they are the reason the
circuit is shaped the way it is.

## 1. Address comparator

The compare is built from a pair of '688 8-bit identity comparators. The low
byte of the address bus arrives on the **ALSB** input header and the high byte
on **AMSB**; each comparator's other side is a bank of address switches
(**BP0–BPF**), each held by a 10k pull-up — two SIP-9 resistor networks carry
most of them. When the bus equals the switch value, the comparator's match
output goes low.

That output is purely **combinational** — it follows the bus the instant it
settles, with no clock of its own. The immediacy is essential (see §5) and is
also the source of the trap that §3 exists to avoid.

## 2. Eight or sixteen bits — one jumper

The two '688s cascade: the low comparator's match feeds the high comparator's
enable, so the high byte is only judged once the low byte already matches, and
the final match is taken from the high comparator. The **"8 Bit" jumper** taps
across the high comparator's enable and output. Leave it open for the full
16-bit cascade; close it to drop the high byte out and collapse the compare to
8 bits. One card, any width from a nibble to sixteen bits.

## 3. Arming

The low comparator's enable hangs on the **BP En** switch, held disabled by a
pull-up. Open the switch and the breakpoint is off — the match is forced
inactive; close it to arm. The card powers up disarmed, so it never halts the
machine on boot, and you choose when it goes live.

## 4. Synchronous HALT flip-flop

The breakpoint's state lives in one '74 flip-flop — **HALT** — clocked on /OSC
(the same edge the clock module's register uses), cleared by /RST, preset tied
high.

This is the single most important choice in the design. /HALT is a
**registered** output: it changes only on a clock edge, exactly once, with no
combinational hazard. Everything the breakpoint does to the rest of the
machine is driven from /HALT, never from the live comparator. An earlier
attempt that drove the mode latch straight from the combinational match
oscillated the latch into metastability — a hung simulator — because a glitch
on an asynchronous SR latch's set input is enough to tip it. Routing through
HALT launders the comparator's combinational output into a clean registered
signal first.

## 5. Edge-triggered — one halt per entry

If the breakpoint halted on the *level* of the match it could never be
escaped: the address freezes at the breakpoint, so the match stays asserted,
so the halt re-asserts forever. The fix is to fire on the match **edge** — the
cycle the bus *first* reaches the address — and then ignore the held level.

The '00 computes this from the match and one delayed copy:

- an inverter gives `match_now` — high while the bus equals BP
- a flip-flop clocked on /OSC gives `matched_last` — the same signal one cycle
  late (a spare flop on the clock module's register, over the handshake)
- `/entry = NAND(match_now, matched_last)` — low for exactly the first cycle
  of a match
- `hold = NAND(HALT, /R)` — keeps the halt set until Run is pressed
- `D_halt = NAND(/entry, hold) = entry OR (HALT · /R)`

So HALT sets on entry and stays set until the Run button pulls /R low, when it
clears on the next /OSC edge. Sitting at the breakpoint, `matched_last` has
caught up, `entry` is false, and it never re-fires on the held match.

## 6. Halts on the matched cycle, not the one after

Stopping **at** the matched cycle rather than one past it is a timing
constraint. /HALT does it **directly**, combinationally, through a spare AND
gate on the clock module: the run leg of the clock mux is gated by
`SEL_RUN · /HALT`. Because HALT is registered and asserts cleanly at the cycle
boundary, the run leg is cut the instant /HALT drops — before the next
address-advancing edge. Relying on the mode latch to gate the clock instead
would lose a cycle to its resample race and stop one instruction late.

This is what makes the card useful for **bringing up a CPU**, not just
debugging a program on it: it freezes on the very first cycle the address
appears, so you can stop the machine on the exact cycle you want to inspect
and walk it forward by hand. More on that in §10.

## 7. Drops the machine into STEP

A breakpoint should *look* like one. /HALT also forces the mode latch into
STEP through a second spare AND gate on the clock module, so the STEP LED
lights and the Step button works immediately without first pressing the mode
button. This is a *held* Set, not a one-shot — safe **only because /HALT is
registered**: its single clean edge can never coincide with the Run button to
release the latch from both sides at once, the condition that oscillates it.

## 8. Continue — Step or Run

- **Single-step:** you are already in STEP, so Step advances one cycle; HALT
  stays set (held by `HALT · /R`), so you keep stepping.
- **Run:** Run pulls /R low, HALT clears on the next /OSC edge, the mode latch
  resolves to RUN while Run still holds (non-simultaneous, so no oscillation),
  and the machine steps off the address and free-runs to the next hit.
- **Run held through a loop:** each re-entry sets HALT for one cycle, then
  clears — the machine pauses a cycle per hit and runs on. Effectively "ignore
  breakpoints while Run is held." HALT is a clean D flip-flop throughout, so
  none of this can hang.

## 9. Reset

/RST clears HALT, so the machine never powers up halted by a stale match. On
an integrated board the same reset net also zeroes the CPU's program counter,
so the machine leaves reset cleanly aligned; on the standalone card, clearing
the bus source is the host's business — the card only guarantees HALT is
clear.

## 10. Two features that fall out of the design

**It breaks on data, not just instructions.** Because the card watches an
address *bus* rather than a dedicated PC tap, on a shared-bus (von Neumann)
machine it matches on **any** access to the target address — an instruction
fetch *or* a data read or write. That is a hardware watchpoint for free: arm
it on a variable's address and the machine stops the moment anything touches
it. If instead you only want to break on the *program counter* — the classic
"stop when execution reaches this line" — qualify the enable with the CPU's
fetch/SYNC strobe, a one-wire addition into the comparator enable, so it only
compares while the bus carries the PC. On a single-cycle or Harvard machine
the distinction disappears: the instruction-address bus only ever carries the
PC, so the same card is a pure program breakpoint with nothing added.

**It stops on the first cycle.** The edge-trigger halts on the very first
cycle the address appears — exactly what you want when you are debugging the
*machine* rather than a program: bringing up a new datapath, checking that an
instruction lands on the right cycle, watching a counter or an address line
reach a value. Set the value, run, and the hardware freezes on the cycle it
appears, every time.

## Design rules (earned the hard way)

1. **Never drive an asynchronous latch from a combinational, clock-aligned
   signal.** Glitches at the edge tip an SR latch metastable; register the
   signal first (here: HALT), then drive from the registered output.
2. **For an on-time halt, gate the run clock with the registered halt
   directly** — not via the mode-select resample, which costs a cycle to the
   resample race and stops one instruction late.
3. **A held Set from a registered source is safe; from a combinational source
   it is not.** Registered transitions land on clock edges; a human button
   never does, so their releases can't line up to oscillate the latch.
4. **Never diode-OR onto a push-pull or weak-pull-up node.** Once a node is
   actively driven, every other contributor must be a gate input, not a diode
   — which retired the original 1N5817 breakpoint-OR in favour of the gated
   scheme.

## The handshake to the clock module

The card is not fully self-contained, by choice — it leans on the clock module
for the few resources already sitting there. A small handshake header carries
/OSC and /RST in (the card's HALT flop and edge logic run in the clock's
domain), HALT and the match terms out (to the two spare AND gates that gate
the run leg and force STEP), and ground and supply. Keeping these on the clock
module means the breakpoint adds no flip-flop or gate it doesn't strictly
need.

## Bill of materials

Four ICs, the address and enable switches, their pull-ups, and one jumper. Two
AND gates and one flip-flop are borrowed from the clock module over the
handshake — no new package for those.

| Part | Role | Qty |
|------|------|-----|
| '688 | 8-bit identity comparator (low + high byte, cascaded) | 2 |
| '00 | Edge detect + halt logic | 1 |
| '74 | HALT flip-flop (one flop spare) | 1 |
| switch | BP0–BPF breakpoint address | 16 |
| switch | BP En (comparator enable) | 1 |
| SIP-9 resnet | address-switch pull-ups | 2 |
| 10k | enable / spare pull-ups | as needed |
| jumper, 2-pin | 8- / 16-bit select | 1 |
| input header | address bus in (ALSB, AMSB) | 2 |
| handshake header | to clock module | 1 |

Borrowed, not added: two clock-module AND gates (force STEP, gate run leg) and
one clock-module flip-flop (matched-last sample).
