# Blinky Hardware Breakpoint — Design & Features

A standalone, hardware run-to-PC breakpoint built into the PC + CALL +
Clock board. It compares the program counter against a switch-set address
and, on a match, halts the machine **at** that instruction and drops it
into single-step — no host computer involved. It complements, rather than
replaces, the Mega-driven breakpoint of the development rig: this one works
on a bare board with no Mega attached.

Designators follow the `Blinky_PC_-_4Bit_-_CALL_-_Clock` capture (W = 4):
U1 ’173 (PC), U13 ’688 (comparator), U10 ’00 (halt logic), U11 ’74 (HALT
flip-flop). It also borrows two spare gates of U9 ’08 and one spare
flip-flop (FF6) of the clock module’s ’273, so it adds only **three** ICs
plus the address switches.

## Summary

1. PC-vs-switch address comparator, switch-enabled
2. Synchronous HALT flip-flop — the heart, and why it’s glitch-free
3. Edge-triggered: one halt per entry, not a held level
4. Halts **at** the breakpoint cycle, not the one after
5. Drops the panel into STEP and lights the mode LED
6. Single-step and Run both continue cleanly from a breakpoint
7. Reset clears the breakpoint state and the PC

The hard-won design rules from bringing this up are collected at the end —
they are the reason the final circuit is shaped the way it is.

## 1. Address comparator

U13 (74688, 8-bit identity comparator) takes the four PC bits (U1 ’173
Q0–Q3 on pins 3–6) on its P inputs and the breakpoint address from four
DIP/tactile switches — S5–S8, labelled **BP0–BP3** — on its Q inputs, each
held by a 10k pull-up (R10–R13). The upper four comparator bits are tied to
GND on both sides, so they always match. The comparator’s enable /G (pin 1)
hangs on a fifth switch, S4 (**BP En**, pull-up R9): open the switch and the
breakpoint is disabled (/P=Q forced high); close it to arm.

When armed and PC equals the switch value, /P=Q (pin 19) goes low. It is a
purely combinational output — it follows the PC the moment the register
settles, with no clock of its own. That immediacy is essential (see §4) and
also the source of the trap that §2 exists to avoid.

## 2. Synchronous HALT flip-flop

The breakpoint’s state lives in one flip-flop: **U11 ’74 FF1 = HALT**,
clocked on /OSC (the same edge the clock module’s ’273 uses), with /CLR on
/RST and /PRE tied high. Q = HALT, /Q = /HALT.

This is the single most important choice in the design. /HALT is a
**registered** output: it changes only on a clock edge, exactly once, with
no combinational hazard. Everything the breakpoint does to the rest of the
machine is driven from /HALT, never from the live comparator. An earlier
attempt that drove the mode latch directly from the combinational /P=Q
oscillated the latch into metastability — a hung simulator — because a
glitch on an asynchronous SR latch’s set input is enough to tip it. Routing
through HALT launders the comparator’s combinational output into a clean
registered signal first. **This is the rule (see Design rules): never drive
an async latch from a combinational, clock-edge-aligned signal.**

## 3. Edge-triggered — one halt per entry

If the breakpoint forced the halt on the *level* of /P=Q, it could never be
escaped: the PC freezes at the breakpoint, so /P=Q stays asserted, so the
halt re-asserts forever. The fix is to fire on the match **edge** — the
cycle the PC *first* reaches the address — and then ignore the held level.

U10 (’00) computes this from /P=Q and one delayed copy:

- gate-a: `match_now = NAND(/P=Q, /P=Q)` — an inverter; high when PC = BP now
- the clock module’s ’273 FF6 samples /P=Q on /OSC → **Q6 = matched-last-cycle**
- gate-b: `/entry = NAND(match_now, Q6)` — low for exactly the entry cycle
- gate-c: `hold = NAND(HALT, /R)` — holds the halt until Run is pressed (/R)
- gate-d: `D_halt = NAND(/entry, hold) = entry OR (HALT · /R)`

So HALT sets on entry and stays set (via the `HALT · /R` feedback) until the
Run button pulls /R low, at which point D_halt drops and HALT clears on the
next /OSC edge. While sitting at the breakpoint, `entry` is false (Q6 has
caught up), so it never re-fires on the held match.

## 4. Halts on the breakpoint cycle, not the one after

Getting the machine to stop **at** the breakpoint instruction rather than
one past it is a timing constraint. The PC loads on the CLK rising edge; the
control flip-flops resample half a period later on /OSC falling. For the
machine to not advance past BP, the run clock must be gated before the next
rising edge.

/HALT does this **directly**, combinationally, through a spare U9 ’08 gate:

- gate-d: `run-enable = SEL_RUN · /HALT` → feeds the mux’s RUN-leg NAND

Because HALT is registered and asserts cleanly at the cycle boundary, the
run leg is gated the instant /HALT drops — before the next PC-advancing
edge. Relying on the mode latch alone to gate the clock would lose a cycle
to the ’273’s SEL_RUN resample race and stop one instruction late. Gating
the run leg with the registered HALT side-steps that race.

## 5. Drops the panel into STEP

A breakpoint should *look* like a breakpoint. /HALT also forces the mode
latch into STEP, through the other spare U9 ’08 gate:

- gate-c: `/S = (mode-set term) · /HALT` → mode-latch Set

When HALT is high, /S is pulled low and the latch shows STEP — the STEP LED
lights, and because the step path isn’t gated by HALT, the Step button works
immediately without first pressing the mode button. This is a *held* Set,
not a one-shot, which is safe **only because /HALT is registered**: its
single clean edge can never coincide with the Run button to release the
latch from both sides simultaneously (the condition that oscillates it). A
literal one-shot would need a NAND the board hasn’t got spare, and isn’t
necessary here.

## 6. Continue — Step or Run

- **Single-step:** you are already in STEP, so the Step button advances one
  instruction. HALT stays set (held by `HALT · /R`) so you keep stepping.
- **Run:** pressing Run pulls /R low; HALT clears on the next /OSC edge, /S
  releases cleanly while Run still holds /R (non-simultaneous → no
  oscillation), the latch resolves to RUN, and the PC steps off the address
  and free-runs to the next hit.
- **Run held through a loop:** each re-entry sets HALT for a single cycle,
  then clears (the `HALT · /R` hold needs /R high to maintain), so the
  machine pauses one cycle per hit and runs on — effectively “ignore
  breakpoints while Run is held.” HALT is a clean D flip-flop throughout, so
  none of this can hang.

## 7. Reset

/RST clears HALT through U11’s /CLR, so the machine never powers up halted by
a stale match. Reset also clears the PC: the ’173’s CLR (pin 15, async,
**active-HIGH** on this part) now sits on the active-high RST net, so the PC
goes to zero on reset and releases synchronously with the rest of the
machine. (Previously pin 15 was tied to GND and the PC retained its value
through reset.)

## Design rules (earned the hard way)

1. **Never drive an asynchronous latch from a combinational, clock-aligned
   signal.** Glitches at the edge tip an SR latch metastable; the simulator
   hangs and real hardware is unpredictable. Register the signal first
   (here: HALT), then drive from the registered output.
2. **For an on-time halt, gate the run clock with the registered halt signal
   directly** — not via the mode-select resample, which costs a cycle to the
   resample race and stops one instruction late.
3. **A held Set from a registered source is safe; a held Set from a
   combinational source is not.** The difference is whether the release can
   line up, within a gate delay, with the opposing latch input. Registered
   transitions land on clock edges; a human button never does.
4. **Never diode-OR onto a push-pull or weak-pull-up node** (carried over
   from the clock module). Once a node is actively driven, every other
   contributor has to be a gate input, not a diode — which is what retired
   the original 1N5817 breakpoint-OR in favour of this gated scheme.

## Bill of materials (breakpoint-specific)

Three added ICs, five switches, five pull-ups. Two gates of U9 ’08 and one
’273 flip-flop (FF6) are borrowed from existing chips — no new package.

| Ref | Part | Role | Qty |
|-----|------|------|-----|
| U13 | 74HC688 | 8-bit identity comparator — PC vs switch address | 1 |
| U10 | 74HC00 | Edge detect + halt logic (4 of 4 gates) | 1 |
| U11 | 74HC74 | HALT flip-flop (FF1; FF2 spare) | 1 |
| S4 | tactile / DIP switch | BP En (comparator enable, /G) | 1 |
| S5–S8 | tactile / DIP switch | BP0–BP3 (breakpoint address) | 4 |
| R9–R13 | 10k | Pull-ups for S4–S8 | 5 |

Borrowed, not added: U9 ’08 gate-c (force STEP) and gate-d (gate run leg);
U14 ’273 FF6 (matched-last sample). The PC ’173 (U1) CLR is rewired to the
active-high RST net — no new part.

**Stock:** 74688 — 3 on hand (`ChipInventory.md`); 74HC00, 74HC74 — see
clock-module stock. Switches/pull-ups from general stock.
