; arith.asm — arithmetic and stack shuffling
; Computes (7 + 5) - 2 = 10, stores it, then leaves 10 and its double on the
; stack. Exercises: PUSH, ADD, SUB, DUP, STORE, named ALU ops.

        ORG   0xE000

RESULT  EQU   0x0010          ; a global in page 0

start:  PUSH  #7
        PUSH  #5
        ADD                   ; TOS = 12
        PUSH  #2
        SUB                   ; TOS = 12 - 2 = 10
        DUP                   ; ( 10 10 )
        STORE RESULT          ; mem[0x0010] = 10, pops one
        DUP                   ; ( 10 10 )
        ADD                   ; TOS = 20
        HALT
