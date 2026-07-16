# Register File Module — User Guide

A general-purpose register file board for TTL-class machines: up to 16
registers of up to 16 bits, with one or two read ports, built from 74HC670
register files behind a single backplane connector. One PCB serves every
configuration; capability is set by which sockets are populated and three
jumpers. In its Thumby configuration (8 × 16, two read ports) it is the
machine's register file; minimally populated it is a Blinky-class 8 × 8
register bank.

The module is synchronous-write, asynchronous-read: writes commit under the
system clock, reads are combinational and always live.

## Interface

All signals on J1 (2×40 box header). Logic levels are 74HC at 5 V.

| Signal | Dir | Function |
|---|---|---|
| D[15:0] | in | Write data. D[15:8] unused in 8-bit builds. |
| WADDR[3:0] | in | Write register select. WADDR[3] used only in 16-register builds. |
| WE | in | Write enable, active high. Sampled against the clock — see Writing. |
| CLK | in | System clock. Used only to time the write window; reads ignore it. |
| AADDR[3:0] | in | Read port A register select. |
| QA[15:0] | out | Read port A data. Three-state, with on-board 10 k pulldowns. |
| /OEA | in | Port A output enable, active low. Strap low in-machine. |
| BADDR[3:0] | in | Read port B register select. |
| QB[15:0] | out | Read port B data. Reads solid zeros in single-port builds. |
| /OEB | in | Port B output enable. Strap high to decommission port B. |
| VCC, GND | — | +5 V on multiple pins. |

## Reading

Both ports are independent and combinational: present an address, the
addressed register's contents appear on Q after the access time. The two
ports share nothing but the storage — reading r2 on port A while reading r5
on port B is the normal case, and both may be read while a write is in
progress elsewhere.

Access time has two shapes. Changing the low address bits (register within a
bank) is a single-chip lookup, ~45 ns worst case. Changing an upper address
bit crosses a bank boundary and re-steers the output enables: ~70 ns worst
case, and the port passes through a brief high-impedance blink while banks
hand over. The pulldowns define the bus during the blink, but the rule
stands: **Q ports feed combinational logic inputs only.** Never hang
anything edge-sensitive on QA or QB.

Reading the register currently being written returns the incoming data once
the write window is open (write-through). Reading any other register during
a write is undisturbed.

Unpopulated data bits read as zero, so an 8-bit build presents clean
zero-extended values on the full 16-bit port.

## Writing

One write port. The contract:

1. Set WADDR and D. Both must be stable **before CLK falls** and held until
   CLK rises again.
2. Assert WE across that clock-low phase.
3. The value is stored. At the next CLK rise the write is closed and D/WADDR
   may change.

The board performs its own write-hazard gating: the storage devices are
transparent latches, and the module only opens them during CLK-low, after
your data has had the high phase to settle. You supply plain WE and CLK; the
glitch protection is internal and not defeatable from the connector. The
consequence of the contract is the one real timing obligation the module
imposes on the host: **whatever produces D must settle within the clock-high
phase.** The low phase belongs to the write.

Minimum usable low phase is ~100 ns (internal gating plus latch pulse and
setup), which corresponds to a ~6.5 MHz ceiling at 50/50 duty — in practice
the host datapath, not this module, sets the clock. Asymmetric duty (long
high, short low) is supported and recommended when chasing speed.

Writes land in both read-port copies simultaneously; the two ports can never
disagree. This is internal machinery — from the connector there is simply
one register file with two windows into it.

## Configurations

Capability is population plus jumpers. Registers grow by stuffing bank rows,
width by stuffing nibble columns, the second read port by stuffing the
mirror array.

| Build | '670s | Jumpers |
|---|---|---|
| 8 reg × 8 bit, 1 port | 4 | JW/JA = 8R · JOEB = VCC |
| 8 × 16, 1 port | 8 | JW/JA = 8R · JOEB = VCC |
| 8 × 16, 2 ports (Thumby) | 16 | JW/JA/JB = 8R · JOEA/JOEB = GND |
| 16 × 8, 2 ports | 16 | all jumpers 16R |
| 16 × 16, 2 ports | 32 | all jumpers 16R |

JW, JA, JB select whether address bit 3 comes from the connector (16R) or is
strapped low (8R). JOEA/JOEB select each port's enable source: GND
(permanently on), VCC (port off), or open (driven from the connector).

**The one hard rule:** the second read port is a complete mirror or nothing.
If any mirror-array socket is stuffed, its population must match the primary
array exactly, or port B returns wrong data for the missing slices. The
silkscreen says so; believe it.

Access and write timing are identical in every configuration — upgrading
never moves the host's timing budget. The only full-population caution is
fan-out on D and WADDR at 32 chips; the board carries optional buffer
footprints (bypassed by zero-ohm links as shipped) if edges get lazy.

## Bring-up

Each population step adds one observable capability and is testable with
DIP switches on D/WADDR/AADDR, the clock module in STEP, and LEDs on QA:

1. **One chip** — four 4-bit registers, one port. Write patterns, walk the
   read address, watch them come back.
2. **Its mirror twin** — the second read port appears. Two chips is the
   earliest demonstration of independent dual reading.
3. **Width** — stuff nibble columns pairwise until 16 bits.
4. **Banks** — stuff the second bank rows; registers r4–r7 appear, and the
   bank-handover behavior on address bit 2 becomes observable.

## Absolute ratings and habits

74HC inputs: do not float any driven-from-connector input; the module
pulls down its own outputs but not your inputs. Q ports drive normal HC
loads with margin; they are not bus drivers — if a Q port must join a wider
shared bus, use the /OE pins deliberately and buffer externally. One 0.1 µF
per populated socket is factory policy; bulk capacitance sits at the
connector. All timing figures above are datasheet maxima across temperature
and voltage; room-temperature parts run meaningfully faster, and nothing in
the module depends on that.
