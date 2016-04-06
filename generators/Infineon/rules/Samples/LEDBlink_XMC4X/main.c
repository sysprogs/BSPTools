#include <xmc_gpio.h>

enum { SystemTickPeriod = 1000 };

static volatile int s_Ticks = 0;

#ifdef __cplusplus
extern "C"
#endif
void SysTick_Handler(void)
{
	s_Ticks++;
}

void Delay(int ticks)
{
	int deadline = s_Ticks + ticks;
	while (s_Ticks < deadline)
	{
	}
}

int main(void)
{
	XMC_GPIO_CONFIG_t config;

	config.mode = XMC_GPIO_MODE_OUTPUT_PUSH_PULL;
	config.output_level = XMC_GPIO_OUTPUT_LEVEL_HIGH;
	config.output_strength = XMC_GPIO_OUTPUT_STRENGTH_MEDIUM;

	XMC_GPIO_Init($$com.sysprogs.examples.ledblink.LEDPORT$$_$$com.sysprogs.examples.ledblink.LEDBIT$$, &config);

	SysTick_Config(SystemCoreClock / SystemTickPeriod);
	for (;;)
	{
		XMC_GPIO_SetOutputHigh($$com.sysprogs.examples.ledblink.LEDPORT$$_$$com.sysprogs.examples.ledblink.LEDBIT$$);
    	Delay($$com.sysprogs.examples.ledblink.DELAYMSEC$$);
		XMC_GPIO_SetOutputLow($$com.sysprogs.examples.ledblink.LEDPORT$$_$$com.sysprogs.examples.ledblink.LEDBIT$$);
    	Delay($$com.sysprogs.examples.ledblink.DELAYMSEC$$);
	}
}
