#include  "sam4l.h"
#include  "ioport.h"

void Delay()
{
	int i;
	for (i = 0; i < 1000000; i++)
		asm("nop");
}

#define LED1 PIN_P$$com.sysprogs.examples.ledblink.LEDPORT$$$$com.sysprogs.examples.ledblink.LEDBIT$$

int main() 
{
    sysclk_init();
    ioport_set_pin_dir(LED1, IOPORT_DIR_OUTPUT);
    ioport_set_pin_level(LED1, IOPORT_PIN_LEVEL_LOW);
    for (;;)
    {
        Delay();
        ioport_set_pin_level(PIN_PA00, IOPORT_PIN_LEVEL_LOW);
        Delay();
        ioport_set_pin_level(PIN_PA00, IOPORT_PIN_LEVEL_HIGH);
    }
}
