# Teensy 4.1 Harness Board — PCB Layout Checklist

Tailored to the final schematic (Teensy 4.1, 5× HCT245 + 5× LVC245 + HCT04,
100Ω DIP-16 terminations, per-port pull jumpers, individual port-E headers).
Work top to bottom; the ordering reflects what matters most for this board.

## 1. Stackup and ground

- [ ] Solid, unbroken ground plane under the entire board. 2-layer with a
  
      committed ground pour is the minimum; 4-layer SIG/GND/PWR/SIG preferred
      at prototype cost.
- [ ] No signal traces that slot the ground plane; check the plane for splits
  
      after routing, not just before.
- [ ] Ground vias generously wherever a signal changes layers.

## 2. Placement

- [ ] Per-port flow in a straight line: Teensy → LVC/HCT pair (face-to-face)
  
      → pull network + select jumper → 100Ω network → header. No port
      interleaving.
- [ ] The shared 5V-side bus node of each port (HCT-B, LVC-B, pull, RN input)
  
      kept physically tiny.
- [ ] LVC chips grouped on the Teensy side (3V3 domain); HCT chips toward the
  
      headers (5V domain). U2 (HCT04) central to the five /OE runs it feeds.
- [ ] Teensy on machined-pin socket strips, removable.
- [ ] Buttons SW0/SW1 and jumpers J6/J7 reachable with the ribbon connected.

## 3. Power and decoupling

- [ ] Each 100nF within 2–3 mm of its chip's VCC pin, short fat traces, own
  
      via pair to the planes. C3–C8 = 5V chips, C12–C16 = LVC chips on 3V3.
- [ ] Bulk: C1/C2 at the 5V entry (Teensy VIN area), C11 at the Teensy 3V3
  
      pins.
- [ ] 5V and 3V3 distribution ≥ 0.5 mm trace (or pour); the 3V3 rail comes
  
      only from the Teensy's regulator — no external source.
- [ ] LED R11/D1 placed where visible with everything plugged in.

## 4. Terminations (RN5–RN9)

- [ ] Placed tight against the '245 B-side pins — driver end, headers
  
      downstream. Same rule as the old adapter's 33Ω parts.
- [ ] **Footprint pinout verified against the vendor datasheet of the actual
  
      isolated 8-element DIP-16 packs bought** (element pairing 1↔16 … 8↔9
      assumed; confirm before ordering). Sockets on RN5–RN9 optional but
      cheap diagnosis if edge behaviour ever needs a value swap.

## 5. Port E — the clock port

- [ ] Each 2-pin header's GND pin directly beside its signal pin, with its
  
      own via to the plane at the connector.
- [ ] The eight port-E routes spread apart, not bundled; keep them clear of
  
      the A–D bus corridors.
- [ ] PE0 (clock, Teensy LED pin) given the cleanest, shortest run.

## 6. Routing

- [ ] Signals 0.2 mm+ anywhere; no length matching, no impedance control —
  
      on-board skew is irrelevant at these edge rates.
- [ ] Route each 8-bit port as a group in bit order; avoids crossovers and
  
      makes visual inspection against the netlist trivial.
- [ ] /OE and DIR0/DIR1 runs kept short; they gate everything.

## 7. Connectors and silkscreen

- [ ] Pin 1 marked on every header; H1–H4 pin 1 = bit 0 (white), pin 8 =
  
      bit 7 (red). Print the colour convention beside each 8-pin header.
- [ ] Jumper truth tables printed at the part: J1–J4/J11 "→3 = pull-up,
  
      →1 = pull-down"; J8/J12/J15 "fitted = drive, open = read";
      J6/J7 "1-2 = button, 2-3 = PE6/PE7".
- [ ] J5 rule printed: "OUT when DUT is externally powered."
- [ ] H11/H12 marked +5V OUT, H13/H16 marked GND.
- [ ] Board name, revision, date on the silk.

## 8. Mechanical

- [ ] Mounting holes at the corners, tied to ground.
- [ ] Ground loop or test point near port E for a scope probe; test points
  
      (or labelled vias) on OEA–OEE, DIR0/DIR1, 5V, 3V3.
- [ ] USB connector clearance for the Teensy with the socket height included.

## 9. Before sending to fab

- [ ] Netlist-driven layout only — no hand-entered connections.
- [ ] ERC/DRC clean; then eyeball the ratsnest per port in bit order.
- [ ] Every footprint checked against the physical part on hand: '245s,
  
      HCT04, DIP-16 packs, SIP-9 packs, button-4, jumpers, Teensy 4.1 DIP-48
      outline.
- [ ] Print the layout 1:1 on paper and place the actual parts on it.
