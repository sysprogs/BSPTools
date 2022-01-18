#include "$$com.sysprogs.stm8.sdk_name$$.h"

void Delay (uint16_t nCount);

void main(void)
{
  GPIO_Init($$com.sysprogs.examples.ledblink.LEDPORT$$, $$com.sysprogs.stm8.gpio_pin_prefix$$_$$com.sysprogs.examples.ledblink.LEDBIT$$, $$com.sysprogs.stm8.gpio_fast_output_mode$$);

  while (1)
  {
    $$com.sysprogs.stm8.gpio_toggle_func$$($$com.sysprogs.examples.ledblink.LEDPORT$$, $$com.sysprogs.stm8.gpio_pin_prefix$$_$$com.sysprogs.examples.ledblink.LEDBIT$$);
    Delay($$com.sysprogs.examples.ledblink.DELAYCYCLES$$);
  }
}

/**
  * @brief  Inserts a delay time.
  * @param  nCount: specifies the delay time length.
  * @retval None
  */
void Delay(__IO uint16_t nCount)
{
  /* Decrement nCount value */
  while (nCount != 0)
  {
    nCount--;
  }
}

#ifdef  USE_FULL_ASSERT

/**
  * @brief  Reports the name of the source file and the source line number
  *   where the assert_param error has occurred.
  * @param  file: pointer to the source file name
  * @param  line: assert_param error line source number
  * @retval None
  */
void assert_failed(uint8_t* file, uint32_t line)
{
  /* User can add his own implementation to report the file name and line number,
     ex: printf("Wrong parameters value: file %s on line %d\r\n", file, line) */
  /* Infinite loop */
  while (1)
  {}
}
#endif
