#include <stdbool.h>
#include <stdint.h>

#ifdef __cplusplus
extern "C" {
#endif
	
#include "nrf_delay.h"
#include "nrf_gpio.h"
#include "boards.h"
	
#ifdef __cplusplus
}
#endif

const uint8_t leds_list[LEDS_NUMBER] = LEDS_LIST;

int main(void)
{
	LEDS_CONFIGURE(LEDS_MASK);

	for (;;)
	{
		for (int i = 0; i < LEDS_NUMBER; i++)
		{
			LEDS_INVERT(1 << leds_list[i]);
			nrf_delay_ms(500);
		}
	}
}
