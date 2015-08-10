#include <$$com.sysprogs.arm.stm32.periph_prefix$$_gpio.h>
#include <$$com.sysprogs.arm.stm32.periph_prefix$$_rcc.h>

void Delay()
{
	int i;
	for (i = 0; i < $$com.sysprogs.examples.ledblink.DELAYCYCLES$$; i++)
		asm("nop");
}

int main()
{
  GPIO_InitTypeDef GPIO_InitStructure;
  
  RCC_AHBPeriphClockCmd(RCC_AHBPeriph_$$com.sysprogs.examples.ledblink.LEDPORT$$, ENABLE);

  GPIO_InitStructure.GPIO_Pin = GPIO_Pin_$$com.sysprogs.examples.ledblink.LEDBIT$$;
  
  GPIO_InitStructure.GPIO_Mode = GPIO_Mode_OUT;
  GPIO_InitStructure.GPIO_OType = GPIO_OType_PP;
  GPIO_InitStructure.GPIO_PuPd = GPIO_PuPd_UP;
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
