# Code & IR Module — Mega Test Rig Wiring

Fitment: 74HC377 in U3/U4. Outputs always drive — every IR pin on the Mega is an input, never an output.

## Before power

**Remove the H6 shunt.** The Mega drives A15 (D37 → H2.8); with the shunt fitted that is a hard short to ground.

## Connections — four 8-way ribbon cables

The Mega's end header is 2×18: even digital pins run along one row, odd pins along the other. Each ribbon therefore lands on 8 physically consecutive pins of a single row — no conductor crossing. See `MegaEdgeConnector_Wiring.svg`.

| Ribbon | Mega pins (in conductor order) | Module | Signals | Direction |
|---|---|---|---|---|
| 1 (even row) | D22, 24, 26, 28, 30, 32, 34, 36 | H1.1–H1.8 | A0–A7 | Mega → module |
| 2 (odd row) | D23, 25, 27, 29, 31, 33, 35, 37 | H2.1–H2.8 | A8–A15 | Mega → module |
| 3 (even row) | D38, 40, 42, 44, 46, 48, 50, 52 | H3.1–H3.8 | IR0–IR7 | module → Mega |
| 4 (odd row) | D39, 41, 43, 45, 47, 49, 51, 53 | H4.1–H4.8 | IR8–IR15 | module → Mega |

Controls and power, discrete wires:

| Mega | Module | Signal |
|---|---|---|
| D2 | H5.2 | CP (register clock) |
| D3 | H5.3 | /EN (sync load enable) |
| GND (end-header corner pin) | H5.1 | Ground |
| 5V (end-header corner pin) | H5.4 | VCC |

The 5V/GND pairs at both ends of the same 2×18 header keep everything on one connector.

Power the module from the Mega's 5 V pin; the Mega itself on USB. Four HC/EEPROM loads are well within budget.

## EEPROMs

Burn `U1_high.hex` into U1 (high bytes, IR[15:8]) and `U2_low.hex` into U2 (low bytes, IR[7:0]). Both files regenerate from `MakeTestHex.cs` if the pattern ever changes — keep it in lockstep with `ExpectedWord()` in the sketch.

Pattern:

| Address | Word |
|---|---|
| 0–15 | walking one, `1 << addr` |
| 16–31 | walking zero, `~(1 << (addr−16))` |
| 32–35 | 0x0000, 0xFFFF, 0xAA55, 0x55AA |
| everything else | `(addr * 0x9E37) ^ 0x55AA` (16-bit) |

Walking patterns catch stuck/shorted IR data lines; the hash makes any address-line fault (open, short, swap) show as a mismatch somewhere in the sweep.

## What the sketch tests

1. **Address line walk** — each of A0–A14 individually reaches the ROMs.
2. **/EN gating** — a CP edge with /EN high must not load. This is the '377 property that makes it an IR.
3. **Registered hold** — address change with no clock must not change IR. A '373 fitted by mistake (transparent) fails here.
4. **A15 mirror** — 0x8000+a reads the word at a (A15 undecoded, shunt removed).
5. **Full 32K sweep** — every word compared against the burn image.

Serial Monitor at 115200. Tests run at power-up; send `r` to rerun. First 32 failures print in full with a XOR diff of the bad bits, then it summarises.
