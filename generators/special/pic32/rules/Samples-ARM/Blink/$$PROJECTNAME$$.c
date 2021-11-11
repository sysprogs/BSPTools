#include <pic32c.h>

static void Delay()
{
	for (volatile int x = 0; x < $$com.sysprogs.examples.ledblink.DELAYCYCLES$$; x++)
	{
		asm("nop");
	}
}

int main()
{
	PORT_REGS->GROUP[$$com.sysprogs.examples.ledblink.LEDPORT$$].PORT_DIRSET = (1U << $$com.sysprogs.examples.ledblink.LEDBIT$$);
	
	for (;;)
	{
		PORT_REGS->GROUP[$$com.sysprogs.examples.ledblink.LEDPORT$$].PORT_OUTSET = (1U << $$com.sysprogs.examples.ledblink.LEDBIT$$);
		Delay();
		PORT_REGS->GROUP[$$com.sysprogs.examples.ledblink.LEDPORT$$].PORT_OUTCLR = (1U << $$com.sysprogs.examples.ledblink.LEDBIT$$);
		Delay();
	}
	
	return 0;
}