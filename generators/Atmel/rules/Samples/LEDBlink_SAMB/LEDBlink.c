#include  "$$com.sysprogs.atmel.sam32._header_prefix$$.h"
#include  "gpio.h"

void Delay()
{
	int i;
	for (i = 0; i < $$com.sysprogs.examples.ledblink.DELAYCYCLES$$; i++)
		asm("nop");
}



int main() 
{
	struct gpio_config config;
	config.direction = GPIO_PIN_DIR_OUTPUT ;

	gpio_pin_set_config(PIN_LP_GPIO_1, &config);
	
	for (;;)
	{
		gpio_pin_set_output_level(PIN_LP_GPIO_$$com.sysprogs.examples.ledblink.LEDBIT$$,false);
		Delay();
		gpio_pin_set_output_level(PIN_LP_GPIO_$$com.sysprogs.examples.ledblink.LEDBIT$$,true);
		Delay();
	}
}
