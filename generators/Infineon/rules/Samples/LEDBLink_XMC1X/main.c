#include <xmc_gpio.h>

#ifdef __cplusplus
extern "C"
#endif
void SysTick_Handler(void)
{
	XMC_GPIO_ToggleOutput($$com.sysprogs.examples.ledblink.LEDPORT$$_$$com.sysprogs.examples.ledblink.LEDBIT$$);
}

int main(void)
{
	XMC_GPIO_SetMode($$com.sysprogs.examples.ledblink.LEDPORT$$_$$com.sysprogs.examples.ledblink.LEDBIT$$, XMC_GPIO_MODE_OUTPUT_PUSH_PULL);
	unsigned periodInMsec = $$com.sysprogs.examples.ledblink.DELAYMSEC$$;
	SysTick_Config((SystemCoreClock / periodInMsec) * 1000);

	for (;;)
	{
	}
}
