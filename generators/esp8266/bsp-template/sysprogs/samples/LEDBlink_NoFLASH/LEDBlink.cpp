#ifdef __cplusplus
extern "C"
{
#endif
	
#include <c_types.h>
#include <eagle_soc.h>
#include <gpio.h>
	
#ifdef __cplusplus
}
#endif

void delay(int cycles)
{
	volatile int i;
	for (i = 0; i < cycles; i++) {}
}

int main()
{
	gpio_init();
	PIN_FUNC_SELECT(PERIPHS_IO_MUX_U0TXD_U, FUNC_GPIO1);

	for (;;)
	{
		gpio_output_set(BIT1, 0, BIT1, 0);
		delay($$com.sysprogs.esp8266.ledblink.DELAYCYCLES$$);
		gpio_output_set(0, BIT1, BIT1, 0);
		delay($$com.sysprogs.esp8266.ledblink.DELAYCYCLES$$);
	}
}