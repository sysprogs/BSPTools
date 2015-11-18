#include "M$$SYS:FAMILY_ID$$.h"

void Delay()
{
	int i;
	for (i = 0; i < $$com.sysprogs.examples.ledblink.DELAYCYCLES$$; i++)
		asm("nop");
}

int main()
{
	SIM_BASE_PTR->SCGC5 |= SIM_SCGC5_PORT$$com.sysprogs.examples.ledblink.LEDPORT$$_MASK;
	PORT$$com.sysprogs.examples.ledblink.LEDPORT$$_BASE_PTR->PCR[$$com.sysprogs.examples.ledblink.LEDBIT$$] = PORT_PCR_MUX(1);

	GPIO$$com.sysprogs.examples.ledblink.LEDPORT$$_PDDR = 1 << $$com.sysprogs.examples.ledblink.LEDBIT$$;

	for(;;)
	{
		GPIO$$com.sysprogs.examples.ledblink.LEDPORT$$_PSOR = 1 << $$com.sysprogs.examples.ledblink.LEDBIT$$;
		Delay();
		GPIO$$com.sysprogs.examples.ledblink.LEDPORT$$_PCOR = 1 << $$com.sysprogs.examples.ledblink.LEDBIT$$;
		Delay();
	}

	return 0;
}