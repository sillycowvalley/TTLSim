; addy_bringup.asm — the design doc's bring-up programs, stages 4 and 5.
;
; Assemble:  addyasm addy_bringup.asm
; Burn addy_bringup_lo.hex / _hi.hex into the two 28C256s, or load
; addy_bringup.hex into the TTLSim EEPROM pair.

; ---- stage 4: LED binary counter (runs forever) ------------------------
        ORG   0
start:  LDI   r0, 0          ; 5000
count:  OUT   r0             ; C000
        ADDI  r0, 1          ; 4001
        BRA   count          ; SUBI r7,3 -> 4F03  (relative: relocatable)

; ---- stage 5: countdown, then halt --------------------------------------
        ORG   0x10
        LDI   r0, 10         ; 500A
loop:   SUBI  r0, 1          ; 4801
        BRNZ  loop           ; SUBINZ r7,2 -> BF02
        HLT                  ; FFFF (same as erased EEPROM)
