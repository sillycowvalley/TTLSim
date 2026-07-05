; aluzoo.asm — the ALU quadrant reached by name and by general form
; The named ops and their ALU #n / ALU m,cn,ssss equivalents must assemble to
; the same opcode. Exercises: named ALU ops, ALU #n, ALU M,CN,Ssss, DB checks.

        ORG   0xE000

        PUSH  #0x0F
        PUSH  #0x33

        AND                   ; named  -> 0x6B
        ALU   #0x2B           ; same '181 code (M=1,CN=0,S=1011) -> 0x6B
        ALU   1, 0, 0b1011    ; same, as fields               -> 0x6B

        OR                    ; named  -> 0x6E
        ALU   1, 0, 0b1110    ; fields -> 0x6E

        SUB                   ; named  -> 0x56
        ALU   0, 1, 0b0110    ; fields -> 0x56

        NOT                   ; named  -> 0x60
        ALU   #0x20           ; M=1,CN=0,S=0000 -> 0x60

        HALT

; The bytes above should show the paired opcodes matching in the .lst.
        DB    "ALU zoo", 0
