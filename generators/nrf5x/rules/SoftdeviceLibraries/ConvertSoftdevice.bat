@echo off
REM Usage: ConvertSoftdevice.bat <Softdevice Name> <Intermediate Hex file name>
set ARM_GCC_PREFIX=arm-none-eabi-
cd /d %~dp0

if not exist soft mkdir soft
if not exist hard mkdir hard

%ARM_GCC_PREFIX%objcopy.exe --gap-fill 0xFF -I ihex -O binary %2 %1_softdevice.bin

%ARM_GCC_PREFIX%gcc empty.c -c -o hard\%1_softdevice.o -mcpu=cortex-m4 -mthumb -mfloat-abi=hard -mfpu=fpv4-sp-d16
%ARM_GCC_PREFIX%objcopy.exe --add-section .softdevice=%1_softdevice.bin --set-section-flags .softdevice=CONTENTS,ALLOC,LOAD,READONLY,CODE hard\%1_softdevice.o --remove-section .text --remove-section .data --remove-section .bss

%ARM_GCC_PREFIX%gcc empty.c -c -o soft\%1_softdevice.o -mcpu=cortex-m4 -mthumb -mfloat-abi=soft
%ARM_GCC_PREFIX%objcopy.exe --add-section .softdevice=%1_softdevice.bin --set-section-flags .softdevice=CONTENTS,ALLOC,LOAD,READONLY,CODE soft\%1_softdevice.o --remove-section .text --remove-section .data --remove-section .bss
