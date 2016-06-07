#include "em_chip.h"
#include "em_cmu.h"


volatile unsigned long SysTickCnt;

#ifdef __cplusplus
extern "C"
#endif

int main()
{
	CHIP_Init();

	SysTick_Config(CMU_ClockFreqGet(cmuClock_CORE) / 1000);

	for (;;)

	{

	GPIO_PinOutSet(gpioPortA, 0);

	GPIO_PinOutClear(gpioPortA, 0);

	}
	return 0;
}
