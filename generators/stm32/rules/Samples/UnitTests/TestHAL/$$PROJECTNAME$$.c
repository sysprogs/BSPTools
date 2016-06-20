#include <$$com.sysprogs.stm32.hal_header_prefix$$_hal.h>
$$UNITTEST:INCLUDES$$

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

	$$UNITTEST:INIT$$
	return 0;
}
