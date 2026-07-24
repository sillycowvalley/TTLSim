# 8080 ALU Module — User Guide

A general-purpose arithmetic/logic board for TTL-class machines: 4 to 16 bits
of '181 function generator, two latched operand ports, conditioned carry, a
five-flag derivation block with an optional two-group flag register, an
optional result permute stage, and a socket for a machine-specific correction
PLD. One PCB serves every width; capability is set by which sockets are
populated and four jumpers.

The module is **purely combinational from operand to result**. The only
clocked things on the board are the operand latches (level, transparent by
default) and the flag register. It computes; it does not sequence, decode, or
choose its own operands.

> **Status: design document, not a built board.** Nothing here has been
> captured in TTLSim or cut in copper. Timing figures are marked with their
> provenance, and the open decisions are listed at the end rather than
> silently resolved.

---

## What the module deliberately does not do

Matching the clock module's philosophy — own one job completely, assume
nothing about the attached machine:

- **No operand selection.** Two operand ports arrive at the connector already
  chosen. Immediate-vs-register-vs-memory steering is the host's decode
  problem, not a jumper on this board.
- **No opcode decode.** S0–S3, M and the carry controls arrive as-is from the
  host's control store. The module has no idea what instruction it is serving.
- **No flag byte assembly.** The five flags leave as five separate lines. The
  packing order into a status byte (and which bits are forced constants) is
  machine-specific and belongs in the host's PSW driver.
- **No register storage.** The accumulator, if the host has one, lives
  elsewhere.

---

## Interface

All signals on **J1 (2×40 box header)**. Logic levels are 74HC at 5 V.

| Signal     | Dir | Function                                                                                                                 |
| ---------- | --- | ------------------------------------------------------------------------------------------------------------------------ |
| A[15:0]    | in  | Operand A. Unpopulated slices ignored.                                                                                   |
| B[15:0]    | in  | Operand B.                                                                                                               |
| F[15:0]    | out | Result. Totem-pole, always driving — see Result port.                                                                    |
| S[3:0]     | in  | '181 function select, passed through unaltered.                                                                          |
| M          | in  | '181 mode: low = arithmetic, high = logic.                                                                               |
| CIN        | in  | Carry/borrow in, **host sense** — see Carry conditioning.                                                                |
| SUB        | in  | Tells the board the host is doing a subtract, so both carry ends read in borrow sense. Tie low for a pure adder machine. |
| COUT       | out | Carry/borrow out, host sense.                                                                                            |
| HCOUT      | out | Carry out of slice 0 (the bit 3 → bit 4 boundary). Half-carry.                                                           |
| SGN        | out | Sign — the MSB of the result at the configured width.                                                                    |
| ZERO       | out | High when every populated result bit is 0.                                                                               |
| PAR        | out | High for **even** parity over F[7:0].                                                                                    |
| CLK        | in  | System clock. Times the flag register and the operand latches only.                                                      |
| /LEA, /LEB | in  | Operand latch enables, active low. Strap high for transparent.                                                           |
| FIN[4:0]   | in  | Flag write-back path (C, SGN, ZERO, HC, PAR).                                                                            |
| /FSEL      | in  | Low = flag register loads from FIN instead of from the ALU.                                                              |
| FEN1       | in  | Flag register enable, group 1 (SGN, ZERO, HCOUT, PAR).                                                                   |
| FEN2       | in  | Flag register enable, group 2 (carry only).                                                                              |
| FOUT[4:0]  | out | Registered flags, same bit order as FIN.                                                                                 |
| PSEL[1:0]  | in  | Permute stage select — see Permute stage.                                                                                |
| /AUXOE     | in  | Enables the aux PLD onto the B port.                                                                                     |
| VCC, GND   | —   | +5 V and ground on multiple pins, interleaved through the connector.                                                     |

**No series elements at the connector.** Unlike the clock module — which
drives its nine outputs through 100 R resistors and therefore forces a label
rename at the boundary — every signal here goes pin-to-pad. Net labels keep
one name on both sides of the interface, and TTL014 does not apply.

---

## Slices and width

One '181 covers four bits. Populate upward:

| Build    | '181s | Width | Carry                                     |
| -------- | ----- | ----- | ----------------------------------------- |
| Nibble   | 1     | 4     | trivial                                   |
| **Byte** | 2     | 8     | ripple between slices                     |
| 12-bit   | 3     | 12    | ripple                                    |
| **Word** | 4     | 16    | ripple, or '182 lookahead if U5 is fitted |

Slice 0 covers bits 0–3, slice 1 bits 4–7, and so on. **JW** (4-position)
steers the sign-bit tap and the carry-out tap to the top populated slice;
setting it wrong is the one configuration error that produces plausible-looking
wrong answers rather than obvious garbage.

Unpopulated slices' result lines carry 10 k pulldowns, so a narrow build
presents clean zero-extended values on the full 16-bit port — and, more
usefully, the zero-detect tree spans all sixteen bits unconditionally and still
gives the right answer at any width.

**Half-carry is free at every width.** HCOUT is the carry out of slice 0,
which is the bit 3 → bit 4 boundary by construction. With U5 fitted it comes
from the corresponding '182 intermediate instead of the inter-chip wire, but
it is the same carry and the same pin at the connector.

---

## Operand ports

Each port passes through a '373 transparent latch before reaching the '181s.
Two 8-bit latches per port at 16 bits, one at 8 bits or below.

The latches exist for one reason: **a read-modify-write on a register file
slot is a combinational loop** if the file's read port feeds the ALU whose
result feeds the file's write port while the write window is open. Closing the
latch before the write opens breaks it. On a machine whose operands come from
somewhere stable — discrete registers, a bus with its own hold — the latches
are dead weight.

So they are optional in use, not in population: strap `/LEA` and `/LEB` high
and both ports are wire-through with one propagation delay of penalty. **JLA**
and **JLB** additionally tie each enable to ground on the board for hosts that
never want to think about it.

The house phase discipline applies unchanged: **operands settle in the clock-high
phase, the low phase belongs to the write.** This module is the thing that must
settle — everything downstream of it is inheriting its delay.

---

## Carry conditioning

The '181's carry pins are active-low when operands are active-high, and most
machines define their carry flag as a **borrow** on subtract, which is the
inverse of the adder's carry-out. That is two inversions in opposite senses,
and they cancel misleadingly on some test vectors — a design that is wrong
here can pass a casual test suite.

The board hides both behind one XOR gate at each end, with `SUB` as the
control:

- `CIN` at the connector is the host's carry/borrow in its own sense. The
  board conditions it to the '181's convention.
- `COUT` at the connector is the host's carry/borrow out, conditioned back.
- `SUB` low = add sense at both ends. `SUB` high = borrow sense at both ends.

`HCOUT` is conditioned the same way, from the same `SUB` line.

⚠ **The polarity table is the single thing to prove first in simulation.**
The vectors that catch it are subtractions at the borrow boundary — on an
8080-class host, `SUI 0` and `SUI 1` against A = 0. Prove it before wiring
anything downstream of COUT.

---

## Flags

Five lines, all combinational, all available whether or not the flag register
is populated:

| Flag  | Derivation                                               | Cost                   |
| ----- | -------------------------------------------------------- | ---------------------- |
| SGN   | Result MSB at the configured width                       | a wire and JW          |
| ZERO  | 16-input all-zeros detect, 2 × '30 plus a combining gate | 3 packages             |
| PAR   | Even parity over F[7:0]: 7 XORs, output inverted         | 2 × '86                |
| HCOUT | Slice 0 carry out, conditioned                           | shares the carry gates |
| COUT  | Top slice carry out, conditioned                         | shares the carry gates |

Two notes that have bitten before:

**ZERO is a real all-zeros detect, not the '181's A=B pin.** The A=B output is
open-drain and only means what you want in one mode with one carry-in. Using
it as a zero flag works until it doesn't.

**Parity is over the low byte only, at every width.** That is the common
convention and it is what a byte-oriented host expects. A 16-bit host wanting
full-width parity needs three more XOR gates off-board; the module does not
offer a jumper for it because half the parity tree would then be
width-dependent for no benefit.

### Flag register (optional population)

One '377 holds group 1 (SGN, ZERO, HCOUT, PAR) and one '74 holds group 2
(carry). Two enables, because the useful masks genuinely differ: an 8-bit
increment updates group 1 and leaves carry alone; a 16-bit add updates carry
and nothing else; a rotate touches carry only.

`FEN1` and `FEN2` are **sampled by the clock edge with setup and hold**, like
any D input — not "low sometime during the state". Same contract as the Code &
IR module's `/EN` and the I/O module's `/LEDCE`; keeping one phase discipline
machine-wide is the point.

`/FSEL` low selects `FIN[4:0]` instead of the ALU-derived flags, through a
2 × '157 input mux. That is the path a stack-pop-status or
load-flags-from-bus instruction needs. Wire `/FSEL` high and the mux is
invisible.

The register's power-up contents are undefined. A host wanting deterministic
flags at reset must write them early — there is no clear pin.

---

## Permute stage (optional population)

2 × '157 per byte on the result path, selected by `PSEL[1:0]`:

| PSEL | Result port carries                                        |
| ---- | ---------------------------------------------------------- |
| 0 0  | ALU result, unmodified                                     |
| 0 1  | Result rotated right one bit                               |
| 1 0  | Result rotated right one bit with COUT entering at the top |
| 1 1  | Reserved — currently the same as 0 0                       |

Left rotates need no hardware: `A + A + CIN` on the '181 is a left shift
through carry natively, with the carry landing correctly at both ends. Only
the right direction needs the wiring permutation, which is why the stage is
two muxes and not a shift register.

The stage costs one mux delay on **every** operation, including the ones that
don't use it. On a host that never rotates, leave the sockets empty — the
board links the ALU result straight to the result port with zero-ohm links in
the bypass position.

---

## Auxiliary function socket

One **GAL22V10** socket, wired to:

- inputs: A[7:0], the registered carry, the registered half-carry
- outputs: an 8-bit correction value onto the **B port**, plus two flag
  override lines
- enable: `/AUXOE`

This is the machine-specific escape hatch, and it is generic only in the sense
that the socket is. The exemplar is 8080/Z80-class **DAA**: 10 inputs, 10
outputs, a 22V10 with nothing to spare, applying its correction through the
existing adder as `A + correction` rather than needing its own datapath. A
BCD-free machine leaves the socket empty and `/AUXOE` strapped high.

⚠ When `/AUXOE` is low the aux PLD owns the B port. The host must not also be
driving B. The board does not arbitrate this — it is one more line in the
host's one-driver-per-net discipline.

---

## Result port

`F[15:0]` is totem-pole and **always driving**. It is not a bus driver.

This is deliberate and it is the same call the Code & IR module makes for its
'377 fitment: the result of a combinational block wants to be point-to-point
into whatever consumes it, and a host that needs the ALU on a shared bus adds
a '541 on its own board where it can gate it against the rest of its sources.
Putting a three-state driver here would mean an output-enable pin, a defined
idle state, and an argument about who owns it — for a saving the host can make
better itself.

---

## Configurations

| Build             | '181     | '373 | Flag reg | Permute | Aux | ~ICs |
| ----------------- | -------- | ---- | -------- | ------- | --- | ---- |
| 8-bit, no frills  | 2        | —    | —        | —       | —   | ~8   |
| 8-bit, full       | 2        | 2    | yes      | yes     | yes | ~15  |
| 16-bit, ripple    | 4        | 4    | yes      | yes     | yes | ~20  |
| 16-bit, lookahead | 4 + '182 | 4    | yes      | yes     | yes | ~21  |

**The one hard rule:** JW must match the populated slice count. Every other
mis-set jumper produces something obviously broken; a wrong JW produces
correct-looking arithmetic with the sign and carry flags taken from the wrong
bit, which passes any test that doesn't cross a boundary.

---

## Timing

**All figures below are estimates, not verified against datasheets.** They are
here to show the shape of the budget, and they need replacing with real
worst-case numbers from the parts actually fitted before any host commits a
clock rate.

The critical path at 8 bits is: operand latch → slice 0 → slice 1 carry →
permute mux → consumer setup. At 16 bits with ripple carry it grows by two
more slice-to-slice carry delays, which is exactly the trade the '182 buys
back.

| Segment                            | Estimate | Provenance        |
| ---------------------------------- | -------- | ----------------- |
| '373 latch, data through           | ~20 ns   | estimate — verify |
| '181 operand to F                  | ~30 ns   | estimate — verify |
| '181 carry in to carry out         | ~20 ns   | estimate — verify |
| '157 permute mux                   | ~15 ns   | estimate — verify |
| Zero detect tree                   | ~25 ns   | estimate — verify |
| Parity tree (2 gate levels of '86) | ~30 ns   | estimate — verify |

Two structural observations that hold regardless of the numbers:

1. **Parity, not arithmetic, is likely the long pole.** The XOR tree starts
   after the result is final, so it stacks on top of the whole ALU path. If a
   host's flag setup is tight, parity is the flag to look at first.
2. **The register file module's contract makes this module the timing owner.**
   That module requires D stable before CLK falls; this module is what
   produces D. The entire clock-high phase is this board's budget, and nothing
   else on the machine gets to spend it.

---

## Bring-up

Each population step adds one observable capability, testable with DIP
switches on A and B, the clock module in STEP, and LEDs on F and the flag
lines:

1. **One '181, no latches, no flags.** Strap `/LEA`/`/LEB` high, `SUB` low,
   `M`/`S` on switches. Walk the function table — this proves the part, the
   power, and the S/M wiring, and nothing else.
2. **Carry conditioning.** Still one slice. Exercise CIN and SUB across the
   four combinations and check COUT against hand-computed borrow. **Do not
   proceed past this step until the polarity table is proven** — everything
   after it inherits the sense.
3. **Second slice.** 8-bit arithmetic, and HCOUT becomes observable at the
   slice boundary. Test a carry that crosses bit 3 and one that doesn't.
4. **Flag combinational block.** ZERO and PAR appear. Parity is best walked
   with a counting pattern on A and B = 0.
5. **Operand latches.** The read-modify-write loop becomes testable, which
   needs a register file attached — this is the first step that is not
   standalone.
6. **Flag register**, then `/FSEL` and the write-back mux.
7. **Permute stage**, then the aux socket.

**Standalone self-test.** The module verifies itself with no CPU: switches on
A and B, LEDs on F and the five flags, `/LEA`/`/LEB` high, flag register
unpopulated or its enables tied low. Every function in the '181 table is then
directly exercisable by hand — this board is unusually pleasant to bring up,
because the entire core is combinational and every input is a switch.

---

## Open decisions

Recorded rather than resolved:

1. **Width for the first build.** 8-bit slices give a free half-carry at the
   inter-chip boundary and let a single read port serve; 16-bit halves the
   pass count on register-pair arithmetic but doubles the ALU and pushes the
   half-carry into a '182 intermediate. The board supports both; the *machine*
   has to pick, and the choice propagates into the register file's port count.
2. **Whether the permute stage belongs here at all.** It is arguably the
   host's business, and it taxes every operation with a mux delay. The
   counter-argument is that it needs COUT, which is on this board.
3. **Flag mux width.** `FIN[4:0]` assumes five flags. A machine with a
   different flag set gets an awkward fit, and the alternative — no on-board
   flag register at all — is a defensible smaller board.
4. **'181 sourcing.** Whether the part is comfortably available in HC at build
   time is not something I have checked. If it isn't, the whole module is a
   different design.
5. **The '181 S-code table** for each host operation has not been pulled from
   the datasheet in preparing this document. It is the first thing to verify
   before capture.

---

## Absolute ratings and habits

74HC inputs: do not float any connector-driven input. The board pulls down its
own unpopulated result bits and pulls up nothing else — `S`, `M`, `CIN`,
`SUB`, `PSEL`, the latch enables and the flag enables must all be driven or
strapped whenever the board is powered.

The result port drives normal HC fan-out and is not a bus driver; if F must
join a shared bus, buffer it externally where the host can arbitrate. One
0.1 µF per populated socket is factory policy; bulk capacitance sits at the
connector. The '181s are the board's current draw — budget for the fully
populated case even on a narrow build, since the sockets are there.
