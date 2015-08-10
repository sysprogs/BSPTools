#include <stm32f10x_gpio.h>
#include <stm32f10x_rcc.h>

void Delay()
{
	int i;
	for (i = 0; i < $$com.sysprogs.examples.ledblink.DELAYCYCLES$$; i++)
		asm("nop");
}

int main()
{
  GPIO_InitTypeDef GPIO_InitStructure;
  
  RCC_APB2PeriphClockCmd(RCC_APB2Periph_$$com.sysprogs.examples.ledblink.LEDPORT$$, ENABLE);

  GPIO_InitStructure.GPIO_Pin = GPIO_Pin_$$com.sysprogs.examples.ledblink.LEDBIT$$;
  
  GPIO_InitStructure.GPIO_Mode = GPIO_Mode_Out_PP;
  GPIO_InitStructure.GPIO_Speed = GPIO_Speed_50MHz;
  GPIO_Init($$com.sysprogs.examples.ledblink.LEDPORT$$, &GPIO_InitStructure);

  for (;;)
  {
	  GPIO_WriteBit($$com.sysprogs.examples.ledblink.LEDPORT$$, GPIO_Pin_$$com.sysprogs.examples.ledblink.LEDBIT$$, Bit_SET);
	  Delay();
	  GPIO_WriteBit($$com.sysprogs.examples.ledblink.LEDPORT$$, GPIO_Pin_$$com.sysprogs.examples.ledblink.LEDBIT$$, Bit_RESET);
	  Delay();
  }
}
