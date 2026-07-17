# Addy — Designator Map

Renumbered 2026-07-17: unique across the project, ordered by layer then schematic position (top-to-bottom, left-to-right). SIM001 and other diagnostics that print designators now identify parts unambiguously.

## Registers

| Designator | Part | Label |
|---|---|---|
| U1 | 670 |  |
| U2 | 670 |  |
| U3 | 670 |  |
| U4 | 670 |  |
| U5 | 670 |  |
| U6 | 670 |  |
| U7 | 670 |  |
| U8 | 670 |  |
| U9 | 670 |  |
| U10 | 670 |  |
| U11 | 670 |  |
| U12 | 670 |  |
| H1 | hdr-out-3 |  |
| U13 | 670 |  |
| U14 | 670 |  |
| U15 | 670 |  |
| U16 | 670 |  |
| U17 | 139 | GW+GRA |
| U18 | 139 | GRB |
| U19 | 00 | GWEN |

## Clock

| Designator | Part | Label |
|---|---|---|
| D1 | led | T0 |
| R1 | resistor |  |
| R2 | resistor |  |
| R3 | resistor |  |
| D2 | led | T1 |
| R4 | resistor |  |
| SW1 | button-4 | NEXT INSTR |
| R5 | resistor |  |
| D3 | led | HALT |
| D4 | led | T2 |
| R6 | resistor |  |
| SW2 | button-4 | HALT REQ |
| R7 | resistor |  |
| D5 | led | CLK |
| U20 | 00 | MUX |
| D6 | led | T3 |
| R8 | resistor |  |
| U21 | 00 | HGLUE |
| U22 | 08 | GLUE |
| SW3 | button-4 | Reset |
| D7 | led | PWR |
| D8 | led | RESET |
| R9 | resistor |  |
| U23 | DS1813 | POR |
| H2 | hdr-out-2 | Power |
| R10 | resistor |  |
| C1 | polarized-capacitor |  |
| U24 | 74 | MUXEN |
| U25 | 08 | HGATE |
| C2 | capacitor | PWR-ON STEP |
| R11 | resistor |  |
| SW4 | button-4 | STEP |
| R12 | resistor |  |
| D9 | led | STEP |
| U26 | 00 | LATCH |
| U27 | 08 | PANEL |
| R13 | resistor |  |
| SW5 | button-4 | RUN 555 |
| R14 | resistor |  |
| D10 | led | 555 |
| U28 | 00 | GATE |
| R15 | resistor |  |
| SW6 | button-4 | RUN CAN |
| R16 | resistor |  |
| D11 | led | CAN |
| SW7 | button-4 | Step |
| C3 | capacitor |  |
| U29 | 00 |  |
| U30 | 273 | SYNC |
| R17 | resistor |  |
| U31 | NE556 | TIMER |
| H3 | hdr-out-2 | RV1 EXT |
| R18 | resistor | trim |
| R19 | resistor |  |
| R20 | resistor |  |
| U32 | 74 |  |
| C4 | capacitor |  |
| D12 | led | STEPRAW |
| R21 | resistor |  |
| U33 | 14 | INV |
| U34 | 02 |  |
| U35 | 161 | TCNT |
| R22 | resistor |  |
| H4 | hdr-out-2 | CANIN |
| J1 | jumper-3pin | CAN SEL |
| U36 | 161 | DIV |
| R23 | resistor |  |
| S1 | switch |  |
| R24 | resistor |  |
| S2 | switch |  |
| R25 | resistor |  |
| S3 | switch |  |
| U37 | 151 | TAP |

## Interfaces

| Designator | Part | Label |
|---|---|---|
| H5 | hdr-out-8 |  |
| H6 | hdr-out-8 | W0..7 |
| H7 | hdr-out-8 |  |
| H8 | hdr-out-8 | W8..15 |
| H9 | hdr-out-8 |  |
| H10 | hdr-out-8 | A0..7 |
| H11 | hdr-out-8 |  |
| H12 | hdr-out-8 | A8..15 |
| H13 | hdr-out-8 |  |
| H14 | hdr-out-8 | B0..7 |
| H15 | hdr-out-8 |  |
| H16 | hdr-out-8 | B8..15 |
| H17 | hdr-out-4 |  |
| H18 | hdr-out-4 | WADDR |
| H19 | hdr-out-4 |  |
| H20 | hdr-out-4 | AADDR |
| H21 | hdr-out-4 |  |
| H22 | hdr-out-4 | BADDR |
| H23 | hdr-out-8 |  |
| H24 | hdr-out-8 | Control |
| R26 | resistor |  |
| R27 | resistor |  |
| R28 | resistor |  |
| R29 | resistor |  |
| H25 | hdr-out-8 |  |
| H26 | hdr-out-8 | T Service |
| R30 | resistor |  |
| R31 | resistor |  |
| R32 | resistor |  |
| R33 | resistor |  |
| R34 | resistor |  |
| H27 | hdr-out-8 | TEST |
| H28 | hdr-out-2 | Power |

## Sequencer

| Designator | Part | Label |
|---|---|---|
| U38 | GAL22V10 | ENABLES |
| U39 | GAL22V10 | SEQUENCER |
| U40 | 32 | WOR |
| U41 | 32 | AOR |
| U42 | 157 | ASEL |

## ALU

| Designator | Part | Label |
|---|---|---|
| U43 | 688 | ZDHI |
| U44 | 688 | ZDLO |
| U45 | 74 | FLAGS |
| U46 | 157 | FMUX |
| U47 | 86 | XOR12 |
| U48 | 86 | XOR08 |
| U49 | 86 | XOR04 |
| U50 | 86 | XOR00 |
| U51 | 283 | ADD12 |
| U52 | 283 | ADD08 |
| U53 | 283 | ADD04 |
| U54 | 283 | ADD00 |

## Instructions

| Designator | Part | Label |
|---|---|---|
| U55 | 28C256 | ROM HI |
| U56 | 28C256 | ROM LO |
| U57 | 377 | IRH |
| U58 | 377 | IRL |

## IO

| Designator | Part | Label |
|---|---|---|
| U59 | 541 | IN |
| U60 | 377 | OUT |

## Operands

| Designator | Part | Label |
|---|---|---|
| U61 | 574 | AH |
| RN1 | resnet-sip9 | PDA-HI |
| U62 | 541 | IMH |
| U63 | 541 | IML |
| U64 | 574 | AL |
| RN2 | resnet-sip9 | PDA-LO |
| U65 | 574 | BH |
| RN3 | resnet-sip9 | PDB-HI |
| U66 | 574 | BL |
| RN4 | resnet-sip9 | PDB-LO |
