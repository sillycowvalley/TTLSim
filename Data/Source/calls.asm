    NOP
    NOP
loop:
    CALL functionA
    NOP
    BRA loop
    NOP
functionA:
    CALL functionB
    NOP
    RET
    NOP
functionB:
    NOP
    RET