.section .text
.cpu cortex-m4
.thumb
.syntax unified
.text

.global Reset_Handler
.global EndOfProgram

Reset_Handler:
push {r0-r7}
mov r0, r12
push {r0, lr}
ldr r0, =g_pBuffer
ldr r0, [r0, #0]
blx FLASHPatcher_RunRequestLoop
pop {r0, r1}
mov lr, r1
mov r12, r0
pop {r0-r7}
EndOfProgram:
bkpt 0
blx FLASHPatcher_ProgramBuffer	//Only needed to keep a reference to the function
