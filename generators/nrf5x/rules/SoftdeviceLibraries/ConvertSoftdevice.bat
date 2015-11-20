set ARM_GCC_PREFIX=F:\SysGCC\arm-eabi\bin\arm-eabi-
cd /d %~dp0

%ARM_GCC_PREFIX%gcc empty.c -c -o %1_softdevice.o -mcpu=cortex-m0 -mthumb
%ARM_GCC_PREFIX%objcopy.exe -I ihex -O binary %2 %1_softdevice.bin
%ARM_GCC_PREFIX%objcopy.exe --add-section .softdevice=%1_softdevice.bin --set-section-flags .softdevice=CONTENTS,ALLOC,LOAD,READONLY,CODE %1_softdevice.o --remove-section .text --remove-section .data --remove-section .bss
