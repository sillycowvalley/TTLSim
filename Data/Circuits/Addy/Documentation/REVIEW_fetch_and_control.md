# Review — Addy Fetch and Control board + GAL1/GAL2 v1

Netlist built by union-find over `connections`, with netlabels merged by
name (bus taps resolved as `startBit + pinNumber − 1`) and the GND/VCC
symbols merged globally. 33 devices, 117 items, 184 connections, 0 links.

**Board contents:** U1 GAL22V10 (SEQ), U2 GAL22V10 (ENA), U3/U4 74HC32,
U5 74HC157, 16 headers, 5 decoupling caps + 10 µF bulk, R1, J1–J5.
Five ICs. The ALU, operand registers, immediate buffers, ROM and IR are
**not** on this board — layers 6 and 7 exist but hold no devices here.

---

## Verdict

The logic is correct. Every equation in both `.pld` files checks out
against the v1 design note, and the '157 select polarity — the one place
this class of design usually gets bitten — is right. What follows is one
electrical fault, one pin-assignment decision that is free today and
expensive after fab, and a handful of pre-fab items.

---

## 1. `/HALTREQ` is driven push-pull into a net with a button on it — FIX

**Severity: hardware damage.**

`PIN 21 = !HALTREQ;` with no `.oe`, so the OLMC is permanently enabled.
When no HLT is executing, U1 pin 21 drives the net **high**. That net is
`/HALTREQ` → H3.6 → the clock module, where SW2 **HALT REQ** pulls the
same net to ground.

Press HALT REQ while the CPU is running normally and you have a GAL
output driving hard into a dead short, held for as long as the button is
down — which at panel rates is a deliberate ~1 s press. The ATF22V10C
datasheet permits one output shorted, briefly; this is neither.

The clock module's own design rules already say this: *"the sole
wired-AND is pulled-up inputs where every driver only pulls low
(/HALTREQ, /TRST)."* The GAL is the driver that doesn't comply.

Fix is one line — output term and OE term identical, so the pin drives
low when asserted and releases to Hi-Z otherwise:

```
HALTREQ.oe = TB1 & !TB0 & !RST & IR15 & IR14 & IR13 & IR12 & IR11;
```

The OE row is separate from the logic rows in the 22V10 array, so this
costs zero product terms.

**`/TRST` has the same shape** (`PIN 20 = !TRST`, push-pull) and the same
rule applies to it. Today U1 is the only driver, so there is no live
hazard — but the clock module has a *T Service* header, and the moment
anything else touches that net the failure is identical. Same one-line
fix, same zero cost. Do both.

---

## 2. WE sits on the smallest macrocell — DECIDE BEFORE FAB

**Severity: forecloses v2 on this copper.**

Product-term headroom, against the validated 22V10 geometry (block sizes
8, 10, 12, 14, 16, 16, 14, 12, 10, 8 for pins 23 → 14):

| U1 pin | Signal | Terms used / available |
|---|---|---|
| 14 | **WE** | **6 / 8** |
| 15 | SUB | 3 / 10 |
| 16 | CIN | 4 / 12 |
| 17 | WSEL7 | 2 / 14 |
| 18 | ASELRS | 3 / 16 |
| 19 | FLAGEN | 1 / 16 |
| 20 | /TRST | 1 / 14 |
| 21 | /HALTREQ | 1 / 12 |
| 22, 23 | SPARE | 0 / 10, 0 / 8 |

The most complex equation on the board landed on the smallest cell, while
FLAGEN (one term) occupies a 16-term cell. U2 has the opposite and
correct arrangement — its two big equations, AOE at 7 and IMMOE at 4,
both sit on the 16-term cells at pins 18 and 19.

This bites in v2. Class 11 gains MOVC (11100), MOVNC (11101) and MOVN
(11110), each a conditional write needing its own WE product term, and
LD needs WE extended to T3. That is at least four more terms on a cell
with two free. **WE will not fit on pin 14 in v2.**

Swapping WE ↔ FLAGEN gives WE a 16-term cell and FLAGEN an 8-term cell
it will never outgrow. WE goes to H7.4 and FLAGEN to H12.2 — both single
header destinations, so it is purely a routing choice while the board is
still a file. After fab it is a cut-and-strap.

Worth the same look at CIN (4/12) and SUB (3/10) against v2's carry-in
selection, though neither is near the edge.

---

## 3. `IR[3]` and `IR[4]` arrive nowhere — CONFIRM

H5 carries IR[0], IR[1], IR[2], IR[5], IR[6], IR[7] with **pins 4 and 5
free**. H6 carries IR[8]–IR[15]. So the two reserved bits are the only
part of the instruction word this board never sees.

That is correct *for this board* — IR[3:4] are reserved and nothing here
uses them. It is a problem only if the Datapath board's imm8 '541 is fed
from these headers, because imm8 is IR[7:0] and bits 3 and 4 would read
as pulldown zeros. Every immediate with a 1 in bit 3 or 4 would be
silently wrong, and the classic symptom is a counter program that works
(`ADDI r0,1`) next to a jump that doesn't (`LDI r7,0x18`).

Confirm the Datapath taps IR[7:0] directly from Code and IR. If it is
meant to come through here, H5 pins 4 and 5 are already sitting there
waiting for the two missing bits — a free fix now.

---

## 4. AOE and IMMOE have no `!RST` term — CHEAP TO REMOVE THE COUPLING

The reset vector needs D = 0, which needs both operand sources off. U2
achieves that only because the clock module clears the T counter during
reset, making every T2-qualified term false. There is no explicit RST
term in either equation.

It works, and the clock module does guarantee it. But RST is already
wired to U2 pin 3 and used in exactly one equation (LEDCE), so adding
`& !RST` to AOE and IMMOE costs **literals, not product terms** — the
term counts stay at 7 and 4. It converts a cross-module behavioural
dependency into a local guarantee for free. Take it.

---

## 5. R1 — value unset, and a label-merge hazard on integration

`R1` has `value = null`. It sits between H3.2 (unnamed net, CLK in from
the clock module) and `CLK_A`, which fans out to H10.2, H11.2, H13.2 and
H14.2.

Two things:

- **Set the value** before fab. Presumably 100 R to match house practice.
- **The naming needs checking against the clock module.** Per the layout
  guidelines, the clock module already drives CLK through a 100 R series
  resistor at its header and names the downstream side `CLK_A`. RST, T
  and FETCH arrive here already carrying the `_A` suffix, which is
  consistent with that. If CLK arrives at H3.2 as the clock module's
  `CLK_A`, and R1's other terminal is *also* labelled `CLK_A`, the global
  label merge loops the signal around R1 and shorts it out — diagnostic
  **TTL014**, exactly the 2026-07-17 incident.

  In this file H3.2's net is unnamed, so nothing is wrong here. The
  question is what it resolves to once the boards are in one project.
  Either R1 is a second series element and its downstream needs a fresh
  name (`CLK_B`), or R1 is redundant with the clock module's own resistor
  and should come out. Load the merged project and check TTL014 on R1.

---

## 6. Header hygiene — pre-fab items

**Missing ground returns.** H5, H6, H7 and H16 have no GND pin. H6 is the
worst case: eight signals (IR[8]–IR[15]) over a ribbon with no return in
the cable. H5 has pins 4 and 5 free and H16 has spare capacity; H7 is
full at four pins. Worth widening H7 to 5 or 6 and reassigning, while it
is still free to do so.

**Unlabelled headers.** All sixteen have `label = null`. On a board whose
entire job is connectors, that is the one thing you will want silkscreened.
Same for keying and orientation — nothing in the file expresses it yet.

**Free pins for future use** (already correct, noted for the record):
H3 4,5,7,8 · H4 4,5,8 · H12 7,8 · H13 7,8.

**SPARE header.** Both GALs bring pins 22 and 23 out to H16 as
SPARE[0..3] — four unused OLMCs on a header. That is the right call and
it is what makes a v2 GAL revision survivable on this copper.

---

## What checks out (verified, not assumed)

- **'157 select polarity.** U5 pin 1 = ASELRS. On a '157, SELECT low
  routes the A inputs; A = IR[10:8] = **rd**, B = IR[7:5] = **rs**. The
  source comment says "1 = rs, 0 = rd" and it is right. `/G` (pin 15) is
  grounded, so the mux is permanently enabled. 4th section inputs tied to
  GND, output unconnected.
- **AADDR steering.** AMUX → U4 '32 ORed with FETCH_A → AADDR[2:0]. T0
  forces 7. Unused 4th gate has both inputs on GND.
- **WADDR steering.** IR[10:8] → U3 '32 ORed with WSEL7 → WADDR[2:0].
  Same tie-off discipline. WADDR[3]/AADDR[3] correctly absent — strapped
  low at the register file per the Thumby build.
- **Reset vector.** Under RST: WE = 1, WSEL7 = 1, CIN = 0, SUB = 0,
  AOE = 0, IMMOE = 0 → D = 0 → r7 ← 0. Correct.
- **All five unused GAL dedicated inputs are jumper-tied to GND** —
  J1 → U1.10, J2 → U1.11, J3 → U1.13, J4 → U2.11, J5 → U2.13. Exactly
  right, and a good pattern.
- **Decoupling.** One 100 nF per IC (C2–C6) plus C1 10 µF bulk. House
  policy met.
- **All 184 connections resolve.** No dangling references, no chip with a
  floating power pin, no duplicate labels, no net confined to a single
  chip.
- **IRCE is decoded from the T bits** (`!TB1 & !TB0`) rather than taken
  from the FETCH input, which is better than the design note describes —
  it does not depend on the FETCH signal path. Worth correcting in the
  v1 design document, which still says "IR /CE comes from FETCH".
- **Every GAL equation matches the design note**, including the two
  places the note is terse: `SUB`'s `(!IR14 # !IR12)` correctly excludes
  ADDIH, and `WE`'s conditional term is `Z ⊕ IR11` expanded into two
  product terms rather than relying on XOR expansion.

---

## Open question for a floating-input audit

Z (U1.4), /EQH (U2.4) and /EQL (U2.5) come in on H12 from the Datapath
board. If that board is absent or its flag hardware unpopulated — which
is precisely the staged-bring-up case — those three GAL inputs float.
The five *internal* unused inputs are jumper-tied; these three are not,
and they cannot be, because in normal operation they are driven.

They need resistors, not jumpers: 10 k pullups on /EQH and /EQL (parks
them de-asserted, ZNEW = 0) and a 10 k pulldown on Z. Three passives,
and staged bring-up stops lying to you.

---

## Bottom line for v0

This board plus the Code and IR board still cannot execute anything —
there is no adder, no A register and no immediate buffer here. The
shortest path to a running v0 is this PCB + Code and IR + a hand-wired
datapath of **11 ICs** on the existing headers:

4× '283 · 4× '86 · 2× '574 (A only) · 1× '541 (imm8) · pulldown networks
on the A and B buses.

Every signal it needs is already brought out: /AOE and /IMMOE on H13,
SUB and CIN on H11, WE and WADDR on H7, AADDR on H8, CLK_A on H10–H14,
IR on H5/H6. Leave H12 (flags), H14 (/LEDCE, /INOE) and the B-side
unpopulated. Burn the v1 GALs — with the `/HALTREQ` fix — and run
`5000 4001 5701`.
