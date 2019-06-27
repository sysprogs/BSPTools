#include <$$com.sysprogs.stm32.hal_header_prefix$$_ll_bus.h>
#include <$$com.sysprogs.stm32.hal_header_prefix$$_ll_gpio.h>
#include <$$com.sysprogs.stm32.hal_header_prefix$$_ll_utils.h>

int main(void)
{
	LL_InitTick($$com.sysprogs.examples.ledblink.STARTFREQUENCY$$, 1000);

	//Warning: if the line below triggers an error, $$com.sysprogs.examples.ledblink.LEDPORT$$ is not connected to a AHB1 (Group 1) on this device.
	//In this case, please search the stm32xxxx_ll_bus.h file for 'PERIPH_$$com.sysprogs.examples.ledblink.LEDPORT$$' to find out the correct
	//macro name and use it to replace LL_AHB1_GRP1_PERIPH_$$com.sysprogs.examples.lBedblink.LEDPORT$$ and LL_AHB1_GRP1_EnableClock() below. 
	LL_AHB1_GRP1_EnableClock(LL_AHB1_GRP1_PERIPH_$$com.sysprogs.examples.ledblink.LEDPORT$$);
	LL_GPIO_SetPinMode($$com.sysprogs.examples.ledblink.LEDPORT$$, LL_GPIO_PIN_$$com.sysprogs.examples.ledblink.LEDBIT$$, LL_GPIO_MODE_OUTPUT);
	LL_GPIO_SetPinOutputType($$com.sysprogs.examples.ledblink.LEDPORT$$, LL_GPIO_PIN_$$com.sysprogs.examples.ledblink.LEDBIT$$, LL_GPIO_OUTPUT_PUSHPULL);
	LL_GPIO_SetPinSpeed($$com.sysprogs.examples.ledblink.LEDPORT$$, LL_GPIO_PIN_$$com.sysprogs.examples.ledblink.LEDBIT$$, LL_GPIO_SPEED_FREQ_LOW);

	for (;;)
	{
		LL_GPIO_SetOutputPin($$com.sysprogs.examples.ledblink.LEDPORT$$, LL_GPIO_PIN_$$com.sysprogs.examples.ledblink.LEDBIT$$);
		LL_mDelay($$com.sysprogs.examples.ledblink.DELAYMSEC$$);
		LL_GPIO_ResetOutputPin($$com.sysprogs.examples.ledblink.LEDPORT$$, LL_GPIO_PIN_$$com.sysprogs.examples.ledblink.LEDBIT$$);
		LL_mDelay($$com.sysprogs.examples.ledblink.DELAYMSEC$$);
	}
}
