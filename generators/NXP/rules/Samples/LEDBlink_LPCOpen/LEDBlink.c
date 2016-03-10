#include <chip.h>

volatile unsigned long SysTickCnt;

#ifdef __cplusplus
extern "C"
#endif
void SysTick_Handler(void)
{
	SysTickCnt++;
}

void Delay(unsigned long tick)
{
	unsigned long systickcnt;

	systickcnt = SysTickCnt;
	while ((SysTickCnt - systickcnt) < tick);
}

const uint32_t ExtRateIn = 0;
const uint32_t OscRateIn = 12000000;
const uint32_t RTCOscRateIn = 32768;

#ifndef LPC_GPIO
#define LPC_GPIO LPC_GPIO_PORT
#endif

int main()
{
	SystemCoreClockUpdate();
	Chip_GPIO_Init(LPC_GPIO);
	
	SysTick_Config(SystemCoreClock / 1000);
	Chip_GPIO_SetPortDIROutput(LPC_GPIO, $$com.sysprogs.examples.ledblink.LEDPORT$$, 1 << $$com.sysprogs.examples.ledblink.LEDBIT$$);

	for (;;)
	{
		Chip_GPIO_WritePortBit(LPC_GPIO, $$com.sysprogs.examples.ledblink.LEDPORT$$, $$com.sysprogs.examples.ledblink.LEDBIT$$, 1);
		Delay($$com.sysprogs.examples.ledblink.DELAYMSEC$$);
		Chip_GPIO_WritePortBit(LPC_GPIO, $$com.sysprogs.examples.ledblink.LEDPORT$$, $$com.sysprogs.examples.ledblink.LEDBIT$$, 0);
		Delay($$com.sysprogs.examples.ledblink.DELAYMSEC$$);
	}

	return 0;
}
