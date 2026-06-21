# Blinky Breakpoint Card — Video Script

Terse, incremental. Each section adds one piece. Spoken lines in plain text,
on-screen cues in brackets.

---

## Cold open

This is the breakpoint card for Blinky. It watches an address, and the instant the machine hits it, the whole thing freezes — at that cycle, not the one after — and drops into single-step. No computer attached. Last video built the clock; this one builds the thing that stops it on a number you choose. Piece at a time.

[Finished card, then fade to a bare '688]

---

## 1. Meet the '688

The chip doing the watching is a 74HC688 — an eight-bit identity comparator. Worth a minute before we wire it, because everything the card does starts here.

It has two eight-bit inputs: word P on one side, word Q on the other. Both are just inputs — neither is a register, nothing is clocked. The '688 is purely combinational; it does one thing, continuously, and asks one question: are these two words identical? The single output — P-equals-Q — is active-low. It goes low only when all eight bit-pairs match *and* the enable, /G, is held low.

That enable is the clever pin. Hold /G high and the output is forced high no matter what the data does — the comparator is switched off. Which is exactly how you cascade them: wire one '688's output straight into the next one's /G, and the second only speaks up if the first already matched. That's how two of them become a sixteen-bit compare — coming up in a moment.

One limit worth naming: it's *identity* only — equal or not equal, no greater-than, no less-than. That's a different family, the '682 and '685. For a breakpoint, equal is all we want.

[Walk through the 74HC688 diagram — the P and Q words, the /G enable, the active-low output, the function table]

---

## 2. The comparator

Now put one to work. One side — word P — takes the address bus in on a header; the other side — word Q — is a row of switches. Dial a number into the switches, and the moment the bus equals it, that active-low match snaps low. That's the whole trigger: a bus, a number, and a compare.

[Switches set, scope the match line snapping low]

---

## 3. Eight bits or sixteen — one jumper

One '688 is eight bits. Add a second for the high byte and chain them — the low one has to match before the high one even looks, through that /G trick — and now it's a sixteen-bit compare. Then a single jumper, labelled "8 Bit," shorts the high comparator out when you don't want it. So the same card breaks on a four-bit testbed or a full sixteen-bit machine. One jumper, any width.

[Drop the jumper, show 8-bit vs 16-bit]

---

## 4. Arm it

The compare hangs on an enable, and the enable sits behind one more switch held off by a pull-up. So the card powers up *disarmed* — no surprise halts on boot. Flip BP En and it's live.

[Toggle BP En]

---

## 5. A quick recap — the '74

The halt itself lives in a 74HC74, so a thirty-second refresher. It's two independent D flip-flops in one package, positive-edge clocked. On each rising clock edge, whatever's sitting on D is captured to Q, and the opposite appears on /Q — sample on the edge, hold until the next one.

The two override pins are what matter to us: preset and clear, both asynchronous, both active-low. Clear low slams Q to zero immediately, ignoring the clock; preset low forces it to one. (Don't pull both low at once — that's the one illegal state, where Q and /Q both go high and stop being complements.)

We use just one of the two flip-flops. Its clock is the same inverted oscillator the clock board already runs on; its clear is wired to reset, so a reset wipes the halt; and preset we tie high and forget. That's the whole part — and the next step is why running the match *through* it, instead of using the match raw, is the single most important decision on the board.

[Walk through the 74HC74 diagram — D captured on the edge, the async preset and clear, the function table]

---

## 6. The heart — a registered halt

Here's the part that matters. That match line is raw and combinational — it twitches the instant the bus settles. Drive anything stateful straight off it and a glitch at the wrong moment wrecks you; my first attempt drove the mode latch directly and oscillated it into a hung machine.

So the match goes through that '74 first — clocked on the same inverted-oscillator edge as the rest of the clock board — and the flip-flop's output is HALT. Everything downstream runs off that registered HALT, never the live match. One clean edge, no hazard.

[Diagram: match → '74 → HALT]

---

## 7. Fire once, on entry

A held match is a trap. The address freezes on the breakpoint, so the match never lets go, so a level-triggered halt would re-fire forever. The fix is to catch the *edge* — the first cycle the bus reaches the address. A '00 compares the match now against a one-cycle-delayed copy; they disagree for exactly one cycle, and that's the entry. It sets HALT, holds it until you press Run, and ignores the held level the whole time it's parked.

[Show one clean halt on entry, not a stream]

---

## 8. Stop at the cycle, not after

The halt has to land *on* the breakpoint cycle, not one past it. So registered HALT gates the run clock directly — kills the free-run leg the instant it asserts, before the next edge can advance the address. That's the difference between stopping *at* your instruction and stopping one too late.

And that first-cycle stop is the whole point when you're debugging the CPU itself, not a program on it: set an address, run, and the hardware freezes the exact cycle it appears.

[Freeze on the matched value, single edge]

---

## 9. It looks like a breakpoint

A halt should look like one. The same registered HALT forces the mode latch into STEP — the STEP LED lights, and the Step button works straight away without reaching for the mode button first. From here you single-step by hand, or press Run to clear the halt and free-run to the next hit. Hold Run through a loop and it pauses a cycle per pass and carries on.

[STEP LED on, step by hand, then Run]

---

## 10. Reset, and the handshake back to the clock

Reset clears HALT, so the machine never wakes up frozen on a stale match. And the card doesn't stand fully alone — a short handshake to the clock board carries the oscillator and reset in, and HALT back out to gate the clock and flip the mode. It borrows a couple of spare gates already sitting over there rather than add its own.

[Show the handshake header between the two boards]

---

## 11. The feature I didn't plan — a watchpoint

Here's a bonus. The card watches a *bus*, not a dedicated program-counter tap. So on a machine that runs data and instructions over one bus, it breaks on *any* access to your address — a fetch, sure, but a load or a store too. Point it at a variable and the machine stops the instant anything touches it. That's a hardware watchpoint, for free.

Want it to ignore data and only catch the program counter? Gate the enable with the CPU's fetch strobe — one wire — and it's a pure code breakpoint again.

[Show a break on a data write, not a fetch]

---

## 12. The test circuit — both boards at once

To prove it, the simplest rig: a '161 counter, clocked straight off the clock module, its outputs running into the breakpoint's address inputs and onto a row of LEDs. Dial a number into the switches, hit Run, and watch the counter climb the LEDs — until it hits the number, and the whole thing freezes on that count. One test exercises both boards: the clock drives the count and obeys the halt; the breakpoint catches the value.

[Counter climbs the LEDs, stops dead on the set number]

---

## 13. On a board

Off the breadboard now — comparators, halt flop, the switch banks, the jumper, all laid out on a PCB with the address switches in a neat row and the LEDs beside them. Headers for the address bus in, a handshake to the clock, and the counter test circuit alongside.

[Bare breadboard, then the finished card running the counter test]

---

## Outro

So: four chips and a handful of switches. A sixteen-bit address compare you can narrow to four, a glitch-free registered halt, a stop that lands on the exact cycle, and a watchpoint that came along for free. Wire it to any machine's address bus — four bits or sixteen, Harvard or shared-bus — and it stops the thing on a number.

Next, it stops standing alone: the spare logic folds into the CPU board, and the breakpoint becomes part of the machine.

[End card]
