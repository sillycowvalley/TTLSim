# TEENSY41 Schematic Symbol ↔ GPIO Pin Table

The TTLSim `TEENSY41` part models the Teensy 4.1's 48 through-hole pins in
their **physical positions** — the DIP-48 outline PJRC defines, socket-strip
compatible. Pin numbers are solder positions; labels are the harness
functions assigned to each GPIO. This table must agree with
`ChipPartDefinition.cs` (the symbol) and `AdapterPorts.h` (the functions).

Verified against `AdapterPorts.h` rev 2 (Teensy-only, 2026-07): all five
ports, both DIR pins and the SW0/SW1 sharing agree.

Power pins sit where the product puts them: symbol 1/34/47 (GND),
15/46 (3.3 V), 48 (Vin). The 3.3 V pins are deliberately unconnected in
the schematic (the rail feeds the LVC245s); Vin is hard-wired to the 5 V
rail (always USB-powered). **The onboard LED is GPIO 13 = symbol pin 35 =
PE0/CLK: the LED blinks with the clock.**

## Side 1 (symbol pins 1–24, top to bottom)

| Symbol pin | Teensy GPIO | Role | Ribbon colour |
|---|---|---|---|
| 1 | — | GND | — |
| 2 | 0 | PA0 | white |
| 3 | 1 | PA1 | grey |
| 4 | 2 | PA2 | violet |
| 5 | 3 | PA3 | blue |
| 6 | 4 | PA4 | green |
| 7 | 5 | PA5 | yellow |
| 8 | 6 | PA6 | orange |
| 9 | 7 | PA7 | red |
| 10 | 8 | PB0 | white |
| 11 | 9 | PB1 | grey |
| 12 | 10 | PB2 | violet |
| 13 | 11 | PB3 | blue |
| 14 | 12 | PB4 | green |
| 15 | — | 3.3V | — |
| 16 | 24 | PD0 | white |
| 17 | 25 | PD1 | grey |
| 18 | 26 | PD2 | violet |
| 19 | 27 | PD3 | blue |
| 20 | 28 | PD4 | green |
| 21 | 29 | PD5 | yellow |
| 22 | 30 | PD6 | orange |
| 23 | 31 | PD7 | red |
| 24 | 32 | PB5 | yellow |

## Side 2 (symbol pins 25–48; pin 25 sits opposite pin 24)

| Symbol pin | Teensy GPIO | Role | Ribbon colour |
|---|---|---|---|
| 25 | 33 | DIR1 (port D direction) | — |
| 26 | 34 | DIR0 (port C direction) | — |
| 27 | 35 | PE1 | grey |
| 28 | 36 | PE2 | violet |
| 29 | 37 | PE3 | blue |
| 30 | 38 | PE4 | green |
| 31 | 39 | PE5 | yellow |
| 32 | 40 | PE6 (shared SW0 via J6) | orange |
| 33 | 41 | PE7 (shared SW1 via J7) | red |
| 34 | — | GND | — |
| 35 | 13 | PE0 (CLK — onboard LED = clock activity light) | white |
| 36 | 14 | PB6 | orange |
| 37 | 15 | PB7 | red |
| 38 | 16 | PC0 | white |
| 39 | 17 | PC1 | grey |
| 40 | 18 | PC2 | violet |
| 41 | 19 | PC3 | blue |
| 42 | 20 | PC4 | green |
| 43 | 21 | PC5 | yellow |
| 44 | 22 | PC6 | orange |
| 45 | 23 | PC7 | red |
| 46 | — | 3.3V | — |
| 47 | — | GND | — |
| 48 | — | Vin (→ 5 V rail) | — |

Notes:
- PE6/PE7 reach the Teensy through the SW bus and jumpers J6/J7; in
  button position those GPIOs are SW0/SW1 instead (through 1 k legs).
- Symbol pin numbers, being physical, also order the loom: PE1–PE7 and
  DIR0/DIR1 sit consecutively (symbol 25–33), with PE0 apart at symbol 35
  — the price of the LED riding the clock, paid knowingly.

---

## GPIO Bank Membership — Which Ports Slew As One

The RT1062's fast GPIO is four banks (GPIO6–9). A port whose eight pins all
land in **one** bank can be written with a single register store and read
with a single load, so every bit changes — and is sampled — on the same
clock edge. A port split across banks changes in groups.

This matters for two things and nothing else: coupling stress (a
single-bank port is the harshest, most realistic bus-turnaround aggressor)
and timing measurement (only a single-bank port gives a write-to-sample
interval short enough to resolve the '245 pair's ~15 ns propagation). It
does **not** matter for speed — banked and unrolled I/O measured within 1–2%
of each other.

| Port | Banks | Source |
|---|---|---|
| A | **3** | measured, Level245Loop 2026-07 |
| C | **1** | measured, Level245Loop 2026-07 |
| B, D, E | not yet recorded | print with `printPortBanks()` on first use |

**Port C is the single-bank port.** For the '574 phantom-capture chase, the
operand bus belongs on Port C: it is the only port measured to present a
coherent byte in one instant, and it also has runtime direction (DIR0) for
tri-state work. Record B, D and E the first time each is banked — the boot
banner prints them via `printPortBanks()`, so it costs nothing.

Bank membership follows the GPIO pins themselves, not direction, so a port's
count is the same whether it is driving or reading.

---

## Two Hardware Consequences of This Mapping

**PE0 carries the onboard LED, and PE0 is the clock.** The LED and its
series resistor hang on the clock line's Teensy side, adding a few mA of
load and some capacitance to the one signal whose edge timing matters most.
Irrelevant at retro bus speeds and genuinely useful as an activity light —
but for the chase test, where the clock edge is the time reference against
which data slews are positioned, expect PE0's edge to be marginally slower
than the other GPIOs. If that ever shows up as skew, the LED is the first
suspect, and cutting it is the fix.

**Vin is hard-wired to the 5 V rail.** That is correct for USB power, where
the Teensy sources 5 V outward to the board. It also means an external 5 V
supply must never be applied to that rail while USB is connected, or it
back-feeds the host through the VUSB pad. If bench-supply operation is ever
wanted, the VUSB pad has to be cut first — and then USB alone will no longer
power the board. Note the choice on the board silkscreen if possible.
