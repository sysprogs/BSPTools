#include <stdint.h>
#include <msp.h>

void SystemInit()
{
	HWREG32(SCS_BASE + OFS_SCB_CPACR) =
            ((HWREG32(SCS_BASE + OFS_SCB_CPACR)
                    & ~(SCB_CPACR_CP11_M | SCB_CPACR_CP10_M))
                    | SCB_CPACR_CP11_M | SCB_CPACR_CP10_M);
}

#ifdef DEBUG
void
__error__(char *pcFilename, uint32_t ui32Line)
{
	asm("bkpt 255");
}
#endif
