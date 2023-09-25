.section .text
.cpu cortex-m4
.thumb
.syntax unified
.text

.global Reset_Handler
.global EndOfProgram

Reset_Handler:
ldr r0, =0x20002000
ldr r1, =end
ldr r2, =0x55555555
.FillStack:
str r2, [r1, #0]
add r1, #4
cmp r1, r0
bne .FillStack
mov sp, r0

ldr r0, =g_pBuffer
blx FLASHPatcher_RunRequestLoop
bkpt 0
//The calls below are needed to prevent the link-time optimizer from hardcoding values implied from the async handler
b FLASHPatcher_Init
b FLASHPatcher_EraseSectors
b FLASHPatcher_ProgramWords
b FLASHPatcher_Complete
b FLASHPatcher_ProgramRepeatedWords
