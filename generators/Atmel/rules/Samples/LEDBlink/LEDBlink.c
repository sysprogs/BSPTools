
#include  "$$com.sysprogs.atmel.sam32._header_prefix$$.h"
#include  "sam_gpio.h"
#include  "pio.h"

#ifdef __cplusplus
extern "C"
#endif

#define LED0	 PIO_PA10
int main() 
{

	for (;;)
	{
		gpio_set_pin_low(LED0);

		gpio_set_pin_high(LED0);
	}

}
