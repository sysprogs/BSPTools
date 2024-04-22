/**
  ******************************************************************************
  * @file    FreeRTOS/FreeRTOS_ThreadCreation/Src/main.c
  * @author  MCD Application Team
  * @version V1.2.2
  * @date    25-May-2015
  * @brief   Main program body
  ******************************************************************************
  * @attention
  *
  * <h2><center>&copy; COPYRIGHT(c) 2015 STMicroelectronics</center></h2>
  *
  * Licensed under MCD-ST Liberty SW License Agreement V2, (the "License");
  * You may not use this file except in compliance with the License.
  * You may obtain a copy of the License at:
  *
  *        http://www.st.com/software_license_agreement_liberty_v2
  *
  * Unless required by applicable law or agreed to in writing, software 
  * distributed under the License is distributed on an "AS IS" BASIS, 
  * WITHOUT WARRANTIES OR CONDITIONS OF ANY KIND, either express or implied.
  * See the License for the specific language governing permissions and
  * limitations under the License.
  *
  ******************************************************************************
  */

/* Includes ------------------------------------------------------------------*/
#include <$$com.sysprogs.stm32.hal_header_prefix$$_hal.h>
#include <cmsis_os2.h>

/* Private typedef -----------------------------------------------------------*/
/* Private define ------------------------------------------------------------*/
/* Private macro -------------------------------------------------------------*/
/* Private variables ---------------------------------------------------------*/
osThreadId_t LEDThread1Handle, LEDThread2Handle;

/* Private function prototypes -----------------------------------------------*/
static void LED_Thread1(void *argument);
static void LED_Thread2(void *argument);

/* Private functions ---------------------------------------------------------*/

/**
  * @brief  Main program
  * @param  None
  * @retval None
  */
int main(void)
{
	HAL_Init();  
	
	__$$com.sysprogs.examples.stm32.LEDPORT$$_CLK_ENABLE();
	GPIO_InitTypeDef GPIO_InitStructure;

	GPIO_InitStructure.Pin = GPIO_PIN_$$com.sysprogs.examples.stm32.LED1BIT$$ | GPIO_PIN_$$com.sysprogs.examples.stm32.LED2BIT$$;

	GPIO_InitStructure.Mode = GPIO_MODE_OUTPUT_PP;
	GPIO_InitStructure.Speed = GPIO_SPEED_FREQ_HIGH;
	GPIO_InitStructure.Pull = GPIO_NOPULL;
	HAL_GPIO_Init($$com.sysprogs.examples.stm32.LEDPORT$$, &GPIO_InitStructure);

	osKernelInitialize();
	
	LEDThread1Handle = osThreadNew(LED_Thread1, NULL, NULL);
	LEDThread2Handle = osThreadNew(LED_Thread2, NULL, NULL);
  
	/* Start scheduler */
	osKernelStart();


	  /* We should never get here as control is now taken by the scheduler */
	for (;;)
		;
}

/**
  * @brief  Toggle LED1
  * @param  thread not used
  * @retval None
  */
static void LED_Thread1(void *argument)
{
	(void) argument;
  
	for (;;)
	{
		HAL_GPIO_WritePin($$com.sysprogs.examples.stm32.LEDPORT$$, GPIO_PIN_$$com.sysprogs.examples.stm32.LED1BIT$$, GPIO_PIN_SET);
		osDelay(2000);
		
		HAL_GPIO_WritePin($$com.sysprogs.examples.stm32.LEDPORT$$, GPIO_PIN_$$com.sysprogs.examples.stm32.LED1BIT$$, GPIO_PIN_RESET);
		osThreadSuspend(LEDThread2Handle);
		osDelay(2000);
		
		osThreadResume(LEDThread2Handle);
	}
}

/**
  * @brief  Toggle LED2 thread
  * @param  argument not used
  * @retval None
  */
static void LED_Thread2(void *argument)
{
	uint32_t count;
	(void) argument;
  
	for (;;)
	{
		HAL_GPIO_TogglePin($$com.sysprogs.examples.stm32.LEDPORT$$, GPIO_PIN_$$com.sysprogs.examples.stm32.LED2BIT$$);
		osDelay(200);
	}
}

#ifdef  USE_FULL_ASSERT
/**
  * @brief  Reports the name of the source file and the source line number
  *         where the assert_param error has occurred.
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
	{
	}
}
#endif

/************************ (C) COPYRIGHT STMicroelectronics *****END OF FILE****/
