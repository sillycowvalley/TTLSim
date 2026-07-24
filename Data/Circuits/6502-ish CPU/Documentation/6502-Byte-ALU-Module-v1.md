# 6502 Byte ALU Module v1 — 74LS181 slice pair

A **generic 8-bit ALU module** for homebrew TTL machines: two '181 slices, a
normalised carry system, the result paths, and the condition signals — for any
attached CPU. The module assumes nothing about the host's register file,
control store, or instruction set. Every machine-facing control input is a
plain level; the module owns *computation and its polarity conventions*, never
sequencing.

Companion to the **Blinky Clock Module v3** (clock, reset, stepping, T counter)
under the same discipline: one board, one job, generic headers, staged bring-up.

**Headline numbers:** 9 core ICs, up to 15 fully optioned. 8-bit operands,
byte-cascadable to 16/24/32. 14 headers, GND on pin 1 of each. All 32 '181
functions exposed. Carry, half-carry, zero, sign, shifted-out bit, A=B and
(optionally) signed overflow presented in **active-high, host-friendly
polarity** regardless of what the '181's pins actually do.

---

## 1. What the module is — and deliberately isn't

**On the board:**

- The two 74LS181 slices and the carry path between them (ripple, or '182
  lookahead in a socket).
- Carry-in selection: a 4:1 mux giving `0 / 1 / CIN_A / CIN_B`.
- The **LS→HC level-translation boundary**. Everything that leaves the module
  leaves at HC levels; everything that arrives can be HC.
- The result paths: raw `F` to the host's register inputs, `F` onto a shared
  data bus, and `F>>1` onto that same bus.
- Condition generation: carry, half-carry, zero, sign, shifted-out bit, A=B,
  and optional signed overflow.
- Optional: B-port 2:1 mux, S-field steer, latched flag register.

**Not on the board, on purpose:**

| Off-module                          | Why                                                                                                                                          |
| ----------------------------------- | -------------------------------------------------------------------------------------------------------------------------------------------- |
| Register file / operand drivers     | Every architecture disagrees about this. The module takes two operands and hands back a result; where they came from is the host's business. |
| Control store, sequencer, T counter | The clock module owns the T counter; the host owns microcode. The ALU module has no state machine at all.                                    |
| Address arithmetic, PC, SP          | Host-side. The module will happily do the arithmetic if the host routes the operands to it.                                                  |
| Flag *meaning*                      | The module emits conditions. Which of them your P register keeps, and what your branches test, is architecture.                              |
| Decimal adjust                      | The '181 gives nothing there. Half-carry is exported so a host that wants BCD can build the adjust itself.                                   |

The dividing line is: **the module computes, the host decides.**

---

## 2. Block diagram

```
  A port  ─────────────────────────────────────────────┐
  (H2/H3)                                              │
                                                       │
  B port  ──┐                                          │
  (H4/H5)   ├──► BSEL '157 ×2  ──────────────┐         │
  D bus   ──┘      (option)                  │         │
  (H6/H7)                                    │         │
                                          ┌──▼─────────▼──┐
  M, S3:0 ──► '86 S-STEER (option) ──────►│               │──► Cn+8 ─► /CPOL ─► COUT
                                          │  2 × 74LS181  │──► Cn+4 ──────────► HCOUT
  CSEL1:0 ─┐                              │  ripple, or   │──► A=B  (pull-up) ► AEQB
  CIN_A  ──┼─► '153 4:1 ─► Cn (norm.) ───►│  '182 socket  │──► /G, /P ─────────► cascade
  CIN_B  ──┘   0 / 1 / A / B              └───────┬───────┘
                                                  │ F (LS levels)
                                          ┌───────▼───────┐
                                          │ '541  HCT     │  the level boundary
                                          │ always enabled│  + fan-out
                                          └──┬────┬────┬──┘
                    ┌────────────────────────┘    │    └───────────────┐
                    │                             │                    │
              F port (H8/H9)                '688 ─► ZERO       ┌───────▼───────┐
              raw result to                 F7   ─► SIGN       │ F   → D  '541 │─┐
              host registers                F0   ─► SOUT       │ F>>1 → D '541 │─┼► D bus
                    │                                          └───────────────┘ │
                    └─► V option ('86 + gate) ─► V             SRIN injects bit 7 ┘
```

---

## 3. Ports and headers

Convention inherited from the clock module: **GND is pin 1 of every header;
VCC appears on the power header only**; 8-pin maximum, wider interfaces split
across two headers.

| Hdr | Pins | Contents                                                                                                                     |
| --- | ---- | ---------------------------------------------------------------------------------------------------------------------------- |
| H1  | 2    | Power: 1 GND · 2 VCC                                                                                                         |
| H2  | 8    | A operand low: 1 GND · 2–5 **A0–A3** · 6–8 GND (ribbon returns)                                                              |
| H3  | 8    | A operand high: 1 GND · 2–5 **A4–A7** · 6–8 GND                                                                              |
| H4  | 8    | B operand low: 1 GND · 2–5 **B0–B3** · 6–8 GND                                                                               |
| H5  | 8    | B operand high: 1 GND · 2–5 **B4–B7** · 6–8 GND                                                                              |
| H6  | 8    | Data bus low: 1 GND · 2–5 **D0–D3** · 6–8 GND                                                                                |
| H7  | 8    | Data bus high: 1 GND · 2–5 **D4–D7** · 6–8 GND                                                                               |
| H8  | 8    | Result low: 1 GND · 2–5 **F0–F3** · 6–8 GND                                                                                  |
| H9  | 8    | Result high: 1 GND · 2–5 **F4–F7** · 6–8 GND                                                                                 |
| H10 | 8    | Function: 1 GND · 2 **M** · 3–6 **S0–S3** · 7 **STEER** · 8 **BSEL**                                                         |
| H11 | 8    | Carry & output control: 1 GND · 2 **CSEL0** · 3 **CSEL1** · 4 **CIN_A** · 5 **CIN_B** · 6 **/FOE** · 7 **/SOE** · 8 **SRIN** |
| H12 | 8    | Conditions: 1 GND · 2 **COUT** · 3 **HCOUT** · 4 **ZERO** · 5 **SIGN** · 6 **SOUT** · 7 **AEQB** · 8 **V**                   |
| H13 | 8    | Cascade & misc: 1 GND · 2 **SUB** · 3 **/G** · 4 **/P** · 5 **/ZCASC** · 6–8 NC                                              |
| H14 | 8    | Flag register (option): 1 GND · 2 **CLK** · 3 **FLGC** · 4 **FLGNZ** · 5 **CLAT** · 6 **ZLAT** · 7 **NLAT** · 8 **VLAT**     |

The three spare pins on each byte-port header are wired to GND rather than
left open — on ribbon cable that gives an alternating-return pattern for free,
and it costs nothing.

### The four byte ports

| Port  | Direction     | Purpose                                                                                              |
| ----- | ------------- | ---------------------------------------------------------------------------------------------------- |
| **A** | in            | A-side operand. Whatever the host's A-side selector puts there.                                      |
| **B** | in            | B-side operand, register flavour.                                                                    |
| **D** | bidirectional | Shared data bus. Source for the B mux (BSEL=1); destination for the F and F>>1 drivers (/FOE, /SOE). |
| **F** | out           | Raw result, HCT-buffered, permanently driven. Feeds host register D inputs directly.                 |

A host that has no separate result path simply leaves H8/H9 unpopulated and
takes results off D. A host that has one (a result-bus machine) uses both:
operands arrive on D, results leave on F.

**Interlock.** Because F can drive D and D can feed the B port, asserting
`/FOE` or `/SOE` while `BSEL` selects the bus closes a combinational loop
through the ALU. One glue gate forces both output enables inactive whenever
`BSEL` selects D. **JP7** defeats the interlock if a host genuinely needs the
loop broken elsewhere; leave it fitted.

### Control inputs

| Signal       | Sense       | Meaning                                                                         |
| ------------ | ----------- | ------------------------------------------------------------------------------- |
| M            | level       | '181 mode: 1 = logic, 0 = arithmetic                                            |
| S3–S0        | level       | '181 function select                                                            |
| STEER        | active-high | XOR-inverts all four S bits (option, JP4)                                       |
| BSEL         | 0 / 1       | B port ← B register / D bus (option, JP5)                                       |
| CSEL1:0      | 2-bit code  | carry-in source: 00 = 0, 01 = 1, 10 = CIN_A, 11 = CIN_B                         |
| CIN_A, CIN_B | active-high | the two external carry-in sources (typically the C flag and a half-carry latch) |
| /FOE         | active-low  | drive F onto D                                                                  |
| /SOE         | active-low  | drive F>>1 onto D                                                               |
| SRIN         | level       | bit injected into result bit 7 of the shifted path                              |
| SUB          | active-high | tells the V logic that the operation is a subtract (option)                     |
| /ZCASC       | active-low  | zero-detect cascade in from a lower-order module                                |

**All carry-in and carry-out signals at the headers are active-high logical
carry.** The '181's active-low pin convention is handled inside the module —
see §5.

---

## 4. Function set

The tables below are the **active-high data convention** (the one you want if
your buses carry true data, which is almost always). Sixteen logic functions,
sixteen arithmetic; the module exposes all of them.

### M = 1 — logic

| S3:S0 | F             | S3:S0 | F            |
| ----- | ------------- | ----- | ------------ |
| 0000  | /A            | 1000  | /A + B       |
| 0001  | /(A + B)      | 1001  | /(A ⊕ B)     |
| 0010  | /A · B        | 1010  | **B**        |
| 0011  | 0 (all zeros) | 1011  | A · B        |
| 0100  | /(A · B)      | 1100  | 1 (all ones) |
| 0101  | /B            | 1101  | A + /B       |
| 0110  | A ⊕ B         | 1110  | A + B        |
| 0111  | A · /B        | 1111  | **A**        |

### M = 0 — arithmetic (every entry implicitly `plus Cn`)

| S3:S0 | F                     | S3:S0 | F                         |
| ----- | --------------------- | ----- | ------------------------- |
| 0000  | A                     | 1000  | A plus A·B                |
| 0001  | A + B                 | 1001  | **A plus B**              |
| 0010  | A + /B                | 1010  | (A + /B) plus A·B         |
| 0011  | minus 1               | 1011  | A·B minus 1               |
| 0100  | A plus A·/B           | 1100  | **A plus A** (shift left) |
| 0101  | (A + B) plus A·/B     | 1101  | (A + B) plus A            |
| 0110  | **A minus B minus 1** | 1110  | (A + /B) plus A           |
| 0111  | A·/B minus 1          | 1111  | **A minus 1**             |

The workhorses, in the module's normalised carry sense:

| Operation                     | M   | S3:S0 | Cn         |
| ----------------------------- | --- | ----- | ---------- |
| Pass A                        | 1   | 1111  | –          |
| Pass B                        | 1   | 1010  | –          |
| Add without carry             | 0   | 1001  | 0          |
| Add with carry                | 0   | 1001  | C          |
| Subtract (borrow-free)        | 0   | 0110  | 1          |
| Subtract with borrow          | 0   | 0110  | C          |
| Compare (subtract, discard F) | 0   | 0110  | 1          |
| Increment                     | 0   | 0000  | 1          |
| Decrement                     | 0   | 1111  | 0          |
| Shift left / ROL              | 0   | 1100  | 0 or C     |
| Negate (0 − B)                | 0   | 0110  | 1, A = $00 |
| Constant $FF                  | 1   | 1100  | –          |
| Constant $00                  | 1   | 0011  | –          |

**Note the asymmetry that catches everyone:** the '181 has no `A plus 1` *and*
`B plus 1` — arithmetic is A-centric, B appears only as an addend or
subtrahend. There is no `B minus 1` either; synthesise it as `/(0 − B)`
(negate, then complement — M=1, S=0101). And there is no right shift: the
'181 is an adder, so `A + A` is the only shift it does. Right shifts come off
the module's SHR driver (§7).

### The S-field steer (option)

`STEER` XORs all four S bits before they reach the '181s. One line therefore
swaps every function for its complement-select twin:

| STEER=0          | STEER=1                  | Use                                                       |
| ---------------- | ------------------------ | --------------------------------------------------------- |
| 0000 (A plus Cn) | 1111 (A minus 1 plus Cn) | conditional inc/dec — sign extension, branch offset fixup |
| 1001 (A plus B)  | 0110 (A minus B minus 1) | conditional add/subtract                                  |
| 1011 (A·B)       | 0100 (/(A·B))            | conditional AND/NAND                                      |

Jumper **JP4** bypasses the '86 entirely if the host has no use for it, which
also removes one gate delay from the S path.

---

## 5. Carry, and the polarity work the module does for you

This is the single largest reason to have a module rather than two loose
'181s on the CPU board.

In active-high data mode the '181's carry pins are **active-low**: a logical
carry-in of 1 means the `Cn` pin is pulled *low*, and a logical carry-out of 1
means `Cn+4` is *low*. Get this backwards and everything appears to work until
you run a multi-byte add.

The module normalises at both ends:

- **`CIN_A`, `CIN_B`, and the `0`/`1` constants are active-high logical
  carry** at the '153 inputs. The '153's output is inverted once on the way to
  the low slice's `Cn` pin.
- **`COUT` at H12 is active-high logical carry**: 1 means carry out of bit 7
  on an add, and 1 means *no borrow* on a subtract — the 6502/ARM convention.
- **`HCOUT` at H12 is likewise active-high**, and is carry out of **bit 3**,
  tapped between the slices. Free, because it has to exist anyway; useful for
  address arithmetic (page-boundary fixups), for BCD adjust on a host that
  wants decimal mode, and as the 8080/Z80 half-carry flag.

**JP2 (`CPOL`)** flips `COUT` to the borrow convention (1 = borrow occurred on
subtract) for hosts that prefer it. Note that flipping it inverts the *add*
carry too — it's a whole-flag polarity choice, not an add/subtract-aware one.

Between the slices, the low slice's `Cn+4` pin drives the high slice's `Cn`
pin **directly, with no inversion** — both are active-low and they agree.

### Ripple versus lookahead

Default is ripple: low slice `Cn+4` → high slice `Cn`. Across two slices this
adds one slice's carry-propagation delay to the worst-case path.

A **74S182** socket (U3) is fitted from day one and bypassed by **JP1**. With
it populated, the two slices' `/G` and `/P` group outputs feed the '182 and
the '182 drives both slices' carry inputs.

Honest assessment: across *two* slices the lookahead gain is modest — you are
removing one ripple stage. It earns its keep in two places:

1. When the microcode store gets fast enough that the ALU is the critical path
   (the point at which the host CPU is chasing 8 MHz rather than 1 MHz).
2. When you **cascade modules** for 16 or 32 bits, where four or eight slices
   ripple and the delay becomes dominant.

The module also exports its **group `/G` and `/P`** on H13, so a host building
a 16- or 32-bit machine from several boards can fit a second-level '182
off-module and get true lookahead across the whole word.

---

## 6. Conditions and flags

All conditions are derived from the **buffered** result `FB` (the HCT '541
output), not from the LS-level '181 pins. One translation point, one place to
probe.

| Signal | Source                                                   | Sense                           |
| ------ | -------------------------------------------------------- | ------------------------------- |
| COUT   | high slice Cn+8, normalised (JP2)                        | active-high carry / no-borrow   |
| HCOUT  | low slice Cn+4, normalised                               | active-high carry out of bit 3  |
| ZERO   | '688 comparing FB against $00                            | active-high, 1 = result is $00  |
| SIGN   | FB bit 7                                                 | active-high, = bit 7            |
| SOUT   | FB bit 0                                                 | the bit the SHR path shifts out |
| AEQB   | both slices' open-collector A=B, wire-ANDed, one pull-up | active-high, 1 = A equals B     |
| V      | option, §8                                               | active-high signed overflow     |

`SIGN` is a straight tap — no logic. Hosts that want "N" simply latch it.

`AEQB` deserves a note: the '181's A=B output is open collector *precisely* so
the slices wire-AND, and it is only valid in subtract mode. It is not a
substitute for `ZERO` on a general operation.

### Zero cascade

The '688's enable input is driven from **`/ZCASC`** (H13). On a single-module
machine, JP6 ties it active and `ZERO` means "this byte is zero." On a
cascaded machine, the lower module's zero output feeds the next module's
`/ZCASC`, so the top module's `ZERO` means "the whole word is zero" — the
comparator chain does the AND for you and no extra gates are needed.

`SIGN` is meaningful **only on the most significant module**. `HCOUT` is
meaningful only on the least significant one.

### The latched flag register (option)

Two '377s, **U14** and **U15**, give a small condition register with two
independent enable domains:

- **U14 (`FLGC`)** — carry-domain flags: C, and by convention half-carry and
  V if the host wants them latched.
- **U15 (`FLGNZ`)** — result-domain flags: Z and N.

Splitting them into two packages is the whole point: it is what lets a host
implement "logic operations set N and Z but never C" without any decode logic
— two control bits, two enables. Both register on the rising edge of `CLK`
from H14; the latched outputs `CLAT`, `ZLAT`, `NLAT`, `VLAT` come back out on
the same header.

Thirteen of the sixteen flip-flops are unused. Bring the spares to a
solder-pad field rather than a header — a host that wants a hidden test flop,
a sign latch, or an interrupt-disable bit can steal them without adding a
package.

Depopulate both if your host already owns its flag register; the raw condition
outputs on H12 are always live either way.

---

## 7. The result paths

Three destinations, one source.

```
   F (LS levels from the '181s)
        │
        ▼
   U9  '541  HCT, permanently enabled  ──►  FB  (HC levels)
        │
        ├──►  F port H8/H9   — always driven, host register D inputs
        │
        ├──►  U7 '541 (HC)   — FB → D bus, /FOE
        │
        └──►  U8 '541 (HC)   — FB shifted right → D bus, /SOE
```

**Why the extra buffer.** LS outputs guarantee only about 2.7 V high; HC
inputs want 3.5 V. Feeding HC registers straight off an LS181 is the classic
homebrew intermittent. One HCT '541 fixes it at a single point, and everything
downstream — the '688, the sign tap, both bus drivers, the F port — sees clean
HC levels. It also buys the fan-out the '181 does not have.

**The shifter.** U8 is wired with crossed inputs: driver output bit *i* takes
`FB` bit *i+1*, and driver output bit 7 takes **`SRIN`**. That single buffer is
the entire right-shift path — no barrel shifter, no ALU configuration, no
cycle cost beyond selecting a different bus source.

**JP3** selects `SRIN`:

| JP3 | SRIN      | Gives                                                    |
| --- | --------- | -------------------------------------------------------- |
| GND | 0         | logical shift right (LSR)                                |
| FB7 | sign bit  | arithmetic shift right (ASR)                             |
| ext | H11 pin 8 | rotate right through carry (ROR), host drives the C flag |

`SOUT` (FB bit 0) is the bit falling out the bottom — latch it as the new
carry.

Left shift needs no path at all: it is `A plus A` in the ALU, with `COUT` as
the bit shifted out and `Cn` as the bit shifted in (0 for ASL, C for ROL).

---

## 8. Options and jumpers

| Ref | Option               | ICs                  | What it buys                                                                                                                                                          |
| --- | -------------------- | -------------------- | --------------------------------------------------------------------------------------------------------------------------------------------------------------------- |
| JP1 | '182 carry lookahead | +1 (U3)              | Removes a ripple stage; matters at speed and when cascading                                                                                                           |
| JP2 | COUT polarity        | –                    | Carry vs borrow convention on subtract                                                                                                                                |
| JP3 | SRIN source          | –                    | LSR / ASR / ROR from one driver                                                                                                                                       |
| JP4 | S-field steer        | +1 (U11 '86)         | One line conditionally complements the function select                                                                                                                |
| JP5 | B-port mux           | +2 ('157 ×2)         | B operand from register **or** straight off the data bus — the "result bus" trick that lets an operand come off memory, through the ALU, into a register in one cycle |
| JP6 | Zero cascade         | –                    | Single module vs multi-byte word                                                                                                                                      |
| JP7 | Bus interlock defeat | –                    | Leave fitted                                                                                                                                                          |
| —   | V (signed overflow)  | +1 '86 + 1 spare AND | See below                                                                                                                                                             |
| —   | Flag register        | +2 ('377 ×2)         | Latched C / Z / N / V with independent enables                                                                                                                        |

### Signed overflow, and why it needs its own logic

The textbook formulation is `V = Cn+7 ⊕ Cn+8` — the carry into the sign bit
XOR the carry out of it. **A 4+4 slicing cannot give you this**: the carry into
bit 7 is internal to the high '181 and never reaches a pin. This is why so
many '181 machines simply drop the V flag.

The way out is the sign-bit formulation, which needs no internal carry:

```
   Beff7 = B7 ⊕ SUB                     ; SUB tells us the '181 is adding /B
   V     = (A7 ⊕ F7) · (Beff7 ⊕ F7)
```

Three XOR gates and one AND. `SUB` is a host control line (H13 pin 2) asserted
whenever the microcode selects a subtract configuration — a single microcode
bit, or decoded from S3:S0 on the host side.

The result is exact for add and subtract, which is the only place V is defined
anyway. On logic operations, drive `SUB` however you like and simply don't
latch V.

The Minimal '181 CPU does not use this option — it dropped V deliberately and
recoded the idioms. A 68000-flavoured or signed-arithmetic-heavy host would
fit it.

---

## 9. Cascading to wider words

The module is a byte slice of a wider ALU. To build 16 bits:

| Wire        | From                      | To                                                                                    |
| ----------- | ------------------------- | ------------------------------------------------------------------------------------- |
| Carry       | low module `COUT`         | high module `CIN_A`, with `CSEL = 10`                                                 |
| Zero        | low module `ZERO`         | high module `/ZCASC` (via an inverter, or take the '688's active-low output directly) |
| Function    | one set of M, S3:0, STEER | both modules, in parallel                                                             |
| Bus enables | one `/FOE`, `/SOE`        | both modules                                                                          |

Then:

- **`SIGN`** comes from the high module only.
- **`HCOUT`** comes from the low module only (and is the bit-3 carry; the
  bit-11 carry is the high module's `HCOUT` if you want it).
- **`COUT`** comes from the high module only.
- **`V`** comes from the high module only.
- For true lookahead across the word, fit '182s on both modules and a
  third-level '182 off-module, fed from the two `/G` / `/P` pairs on H13.

Two modules is 16 bits at 18 core ICs. That is not a cheap 16-bit ALU, but it
is a *working* one you can build in an evening from parts you already have,
and the polarity work is already done.

---

## 10. Fitting it to different machines

**Accumulator machine (the Minimal '181 CPU).** A port ← the A-side tri-state
mini-bus (accumulator, index registers, constants, masks). B port ← the B
register. D ← the shared data bus. F → register D inputs. BSEL fitted, giving
the result-bus behaviour. Flag register fitted or depopulated depending on
whether the host wants to own it.

**Two-address register-file machine.** A port ← file read port A, B port ←
file read port B, F → file write port. D and the bus drivers unpopulated
entirely — a register-to-register machine has no need for them. BSEL
unpopulated. This is the leanest configuration: 9 ICs minus the two bus
drivers.

**Stack machine.** A ← TOS register, B ← NOS register, F → TOS. `AEQB` becomes
directly useful. The SHR driver gives you the shift words for free.

**Single-bus machine (the classic minimal design).** A and B ports both hang
off latches on the one bus; D strapped to the same bus; results return through
`/FOE`. F port unpopulated. Every operation costs two staging cycles, which is
the price of one bus — the module does not change that arithmetic, it just
makes the ALU end of it a solved problem.

**Bench instrument.** Switches on A, B, M, S3:0 and CSEL; LEDs on F and the
condition header. Nothing else populated. This is also the module's own
stage-A bring-up (§12), and it is worth keeping the switch/LED harness
afterwards — it is the fastest way to answer "what does S=1101 actually do."

---

## 11. Bill of materials

### Core — 9 ICs

| Qty | Ref    | Part     | Role                                                                                                      |
| --- | ------ | -------- | --------------------------------------------------------------------------------------------------------- |
| 2   | U1, U2 | 74LS181  | ALU slices, low and high nibble                                                                           |
| 1   | U4     | 74HC153  | carry-in 4:1 select (0 / 1 / CIN_A / CIN_B) — one section; second section's inputs brought to a pad field |
| 1   | U9     | 74HCT541 | **the level boundary** — F at LS levels → FB at HC levels, permanently enabled                            |
| 1   | U7     | 74HC541  | FB → D bus, /FOE                                                                                          |
| 1   | U8     | 74HC541  | FB shifted right → D bus, /SOE (crossed inputs, bit 7 ← SRIN)                                             |
| 1   | U10    | 74HC688  | zero detect on FB, cascade via /ZCASC                                                                     |
| 1   | U12    | 74HCT04  | carry polarity normalisation (in and out), HCT thresholds on Cn+4 / Cn+8 / A=B                            |
| 1   | U13    | 74HC00   | glue: bus interlock, SRIN selection, zero-cascade conditioning                                            |

Plus: one pull-up for the wire-ANDed A=B, decoupling per package, and the
jumper field.

### Options — up to 6 more

| Qty | Ref      | Part    | Role                                           |
| --- | -------- | ------- | ---------------------------------------------- |
| 1   | U3       | 74S182  | carry lookahead (socketed, JP1)                |
| 2   | U5, U6   | 74HC157 | B-port 2:1 mux, BSEL (JP5)                     |
| 1   | U11      | 74HC86  | S-field steer, 4 gates (JP4)                   |
| 1   | U16      | 74HC86  | V-detect, 3 gates + one spare AND from U13     |
| 2   | U14, U15 | 74HC377 | latched flag register, two independent enables |

**Fully populated: 15 ICs.** Leanest useful build (register-file machine, no
bus drivers, no options): 7.

### Notes on part selection

- The '181s are **LS**, not HC. HC '181s are rare and expensive; LS is the
  part everyone actually has. The whole level-translation story in §7 exists
  because of that choice.
- U9 and U12 **must** be HCT — they are the parts reading LS outputs.
- U7, U8, U10 can be plain HC: they read U9's output, which is already clean.
- The '182 is **S**, not LS or HC — that is the only speed grade it came in,
  and it is the only Schottky part on the board.

---

## 12. Bring-up sequence

Same discipline as the rest of the project: each stage is a complete, testable
thing, verified before the next layer goes on.

| Stage | Fit                                                | Prove it                                                                                                                                                                                                            |
| ----- | -------------------------------------------------- | ------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------- |
| **A** | U1, U2, power, switch/LED harness on A, B, M, S, F | Walk the M=1 logic table entry by entry against §4. All sixteen. This is where a wrong S-bit order or a swapped nibble shows up immediately.                                                                        |
| **B** | U4, U12 (carry section)                            | `A=$0F, B=$01, S=1001, Cn=0` → F=$10. The nibble boundary is the thing being tested. Then `Cn=1` and confirm F=$11 — that proves the **carry-in inversion**, which is the single most likely thing to be backwards. |
| **C** | U9, F port                                         | Scope or LED the F header; confirm HC-level swing, not the LS 2.7 V.                                                                                                                                                |
| **D** | U7, /FOE                                           | Tri-state check: with /FOE high, the D port must float — pull it with a resistor and watch it follow the resistor, not the ALU.                                                                                     |
| **E** | U10, U12 (condition section), pull-up              | ZERO on `$00`; SIGN on `$80`; COUT polarity on `$FF + $01` (must read 1) **and** on subtract `$05 − $03` (must also read 1 — no borrow). HCOUT on `$0F + $01`.                                                      |
| **F** | U8, JP3                                            | `F=$81`, SRIN=0 → D reads `$40`, SOUT reads 1. Then JP3 to FB7 and confirm `$C0`.                                                                                                                                   |
| **G** | U5, U6, JP5, JP7                                   | Put a known pattern on D, select BSEL=1, confirm the ALU sees it. Then assert /FOE and confirm the **interlock** blocks the driver.                                                                                 |
| **H** | U3, JP1                                            | Populate the '182 and re-run stage B and E. Results must be **identical** — this stage changes timing, never behaviour. Diff before and after.                                                                      |
| **I** | U11, JP4                                           | STEER=0 with S=0000, Cn=1 → increment. STEER=1, same inputs → decrement.                                                                                                                                            |
| **J** | U16                                                | `$7F + $01` → V=1, SIGN=1. `$FF + $01` → V=0, COUT=1. `$80 − $01` with SUB=1 → V=1.                                                                                                                                 |
| **K** | U14, U15                                           | Clock a condition in, change the inputs, confirm the latched value holds. Then confirm `FLGC` alone does not disturb Z and N.                                                                                       |

Stages H and I are the two that can be done at any time — they are pure
retrofits. Stage E is the one worth being slow and pedantic about; every
polarity mistake on this board lives there.

---

## 13. Known traps

Collected from the '181's reputation and from what this design has already run
into:

- **The carry pins are active-low in active-high data mode.** Both of them.
  The module hides this, but if you probe the '181 pins directly during
  bring-up, expect the inverse of what the header says.
- **`A minus B` needs `Cn = 1`, not 0.** The function is `A minus B minus 1
  plus Cn`. A carry-in of 0 gives you `A − B − 1`, which is a borrow-in
  subtract, not a plain one.
- **There is no `B minus 1` and no `B plus 1` that isn't A-mediated.**
  Decrementing a value that lives on the B side takes two ALU passes:
  negate (`$00 − B`) then complement. Route the operand to the A side if you
  can.
- **There is no right shift.** The SHR driver is not an optimisation, it is
  the only right-shift path.
- **`A=B` is open collector and subtract-mode-only.** One pull-up across both
  slices, and don't read it after a logic operation.
- **V is not derivable from the carry chain** in a 4+4 split — the carry into
  bit 7 never leaves the package. Use the sign-bit formulation (§8) or accept
  no V flag.
- **LS outputs into HC inputs is the classic intermittent.** It works on the
  bench and fails in the case. U9 is not optional.
- **Tie unused '181 inputs.** A floating select or mode line produces results
  that look like a microcode bug for a long time before you suspect the wiring.
- **The '182 is a Schottky part** with Schottky current draw. Budget for it,
  and decouple it properly.
- **Changing M mid-cycle while carry is propagating** produces transient
  garbage on F. Harmless if the host only samples on a clock edge — which it
  should be — but worth knowing when you are staring at a scope.

---

## 14. Fit against the Minimal '181 CPU

If this module is adopted, the following moves off the CPU board:

| Moves to module              | Qty    |
| ---------------------------- | ------ |
| 74LS181                      | 2      |
| 74HC153 (carry-in select)    | 1      |
| 74HC86 (S-field steer)       | 1      |
| 74HC688 (Z detect)           | 1      |
| 74HC541 (F→bus)              | 1      |
| 74HC541 (SHR)                | 1      |
| 74HC157 ×2 (BSEL result bus) | 2      |
| 74HC377 ×2 (C, and N/Z)      | 2      |
| **Total**                    | **11** |

The CPU board drops from 44 logic ICs to **33**, and the ALU module carries 15
fully populated. Total machine IC count rises by roughly four — the honest
price of modularity, paid in header buffers and level translation that an
integrated design gets to skip. What you buy is that the ALU can be built,
tested and *finished* on its own bench, and then reused.

Staging note: the module's stages A–E map cleanly onto the CPU's **stage 3**
(ALU + A), and its stages F–G onto **stage 17** (result bus). The CPU's stage
14 (shift paths) becomes populating U8 and setting JP3 — a jumper and a chip
rather than a wiring change on the CPU board.

### One thing to check on the CPU document

The CPU design document assigns the two halves of a single **74HC153** to two
independent jobs: section 1 as the carry-in 4:1 (CSEL), section 2 as the
microcode Z-address-line select (TESTSEL).

The '153's two sections have **independent enables and independent data
inputs, but shared select lines**. Section 2 is therefore addressed by the same
CSEL code as section 1 — it cannot respond to an independent TESTSEL control
bit. As described, those two functions can't share the package.

The fix is small and, conveniently, is forced by this module anyway: CSEL moves
to the module's own '153, and TESTSEL becomes a 2:1 on the CPU board — one
section of a '157, or two gates. Net CPU IC count is unchanged from the table
above (the '153 leaves, a '157 section arrives).

I may be missing an intended trick here, so it's worth a second look before
anything is re-burned — but on the face of the description in the document, the
select lines don't allow it.

---

## 15. Open items

- **Propagation numbers are not in this document on purpose.** The LS181's
  A→F, A→Cn+4 and Cn→Cn+4 delays, and the '182's contribution, need to come
  off the datasheets rather than from memory before anyone builds a timing
  budget on them. The structure of the critical path is `A/B setup → slice 0 →
  carry → slice 1 → U9 → destination setup`; fill in the numbers when the
  parts are in hand.
- **PCB.** The module is a candidate for the same parameterised generic layout
  as the memory and microcode shadow boards — four byte ports, a control
  header block, and a jumper field is a shape that repeats.
- **A second board revision** would be the place to consider a card-edge or
  backplane connector instead of fourteen headers. Fourteen is the price of
  the 8-pin-max convention; it is defensible for a bench module and
  inconvenient for a rack.
