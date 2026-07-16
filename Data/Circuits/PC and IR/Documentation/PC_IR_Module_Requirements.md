# PC / IR Module — Requirements & Interface (Merged)

A **generic program-counter and instruction-register board** for homebrew TTL
CPUs, third board in the family after the Clock/T-Cycle Module and the
Register File Module. One PCB, any machine; capability set by population and
jumpers; every machine-facing input pulled to its inert level so an absent
source does nothing. The module owns **program-flow state and its load
plumbing** — never decode, never datapath policy, never the address bus.

Target hosts: the 6502 clone and Thumby, with the host-side changes agreed:
both machines take their MAR/address-mux structure to their memory boards,
and Thumby's sequencer asserts a load *level* for the IR rather than
qualifying a strobe.

Technology: 74HC, 5 V. Full population ≈ 16–18 ICs; typical builds less.

---

## Scope — settled decisions

These were the conflicts between the two source proposals, resolved:

1. **No MAR, no address port, no ownership decoder.** The address mux, any
   latched-address stage, MMIO decode, and the one-driver-per-net interlock
   on the address bus all belong to the memory board — that is where the mux
   lives, so that is where ownership is enforceable. This board exports PC
   on one always-driven port and stops there.
2. **16-bit-wide board, byte-granular strobes.** Width is population, not
   topology. Thumby asserts both byte strobes and gets one-edge 16-bit
   loads; the 6502 clone populates and drives bytes independently.
3. **IR is '377-based** — loads by enable under the single CLK edge. No raw
   edge input, no gated clocks, ever. This is the one forced change to the
   Thumby control scheme: the sequencer asserts /LDIRL·/LDIRH as levels
   through the FETCH state.
4. **Reset policy is a jumper**, not topology: PC clears to $0000, or reset
   leaves PC alone and the host boots by the IR force mechanism.
5. **The jam/strap constant stays.** With full-width load ports already on
   the '161s, a strapped buffer pair onto the load nets provides *any*
   16-bit constant (interrupt vector, reset vector) for two chips — the
   objection to vector-jamming assumed a mux tier that isn't needed.
6. **No tri-state read-back ports.** PC_OUT feeds combinational consumers
   (address mux, A-source mux, write-back mux) on both hosts; one
   always-driven port suffices and saves two buffers.

---

## Functional requirements

### PC section

1. **16-bit synchronous counter**, 4× 74HC161, RCO cascade between slices,
   terminal carry exported (TC) for width extension or a cycle counter.
   8-bit and 12-bit builds by population.
2. **Count enable PCINC**, active high, sampled on the rising CLK edge.
3. **Byte load strobes /LDPCL, /LDPCH**, independently assertable; both
   together = a full 16-bit jump in one edge.
4. **Load dominates count — enforced on-board.** The '161 gives
   load-over-count per 4-bit slice only; a partial byte load with PCINC
   high would let the other byte increment. On this board the counting
   enable is gated: `CE = PCINC · /LDPCL · /LDPCH` (one 3-input AND), so
   *any* load suppresses *all* counting that edge. This upgrades the
   source proposals' host contract into a structural guarantee.
5. **PCL source select.** The low byte loads from either D[7:0] or the
   ALT[7:0] port, selected per-cycle by /PCLALT (low = ALT). This is the
   generalized dual-load idiom: Thumby's JA/JAL byte concatenation
   (PCL ← IR[7:0], PCH ← D[15:8], same edge), the 6502 clone's
   PCL-from-operand tricks, or nothing at all — /PCLALT is pulled up, so
   an idle host loads from D and the mux is invisible.
   - **On-board loopback jumper block**: ALT[7:0] ↔ IR[7:0], so Thumby's
     concatenation never leaves the board.
   - The mux is **2× 74HC257** (tri-state), not '157 — see jam interlock.
6. **Jam constant.** 2× '541 with a 16-bit strap field (each bit to +5 or
   GND) drive the '161 D inputs while /JAM_OE is low. Thumby straps $0002
   and drives /JAM_OE from its INT state; any host may strap any vector,
   including a reset vector for a JRST=CLR-less boot.
   - **Interlock, on-board half**: /JAM_OE low disables the '257 tier, so
     the board's own low-byte drivers can never fight the jam.
   - **Interlock, host half (contract)**: the host must not drive D[15:8]
     while /JAM_OE is low. (Thumby already satisfies this — its F-path
     buffers are off during INT.)
7. **Reset jumper JRST** selects what /RST does to the PC:
   - **CLR** — '161 /CLR: boot at $0000 (Thumby).
   - **OFF** — reset does not touch PC; the host boots by microcode via
     IR force (6502 clone) or by jamming a vector.
   - **Permitted substitution**: the '163 is pin-identical and may be
     populated in place of the '161. JRST=CLR then reads "clear on the
     first rising edge with /RST held" — legitimate for a host whose
     reset is guaranteed to span a clock edge, at the price that a
     stopped-clock or STEP-mode reset no longer snaps the panel to
     $0000. The '161 stays the default because bring-up runs in STEP
     and reset is machine-state initialisation, not a clocked
     datapath operation.

### IR section

8. **16-bit register, 2× 74HC377**, D inputs from D[15:0], loaded by
   /LDIRL and /LDIRH under the single CLK. Outputs always driven — a
   microcode address or decode input must never float. 8-bit hosts
   populate the low '377 only.
9. **No decode, full fan-out.** All populated bits exported on IR_OUT.
   Class bits, opcode subfields, register fields, immediates are the
   consumers' interpretation; field taps in useful groupings are broken
   out on a secondary header, but any decoding is host business. Sign and
   zero extension of immediates likewise stay off-board.
10. **Force-to-constant (optional population), low byte.** A '157 pair
    between the '377 outputs and IR_OUT[7:0]: while /FORCE is low, IR_OUT
    presents a strap-selected constant (default $00) instead of register
    contents. Purpose: generic microcoded reset — tie /FORCE to reset and
    the machine wakes executing a known opcode whose microcode performs a
    vectored boot. Builds without it fit bypass links across the '157
    footprints. /FORCE is a level: hold through reset, release before the
    first post-reset fetch completes.
11. **IR is not cleared by reset** (the '377 has no clear; force covers
    the need).

### Observability

12. **LED arrays on PC[15:0] and IR[15:0]** (optional population), driven
    through dedicated '541 buffers so indicator current never loads the
    datapath nets. Series resistors on board; LEDs face the panel edge.
13. **Test points**: CLK as received, /RST, the gated count enable CE,
    /JAM_OE, TC.

---

## Interface

Family conventions: GND on pin 1 of every header; 100R series on all
outbound signals; every input pulled to its **inert** level (4k7) — strobes
and /JAM_OE, /FORCE, /PCLALT pulled up; PCINC pulled down. An unconnected
board holds state and does nothing.

### J1 — main header

| Signal | Dir | Function |
|---|---|---|
| GND, VCC | — | power |
| CLK | in | single clock; all loads and counting on its rising edge |
| /RST | in | reset; effect on PC per JRST; never touches IR |
| D[15:0] | in | shared load source: PCH ← D[15:8], PCL ← D[7:0] (via mux), IR ← D[15:0] |
| ALT[7:0] | in | alternate PCL load source; jumper-loopable to IR[7:0] |
| /PCLALT | in | low: PCL loads from ALT; pulled up (default D) |
| /LDPCL, /LDPCH | in | PC byte loads; both = 16-bit jump; any load suppresses counting |
| PCINC | in | count enable, active high, pulled down |
| /LDIRL, /LDIRH | in | IR byte loads (levels, sampled at the edge) |
| /JAM_OE | in | strap constant onto the PC load nets while low; host must release D[15:8] |
| /FORCE | in | IR_OUT[7:0] = strap constant while low (if force tier populated) |
| PC[15:0] | out | the PC, always driven; feeds combinational consumers only |
| IR[15:0] | out | the IR (or forced constant on the low byte), always driven |
| TC | out | PC terminal carry |

### J2 — secondary header

IR field taps (straight copies of IR_OUT in host-useful groupings), spare
strap positions.

### Jumpers

| Jumper | Function |
|---|---|
| JRST | /RST → '161 /CLR (**CLR**) or open (**OFF**) |
| JLOOP | ALT[7:0] ↔ IR[7:0] loopback, per-bit (shunt block or solder bridges) |
| Strap fields | jam constant (16 bit), force constant (8 bit) |

---

## Contracts the host must honour

- D (and ALT) stable before the CLK edge that strobes any load; standard
  '161/'377 setup.
- Load dominates count is guaranteed on-board; the host must still not
  *expect* a jump-and-increment in one edge.
- D[15:8] released (drivers off) while /JAM_OE is low.
- /PCLALT and /JAM_OE sourced from registered outputs that change only in
  the settled window after an edge — normal state-machine discipline; the
  muxes and buffers here are combinational and do not add break-before-make.
- PC_OUT and IR_OUT feed combinational inputs only; never anything
  edge-sensitive, and never a shared bus without a host-side buffer.
- One driver per net everywhere downstream; this board never tri-states
  its output ports.

---

## Population options

| Build | Populate | Typical host |
|---|---|---|
| PC-only | '161×4, glue | single-cycle / Harvard machines |
| Thumby | + '257×2, jam '541×2 (strap $0002), '377×2, JRST=CLR, JLOOP fitted, no force tier (bypass links) | 16-bit, four-state sequencer |
| 6502 clone | '161×4, glue, '377×1 (low), force '157×2 (strap reset opcode), JRST=OFF; '257 unpopulated with D bypass, jam optional | 8-bit microcoded von Neumann |
| Full | everything | bench / future machines |

### Chip bill (full)

| Function | Parts | Qty |
|---|---|---|
| PC | '161 | 4 |
| PCL source mux (tri-state) | '257 | 2 |
| Jam buffers + strap field | '541 | 2 |
| IR | '377 | 2 |
| IR force tier + strap field | '157 | 2 |
| LED buffers (PC, IR) | '541 | 4 |
| Glue (CE gate, '257 enable inversion) | '11 + '04 | 2 |
| **Total** | | **18** + LEDs, networks, decoupling |

---

## Bring-up

All stages run standalone under the Clock/T-Cycle Module in STEP, DIP
switches on D/ALT, before any other machine board exists.

1. **PC counts** — PCINC high, watch the LEDs count; prove JRST=CLR gives
   $0000 on reset.
2. **Loads** — prove byte loads, the 16-bit jump, and that any load
   suppresses counting board-wide (switch PCINC high during a byte load;
   the other byte must not move).
3. **PCL mux** — park different values on D[7:0] and ALT[7:0], toggle
   /PCLALT across loads.
4. **Jam** — hold /JAM_OE, strobe both loads, verify the strap constant;
   verify the '257 tier is dead while jammed.
5. **IR** — load opcodes by byte; then hold /FORCE and verify the strap
   constant wins on IR_OUT[7:0]; release and verify the register returns.
6. **Loopback** — fit JLOOP, load an instruction, take a JA-style load
   (/PCLALT low + both PC strobes) and verify PCL ← IR[7:0], PCH ← D[15:8].

---

## Deliberately excluded

- MAR, address mux, address-bus ownership decode, paged/AUX high-byte
  constants — all memory-board territory, where the mux and therefore the
  one-driver interlock actually live.
- Sequencer, decode logic, condition evaluation — consumers of IR_OUT,
  owners of every control input on J1.
- Field extraction, sign/zero extension — B-source-leg territory.
- Any clock gating or strobe qualification. One clock domain, one edge,
  loads by enable.

## Design rules embodied

- One clock domain, one edge; loads by enable, never gated clocks.
- One driver per net; on-board interlocks where the board's own drivers
  could collide, explicit contracts where the host's could.
- Inputs pulled to inert (absent = nothing happens); outputs 100R series.
- Reset is machine state, not configuration state: the module offers reset
  *mechanisms* (CLR, jam, force) and no reset *policy*.
- Population defines capability; timing never moves between builds.

## Open questions

1. Main-header form — one 2×40 box header vs the grouped-header style of
   the clock module; settle when the TTLSim capture is exported.
2. JLOOP form — solder bridges vs a 2×8 shunt block.
3. LED placement — board-edge vs front-panel ribbon; must match whatever
   the Register File Module chose.
4. Whether the force tier should optionally cover IR[15:8] for a future
   16-bit-opcode microcoded host (footprints are cheap; decide at layout).
