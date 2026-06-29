    NOP
    NOP
loop:
    CALL functionA
    BRA loop
functionA:
    CALL functionB
    RET
functionB:
    RET