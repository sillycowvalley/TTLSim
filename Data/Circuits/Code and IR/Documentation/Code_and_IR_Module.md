# Code & IR Module — User Guide

A registered word store for TTL-class machines: 32 K × 16 of EEPROM behind
a 16-bit output register, on one board with one contract — **address in,
registered word out**. In a CPU it is the fetch unit: program memory plus
instruction register. Fitted differently (see Output stage) the same board
is a clocked lookup engine or a transparent, bus-attachable ROM port.

The board is byte-lane symmetric: one EEPROM plus one register chip is a
complete 8-bit lane (U1+U3 high byte, U2+U4 low byte). Narrower machines
populate one lane; the address side is common.

Logic levels are 74HC at 5 V throughout. EEPROM outputs are permanently
enabled — /CE and /OE are tied to ground and /WE to VCC, so the store is
purely combinational and can never be written in-circuit. The ROM data
nets are private, point-to-point into the register D-inputs, and leave the
board only through the register stage.

Capture: `Code_and_IR.ttlproj`. 4 ICs + passives.

## Block diagram

```
              A[0..14]  (A bus in; A15 arrives but is not decoded)
                  │
        ┌─────────┴─────────┐
        │                   │
   U1 28C256           U2 28C256         /CE, /OE → GND (always driving)
   high bytes          low bytes         /WE → VCC (write-protected)
        │                   │
   D[7:0] (private)    D[7:0] (private)
        │                   │
   U3 '37x             U4 '37x           pin 11 ← H5.2 (10 k pullup R2)
        │                   │            pin 1  ← H5.3 (10 k pullup R3)
     IR[15..8]           IR[7..0]
```

## Parts

| Ref | Part | Role |
|---|---|---|
| U1 | AT28C256 (32 K × 8 EEPROM) | Program store, **high byte** → IR[15:8] |
| U2 | AT28C256 (32 K × 8 EEPROM) | Program store, **low byte** → IR[7:0] |
| U3 | 74HC377 / '374 / '373 | Output register, high byte |
| U4 | 74HC377 / '374 / '373 | Output register, low byte |
| R2 | 10 k | Pullup on the shared pin-11 line (CP / LE) |
| R3 | 10 k | Pullup on the shared pin-1 line (/E / /OE) |
| C1–C4 | 100 nF | One decoupler per IC socket (house policy) |

## Interface

| Header | Pin | Signal | Dir | Function |
|---|---|---|---|---|
| H1 | 1–8 | A0–A7 | in | Word address, low byte |
| H2 | 1–7 | A8–A14 | in | Word address, high bits |
| H2 | 8 | A15 | in | Not decoded on-board; see A15 below |
| H3 | 1–8 | IR0–IR7 | out | Registered word, low byte |
| H4 | 1–8 | IR8–IR15 | out | Registered word, high byte |
| H5 | 1 | GND | — | Ground |
| H5 | 2 | CP / LE | in | Register clock (or latch enable — per fitment) |
| H5 | 3 | /EN | in | Load enable (or output enable — per fitment) |
| H5 | 4 | VCC | — | +5 V |
| H6 | 1–2 | A15 strap | — | Shunt ties A15 to GND |

H5 pins 2 and 3 are deliberately unnamed on the schematic: their meaning
depends on which register chip is fitted. Both carry on-board 10 k pullups
(R2, R3), so an unconnected control pin is always at a defined level.

## Output stage — three fitments, one footprint

U3/U4 accept any of the '373/'374/'377 family. All three share the same
20-pin footprint, and the substitution is **pin-exact** — no wiring
changes, no adapters:

| Pin(s) | '377 | '374 | '373 |
|---|---|---|---|
| 1 | /E — synchronous load enable | /OE — output enable | /OE — output enable |
| 11 | CP — clock, rising edge | CP — clock, rising edge | LE — latch enable, level |
| 3, 4, 7, 8, 13, 14, 17, 18 | D0–D7 | same | same |
| 2, 5, 6, 9, 12, 15, 16, 19 | Q0–Q7 | same | same |
| 10 / 20 | GND / VCC | same | same |

Only pins 1 and 11 change *meaning*; the board routes pin 11 to H5.2 via
R2 and pin 1 to H5.3 via R3, so all three fitments use the same header
pins.

| Fitted | H5.2 is | H5.3 is | Behavior |
|---|---|---|---|
| **74HC377** | CP | /E, synchronous | Loads on rising CP edges where /E is low; **outputs always drive**. The CPU fetch fitment. |
| **74HC374** | CP | /OE, asynchronous | Loads on **every** rising edge; outputs three-state under /OE. Clocked bus port. |
| **74HC373** | LE | /OE, asynchronous | Transparent while LE high, holds while low; outputs under /OE. With LE left pulled high: pure three-state ROM buffer. |

Pros and cons:

- **'377** — the only fitment whose load can be *qualified* without gating
  the clock: /E is sampled synchronously at the edge, so "load exactly
  once per fetch" falls out for free. The price is totem-pole outputs that
  always drive — the IR nets must be point-to-point; the module can never
  sit on a shared bus in this fitment.
- **'374** — gains three-state outputs (bus-attachable, host-controlled
  /OE) but loses the load enable: it captures on *every* rising edge. To
  hold a value the host must either freeze the address (fine in machines
  where the address source is stable across the relevant states) or gate
  the clock (don't — one clock domain, no gated clocks). Good as a
  registered, bus-facing lookup port.
- **'373** — a transparent latch, not a flip-flop. While LE is high the
  outputs follow the ROM combinationally; drop LE to freeze. With LE left
  on its pullup the board degenerates to a plain three-state ROM buffer —
  the async-memory fitment. The transparency that makes that possible is
  exactly why it cannot be an IR: an output that tracks its own address
  inputs mid-instruction is a feedback hazard.

Defaults with nothing driving H5 (pullups win): '377 holds its last word
and drives it; '374 and '373 are silent (three-stated; the '373 is also
transparent behind its closed outputs). Design rule embodied: internal
nets fail toward working (ROM outputs enabled), bus-facing controls fail
toward quiet.

'374/'373 fitments provide **no bus pulls** — the host's bus defines the
idle state. Do not fit '573/'574 here: same functions, different
(broadside) pinout, not footprint-compatible.

## Hooking up to a CPU (Addy)

Fit **'377s**. This is not a preference: a CPU's IR must load exactly once
per instruction and *hold* while its own execution changes the address
feeding the EEPROMs. Only the '377's synchronous enable gives
load-once-per-fetch without gating the clock.

| Module pin | Connect to |
|---|---|
| H1.1–H2.7 (A0–A14) | Register file QA[14:0] — the PC drives the address directly during fetch; no MAR |
| H2.8 (A15) | Nothing. Fit the H6 shunt (see A15 below). |
| H3 (IR0–7) | Datapath imm8 '541 inputs; IR[2:0] continues to the register file as BADDR |
| H4 (IR8–15) | Control board: GAL inputs IR[15:11], ASEL/WOR steering IR[10:8] (IR[10:5] via H3/H4 as fielded) |
| H5.2 | CLK — the machine clock, same edge domain as every register (Addy: the CLK_A service copy) |
| H5.3 | /IRCE from the control GAL — low during T0 only, so the load edge is the edge that ends fetch |
| H5.1 / H5.4 | System GND / +5 V |

**Timing contract.** /EN is sampled by the clock edge with setup/hold like
a D input — it must be stable before the rising edge, not merely "low
sometime during the state." Address-to-load: the word is fetchable when
the address has been stable for the EEPROM access time plus register setup
(~165 ns worst-case with 150 ns parts); in Addy this is the T0 high-phase
budget, and the PC is frozen for all of T0 by construction. After the load
edge, IR is valid at the pins one register clock-to-Q later (~35 ns) and
holds until the next enabled edge.

**Sequencer dry runs**: H3/H4 double as the strap point — disconnect the
module and strap the control board's IR inputs to NOP (0x1000) to exercise
the sequencer with no fetch path present.

## Standalone self-test

The module verifies with no CPU attached: DIP switches (with pulldowns) on
H1/H2, LEDs on H3/H4, H5.3 grounded, and a debounced button or slow clock
into H5.2. Each press latches and displays the addressed word — walk the
address switches through a programmed pattern and read the LEDs. With a
'373 fitted and H5.2 left pulled high, the LEDs track the switches with no
clocking at all.

## Programming

The two EEPROMs split every 16-bit word into byte lanes: **U1 holds the
high bytes** (IR[15:8]), **U2 the low bytes** (IR[7:0]), both at the word
address. The assembler tooling emits one hex file per lane. Sockets are
specified as ZIF — this is the board that gets reburned constantly, and
pulling chips must never disturb wiring.

In the simulation capture, the same split applies to the embedded Intel
HEX `program` images on U1 and U2; `propagationDelayNs` is left null, so
the simulator uses the part's default (150 ns) speed grade.

An erased EEPROM reads 0xFFFF at every address. Addy encodes HLT as 0xFFFF
precisely so that a runaway PC into blank memory freezes the machine
rather than executing noise; other hosts inherit whatever their all-ones
word means and should choose it as carefully.

## A15 and capacity

The 28C256 decodes A[14:0]: 32 K words. A15 arrives at H2.8, is not
decoded on-board, and exists for hosts with bank-select ambitions. With
nothing driving it, fit the H6 shunt (A15 → GND) so the pin is defined;
**remove the shunt before any host drives A15** — it is a hard short to
ground otherwise, and the header should be labelled so.

For a 16-bit-PC host like Addy, addresses 0x8000+ execute the mirror of
0x0000+. Harmless — but a wrong jump target may *appear to work*, which is
worth remembering mid-debug.

Larger parts: the W27C512 (64 K × 8) is **not** usable on this board as
built — its pins 1/27 are A15/A14 where the 28C256 has A14//WE, and both
are hard-wired here. 64 K support means per-socket jumpers on pins 1 and
27 in a future revision; recorded so nobody discovers it with a programmer
in hand.

## Absolute maximums and manners

Worst-case datasheet timing, HC at 5 V, 150 ns EEPROMs: address stable to
loadable word ~165 ns; register clock-to-Q ~35 ns; the module is happy far
beyond any host clock the register-file write contract permits. The
register outputs are ordinary HC totem-pole ('377) or three-state
('374/'373) drivers — observe normal fan-out, and on shared buses the host
enforces one-driver-at-a-time; this module's contribution is that its
drivers default off.
