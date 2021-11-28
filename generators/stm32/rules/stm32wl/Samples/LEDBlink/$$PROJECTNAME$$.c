#include <$$com.sysprogs.stm32.hal_header_prefix$$_hal.h>
#include <stm32_hal_legacy.h>

#ifdef __cplusplus
extern "C"
#endif
void SysTick_Handler(void)
{
	HAL_IncTick();
	HAL_SYSTICK_IRQHandler();
}

int main(void)
{
	HAL_Init();

	__$$com.sysprogs.examples.ledblink.LEDPORT$$_CLK_ENABLE();
	GPIO_InitTypeDef GPIO_InitStructure;

	GPIO_InitStructure.Pin = GPIO_PIN_$$com.sysprogs.examples.ledblink.LEDBIT$$;

	GPIO_InitStructure.Mode = GPIO_MODE_OUTPUT_PP;
	GPIO_InitStructure.Speed = GPIO_SPEED_FREQ_HIGH;
	GPIO_InitStructure.Pull = GPIO_NOPULL;
	HAL_GPIO_Init($$com.sysprogs.examples.ledblink.LEDPORT$$, &GPIO_InitStructure);

	for (;;)
	{
		HAL_GPIO_WritePin($$com.sysprogs.examples.ledblink.LEDPORT$$, GPIO_PIN_$$com.sysprogs.examples.ledblink.LEDBIT$$, GPIO_PIN_SET);
		HAL_Delay($$com.sysprogs.examples.ledblink.DELAYMSEC$$);
		HAL_GPIO_WritePin($$com.sysprogs.examples.ledblink.LEDPORT$$, GPIO_PIN_$$com.sysprogs.examples.ledblink.LEDBIT$$, GPIO_PIN_RESET);
		HAL_Delay($$com.sysprogs.examples.ledblink.DELAYMSEC$$);
	}
}
