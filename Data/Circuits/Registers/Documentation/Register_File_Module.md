# Register File Module — User Guide

**8 registers × 16 bits, two read ports, one write port.** Built from
sixteen 74HC670 register files in two lockstep arrays behind a set of
ribbon headers.

The module is synchronous-write, asynchronous-read: writes commit under
the system clock, reads are combinational and always live.

## Contents

| Function | Parts |
|---|---|
| Register array A (port QA) | U1–U8, 74HC670 |
| Register array B (port QB) | U9–U16, 74HC670 |
| Write decode + port A read decode | U17, 74HC139 |
| Port B read decode | U18, 74HC139 |
| Write gating | U19, 74HC00 |
| Decoupling | 19 × 0.1 µF, one per package |

**19 ICs.** There are no resistors on this board — see *No pulldowns*.

## Interface

Ten headers, not a single backplane connector. Six 8-pin, three 4-pin,
one 3-pin: 63 pins total. Logic levels are 74HC at 5 V.

| Header | Pins | Signal | Pin order |
|---|---|---|---|
| H1 | 8 | D[7:0] | pin 1 = D0 … pin 8 = D7 |
| H2 | 8 | D[15:8] | pin 1 = D8 … pin 8 = D15 |
| H3 | 8 | QA[7:0] | pin 1 = QA0 … pin 8 = QA7 |
| H3 | 8 | QA[15:8] | pin 1 = QA8 … pin 8 = QA15 |
| H4 | 8 | QB[7:0] | pin 1 = QB0 … pin 8 = QB7 |
| H4 | 8 | QB[15:8] | pin 1 = QB8 … pin 8 = QB15 |
| H5 | 4 | BADDR[2:0], /OEB | pins 1–3 = bits 0–2, pin 4 = /OEB |
| H6 | 4 | AADDR[2:0], /OEA | pins 1–3 = bits 0–2, pin 4 = /OEA |
| H7 | 4 | WADDR[2:0], WE | pins 1–3 = bits 0–2, pin 4 = WE |
| H8 | 3 | VCC, CLK, GND | pin 1 = VCC, pin 2 = CLK, pin 3 = GND |

In every multi-bit header **pin 1 carries bit 0** and bits ascend with
pin number.

| Signal | Dir | Function |
|---|---|---|
| D[15:0] | in | Write data. |
| WADDR[2:0] | in | Write register select, 0–7. |
| WE | in | Write enable, active high. Sampled against the clock. |
| CLK | in | System clock. Times the write window; reads ignore it. |
| AADDR[2:0] | in | Read port A register select. |
| QA[15:0] | out | Read port A data. Three-state. |
| /OEA | in | Port A enable, active low. Drive low to read. |
| BADDR[2:0] | in | Read port B register select. |
| QB[15:0] | out | Read port B data. Three-state. |
| /OEB | in | Port B enable, active low. Drive low to read. |
| VCC, GND | — | +5 V. Single entry point on H8. |

**/OEA and /OEB are connector inputs.** They drive the enable pins of
the read decoders, so both must be driven — low to enable a port, high
to tri-state it. Leaving them floating leaves the ports undefined.

All 19 packages draw their power through H8's single VCC pin. At HC
quiescent and normal switching rates that is a few milliamps and
perfectly comfortable, but it is one pin.

## Socket map

Both arrays are organised the same way: **rows are banks, columns are
nibbles.**

| | bits 3:0 | bits 7:4 | bits 11:8 | bits 15:12 |
|---|---|---|---|---|
| **Array A** bank 0 (r0–r3) | U1 | U2 | U3 | U4 |
| **Array A** bank 1 (r4–r7) | U5 | U6 | U7 | U8 |
| **Array B** bank 0 (r0–r3) | U9 | U10 | U11 | U12 |
| **Array B** bank 1 (r4–r7) | U13 | U14 | U15 | U16 |

Array A feeds QA, array B feeds QB. Both arrays receive the same D and
the same write strobes, so they hold identical contents — array B is a
write-shadow of array A, and the two ports can never disagree.

Address bits 1:0 go directly to each '670's WA/WB and RA/RB pins.
Address bit 2 selects the bank, via the decoders.

## Bank decode and write gating

**U19 (74HC00)** — two gates used, two tied off:

```
gate 1:  /CLK  = NAND(CLK, CLK)          ; plain inverter
gate 2:  /GWEN = NAND(WE, /CLK)          ; low when WE high AND CLK low
```

`/GWEN` is the write trap: it can only assert during the clock-low
phase, and only when the host asserts WE. This is the module's internal
glitch protection and it is not defeatable from the connector.

**U17 (74HC139)**, both halves, B inputs tied low so each is a 1-of-2:

```
half 1:  /E = /GWEN,  A = WADDR[2]   ->  /GW[0], /GW[1]     write strobes
half 2:  /E = /OEA,   A = AADDR[2]   ->  /GRA[0], /GRA[1]   port A read enables
```

**U18 (74HC139)**, one half used:

```
half 1:  /E = /OEB,   A = BADDR[2]   ->  /GRB[0], /GRB[1]   port B read enables
half 2:  unused, inputs tied off
```

`/GW[n]` drives pin 12 (E_W) of every chip in bank *n* of **both**
arrays — which is what keeps the shadow in lockstep. `/GRA[n]` drives
pin 11 (E_R) of array A's bank *n*; `/GRB[n]` does the same for array B.

## Reading

Both ports are independent and combinational: present an address, the
addressed register's contents appear on Q after the access time.
Reading r2 on port A while reading r5 on port B is the normal case, and
both may be read while a write is in progress elsewhere.

Access time has two shapes. Changing the low address bits is a
single-chip lookup, ~45 ns worst case. Changing address bit 2 crosses a
bank boundary and re-steers the read enables: ~70 ns worst case, and
the port passes through a brief high-impedance blink while the banks
hand over.

**Q ports feed combinational logic inputs only.** Never hang anything
edge-sensitive on QA or QB.

Reading the register currently being written returns the incoming data
once the write window is open (write-through), on both ports. Reading
any other register during a write is undisturbed.

### No pulldowns

**This board has no resistors of any kind.** There are no pulldowns on
QA or QB. Consequences:

- During the bank-handover blink the Q bus is genuinely undefined, not
  pulled low. It is high-impedance and holds residual charge.
- If a socket is left unpopulated, its bits do not read as zero — they
  float and return whatever charge they last held.
- Any open in a Q path presents as an *intermittent* wrong bit,
  sometimes high when a zero was written and sometimes low when a one
  was, because nothing defines the node. A consistently wrong bit is a
  short; an inconsistently wrong bit is an open. This distinction is
  the fastest fault-finding tool the board offers.

If the host needs a defined level on an unpopulated or handover-blinked
bit, it must provide the pulldown itself.

## Writing

One write port. The contract:

1. Set WADDR and D. Both must be stable **before CLK falls** and held
   until CLK rises again.
2. Assert WE across that clock-low phase.
3. The value is stored. At the next CLK rise the write is closed and
   D/WADDR may change.

The storage devices are transparent latches and the module only opens
them during CLK-low, after your data has had the high phase to settle.
The consequence is the one real timing obligation the module imposes on
the host: **whatever produces D must settle within the clock-high
phase.** The low phase belongs to the write.

Minimum usable low phase is ~100 ns, corresponding to a ~6.5 MHz
ceiling at 50/50 duty — in practice the host datapath, not this module,
sets the clock. Asymmetric duty (long high, short low) is supported and
recommended when chasing speed.

Writes land in both arrays simultaneously.

## Partial population

Sockets may be left empty during bring-up, and the module works with
the populated subset. This is a diagnostic convenience, not a supported
configuration — unpopulated bits float rather than reading zero.

The useful ladder, each step adding one observable capability:

1. **U1 alone** — four 4-bit registers on port A. Write patterns, walk
   the read address, watch them come back.
2. **U9** — array B's matching socket. Port B appears; this is the
   earliest demonstration of independent dual reading.
3. **Width** — add nibble columns: U2/U10, then U3/U11, then U4/U12.
4. **Banks** — add the bank 1 rows. Registers r4–r7 appear and the
   handover behaviour on address bit 2 becomes observable.

**Keep the arrays matched.** If array B is populated at all, its
population should mirror array A exactly, or port B returns floating
garbage for the missing slices while port A looks fine.

## Designator reuse

H3 and H4 are each used twice (the two halves of QA and of QB), C5 four
times, C6 eight times, C7 twice. Twelve devices share five designators.
Anything that keys on designator — a BOM, a netlist diff, a
pick-and-place file — will mis-handle these.

## Absolute ratings and habits

74HC inputs: do not float any driven-from-connector input. The module
pulls down nothing — not your inputs and not its own outputs. Q ports
drive normal HC loads with margin; they are not bus drivers. If a Q
port must join a wider shared bus, use /OEA and /OEB deliberately and
buffer externally.

One 0.1 µF per package is fitted; bulk capacitance belongs at the power
header. All timing figures above are datasheet maxima across
temperature and voltage; room-temperature parts run meaningfully
faster, and nothing in the module depends on that.

## Bit-order hazard

Every multi-bit group on the '670 puts its **least significant bit on
the higher pin number**: WA(14) > WB(13), RA(5) > RB(4), Q1(10) >
Q2(9), Q3(7) > Q4(6). D1 is orphaned at pin 15 while D2–D4 sit at pins
1–3. Wiring any group in ascending pin order silently reverses it.

The board itself observes this convention — U1 pin 14 carries WADDR0
and pin 5 carries AADDR0. The hazard lives in the **cabling**: a ribbon
whose conductor order is reversed or transposed will survive repeated
visual inspection, because tracing a signal name from the board
backwards confirms the naming convention rather than the wiring.
Compare each ribbon's conductor colours against the others, or probe
the physical pin.
