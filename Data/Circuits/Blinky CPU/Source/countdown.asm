; countdown.asm — a loop with a conditional branch and port output
; Writes 5,4,3,2,1 to port 0, then halts. Exercises: labels, forward and
; backward references, CMP, BEQ/BNE, OUT #port, SUB, DUP.

        ORG   0xE000

PORT0   EQU   0x00

start:  PUSH  #5              ; counter on TOS

loop:   DUP
        OUT   #PORT0          ; emit the counter (pops the copy)
        PUSH  #1
        SUB                   ; counter = counter - 1
        DUP
        PUSH  #0
        CMP                   ; sets Z when counter == 0
        BNE   loop            ; keep going while non-zero

        DROP                  ; discard the 0
        HALT
