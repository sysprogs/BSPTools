#include  "$$com.sysprogs.atmel.sam32._header_prefix$$.h"
#include  "sam_gpio.h"
#include  "pio.h"

void Delay()
{
	int i;
	for (i = 0; i < $$com.sysprogs.examples.ledblink.DELAYCYCLES$$; i++)
		asm("nop");
}

int main() 
{
	gpio_configure_pin(PIO_P$$com.sysprogs.examples.ledblink.LEDPORT$$$$com.sysprogs.examples.ledblink.LEDBIT$$, PIO_TYPE_PIO_OUTPUT_1 | PIO_DEFAULT);
	
	for (;;)
	{
		gpio_set_pin_low(PIO_P$$com.sysprogs.examples.ledblink.LEDPORT$$$$com.sysprogs.examples.ledblink.LEDBIT$$);
		Delay();
		gpio_set_pin_high(PIO_P$$com.sysprogs.examples.ledblink.LEDPORT$$$$com.sysprogs.examples.ledblink.LEDBIT$$);
		Delay();
	}
}
