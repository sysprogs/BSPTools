#include <lpc12xx_gpio.h>
#include <lpc12xx_iocon.h>
#include <lpc12xx_sysctrl.h>

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

int main()
{
	IOCON_PIO_CFG_Type PIO_mode;

	SysTick_Config(SystemCoreClock / 1000 - 1);
	SYS_ConfigAHBCLK(SYS_AHBCLKCTRL_GPIO$$com.sysprogs.examples.ledblink.LEDPORT$$, ENABLE);

	IOCON_StructInit(&PIO_mode);
	PIO_mode.type = IOCON_PIO_$$com.sysprogs.examples.ledblink.LEDPORT$$_$$com.sysprogs.examples.ledblink.LEDBIT$$;

	GPIO_SetDir(LPC_GPIO$$com.sysprogs.examples.ledblink.LEDPORT$$, $$com.sysprogs.examples.ledblink.LEDBIT$$, 1);

	for (;;)
	{
		GPIO_SetHighLevel(LPC_GPIO$$com.sysprogs.examples.ledblink.LEDPORT$$, $$com.sysprogs.examples.ledblink.LEDBIT$$, 1);
		Delay($$com.sysprogs.examples.ledblink.DELAYMSEC$$);
		GPIO_SetLowLevel(LPC_GPIO$$com.sysprogs.examples.ledblink.LEDPORT$$, $$com.sysprogs.examples.ledblink.LEDBIT$$, 1);
		Delay($$com.sysprogs.examples.ledblink.DELAYMSEC$$);
	}

	return 0;
}
