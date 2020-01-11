#include <stdint.h>
#include <msp.h>

void SystemInit()
{
	SCB->CPACR |= ((3UL << 10 * 2) | /* Set CP10 Full Access */
				   (3UL << 11 * 2)); /* Set CP11 Full Access */
}

#ifdef DEBUG
void
__error__(char *pcFilename, uint32_t ui32Line)
{
	asm("bkpt 255");
}
#endif
