#include <mbed.h>

DigitalOut g_LED(LED$$com.sysprogs.examples.ledblink.LEDNUM$$);

int main() 
{
	for (;;)
	{
		g_LED = 1;
		wait_ms($$com.sysprogs.examples.ledblink.DELAYMSEC$$);
		g_LED = 0;
		wait_ms($$com.sysprogs.examples.ledblink.DELAYMSEC$$);
	}
}