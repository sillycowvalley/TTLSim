; frame.asm — a subroutine that opens a frame and uses a local
; main pushes two args, calls sum3, which adds a constant via a local slot.
; Exercises: CALL/RET, ENTER, LOCAL! / LOCAL@, the return stack.

        ORG   0xE000

main:   PUSH  #10
        PUSH  #20
        CALL  addsave
        HALT

; addsave: ( a b -- a+b )  also stashes the result in local 0 as a demo
addsave:
        ENTER #1              ; one local slot
        ADD                   ; TOS = a + b
        DUP
        LOCAL! 0              ; local[0] = a + b
        LOCAL@ 0              ; push it back (demonstrates round trip)
        DROP
        RET                   ; frame + BP restored automatically
