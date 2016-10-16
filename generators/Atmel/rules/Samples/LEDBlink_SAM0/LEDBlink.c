#include <system.h>
#include <port.h>

#define LED1 PIN_P$$com.sysprogs.examples.ledblink.LEDPORT$$$$com.sysprogs.examples.ledblink.LEDBIT$$

void Delay()
{
	int i;
	for (i = 0; i < $$com.sysprogs.examples.ledblink.DELAYCYCLES$$; i++)
		asm("nop");
}

int main(void)
{
    system_init();
    
    struct port_config pin_conf;
    port_get_config_defaults(&pin_conf);

    pin_conf.direction  = PORT_PIN_DIR_OUTPUT;
    port_pin_set_config(LED1, &pin_conf);
    for (;;)
    {
        port_pin_set_output_level(LED1, false);
		Delay();
        port_pin_set_output_level(LED1, true);
		Delay();
    }
}
