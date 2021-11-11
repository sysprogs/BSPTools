#include <xc.h>

static void Delay()
{
	for (volatile int x = 0; x < $$com.sysprogs.examples.ledblink.DELAYCYCLES$$; x++)
	{
		asm("nop");
	}
}

int main()
{
	TRIS$$com.sysprogs.examples.ledblink.LEDPORT$$CLR = 1 << $$com.sysprogs.examples.ledblink.LEDBIT$$;
	for (;;)
	{
		PORT$$com.sysprogs.examples.ledblink.LEDPORT$$SET = 1 << $$com.sysprogs.examples.ledblink.LEDBIT$$;
		Delay();
		PORT$$com.sysprogs.examples.ledblink.LEDPORT$$CLR = 1 << $$com.sysprogs.examples.ledblink.LEDBIT$$;
		Delay();
	}
	
	return 0;
}