# Addy — Designator Map

Regenerated 2026-07-17 after IN switch bank added. Unique across the project, ordered by layer then position.

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
| U20 | 00 | MUX |
| U21 | 00 | HGLUE |
| U22 | 08 | GLUE |
| D1 | led | PWR |
| H2 | hdr-out-2 | Power |
| R1 | resistor |  |
| C1 | polarized-capacitor |  |
| U23 | 74 | MUXEN |
| U24 | 08 | HGATE |
| U25 | 00 | GATE |
| D2 | led | T0 |
| R2 | resistor |  |
| R3 | resistor |  |
| R4 | resistor |  |
| U26 | 00 |  |
| D3 | led | T1 |
| R5 | resistor |  |
| SW1 | button-4 | NEXT INSTR |
| R6 | resistor |  |
| D4 | led | HALT |
| U27 | 273 | SYNC |
| R7 | resistor |  |
| U28 | NE556 | TIMER |
| D5 | led | T2 |
| R8 | resistor |  |
| H3 | hdr-out-2 | RV1 EXT |
| SW2 | button-4 | HALT REQ |
| R9 | resistor | trim |
| R10 | resistor |  |
| D6 | led | CLK |
| R11 | resistor |  |
| D7 | led | T3 |
| R12 | resistor |  |
| R13 | resistor |  |
| U29 | 74 |  |
| C2 | capacitor |  |
| D8 | led | STEPRAW |
| R14 | resistor |  |
| U30 | 14 | INV |
| U31 | 02 |  |
| U32 | 161 | TCNT |
| R15 | resistor |  |
| SW3 | button-4 | Reset |
| D9 | led | RESET |
| R16 | resistor |  |
| U33 | DS1813 | POR |
| H4 | hdr-out-2 | CANIN |
| C3 | capacitor | PWR-ON STEP |
| R17 | resistor |  |
| SW4 | button-4 | STEP |
| R18 | resistor |  |
| D10 | led | STEP |
| U34 | 00 | LATCH |
| U35 | 08 | PANEL |
| J1 | jumper-3pin | CAN SEL |
| R19 | resistor |  |
| SW5 | button-4 | RUN 555 |
| R20 | resistor |  |
| D11 | led | 555 |
| U36 | 161 | DIV |
| R21 | resistor |  |
| S1 | switch |  |
| R22 | resistor |  |
| S2 | switch |  |
| R23 | resistor |  |
| SW6 | button-4 | RUN CAN |
| R24 | resistor |  |
| D12 | led | CAN |
| R25 | resistor |  |
| S3 | switch |  |
| U37 | 151 | TAP |
| SW7 | button-4 | Step |
| C4 | capacitor |  |

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
| H29 | hdr-out-8 |  |
| H30 | hdr-out-8 |  |
| S4 | switch | IN0 |
| S5 | switch | IN1 |
| RN1 | resnet-sip9 | PDIN |
| S6 | switch | IN2 |
| S7 | switch | IN3 |
| S8 | switch | IN4 |
| S9 | switch | IN5 |
| U59 | 541 | IN |
| S10 | switch | IN6 |
| S11 | switch | IN7 |
| U60 | 377 | OUT |
| D13 | led | OUT7 |
| R35 | resistor |  |
| D14 | led | OUT6 |
| R36 | resistor |  |
| D15 | led | OUT5 |
| R37 | resistor |  |
| D16 | led | OUT4 |
| R38 | resistor |  |
| D17 | led | OUT3 |
| R39 | resistor |  |
| D18 | led | OUT2 |
| R40 | resistor |  |
| D19 | led | OUT1 |
| R41 | resistor |  |
| D20 | led | OUT0 |
| R42 | resistor |  |

## Operands

| Designator | Part | Label |
|---|---|---|
| U61 | 574 | AH |
| RN2 | resnet-sip9 | PDA-HI |
| U62 | 541 | IMH |
| U63 | 541 | IML |
| U64 | 574 | AL |
| RN3 | resnet-sip9 | PDA-LO |
| U65 | 574 | BH |
| RN4 | resnet-sip9 | PDB-HI |
| U66 | 574 | BL |
| RN5 | resnet-sip9 | PDB-LO |
