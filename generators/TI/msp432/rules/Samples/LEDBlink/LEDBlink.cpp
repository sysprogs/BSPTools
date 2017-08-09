#include <driverlib.h>

#include <stdint.h>
#include <stdbool.h>

int main(void)
{
    volatile uint32_t ii;

    $$com.sysprogs.examples.msp432.ROMPREFIX$$WDT_A_holdTimer();
    $$com.sysprogs.examples.msp432.ROMPREFIX$$GPIO_setAsOutputPin(GPIO_PORT_$$com.sysprogs.examples.ledblink.LEDPORT$$, GPIO_PIN$$com.sysprogs.examples.ledblink.LEDBIT$$);

    while (1)
    {
        for(ii=0; ii < $$com.sysprogs.examples.ledblink.DELAY$$; ii++);
        $$com.sysprogs.examples.msp432.ROMPREFIX$$GPIO_toggleOutputOnPin(GPIO_PORT_$$com.sysprogs.examples.ledblink.LEDPORT$$, GPIO_PIN$$com.sysprogs.examples.ledblink.LEDBIT$$);
    }
}
