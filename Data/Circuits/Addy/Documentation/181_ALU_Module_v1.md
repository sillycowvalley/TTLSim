# '181 ALU Module v1 — Blinky Family Byte Slice

A **generic 8-bit ALU module** for the Blinky family: two 74LS181 slices, a
'182 lookahead, an HCT level fence, combinational condition outputs, and a
**function-code GAL** that turns the '181's seven raw control lines into a
four-bit operation code whose meaning is set per host by the burned JEDEC.
Two identical boards cable together for a 16-bit machine. The module owns
*computation and the '181's polarity conventions*; it never sequences or
chooses its own operands.

One PCB serves Addy v2 (as a pair), the 8080-ish machine, the 6502-ish
machine, and the bench. Capability is set by socket population, the JEDEC in
U3, and a small link field — generic through subtraction, per house policy.

Companion to the **Blinky Clock Module v3** and the **Register File Module**
under the same discipline: one board, one job, generic headers, staged
bring-up, fails toward safe.

> **Status: captured and simulation-verified (solo/Addy configuration),
> not yet built in copper.** The TTLSim capture (`181_ALU_Module.ttlproj`,
> with embedded testbench and the `ALU_ADDY` JEDEC in U3) passes all 18
> function-code vectors and the SHR sequence with every condition output
> correct — F, COUT, HCOUT, ZERO, SIGN, SOUT, AEQB, V, PAR (run of
> 2026-07-24, zero warnings). **The 16-bit pair configuration is also
> verified** (`181_ALU_Pair.ttlproj`): 15 vectors covering seam carry both
> directions, the zero cascade, cross-board AEQB, word-wide V/SIGN, and the
> chained 16-bit right shift — all passing, same date, with the boards
> joined exactly as the copper will be: three ribbon `links` (H8→H11
> cascade, H4↔H4H and H5↔H5H multi-drop) and J1H/J2H at 2-3, nothing
> else shared. **The 8080 configuration is also verified** (`ALU_8080.pld`
> in U3, J3 at 2-3): 24 vectors + the RRC/RAR shift rows, all passing —
> including the LK_BORROW conditioning, the A7 rotate carry, and the
> S=0000/1111/1100 arithmetic codes no Addy vector touches. Every copper
> path on the board has now been exercised in simulation. Along the way
> three TTLSim '181 model defects were found and fixed (A=B drive type,
> wrong function-table column, P̄/Ḡ exports inconsistent at S=1100).
> Jumper state in a
> `.ttlproj`: `switchClosed` false = position 1-2, true = 2-3.
> Verification en route surfaced and fixed two
> TTLSim model defects (A=B drive type; wrong function-table column) and
> one schematic defect ('86 gate-3/gate-4 output pin swap in the V logic).
> Timing figures remain catalogue-model estimates; the pair configuration
> and the 8080/6502 JEDECs are not yet exercised.

This document supersedes three earlier drafts — *6502 Byte ALU Module v1*,
*8080 ALU Module*, and *Thumby ALU Module* — reconciling them into one
module. The deltas, including one defect found in the Thumby draft, are
recorded at the end.

---

## What the module deliberately does not do

The partition discipline is what keeps it generic:

- **No operand selection.** A and B arrive at the headers already chosen.
  Immediate-vs-register-vs-memory steering is the host's decode problem.
- **No instruction decode.** A four-bit *function code* arrives, plus the C
  flag. No instruction reaches the board — the code's meaning is the
  published table of the JEDEC burned into U3, and nothing else.
- **No flag *meaning*.** The module emits conditions. Which of them the
  host's status register keeps, how they pack into a PSW byte, shadow/restore
  for interrupts, and what branches test — all architecture, all host-side.
- **No register file, no write-back mux, no PC/SP arithmetic.**
- **No decimal adjust logic.** The aux socket (option) lets an 8080-class
  host bolt its DAA GAL onto the B port, but the equations are the host's.

The dividing line: **the module computes, the host decides.** The
function-code GAL softens the original "no opcode reaches the board" only
this far: a *code* reaches the board, and each host ships the JEDEC that
defines it.

---

## Polarity contract — the decision, stated once

**Data is active-high (true binary) at every header and at the '181 pins.
There are no boundary inverters. Carry polarity is folded into the
function-code GAL on the way in and one HCT stage on the way out.**

Rationale, recorded so future-us doesn't relitigate:

1. In active-high data mode the '181's Cn and Cn+8 are active-low. Cn is
   driven by U3, so its inversion is written into the GAL equation and
   costs nothing; Cn+8 costs one HCT inverter section. Inverting the *data*
   instead costs six '240s and — decisively — changes which datasheet
   column applies. With an inverting boundary the true-data behaviour is the
   **active-LOW** column, whose logic rows are the De Morgan duals of the
   active-high rows: XOR↔XNOR (S=0110↔1001), AND↔OR (1011↔1110),
   constants 0↔1 (0011↔1100), increment↔decrement (0000↔1111
   arithmetic). ADD, SUB, and pass-A are identical in both columns, so an
   add/sub test sweep passes while every logic operation is wrong. The
   Thumby draft ratified the inverting boundary together with the claim that
   the active-HIGH column applies verbatim — those two statements are
   incompatible, and the defect is exactly the kind a casual test suite
   misses.
2. All published S/M codes in this document and in every host JEDEC are
   therefore the **active-HIGH column**, applied verbatim.
3. Consequence accepted: the '181's A=B wire-AND cannot serve as a general
   zero flag in this convention (it asserts on F = all-ones). Zero detect is
   a '688, which has clean totem-pole timing anyway. A=B is still brought
   out as a subtract-mode equality condition.

This also resolves the standing Addy v2 open question ('564/'540 inverting
variants vs adjusted GAL3 codes): neither. Active-high data, and the carry
inversion lives in a GAL equation — now U3's, on the module, rather than
GAL3's on the CPU board.

---

## The function-code GAL (U3, GAL16V8)

Ten '181 codes cover all three current hosts; no host uses more than
twelve distinct hardware behaviours. So the module's control interface is a
**4-bit function code, FC[3:0]**, decoded on-board by one GAL16V8 whose
JEDEC is burned per host. All eight OLMCs are used:

| GAL output | Drives | Replaces at the header |
|------------|--------|------------------------|
| S3–S0, M | the '181s directly | five raw select lines |
| SUB | the V rule; the LK_BORROW conditioning | a host-driven SUB wire |
| BKILL | B latch /OE, **active-high** (high = latch Hi-Z; pulldowns give B = 0) | a host-driven B-kill wire |
| Cn | the '181/'182 carry-in **pin-direct, active-low sense in the equation** | a '153 carry mux, its select lines, and the carry-in polarity gate |

Inputs: FC[3:0], **CFLG** (the host's latched C flag), and **A7** (tapped
on-board from the A latch output, for rotate-left-around codes where the
carry-in is the bit coming round). Six of ten inputs used; every output is a
function of at most six variables, and since the code assignments are free
per JEDEC, any output that lands on an unlucky (parity-shaped) minterm
pattern is fixed by renumbering the codes — BlinkyJED flags it immediately.

What this buys, beyond five wires:

- **The code can *be* an instruction field.** The 8080 ALU group is
  `10 ooo sss` with ooo = ADD/ADC/SUB/SBB/ANA/XRA/ORA/CMP in order —
  FC[2:0] = IR[5:3] wired through, FC3 selecting a second page. The 6502's
  cc=01 `aaa` field lines up the same way. Addy's GAL1 sheds the five S/M
  outputs it was to grow, emitting FC instead.
- **The '153 carry mux, its two select lines, both external carry-in pins,
  and the carry-in conditioning gates all disappear.** Carry-in behaviour is
  an equation: `Cn = 1` for plain SUB/CMP, `= CFLG` for ADC-class, `= /CFLG`
  for 8080 SBB, `= A7` for RLC — per code, per host, no board change.
- Net control interface: **FC[3:0] + CFLG**, down from nine lines. The GAL
  is roughly IC-neutral against the parts it deletes.

**Per-board, identical JEDECs.** A 16-bit pair fits U3 on both boards with
the same burn; FC and CFLG parallel across the seam exactly as S/M would
have. Boards stay interchangeable. (The high board's Cn comes from the seam,
not its GAL — link LK_CN, see Carry.)

**Bench testing is with the GAL fitted.** There is no raw S/M access on the
board. The division of proof: the full 32-row '181 sweep is a **TTLSim
exercise**, where S, M, and Cn are directly drivable and both the '181 and
'182 have models; the **JEDEC** is proven off-board through the standard
pipeline — BlinkyJED equations → fuse map → WinCUPL diff → Arduino
exhaustive test (at six inputs, exhaustive genuinely means exhaustive: 64
vectors against the published code table) — and only then burned and
fitted; the **bench** then walks the FC code table against both. Never
power the board with U3's socket empty — S, M, and Cn float.

### Exemplar code tables

Normative for nothing — each host's `.pld` is the authority — but these are
the assignments the exemplar JEDECs will use. Arithmetic rows implicitly
*plus Cn*; all codes active-high column.

**`ALU_ADDY.pld`** (FC emitted by GAL1 from the opcode):

| FC | Op | M | S | Cn | SUB | BKILL |
|----|----|---|---|----|-----|--------|
| 0000 | ADD / ADDI / ADDIH / CMN | 0 | 1001 | 0 | 0 | — |
| 0001 | SUB / SUBI / CMP / CMPI | 0 | 0110 | 1 | 1 | — |
| 0010 | ADDC | 0 | 1001 | CFLG | 0 | — |
| 0011 | SUBC | 0 | 0110 | CFLG | 1 | — |
| 0100 | MOV / LDI / TST | 0 | 1001 | 0 | 0 | asserted |
| 0101 | AND | 1 | 1011 | — | 0 | — |
| 0110 | OR | 1 | 1110 | — | 0 | — |
| 0111 | XOR | 1 | 0110 | — | 0 | — |
| 1xxx | spare | | | | | |

**`ALU_8080.pld`** (FC[2:0] = IR[5:3] for page 0):

| FC | Op | M | S | Cn |
|----|----|---|---|----|
| 0000 | ADD / ADI | 0 | 1001 | 0 |
| 0001 | ADC / ACI | 0 | 1001 | CFLG |
| 0010 | SUB / SUI | 0 | 0110 | 1 |
| 0011 | SBB / SBI | 0 | 0110 | /CFLG |
| 0100 | ANA / ANI | 1 | 1011 | — |
| 0101 | XRA / XRI | 1 | 0110 | — |
| 0110 | ORA / ORI | 1 | 1110 | — |
| 0111 | CMP / CPI | 0 | 0110 | 1 |
| 1000 | INR | 0 | 0000 | 1 |
| 1001 | DCR | 0 | 1111 | 0 |
| 1010 | CMA | 1 | 0000 | — |
| 1011 | RLC | 0 | 1100 | A7 |
| 1100 | RAL | 0 | 1100 | CFLG |
| 1101 | DAA pass (aux GAL on B) | 0 | 1001 | 0 |
| 1110 | PASS A (RRC/RAR via SHR driver) | 1 | 1111 | — |
| 1111 | spare | | | |

SUB asserted on 0010, 0011, 0111 (feeds LK_BORROW: 8080 CY is a borrow).

**`ALU_6502.pld`** (FC[2:0] = the cc=01 `aaa` field for page 0):

| FC | Op | M | S | Cn |
|----|----|---|---|----|
| 0000 | ORA | 1 | 1110 | — |
| 0001 | AND / BIT | 1 | 1011 | — |
| 0010 | EOR | 1 | 0110 | — |
| 0011 | ADC | 0 | 1001 | CFLG |
| 0100 | PASS A (STA slot — unused by ALU, harmless) | 1 | 1111 | — |
| 0101 | PASS A (LDA — NZ update) | 1 | 1111 | — |
| 0110 | CMP / CPX / CPY | 0 | 0110 | 1 |
| 0111 | SBC | 0 | 0110 | CFLG |
| 1000 | INC / INX / INY | 0 | 0000 | 1 |
| 1001 | DEC / DEX / DEY | 0 | 1111 | 0 |
| 1010 | ASL | 0 | 1100 | 0 |
| 1011 | ROL | 0 | 1100 | CFLG |
| 1100 | PASS A (LSR/ROR via SHR driver) | 1 | 1111 | — |
| 1101–1111 | spare | | | |

SUB asserted on 0110, 0111 (V rule; LK_BORROW open — 6502 C is no-borrow).

**Trap, recorded:** M=0 S=1111 is *A − 1 plus Cn* — it passes A only with
Cn = 1. The Addy v2 design's MOV entry (M=0, S=1111, no Cn note) is this
trap. The BKILL form (ADD, B forced to 0) removes the case entirely and
produces the same real arithmetic flags MOV already promises.

---

## Block diagram

```
  A port ──► U1 '573/'574 A latch ──────────────┬── A7 ──┐
  (H2/H3)        /OE = GND                      │        │
                                                │        │
  B port ──► U2 '573/'574 B latch ──────────────┤        │
  (H3/H5)        /OE ◄ BKILL ────────────────┐  │        │
                 10k pulldowns ┴ (B = 0)     │  │        │
                                          ┌──▼──▼─────┐  │
  FC3:0 ──►┌──────────────┐  S3:0, M ────►│           │──► Cn+8 ─► cond. ─► COUT
  CFLG ───►│ U3 GAL16V8  │  Cn (direct) ►│ U4, U5    │──► Cn+4 ─► cond. ─► HCOUT
           │ per-host     │  SUB ─► V,LK  │ 2×74LS181 │──► A=B (pull-up) ─► AEQB
           │ JEDEC        │  BKILL ───────┘ + U6 '182 │──► /G, /P ────────► H8
           └──────────────┘                └─────┬─────┘◄── LK_CN: GAL Cn / CNX (seam)
                                                 │ F (LS levels)
                                        ┌────────▼────────┐
                                        │ U7 74HCT541     │   the level fence
                                        │ always enabled  │   one per board
                                        └──┬────┬────┬────┘
                ┌──────────────────────────┘    │    └───────────────────┐
                │                               │                        │
          F port (H6/H9)                 U10 '688 ─► ZERO       U14 '541: FB → D (/FOE)
          always driven,                 FB7 ──────► SIGN       U15 '541: FB>>1 → D (/SOE)
          HC levels                      FB0 ──────► SOUT              SRIN ─► bit 7
                │
                └─► U12 V logic ('86 + AND) ─► V        U16/U17 '377 flag latch (option)
                                                        U13 '280 parity (option)
                                                        U18 GAL22V10 aux → B port (option)
```

---

## Parts

### Core — 11 ICs

| Ref | Part | Role |
|-----|------|------|
| U4, U5 | 74LS181 | ALU slices, low and high nibble |
| U6 | 74S182 | carry lookahead across the two slices — fitted as standard (stock is plentiful; TTLSim has a '182 model) |
| U1, U2 | 74HC573 **or** 74HC574 | A and B operand latches — pin-compatible pair; transparent or edge per host |
| U7 | 74HCT541 | **the level fence** — F at LS levels → FB at HC levels, permanently enabled |
| U10 | 74HC688 | zero detect on FB, cascade via /ZCASC |
| U8 | 74HCT04 | Cn+4 / Cn+8 / A=B normalisation (HCT thresholds on LS outputs) |
| U9 | 74HCT86 | LK_BORROW conditioning on COUT/HCOUT; two gates spare |
| U11 | 74HC00 | glue: bus interlock, zero-cascade conditioning, spare AND for V |
| U3 | GAL16V8 (ATF16V8) | **function-code decode** — per-host JEDEC; socketed |

Plus one pull-up for the wire-ANDed A=B, 10 k pulldown network on the
post-latch B nets (the BKILL zero), 0.1 µF per populated socket, link field.

### Options

| Ref | Part | Buys | Needed by |
|-----|------|------|-----------|
| U14 | 74HC541 | FB → D bus, /FOE | bus machines |
| U15 | 74HC541 | FB shifted right → D bus, /SOE; crossed inputs, bit 7 ← SRIN | 8080-ish, 6502-ish |
| U12 | 74HC86 | V (signed overflow), sign-bit formulation, + one AND from U11 | Addy, 6502-ish |
| U13 | 74HC280 | even parity over FB[7:0] | 8080-ish |
| U16, U17 | 74HC377 | latched flags, two independent enable domains (C-domain / NZ-domain) | hosts not owning their flags |
| U18 | GAL22V10 | aux function socket onto the B port (/AUXOE) — DAA exemplar | 8080-ish |

**Core 11; fully optioned 18. A 16-bit pair, Addy configuration: 23.**

---

## Interface

Header convention as built: **GND on pin 1 of the control and condition
headers (FC+CTL, STROBES, COND, FLAGS); the data headers are all-signal**;
VCC appears on the power header only. No series elements at any header —
labels keep one name across the boundary; TTL014 does not apply.

| Hdr | Pins | Name | Contents |
|-----|------|------|----------|
| H1 | 2 | PWR | 1 VCC · 2 GND |
| H2 | 8 | A[7:0] | A operand, all-signal |
| H3 | 8 | B[7:0] | B operand, all-signal |
| H3 | 8 | FC+CTL | 1 GND · 2–5 FC0–FC3 · 6 CFLG · 7 LEA · 8 LEB |
| H5 | 4 | STROBES | 1 GND · 2 /FOE · 3 /SOE · 4 spare — fully multi-droppable |
| H9 | 8 | F[7:0] | result (FB), all-signal |
| H7 | 8 | COND | 1 GND · 2 COUT · 3 HCOUT · 4 ZERO · 5 SIGN · 6 SOUT · 7 AEQB · 8 V |
| H6 | 8 | CASCADE | 1 /G · 2 /P · 3 /CN8 · 4 CNX · 5 /ZCASC · 6 /AUXOE · 7 PAR · 8 /ZOUT — all-signal (full) |
| H9 | 8 | D[7:0] | shared data bus (option), all-signal |
| H4 | 8 | FLAGS | 1 GND · 2 CLK · 3 /FLGC · 4 /FLGNZ · 5 CLAT · 6 ZLAT · 7 NLAT · 8 VLAT |

### Control inputs

| Signal | Sense | Meaning |
|--------|-------|---------|
| FC3–FC0 | code | the operation, per U3's published JEDEC table |
| CFLG | active-high | the host's latched C flag — U3's carry-in source for ADC/SBC-class codes |
| /LEA, /LEB | per U1/U2 fit | latch enable ('573) or clock ('574). Strap for transparent |
| /FOE, /SOE | active-low | drive FB / FB>>1 onto D |
| SRIN | level | bit injected at result bit 7 of the shifted path. Strapped at the **top board's unplugged H8 (CASC UP) pin 5** — GND = LSR, loop FB7 (H6) back = ASR, drive the C flag = ROR. Lower boards receive it on the cascade ribbon (H11 pin 5 ← the board above's FB0). Deliberately absent from H5 so the strobes ribbon can multi-drop |
| /ZCASC | active-low | zero-detect cascade in from the lower-order board |
| /CN8, CNX | pin-level (active-low) | raw carry seam: low board's /CN8 → high board's CNX. LK_CN selects CNX in place of U3's Cn on the high board |
| /AUXOE | active-low | aux GAL owns the B port. One driver per net — host's discipline |
| FLGC, FLGNZ | edge-sampled | flag latch enables, carry domain and NZ domain, sampled by CLK with setup/hold — same contract as the register file's /EN |

### Conditions (all combinational, from FB, always live)

| Signal | Source | Sense |
|--------|--------|-------|
| COUT | Cn+8, normalised; SUB-conditioned if LK_BORROW | active-high carry; without LK_BORROW: 1 = no borrow on subtract ('181/6502/Addy convention). With: 1 = borrow (8080 convention) |
| HCOUT | Cn+4 between slices, normalised | active-high carry out of bit 3 |
| ZERO | '688 on FB, /ZCASC-gated | 1 = result zero (whole word on the top board of a pair) |
| SIGN | FB bit 7 | the sign bit — meaningful on the most significant board only |
| SOUT | FB bit 0 | the bit falling out of the SHR path |
| AEQB | wire-ANDed open-collector A=B | 1 = A equals B; valid in subtract mode (S=0110, Cn=0) only |
| V | sign-bit formulation | active-high signed overflow — most significant board only |
| PAR | '280 (option) | 1 = even parity over FB[7:0] |

**Carry sense is a per-board link, not a per-operation pin.** LK_BORROW
routes U3's SUB output into the COUT/HCOUT XORs for hosts whose C flag is a
borrow (8080). Addy and the 6502-ish machine leave it open: C = 1 = no
borrow, matching the ADDC/SUBC discipline, while SUB still reaches the V
rule.

**V needs no internal carry.** The textbook Cn+7 ⊕ Cn+8 is unreachable in a
4+4 split — the carry into bit 7 never leaves the package. The sign-bit form
is exact wherever V is defined:

```
Beff7 = B7 ⊕ SUB
V     = (A7 ⊕ F7) · (Beff7 ⊕ F7)
```

On logic operations V is garbage — don't latch it (the host's FLGC/FLGNZ
split, or its own flag mask, already expresses this).

---

## Width — one board or two

The board is a byte slice. 16 bits = two identical boards with identically
burned U20s:

One straight ribbon, H8 (low) → H11 (high), pin-for-pin:

| Ribbon pin | Low board (H8) | High board (H11) | Direction |
|------------|----------------|-------------------|-----------|
| 1 | GND | GND | reference |
| 2 | /CN8 (raw Cn+y, LS levels) | CNX — LK_CN in the SEAM position | up |
| 3 | /ZOUT ('688 active-low) | /ZCASC — LK_ZC in the SEAM position | up |
| 4 | AEQB | AEQB — the OC wire-AND, pull-ups in parallel | both |
| 5 | SRIN | FB0 | **down** — the shift chain |

FC3:0, CFLG, LEA, LEB, /FOE, /SOE parallel across both boards via the
host's multi-drop bus ribbons on H4/H5.

The seam carry is a **raw pin-level wire** — Cn+8 out and Cn in are both
active-low LS-side signals and agree directly, exactly like the inter-slice
hop inside a board. No conditioning crosses the seam. On the high board,
LK_CN disconnects U3's Cn output in favour of CNX; everything else about
the two boards, including the JEDEC, is identical.

Then: **SIGN, V, COUT** are read from the high board; **HCOUT** from the low
board (the bit-3 boundary); **ZERO** from the high board (the cascade has
done the AND); **SRIN/SOUT** chain through for 16-bit shifts (low SOUT is
the word's shift-out; high board's SRIN is the injected bit; low board's
SRIN takes high FB0, strapped at the H5 pins).

Each board's '182 gives lookahead within the board; the seam adds one
serial carry hop. For a pair chasing clock rate, both boards' group /G and
/P come out on H8 for a second-level '182 on the host or a small carry
card, restoring single-level lookahead across all four slices — a retrofit,
not a respin. At Addy's register-file-limited ceiling the one-wire seam is
expected to suffice (estimates only — see Timing).

Solo operation needs no links thrown: /ZCASC is pulled active on-board via
LK_ZC, LK_CN sits in the GAL position, and the cascade pins go unread.

---

## '181 operation usage by host

The union view behind the FC tables. Active-high column; arithmetic rows
implicitly *plus Cn*.

| M | S3:0 | Function | Addy v2 | 8080-ish | 6502-ish |
|---|------|----------|---------|----------|----------|
| 0 | 0000 | A | — | INR (Cn=1) | INC, INX, INY (Cn=1) |
| 0 | 0110 | A − B − 1 | SUB, SUBI (Cn=1); SUBC (Cn=C); CMP, CMPI (Cn=1) | SUB, SUI (Cn=1); SBB, SBI (Cn=¬CY); CMP, CPI (Cn=1) | SBC (Cn=C); CMP, CPX, CPY (Cn=1) |
| 0 | 1001 | A + B | ADD, ADDI, ADDIH, CMN (Cn=0); ADDC (Cn=C); **MOV, LDI, TST via BKILL** | ADD, ADI (Cn=0); ADC, ACI (Cn=CY); DAA correction pass (aux GAL on B) | ADC (Cn=C) |
| 0 | 1100 | A + A | — | RLC (Cn=A7), RAL (Cn=CY) | ASL (Cn=0), ROL (Cn=C) |
| 0 | 1111 | A − 1 | — | DCR (Cn=0) | DEC, DEX, DEY (Cn=0) |
| 1 | 0000 | /A | — | CMA | — |
| 1 | 0110 | A ⊕ B | XOR | XRA, XRI | EOR |
| 1 | 1011 | A · B | AND | ANA, ANI | AND, BIT |
| 1 | 1110 | A + B | OR | ORA, ORI | ORA |
| 1 | 1111 | A (pass) | — | RRC, RAR → pass A, take the SHR driver | LSR, ROR → SHR driver; LDA/TXA/TYA when routed through for NZ |

Ten codes cover all three machines; the remaining 22 are spare. A
Thumby/ARM-flavoured host would add pass-B (M=1 S=1010, MOV of a
post-shifter operand), /B (0101, MVN), and A·/B (0111, BIC) — its ISA is not
ratified, so those are noted rather than columned. All fit a fourth JEDEC
without touching the board.

---

## Per-host population matrix

| Fit | Addy v2 (×2 boards) | 8080-ish | 6502-ish | Bench |
|-----|--------------------|----------|----------|-------|
| U4–U6 '181 ×2 + '182 | ● | ● | ● | ● |
| U3 JEDEC | `ALU_ADDY` (both boards) | `ALU_8080` | `ALU_6502` | host JEDEC under test |
| U1, U2 latches | **'574** (edge — the '670 write-through fence; replaces the CPU board's 4× '574) | '573 transparent | '573 or strap-through | strap-through |
| U14 D-bus driver | — (F port → register file D) | ● | ● | — |
| U15 SHR driver | — | ● | ● | — |
| U12 V | ● (high board) | — | ● | — |
| U13 parity | — | ● | — | — |
| U16/U17 flag latch | — (flags live in the CPU's '74/'157 block) | ● or host-side | ● or host-side | — |
| U18 aux GAL | — | ● (DAA) | — | — |
| LK_BORROW | open | **fitted** | open | open |
| LK_CN | GAL (low) / SEAM (high) | GAL | GAL | GAL |

Addy pair: 23 ICs replacing the v2 board's integrated ALU block of roughly
12 (4× '181, '182, 2× HCT541 fence, 2× '688, 4× '574, V '86) — the
modularity premium is about eleven packages of headers, fences, and
per-board decode, paid for the usual reason: the ALU gets built, tested, and
*finished* on its own bench, once, for three machines. On the CPU side, GAL1
sheds the planned S3–S0/M outputs (emitting FC3:0 instead — one fewer
output, and far fewer product terms), and GAL3's ALU-decode role shrinks
toward vestigial: revisit the 3-GAL split when the Fetch & Control board is
next touched.

Note the v2 parts list says 4× HCT541 for the fence; one octal buffer per
byte lane is correct — 2 for 16 bits. Carried into this design as U7 per
board.

---

## Substitutions

| Position | Alternative | Pros | Cons |
|----------|-------------|------|------|
| U6 '182 | ripple ('04 sections between slices) | none remaining — TTLSim has a '182 model and stock is plentiful | ~2 extra carry hops; recorded only so it isn't re-proposed |
| U1/U2 '573 | '574 | edge-triggered — required where the operand source has a write-through hazard (Addy's '670) | needs a real clock edge from the host, not a level |
| U7 HCT541 | HCT245 (DIR strapped) | same job | control pins to strap; no gain — **don't**, and never with /OE grounded on a shared bus (bus-contention trap, see IO module U4 note) |
| U10 '688 | wire-AND A=B | one resistor | **not equivalent in this polarity convention** — asserts on all-ones, subtract-mode only; rejected as Z |
| U8/U9 HCT | HC | — | **fails** — these read LS outputs; VIH not met. The classic works-on-bench intermittent. Must be HCT |
| U13 '280 | 2× '86 tree | no '280 sourcing | two packages and a slower tree for the same flag |
| U3 GAL16V8 | GAL22V10 | more inputs/terms if a host outgrows 16V8 | bigger part for no current need; footprint is 20-pin — a 22V10 is a respin, so confirm the 16V8 budget per JEDEC before layout |
| 74LS181 | 74HC181 | drops the fence | effectively unobtainable; the fence architecture exists because LS is what's in the drawer |

---

## Hookup

1. Power first, alone. Current check — the '181s and the '182 (a Schottky
   part) are the board's draw; budget the fully populated case.
2. Clock contract only if U16/U17 or edge latches are fitted: GND, +5, CLK
   from the clock module's four-pin header. /RST is not used — the flag
   latch has no clear; a host wanting deterministic flags writes them early.
3. Operands to H2–H5, FC and CFLG to H4, conditions from H7. F from H6/H9
   **or** the D bus via H9/H7 — a register-file machine (Addy) uses F and
   leaves D unpopulated; a bus machine uses D and may leave F unread.
4. Pair join: one straight ribbon, low H8 → high H11, plus the host's
   multi-drop control ribbons on both boards' H4/H5. LK_CN and LK_ZC to
   SEAM (2-3) on the high board — the only shunts that differ between the
   boards of a pair. Read word-wide conditions from the boards stated
   above — reading SIGN or V from the low board of a pair is the class of
   error that yields plausible numbers with the wrong flag.
5. **Fails toward safe:** /FOE, /SOE, /AUXOE, /ZCASC idle high (inactive)
   via pull-ups, and BKILL is GAL-driven (active-high — the '574 /OE sense is why the
   polarity is high-asserted); a disconnected control ribbon yields a passive board driving
   nothing onto any shared net. The one unsafe state is U3's socket empty —
   S, M, and Cn float. Don't power the board without a burned GAL fitted.

---

## Self-test

The proof splits three ways: the raw '181 is proven **in simulation**, the
JEDEC is proven **off-board**, and the bench proves the **assembly of the
two**. On the bench the board runs standalone, no CPU: switches on A, B,
FC[3:0], and CFLG; LEDs on F and H7.

1. **TTLSim: the 32-row sweep.** With S, M, and Cn driven directly in the
   simulator (both Cn states on the arithmetic rows), sweep walking-ones,
   all-ones, alternating patterns, and the 0x00/0xFF boundaries against the
   '181 and '182 models. The captured table — not a datasheet
   transcription — ships as the module's function table, and every later
   stage checks against it.
2. **JEDEC pipeline.** BlinkyJED equations → fuse map → WinCUPL diff →
   Arduino exhaustive test (64 vectors against the published code table) →
   burn. No JEDEC touches the board unproven.
3. **Bench: walk the FC table.** Every code in the fitted JEDEC, checked
   against its published table and the stage-1 capture. A swapped S bit or
   crossed nibble shows immediately; probe U3's output pins directly on
   any disagreement — the GAL pins are the seam between "wrong burn" and
   "wrong wiring". Confirm HC-level swing (>4.0 V) at the F header — not
   the LS 2.7 V.
4. **Carry polarity — be slow and pedantic here.** ADD code: `0F + 01` →
   F=10. ADC code, CFLG=1: → F=11 (proves the Cn pin sense in the equation,
   the likeliest thing to be backwards). ADD `FF + 01` → COUT=1. SUB code:
   `05 − 03` → F=02, COUT=1 (no borrow); `00 − 01` → F=FF, COUT=0
   (borrow). With LK_BORROW fitted, repeat and confirm the flip on subtract
   codes and on `SUI 0` / `SUI 1` against A=0 — the vectors where two
   inversions cancel misleadingly. **Nothing downstream of COUT gets wired
   until this stage passes.**
5. **Conditions.** ZERO on 00; cascade: force /ZCASC inactive and confirm
   ZERO dies. SIGN on 80. HCOUT on `0F + 01`. AEQB via a borrow-subtract
   code with CFLG set so Cn=0 (SBB/SBC/SUBC class) and equal operands —
   then confirm it lies on a plain-subtract code (that's the point of the
   demonstration).
6. **BKILL** via a code that asserts it (MOV on `ALU_ADDY`). Any B pattern
   on the switches: F = A, COUT = 0. Proves the pulldown-zero and the MOV
   path. JEDECs without a B-kill code can't exercise this stage — it's
   burn-dependent, and that's fine.
7. **SHR driver.** F=81, SRIN=0 → D reads 40, SOUT=1. Strap SRIN to FB7 → C0.
8. **V.** `7F + 01` → V=1, SIGN=1. `FF + 01` → V=0. `80 − 01` on a
   subtract code → V=1.
9. **Flag latch.** Clock a condition in, change the inputs, confirm hold.
   FLGC alone must not disturb Z/N and vice versa.
10. **Pair.** Two boards joined, LK_CN to SEAM on the high board; 16-bit
    carry across the seam (`00FF + 0001`), 16-bit ZERO, SIGN from the high
    board, 16-bit right shift through the chained SRIN.

---

## Timing

**Estimates from catalogue models — replace with datasheet worst-case
before any host commits a clock rate.** The structure of the critical path:
operand latch → slice → carry → slice → fence → consumer setup, with the
control path (FC → U3 → S/M/Cn) settling in parallel from the earlier IR
edge — except **Cn from CFLG, which is in series**: CFLG → U3 → '181, so
the GAL delay lands inside the arithmetic setup on ADC/SBC-class codes.

| Path | Estimate |
|------|----------|
| '573 through | ~20 ns |
| U3 GAL16V8 (FC/CFLG → S, M, Cn) | ~10 ns |
| '181 operand → F | ~30 ns |
| '181 carry in → carry out | ~20 ns |
| '182 stage | ~12 ns |
| HCT541 fence | ~8 ns |
| '688 zero (after result) | ~25 ns |
| '280 parity (after result) | ~30 ns |

Structural observations that hold regardless of the numbers:

1. **Zero and parity stack on top of the whole ALU path** — they start when
   the result is final. If a host's flag setup is tight, look there first.
2. **The register file contract makes this module the timing owner** on
   Addy: D must be stable before CLK falls, and this board produces D. The
   clock-high phase is this board's budget — and it now includes U3's
   ~10 ns on the CFLG → Cn leg.
3. A 16-bit pair on the one-wire seam adds one serial carry hop per board
   boundary over a single-level second-stage '182. The /G, /P export exists
   so that gap can be closed later without a respin.

---

## Absolute ratings and habits

74HC inputs: no connector-driven input may float. The board pulls its own
option nets (/FOE, /SOE, /AUXOE, /ZCASC) inactive via pull-ups, and SRIN low (LSR-safe); **FC, CFLG,
SRIN, and the latch controls must be driven or strapped whenever the board
is powered**, and the board is never powered with U3's socket empty — a
floating S line produces results that look like a microcode bug for a long
time.

The F port drives normal HC fan-out and is not a bus driver; F onto a shared
bus goes through U14 where the host can arbitrate. When /AUXOE is low the aux
GAL owns the B port — one driver per net is the host's discipline, the board
does not arbitrate. U7, U8, U9 must be HCT (they read LS outputs); U14, U15,
U10 may be plain HC (they read U7). The '182 is Schottky — decouple it
properly. FC changing mid-cycle while carry propagates produces transient
garbage on F; harmless for edge-sampling hosts, known when scoping. /CN8
crosses the seam at LS levels by design — it lands only on the partner's LS
Cn input, never on an HC input. 0.1 µF per populated socket; bulk at the
power header.

---

## Open decisions

1. **Second-level '182 home for the Addy pair** — host board vs a small
   carry card — deferred until the pair is wired in TTLSim and the simulated
   seam delay is known. (The '182 sim model exists; the SUB-mode P/G
   behaviour — comparison encoding, not sums — is the thing to exercise.)
2. **GAL1/GAL3 split on the Addy Fetch & Control board.** With S/M/Cn decode
   moved into U3, GAL3's remit shrinks — possibly to nothing. Revisit the
   3-GAL partition before that board's PCB layout is finalised.
3. **Aux GAL socket** — whether it earns its board area on a module two of
   three hosts leave empty, or moves to an 8080-only daughter position.
4. **Header pinout ratification** — pending the TTLSim export, as with every
   board in the family.
5. **Flag latch spares** — thirteen unused flip-flops in U16/U17; pad field
   vs header.

---

## Deltas from the source documents

Recorded so the reconciliation isn't relitigated.

**From the 6502 Byte ALU draft (adopted nearly whole):** byte-slice
cascadable architecture, HCT541 fence, crossed-'541 SHR driver, '688 with
/ZCASC cascade, wire-AND A=B as a condition, header convention, bring-up
staging. **Superseded within this document:** its '153 carry mux and
CSEL/CIN_A/CIN_B interface — replaced by the function-code GAL, which
absorbs carry-source selection and carry-in polarity into per-host
equations. Its ripple-default carry — the '182 is fitted as standard now
that stock is confirmed plentiful.

**From the 8080 ALU draft:** operand latches with the read-modify-write
rationale (generalised: the same latch is Addy's '670 write-through fence),
SUB-conditioned borrow sense (demoted to the LK_BORROW link, with SUB now a
U3 output rather than a host wire), the aux GAL socket, parity
(re-implemented as one '280 instead of the '86 tree). **Rejected:** the
permute stage (taxes every operation with a mux delay; the SHR driver does
the job off the critical path), the 16-bit-board-populated-narrow form
factor (two identical byte boards mean one PCB, one BOM, and no dead area on
the 8-bit machines), the FIN/FSEL flag write-back mux (architecture;
host-side).

**From the Thumby ALU draft:** the sweep-captured function table as the
shipped datasheet, the verification plan's polarity-proof vectors.
**Rejected:** the inverting data boundary — six '240s, and its ratified
claim that the active-HIGH column applies verbatim under inversion is a
defect (the active-LOW column applies; XOR/XNOR, AND/OR, the constants, and
inc/dec all swap while ADD/SUB/pass-A stay identical, which is precisely the
failure a casual sweep misses). The wire-AND zero detect, the on-board NZCV
shadow/restore block, and the C-source mux go with it — the first is
polarity-dependent, the rest are architecture. Its GAL16V8-as-'182 and
ripple options are also dropped: '182 stock is plentiful and TTLSim now has
a '182 model, so neither the procurement nor the simulation argument
survives.

**Added in this revision:** the function-code GAL (U3) — 4-bit FC + CFLG
replaces the nine-line raw control interface; carry-in becomes a per-host
equation driving the Cn pin directly; SUB and BKILL become GAL outputs;
per-board identical JEDECs keep pair boards interchangeable, with LK_CN as
the single differing link; the raw /CN8→CNX seam replaces the conditioned
COUT→CIN_A cascade.
