#include <xmc_gpio.h>

#ifdef __cplusplus
extern "C"
#endif
void SysTick_Handler(void)
{
	XMC_GPIO_ToggleOutput(XMC_GPIO_PORT$$com.sysprogs.examples.ledblink.LEDPORT$$, $$com.sysprogs.examples.ledblink.LEDBIT$$);
}

int main(void)
{
	XMC_GPIO_SetMode(XMC_GPIO_PORT$$com.sysprogs.examples.ledblink.LEDPORT$$, $$com.sysprogs.examples.ledblink.LEDBIT$$, XMC_GPIO_MODE_OUTPUT_PUSH_PULL);
	unsigned periodInMsec = $$com.sysprogs.examples.ledblink.DELAYMSEC$$;
	SysTick_Config((SystemCoreClock / 1000) * periodInMsec);

	for (;;)
	{
	}
}
