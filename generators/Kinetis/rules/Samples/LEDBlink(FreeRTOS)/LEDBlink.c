#include "M$$SYS:FAMILY_ID$$.h"
#include "fsl_os_abstraction.h"
#include "fsl_gpio_driver.h"

void MainTaskBody(task_param_t param)
{
	for (;;)
	{
		GPIO_HAL_ClearPinOutput($$com.sysprogs.arm.kinetis.gpio_prefix$$$$com.sysprogs.examples.ledblink.LEDPORT$$_BASE_PTR, $$com.sysprogs.examples.ledblink.LEDBIT$$);
		OSA_TimeDelay($$com.sysprogs.examples.ledblink.DELAYMSEC$$);
		GPIO_HAL_SetPinOutput($$com.sysprogs.arm.kinetis.gpio_prefix$$$$com.sysprogs.examples.ledblink.LEDPORT$$_BASE_PTR, $$com.sysprogs.examples.ledblink.LEDBIT$$);
		OSA_TimeDelay($$com.sysprogs.examples.ledblink.DELAYMSEC$$);
	}
}

enum { kMainTaskStackSize = 512, kMainTaskPriority = 1 };

int main()
{
	osa_status_t status;
	OSA_TASK_DEFINE(task_func, kMainTaskStackSize);
	
	SIM_BASE_PTR->SCGC5 |= SIM_SCGC5_PORT$$com.sysprogs.examples.ledblink.LEDPORT$$_MASK;
	PORT$$com.sysprogs.examples.ledblink.LEDPORT$$_BASE_PTR->PCR[$$com.sysprogs.examples.ledblink.LEDBIT$$] = PORT_PCR_MUX(1);
	OSA_Init();
	
	GPIO_HAL_SetPinDir($$com.sysprogs.arm.kinetis.gpio_prefix$$$$com.sysprogs.examples.ledblink.LEDPORT$$_BASE_PTR, $$com.sysprogs.examples.ledblink.LEDBIT$$, kGpioDigitalOutput);

	
	status = OSA_TaskCreate(MainTaskBody,
		(uint8_t *)"task_name",
		kMainTaskStackSize,
		task_func_stack,
		kMainTaskPriority,
		0,
		false,
		&task_func_task_handler);

	OSA_Start();
	return 0;
}