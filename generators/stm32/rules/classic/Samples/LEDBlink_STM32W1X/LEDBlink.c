#include <stm32w108xx_gpio.h>

void Delay()
{
	int i;
	for (i = 0; i < $$com.sysprogs.examples.ledblink.DELAYCYCLES$$; i++)
		asm("nop");
}

int main()
{
  GPIO_InitTypeDef GPIO_InitStructure;
  
  GPIO_InitStructure.GPIO_Pin = GPIO_Pin_$$com.sysprogs.examples.ledblink.LEDBIT$$;
  GPIO_InitStructure.GPIO_Mode = GPIO_Mode_OUT_PP;
  GPIO_Init($$com.sysprogs.examples.ledblink.LEDPORT$$, &GPIO_InitStructure);

  for (;;)
  {
	  GPIO_WriteBit($$com.sysprogs.examples.ledblink.LEDPORT$$, GPIO_Pin_$$com.sysprogs.examples.ledblink.LEDBIT$$, Bit_SET);
	  Delay();
	  GPIO_WriteBit($$com.sysprogs.examples.ledblink.LEDPORT$$, GPIO_Pin_$$com.sysprogs.examples.ledblink.LEDBIT$$, Bit_RESET);
	  Delay();
  }
}