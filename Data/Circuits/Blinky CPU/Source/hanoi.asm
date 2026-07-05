; hanoi.asm — Towers of Hanoi on Blinky-M (rev 14)
;
; Recursive solver. Each level owns a 4-byte page-0 frame via ENTER; the four
; arguments (n, from, to, via) are reached by BP-relative LOCAL@/LOCAL! in any
; order, so no value is ever permuted and RET frees the frame automatically.
;
; Calling convention: caller pushes  via, to, from, n  (n on top) so the base
; case tests n without touching the frame. Frame after ENTER 4:
;   n at BP-4,  from at BP-3,  to at BP-2,  via at BP-1.
;
; The move is two port writes: the "from" peg to FROM_PORT, the "to" peg to
; TO_PORT. Watch the ports (or the panel) to see the disk sequence.

        ORG   0xE000

FROM_PORT EQU 0                 ; I/O page 0x01, port 0
TO_PORT   EQU 1                 ; I/O page 0x01, port 1
DISKS     EQU 8                 ; n = 8 -> 255 moves

main:   PUSH  #2                ; via  = peg 2
        PUSH  #1                ; to   = peg 1
        PUSH  #0                ; from = peg 0
        PUSH  #DISKS            ; n
        CALL  hanoi
        HALT

; ---- hanoi ( via to from n -- ) ----------------------------------------
hanoi:  TST                     ; flags of n (non-destructive)
        BEQ   leaf              ; n == 0 -> unwind arguments, no frame

        ENTER #4                ; open this level's frame (BP += 4)
        LOCAL! -4               ; n
        LOCAL! -3               ; from
        LOCAL! -2               ; to
        LOCAL! -1               ; via

        ; child A: hanoi(n-1, from, via, to)
        LOCAL@ -2               ; to    -> callee's via
        LOCAL@ -1               ; via   -> callee's to
        LOCAL@ -3               ; from  -> callee's from
        PUSH  #1
        LOCAL@ -4               ; n
        SUB                     ; n - 1  ( a b -- b-a ) with a=1, b=n
        CALL  hanoi

        ; move from -> to
        LOCAL@ -3               ; from
        OUT   #FROM_PORT        ; pops
        LOCAL@ -2               ; to
        OUT   #TO_PORT          ; pops

        ; child B: hanoi(n-1, via, to, from)
        LOCAL@ -3               ; from  -> callee's via
        LOCAL@ -2               ; to    -> callee's to
        LOCAL@ -1               ; via   -> callee's from
        PUSH  #1
        LOCAL@ -4               ; n
        SUB                     ; n - 1
        CALL  hanoi

        RET                     ; PC and BP restored together; frame freed

leaf:   NIP                     ; discard from (pure pointer move)
        NIP                     ; discard to
        NIP                     ; discard via
        DROP                    ; discard n (= 0)
        RET
