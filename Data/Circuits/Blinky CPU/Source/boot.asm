; Blinky-M stage-0 boot — the program currently in the BOOT EEPROM.
; Two NOPs, a JUMP over a HALT trap, and a loop back. Runs forever;
; any misexecution lands on a HALT (0xFF — also the erased-EEPROM fill)
; and lights the HALTED LED.
;
; Stage 0 resets PC to 0x0000 (the PC-hi 0xE0 reset-load mechanism is
; not built yet), so this assembles at ORG 0x0000, not the final 0xE000.

        ORG 0x0000

reset:
        NOP
        NOP
        JUMP skip       ; 3-byte absolute, hops the trap

trap:
        HALT            ; only reached if the JUMP misexecutes

skip:
        NOP
        JUMP reset      ; loop forever
