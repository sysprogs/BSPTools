#include <stdbool.h>
#include <stdint.h>
#include <inc/hw_memmap.h>
#include <driverlib/debug.h>
#include <driverlib/gpio.h>
#include <driverlib/rom.h>
#include <driverlib/sysctl.h>

#include <tiva_device.h>

int main(void)
{
    $$com.sysprogs.examples.tiva.ROMPREFIX$$SysCtlPeripheralEnable(SYSCTL_PERIPH_GPIO$$com.sysprogs.examples.ledblink.LEDPORT$$);
    $$com.sysprogs.examples.tiva.ROMPREFIX$$SysCtlDelay(1);

    $$com.sysprogs.examples.tiva.ROMPREFIX$$GPIOPinTypeGPIOOutput(GPIO_PORT$$com.sysprogs.examples.ledblink.LEDPORT$$_BASE, GPIO_PIN_$$com.sysprogs.examples.ledblink.LEDBIT$$);

    for (;;)
    {
        $$com.sysprogs.examples.tiva.ROMPREFIX$$GPIOPinWrite(GPIO_PORT$$com.sysprogs.examples.ledblink.LEDPORT$$_BASE, GPIO_PIN_$$com.sysprogs.examples.ledblink.LEDBIT$$, GPIO_PIN_$$com.sysprogs.examples.ledblink.LEDBIT$$);
        $$com.sysprogs.examples.tiva.ROMPREFIX$$SysCtlDelay($$com.sysprogs.examples.ledblink.DELAY$$);

        $$com.sysprogs.examples.tiva.ROMPREFIX$$GPIOPinWrite(GPIO_PORT$$com.sysprogs.examples.ledblink.LEDPORT$$_BASE, GPIO_PIN_$$com.sysprogs.examples.ledblink.LEDBIT$$, 0);
        $$com.sysprogs.examples.tiva.ROMPREFIX$$SysCtlDelay($$com.sysprogs.examples.ledblink.DELAY$$);
    }
}
