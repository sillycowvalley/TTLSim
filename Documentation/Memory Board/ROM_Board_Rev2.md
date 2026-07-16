# ROM Board Rev 2 — Shadow-ROM Module

**Status: PROPOSED — NOT RATIFIED.** GAL equations are drafts; timing figures marked
"ballpark" must be pinned to datasheets before layout.

A self-contained, host-agnostic memory module: 64K × 8 of fast SRAM with a
configurable ROM window (4K/8K/16K/32K, any 4K base) shadowed from EEPROM at
power-on. Everything outside the window is plain RAM, zeroed at boot. After boot
the whole 64K is treated as RAM — the host **read** path is raw SRAM with zero
board logic in it; the write path adds one fast-GAL pass.

## Changes from Rev 1

- **Write protection dropped entirely.** No WP jumper; the window is ordinary
  RAM after boot. Speed won.
- **SRAM /CE grounded permanently** (CS2 to VCC). Reads gated by /OE, writes by
  /WE. Host /CE removed from the interface.
- **Host /OE wires directly to the SRAM.** No board logic in the read path.
- **/WE merge stays in the GAL, at the fastest grade:** COPYCTL becomes an
  **ATF16V8C-7** (7.5 ns worst-case). The AC08 alternative was considered and
  dropped — same speed, one more chip, logic outside the PLD.
- **Smaller EEPROMs supported:** 28C64 / 28C128 / 28C256 drop into the same
  28-pin socket. Constraint: EEPROM ≥ window size.
- **Clock is 8 MHz**, so common 150 ns EEPROM grades work. 10 MHz is permitted
  only with a -70 EEPROM (see §7).
- 22V10 simulation status corrected: **fully supported in TTLSim today**.

---

## 1. Parts

| Part | Role |
|---|---|
| 28C256 / 28C128 / 28C64 (150 ns ok) | ROM image, always assembled at offset 0; 28-pin socket, drop-in family |
| W24512 | 64K × 8 SRAM, 15 ns — /CE grounded, CS2 tied to VCC |
| DS1813 | Reset supervisor — stable start + pushbutton/host re-shadow |
| ATF16V8C-7 ("COPYCTL") | Registered copy controller + the /WE merge (7.5 ns host write path) |
| ATF22V10 ("DECODE") | Combinational region decode, address masking, EOE/ZOE steering, READY//WAIT. Not in the host path; stock speed grade |
| 4 × 74HC163 | 16-bit copy address counter |
| 2 × 74HC541 | Counter → address bus, enabled during copy only |
| 1 × 74HC541 | Zero driver — inputs grounded, drives 0x00 onto the data bus outside the region |
| 8 MHz can oscillator | Copy clock |
| SIP-9 pull-up network | 6 DIP-switch pulls + host /WE and /OE pulls (8 of 8 used) |
| 6-position DIP switch | 2 × SIZE, 4 × BASE |
| 2 × LED + resistors | /WAIT (lit during copy) and READY (lit after boot) |

The host /WE and /OE pull-ups keep COPYCTL inputs and the SRAM /OE defined when
no host is connected (bench bring-up) — the board boots and holds a zeroed,
shadowed, readable memory standalone.

---

## 2. Configuration

DIP switch closed (ON) pulls the GAL input LOW; open = pulled HIGH.
Convention: **switch ON = logic 0**; the equations account for polarity.

### Size — SIZE1, SIZE0

| SIZE1 | SIZE0 | ROM window |
|---|---|---|
| 0 | 0 | 4K |
| 0 | 1 | 8K |
| 1 | 0 | 16K |
| 1 | 1 | 32K |

### Base — BASE3..BASE0 = A15..A12 of the window base (any 4K boundary)

Base bits below the size boundary are **ignored by the decode logic**, so a
misaligned base auto-aligns instead of silently misbehaving.

### Worked examples

| Host | Setting | Result |
|---|---|---|
| Z80 | BASE = 0x0, SIZE = 8K | ROM at 0x0000–0x1FFF: RST and mode-1 vectors covered; 56K RAM above |
| 65C02 | BASE = 0xF, SIZE = 4K | ROM at 0xF000–0xFFFF: FFFA–FFFF vectors covered; 60K RAM below |

One board serves both — switch settings only.

### EEPROM choice

Any 28-pin 28Cxx ≥ the window size: 28C64 for 4K/8K windows, 28C128 adds 16K,
28C256 covers all four. The address masking (§4.3) forces EA14/EA13/EA12 low for
small windows, so a smaller part never sees a driven address line it lacks; the
GAL driving the smaller parts' pin-1/pin-26 NC or RDY pins is harmless (the part
is never written in-circuit, so RDY stays released — prefer the 28C64B, where
pin 1 is a true NC). The 24-pin 28C16 is **not** supported — different pinout,
would need a second socket.

### EEPROM image convention

The image is always assembled at EEPROM offset 0. BASE affects only where the
window sits in host address space. The first *size* bytes are copied.

---

## 3. Host interface

Four 8-pin headers:

| Header | Pins |
|---|---|
| ADDR-L | A0–A7 |
| ADDR-H | A8–A15 |
| DATA | D0–D7 |
| CTL | /OE, /WE, READY, /WAIT, /RST, VCC, GND, (reserved) |

/CE is gone — the SRAM is always selected. The freed CTL pin is reserved.

Contract (polite host):

- The host must not drive ADDR, DATA, or assert /WE//OE actively low while
  /WAIT is asserted. Sharing /RST makes this nearly automatic — Z80 and 65C02
  both hold control lines inactive-high in reset, and the pull-ups cover the
  unconnected case.
- /WAIT maps onto Z80 /WAIT; READY maps onto 65C02 RDY. Boot stalls cost zero
  glue on either CPU.
- /RST is bidirectional in spirit: the DS1813 RST pin doubles as a debounced
  input, so a pushbutton or the host pulling it low forces a full re-shadow.

---

## 4. Theory of operation

### 4.1 Reset

DS1813 asserts /RST low ≥ 150 ms after power (or on button/host pull). During
/RST low:

- Counter /CLR is wired directly to /RST — synchronous clear, oscillator
  free-running, clears within one clock.
- COPYCTL registers (state, DONE, DRIVE, WECOPY) are forced to reset values by
  gating every D-term with RST.
- /WAIT asserted, READY low.

Copy starts the first clock after /RST releases.

### 4.2 Copy pass — full 64K, three clocks per byte

The counter sweeps the entire 64K linearly. Inside the ROM window the EEPROM
drives the data bus; outside it, the zero-'541 drives 0x00. Every location is
written, so the SRAM powers up fully deterministic — RAM zeroed, window
shadowed — and hardware matches simulation exactly (real SRAM powers up random;
the simulator can't model random; zeroing removes the divergence).

Per-byte cycle, state S1S0, one state per 8 MHz clock (125 ns):

| State | Name | Bus drivers | SRAM /WE | Notes |
|---|---|---|---|---|
| 00 | SETTLE | **none** (dead time) | high | Address changed at entry; settles a full phase |
| 01 | WRITE | EEPROM /OE or zero-'541 (by region) | **low** | Data valid well before /WE rises |
| 10 | HOLD | still driving | high | Data/address held past /WE rise; counter increments at exit |

The dead SETTLE phase gives guaranteed break-before-make between the EEPROM and
the zero driver at every region boundary — the boundary case isn't special.

Three clocks/byte (not two) kills the /WE-rise vs address-change race: /WE
(registered) rises at the WRITE→HOLD edge; the counter increments a full phase
later at HOLD→SETTLE. Address hold after /WE-rise is 125 ns by construction
instead of a GAL-tCO-vs-'163-min-tCO race.

Counter enable (CEP) is asserted during HOLD only, so the increment lands on the
HOLD→SETTLE edge. DONE registers at the end of HOLD of byte 0xFFFF (top '163
RCO high while count = 0xFFFF) and latches until the next /RST.

**Boot time: 65,536 × 375 ns = 24.6 ms at 8 MHz.**

### 4.3 EEPROM addressing during copy

The counter drives the shared address bus, so inside a window based at e.g.
0x8000 the bus carries 0x8000+offset — but the EEPROM needs plain offset. A11–A0
go straight from the bus (4K granularity: they always pass). A14–A12 come from
DECODE, masked to the size:

- EA14 = A14 only when size = 32K
- EA13 = A13 when size ≥ 16K
- EA12 = A12 when size ≥ 8K
- otherwise forced low

Because the base is size-aligned (enforced by the ignored-bits rule), the masked
bus address equals the EEPROM offset exactly — no adder.

EEPROM /CE is wired to /WAIT: low for the whole copy (access is address-limited
tACC, not the /CE-access penalty), high forever after — the EEPROM is parked and
never touches the bus again.

### 4.4 Host phase (after DONE)

- **Read: raw SRAM.** Address and /OE wire directly — tAA 15 ns, tOE ~8 ns.
  Zero board logic.
- **Write: one fast-GAL pass.** Two writers (copy engine, host) share the SRAM
  /WE pin, and hosts actively drive /WE high in reset (not tri-state), so a
  merge is structural. It lives in COPYCTL at the -7 grade: 7.5 ns worst-case.

---

## 5. DECODE — ATF22V10, combinational

### Inputs (12)

A15, A14, A13, A12, SIZE1, SIZE0, BASE3, BASE2, BASE1, BASE0, DONE, DRIVE

### Outputs (8 of 10 macrocells)

| Output | Function |
|---|---|
| /INREGION | Region match, active low (see below) |
| EA14, EA13, EA12 | Masked EEPROM address high bits |
| READY | = DONE (also 65C02 RDY) |
| /WAIT | = !DONE (also EEPROM /CE and the address-'541 enables, directly) |
| EOE | EEPROM /OE: active when DRIVE & in-region |
| ZOE | Zero-'541 /OE: active when DRIVE & out-of-region |

### Draft equations (positive-logic sketch; switch polarity folded in later)

Region compare, per size: 4K compares A15–A12; 8K compares A15–A13; 16K compares
A15–A14; 32K compares A15 only. Computed **active-low** to fit the product-term
budget (the active-high product of bypassed XNORs explodes; the complement is a
clean sum):

```
!INREGION = (A15 ^ B3)
          # (A14 ^ B2) & !(S1&S0)
          # (A13 ^ B1) & !S1
          # (A12 ^ B0) & !S1 & !S0
```

Term count: 2 + 4 + 2 + 2 = 10 → place on a ≥10-term macrocell (22V10 rows are
8/10/12/14/16/16/14/12/10/8). This is why DECODE is a 22V10: this compare plus
the masking plus the steering does not fit two 16V8s.

```
EA14 =  A14 &  S1 & S0
EA13 =  A13 &  S1
EA12 =  A12 & (S1 # S0)

EOE  = DRIVE & INREGION          ; INREGION = feedback of !(/INREGION)
ZOE  = DRIVE & !INREGION
READY =  DONE
/WAIT = !DONE
```

DRIVE is gated by !DONE inside COPYCTL, so EOE/ZOE self-park after boot.

---

## 6. COPYCTL — ATF16V8C-7, registered, CLK = 8 MHz

### Inputs (3 of 8, pin 1 = CLK, pin 11 /OE = GND)

/RST, RCO (top '163), host /WE

### Outputs (6 of 8 macrocells)

| Output | Type | Function |
|---|---|---|
| S1, S0 | reg | Byte-cycle state: 00 SETTLE → 01 WRITE → 10 HOLD → 00 |
| DONE | reg | Latched copy-complete; cleared only by /RST |
| DRIVE | reg | Bus-driver window (WRITE + HOLD), exported to DECODE |
| /SRAMWE | reg + comb | The merge: copy pulse during copy; host /WE forwarded after DONE |
| CNTEN | comb | Counter CEP: asserted in HOLD while !DONE |

Rev 1's /SRAMCE and /SRAMOE outputs and the hostCE/hostOE/WP//INREGION inputs
are gone — the audit is roomy now (2 spare macrocells, 5 spare inputs).

### Draft equations (positive logic; active-low pins inverted at the pin)

```
; state machine, held at 00 during reset and after DONE
S0.D  := RST & !DONE & !S1 & !S0          ; SETTLE -> WRITE
S1.D  := RST & !DONE &  S0                ; WRITE  -> HOLD

DRIVE.D := RST & !DONE & (!S1 & !S0 # S0) ; on entering WRITE, off entering SETTLE

DONE.D  := RST & (DONE # (RCO & S1))      ; latch at end of HOLD of byte FFFF

; copy write pulse: registered — set entering WRITE, cleared entering HOLD
WECOPY.D := RST & !DONE & !S1 & !S0

SRAMWE = WECOPY                            ; during copy
       # DONE & HOSTWE                     ; host phase (DONE gate: defence in depth)
```

(Exact CUPL/BlinkyJED source, physical pinout, and polarity handling to be
ratified against WinCUPL, per the ALU Rev 2 procedure.)

---

## 7. Timing budget (worst-case ballpark — pin to datasheets before ratifying)

### Copy, 8 MHz → 125 ns per state, 375 ns per byte, 150 ns EEPROM

| Path | Budget | Used (ballpark) | Margin |
|---|---|---|---|
| Address → EEPROM data: '163 tCO 35 + '541 25 + tACC 150 | 250 ns (SETTLE + WRITE) | ~210 | ~40 ns |
| /OE → data before /WE rise: GAL tCO 15 + DECODE 15 + tOE ~70 (150 ns grade) | 125 ns (WRITE) | ~100 | ~25 ns |
| Zero-'541 data valid: GAL 15 + DECODE 15 + '541 enable 25 | 125 ns | ~55 | ~70 ns |
| /WE pulse width (W24512 tWP ≈ 12 ns min) | 125 ns | — | huge |
| Address hold after /WE rise | 125 ns (HOLD) by construction | — | huge |

10 MHz remains legal **only** with a -70 EEPROM (Rev 1 budget); the board is
specified at 8 MHz so any 28Cxx grade drops in. Boot 24.6 ms vs 19.7 ms — nobody
will notice.

### Host, after DONE

| Path | Delay |
|---|---|
| Read: address → data | 15 ns (SRAM tAA, nothing else) |
| Read: /OE → data | ~8 ns |
| Write: /WE through COPYCTL (-7) | 7.5 ns + SRAM write timing |

### Worked example: W65C02S at 14 MHz (its fastest rated speed)

tcyc = 70 ns; address valid ~30 ns in (tADS), data setup ~10 ns → ~30 ns left
for memory: read path 15 ns ✓. Write: /WE derived from PHI2·RWB, ~35 ns
PHI2-high − 7.5 ns merge ≈ 27 ns pulse vs 12 ns minimum ✓. The board sustains
the fastest rated '02 with margin; the first wall beyond that is the CPU's own
address setup, not this memory. (W65C02S figures are ballpark — pin before
committing a host design.)

---

## 8. LEDs

- /WAIT LED: lit during copy (~25 ms blip at power-on; steady if the board is
  held in reset or the copy engine is wedged — the useful diagnostic case).
- READY LED: lit steady after boot — the at-a-glance "board is alive" indicator.

---

## 9. TTLSim verification plan

Every part on the board is simulated today, including the ATF22V10.

1. EEPROM Propagation Delay: leave at the 150 ns default (the 8 MHz budget is
   built for it), or override to 70 when simulating the 10 MHz variant.
2. COPYCTL delay override: 8 ns (7.5 rounded up — conservative direction).
   Whether the override accepts fractional ns is unverified; integer 8 is safe
   either way.
3. Load a recognisable test image (e.g. address-echo pattern) into the EEPROM.
4. Sweep the config matrix: 4 sizes × bases including 0x0 (Z80 case), 0xF
   (65C02 case), and a deliberately misaligned base to confirm auto-align.
5. Per configuration, after /WAIT releases: window contents equal the image;
   every out-of-window cell reads 0x00; host writes land everywhere (no WP in
   Rev 2).
6. Repeat step 5 with a 28C64 and a 28C128 model against ≤8K and ≤16K windows.
7. Confirm no bus contention at region boundaries during copy (SETTLE dead
   phase) and that the board boots standalone with no host attached (pull-ups
   hold /WE and /OE).
8. Pulse /RST mid-operation after dirtying the RAM; confirm a full clean
   re-shadow.
9. Measure /WAIT release time against the 24.6 ms prediction.

---

## 10. Open items

- Ratify both GALs: BlinkyJED vs WinCUPL fuse-for-fuse, per the ALU Rev 2
  procedure. Pin assignments above are logical, not physical, until then.
- Pin §7 timing to actual datasheet maxima: '163, '541, ATF16V8C-7, ATF22V10,
  the chosen EEPROM grade, W65C02S if that host proceeds.
- Confirm whether the TTLSim per-device delay override accepts fractional ns
  (affects modelling the -7 GAL; 8 ns integer is the safe fallback).
- Decide the physical DIP-switch orientation/legend so ON = 0 reads naturally
  on the silkscreen.
- Decide what, if anything, the reserved CTL pin should carry (candidate: the
  8 MHz clock, for hosts that want a synchronous relationship with the board).
