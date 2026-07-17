; addy_selftest.asm — full ISA self-test (all 21 opcodes, both branch
; directions, both conditional polarities, flags, 16-bit zero detect,
; computed jumps, PC+1 semantics, IN echo).
;
; Observables:
;   PASS -> OUT = 0xAA (alternating LEDs), HALT lit.
;   FAIL -> OUT = last good checkpoint number, HALT lit.
;           (checkpoint N on the LEDs means stage N+1 is the one that failed)
;
; Assumes the IN buffer straps read 0xA5. Runtime ~230 clocks
; (~15 s at 16 Hz can, ~75 s at 3 Hz panel).

        ORG   0
        LDI   r6, 0          ; r6 = checkpoint counter
        OUT   r6             ; LEDs: 00

; ---- stage 1: Z set by LDI 0, cleared by LDI 1 --------------------------
        LDI   r0, 0          ; Z=1
        JNZ   fail
        LDI   r0, 1          ; Z=0
        JZ    fail
        ADDI  r6, 1
        OUT   r6             ; 01

; ---- stage 2: three-register ADD and SUB --------------------------------
        LDI   r1, 2
        LDI   r2, 3
        ADD   r3, r1, r2     ; r3 = 5, inputs preserved
        CMPI  r3, 5
        JNZ   fail
        SUB   r3, r3, r2     ; r3 = 2
        CMPI  r3, 2
        JNZ   fail
        ADDI  r6, 1
        OUT   r6             ; 02

; ---- stage 3: CMP and CMN (flags only, no writeback) --------------------
        CMP   r1, r1         ; equal -> Z
        JNZ   fail
        CMP   r1, r2         ; 2 vs 3 -> not Z
        JZ    fail
        LDI   r0, 0
        CMN   r0, r0         ; 0 + 0 -> Z
        JNZ   fail
        ADDI  r6, 1
        OUT   r6             ; 03

; ---- stage 4: MOV and the two-operand ADD form --------------------------
        MOV   r4, r3         ; r4 = 2
        CMPI  r4, 2
        JNZ   fail
        ADD   r4, r1         ; rd,rt form: r4 = r4 + r1 = 4
        CMPI  r4, 4
        JNZ   fail
        ADDI  r6, 1
        OUT   r6             ; 04

; ---- stage 5: conditional moves, both polarities, taken and not ---------
        LDI   r5, 0x11
        CMP   r1, r1         ; Z=1
        MOVNZ r5, r2         ; must NOT write
        CMPI  r5, 0x11
        JNZ   fail           ; (also leaves Z=1)
        MOVZ  r5, r3         ; Z=1 -> writes r5 = 2
        CMPI  r5, 2
        JNZ   fail
        CMP   r1, r2         ; Z=0
        MOVZ  r5, r1         ; must NOT write
        CMPI  r5, 2
        JNZ   fail
        ADDI  r6, 1
        OUT   r6             ; 05

; ---- stage 6: ADDIH, 16-bit values, high-byte zero detect ---------------
        LDI   r1, 0x34
        ADDIH r1, 0x12       ; r1 = 0x1234, not Z
        JZ    fail
        LDI   r2, 0x34
        SUB   r3, r1, r2     ; r3 = 0x1200 — Z must stay clear (ZDHI proof)
        JZ    fail
        ADDIH r3, 0xEE       ; 0x1200 + 0xEE00 = 0x0000 (16-bit wrap) -> Z
        JNZ   fail
        ADDI  r6, 1
        OUT   r6             ; 06

; ---- stage 7: countdown — backward BRNZ (SUBINZ), then TST --------------
        LDI   r0, 3
cdown:  SUBI  r0, 1
        BRNZ  cdown          ; taken twice, falls through on zero
        TST   r0             ; flags <- r0 = 0 -> Z
        JNZ   fail
        ADDI  r6, 1
        OUT   r6             ; 07

; ---- stage 8: forward BRZ (ADDIZ) and forward BRNZ (ADDINZ) -------------
        CMP   r1, r1         ; Z
        BRZ   fwd1
        JMP   fail
fwd1:   CMP   r1, r2         ; not Z
        BRNZ  fwd2
        JMP   fail
fwd2:   ADDI  r6, 1
        OUT   r6             ; 08

; ---- stage 9: backward BRZ (SUBIZ) --------------------------------------
        JMP   bztest
bzland: JMP   bzdone         ; backward-branch landing pad
bztest: CMP   r1, r1         ; Z
        BRZ   bzland         ; backward -> SUBIZ encoding
        JMP   fail
bzdone: ADDI  r6, 1
        OUT   r6             ; 09

; ---- stage 10: computed jump via JR (MOV r7, rs) ------------------------
        LDI   r1, jrok
        JR    r1
        JMP   fail
jrok:   ADDI  r6, 1
        OUT   r6             ; 0A

; ---- stage 11: r7 reads as PC+1 (write-through semantics) ---------------
        LDI   r2, 0
        ADD   r4, r7, r2     ; r4 <- PC+1 = address of 'here'
here:   LDI   r5, here
        CMP   r4, r5
        JNZ   fail
        ADDI  r6, 1
        OUT   r6             ; 0B

; ---- stage 12: IN echo (strap pattern) ----------------------------------
        IN    r1             ; 0x00A5 from the straps
        CMPI  r1, 0xA5
        JNZ   fail
        ADDI  r6, 1
        OUT   r6             ; 0C

; ---- pass ----------------------------------------------------------------
        LDI   r0, 0xAA
        OUT   r0
        HLT                  ; PASS: 0xAA + HALT

fail:   HLT                  ; FAIL: LEDs hold the last good checkpoint
