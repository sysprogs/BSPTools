#include <stdint.h>
#include <inc/hw_nvic.h>
#include <inc/hw_types.h>

void SystemInit()
{
    HWREG(NVIC_CPAC) = ((HWREG(NVIC_CPAC) &
                         ~(NVIC_CPAC_CP10_M | NVIC_CPAC_CP11_M)) |
                        NVIC_CPAC_CP10_FULL | NVIC_CPAC_CP11_FULL);
}

#ifdef DEBUG
void
__error__(char *pcFilename, uint32_t ui32Line)
{
	asm("bkpt 255");
}
#endif
