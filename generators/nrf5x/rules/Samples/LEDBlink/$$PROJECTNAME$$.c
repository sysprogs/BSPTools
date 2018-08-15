#include <stdbool.h>
#include <stdint.h>

#ifdef __cplusplus
extern "C" {
#endif
	
#include "nrf_delay.h"
#include "nrf_gpio.h"
	
#ifdef __cplusplus
}
#endif


int main(void)
{
	nrf_gpio_cfg_output($$com.sysprogs.examples.ledblink.LEDBIT$$);

	for (;;)
	{
		nrf_gpio_pin_set($$com.sysprogs.examples.ledblink.LEDBIT$$);
		nrf_delay_ms($$com.sysprogs.examples.ledblink.DELAYMSEC$$);
		nrf_gpio_pin_clear($$com.sysprogs.examples.ledblink.LEDBIT$$);
		nrf_delay_ms($$com.sysprogs.examples.ledblink.DELAYMSEC$$);
	}
}
