# Addy I/O Module — User Guide

An 8-bit parallel I/O panel for TTL-class machines: one **tri-state input
port** (DIP switches or an external header) that drives a shared read bus
on demand, and one **registered output port** (LEDs plus a header) that
captures a write bus on a strobed clock edge. In a CPU it is the console:
the `IN` instruction reads the switches, the `OUT` instruction lights the
LEDs. The board is equally usable as a generic switch-and-lamp panel for
any bus-structured design.

Logic levels are 74HC at 5 V throughout. Two ICs, everything else passive.

## Block diagram

```
 SW1 ×8 (closed = 1)  ──┬──  IN[0..7]  ──  H5 "Input Port"
 RN1 10 k pulldowns  ───┘       │
                           U1 74HC541          /INOE ← H2.4 (10 k pullup R1)
                                │  (three-state)
                            BBUS[0..7]  ──  H3 "B Bus"

  H4 "Data Bus"  ──  D[0..7]
                        │
                   U2 74HC377          CLK_A  ← H2.2 (no pull — must be driven)
                        │              /LEDCE ← H2.3 (no pull — must be driven)
                    OUT[0..7]  ──┬──  H6 "Output Port"
                                 └──  D1–D8 LEDs → RN2 330 R → GND
```

## Parts

| Ref | Part | Role |
|---|---|---|
| U1 | 74HC541 | Input port buffer, three-state, onto BBUS |
| U2 | 74HC377 | Output register, loads from D, always drives OUT |
| SW1 | 8-way DIP switch | Input source; closed = logic 1 |
| RN1 | 10 k SIP-9 | Pulldowns on all eight input lines (open switch = 0) |
| RN2 | 330 R SIP-9 | LED current limit, common to GND |
| D1–D8 | LEDs (blue) | OUT0–OUT7, active high — lit = 1 |
| R1 | 10 k | Pullup on /INOE — input port defaults off the bus |
| C1 | 10 µF | Bulk capacitance at the power header |
| C2, C3 | 100 nF | Per-IC decoupling |

## Interface

| Header | Pin | Signal | Dir | Function |
|---|---|---|---|---|
| H1 "Power" | 1 | **VCC** | — | +5 V — **note pin order below** |
| | 2 | **GND** | — | Ground |
| H2 "Control" | 1 | GND | — | Ground |
| | 2 | CLK_A | in | Output-register clock, rising edge |
| | 3 | /LEDCE | in | Output-register load enable, active low, synchronous |
| | 4 | /INOE | in | Input-port output enable, active low, asynchronous |
| H3 "B Bus" | 1–8 | BBUS0–7 | out (3-state) | Read bus — input port drives here when /INOE low |
| H4 "Data Bus" | 1–8 | D0–7 | in | Write bus — output register D inputs |
| H5 "Input Port" | 1–8 | IN0–7 | in | External input lines, parallel with SW1 |
| H6 "Output Port" | 1–8 | OUT0–7 | out | Registered output, always driven |

**⚠ H1 pin order.** This board's power header is **VCC on pin 1, GND on
pin 2** — the *reverse* of the clock module's power header (GND pin 1).
A ribbon carried straight across from the clock module reverses polarity.
Check before power; label the connector.

Every net on the board is also carried on a named label (`D`, `BBUS`,
`IN`, `OUT`, `CLK_A`, `/LEDCE`, `/INOE`), so inside a combined simulation
project the module connects by name merge with no links; the headers are
the physical-build (and ribbon-link) interface.

## Input port

The '541 has both output enables in use: pin 19 is hard-grounded, pin 1 is
`/INOE`. Drive `/INOE` low and the eight input lines appear on BBUS,
bit-for-bit; release it and the port three-states within ~30 ns. R1 pulls
`/INOE` high, so an unconnected control header leaves BBUS untouched —
the module defaults to quiet on the shared bus.

The input lines themselves are a wired-parallel of three things: the DIP
switches (closed = tied to VCC = 1), the RN1 pulldowns (open = 0), and
the H5 header. H5 therefore serves two roles:

- **Monitor** — a listener on H5 reads the current switch settings.
- **External source** — an external driver on H5 takes over the input
  lines, **but only if the corresponding switches are open**. A closed
  switch is a hard tie to VCC; an external device driving that line low
  fights it directly. Rule: driving H5 from active logic means all eight
  switches open, always. Passive sources (more switches, jumpers to GND
  or VCC) merely need to not disagree.

There is no register on the input path — BBUS shows the live line state
whenever the port is enabled. Debounce and synchronisation are the host's
concern; in a machine that samples BBUS with an ordinary registered edge
(as Addy does), switch bounce is harmless for human-speed input.

## Output port

The '377 loads from the D bus on a rising CLK_A edge where /LEDCE is low,
and holds otherwise — the same synchronous-enable contract as the Code &
IR module's register: `/LEDCE` is sampled *by* the edge with setup and
hold, so it must be stable before the edge, not merely low sometime during
the state. There is no gated clock anywhere; CLK_A can be the free-running
machine clock.

The register's outputs are totem-pole and always drive: the LEDs (active
high through RN2), and H6. H6 is an output-only port — point-to-point
loads (a display board, a logic analyser, a downstream latch), never a
shared bus.

`CLK_A` and `/LEDCE` carry **no pullups** — unlike `/INOE`, they must be
driven whenever the board is powered. The asymmetry is deliberate: a
floating bus-facing enable could corrupt a shared bus, so it fails safe;
the clock and load enable belong to the host's clock domain and have no
safe stand-alone meaning. (In simulation the floating-input diagnostics
catch them; on copper, tie or drive them.)

The register's power-up contents are undefined — expect random LEDs until
the first load. A host wanting dark LEDs at reset should write zero early.

## Hooking up to a CPU (Addy)

| Module pin | Connect to |
|---|---|
| H1 | System +5 V / GND — **mind the pin order** |
| H2.2 CLK_A | The machine clock, CPU-side service copy (same edge domain as every register) |
| H2.3 /LEDCE | OUT strobe from the control GAL — low only in the T-state that executes `OUT` |
| H2.4 /INOE | IN strobe from the control GAL — low while `IN` needs the operand on the B bus |
| H3 BBUS | The operand B bus. The '541 is one of several three-state sources there; the host's decoder enforces one-at-a-time. The upper byte of a 16-bit read is zero-extended on the datapath, not here. |
| H4 D | The result/data bus (the value `OUT rs` writes) |

The named nets match Addy's own (`D`, `BBUS`, `CLK_A`), so dropping the
module into the Addy project connects the buses by label merge; only the
two strobes need routing from the control board.

## Standalone self-test

The module verifies itself with a ribbon and a clock, no CPU attached:
link **H3 to H4** (B Bus straight into Data Bus), ground `/INOE` and
`/LEDCE`, and feed a slow clock or debounced button into `CLK_A`. Every
clock edge copies the switches to the LEDs — walk the switches, watch the
lamps follow one edge behind. Remove the H3–H4 link before connecting to
a host.

## Absolute maximums and manners

Worst-case HC at 5 V: '541 enable-to-bus ~30 ns, data-through ~20 ns;
'377 clock-to-Q ~35 ns, D setup ~15 ns. Nothing here is ever the timing
long pole. BBUS discipline is the host's: this module guarantees only
that its own driver defaults off (R1) and turns on solely under /INOE.
The OUT header drives normal HC fan-out; the LEDs are already budgeted at
~10 mA per segment through RN2. Keep the DIP switches open whenever H5
carries an active external driver.
