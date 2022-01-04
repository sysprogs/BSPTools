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

#include "app_azure_rtos_config.h"
#include "app_threadx.h"

static UINT App_ThreadX_Init(VOID *memory_ptr);


static UCHAR tx_byte_pool_buffer[TX_APP_MEM_POOL_SIZE];
static TX_BYTE_POOL tx_app_byte_pool;

void Error_Handler(void)
{
	asm("bkpt 255");
}

VOID tx_application_define(VOID *first_unused_memory)
{
	VOID *memory_ptr;

	if (tx_byte_pool_create(&tx_app_byte_pool, "Tx App memory pool", tx_byte_pool_buffer, TX_APP_MEM_POOL_SIZE) != TX_SUCCESS)
	{
		Error_Handler();
	}
	else
	{
		memory_ptr = (VOID *)&tx_app_byte_pool;

		if (App_ThreadX_Init(memory_ptr) != TX_SUCCESS)
		{
			Error_Handler();
		}
	}

}

TX_THREAD LEDThread1Handle, LEDThread2Handle;

/* Private function prototypes -----------------------------------------------*/
static void LED_Thread1(ULONG argument);
static void LED_Thread2(ULONG argument);

/* Private functions ---------------------------------------------------------*/

static UINT App_ThreadX_Init(VOID *memory_ptr)
{
	UINT ret = TX_SUCCESS;
	TX_BYTE_POOL *byte_pool = (TX_BYTE_POOL*)memory_ptr;

	/* USER CODE BEGIN App_ThreadX_Init */
	CHAR *pointer;

	/* Allocate the stack for ThreadOne.  */
	if (tx_byte_allocate(byte_pool, (VOID **) &pointer, APP_STACK_SIZE, TX_NO_WAIT) != TX_SUCCESS)
	{
		ret = TX_POOL_ERROR;
	}

	/* Create ThreadOne.  */
	if (tx_thread_create(&LEDThread1Handle,
		"Thread1",
		LED_Thread1,
		0,
		pointer,
		APP_STACK_SIZE,
		THREAD_ONE_PRIO,
		THREAD_ONE_PREEMPTION_THRESHOLD,
		DEFAULT_TIME_SLICE,
		TX_AUTO_START) != TX_SUCCESS)
	{
		ret = TX_THREAD_ERROR;
	}

	/* Allocate the stack for ThreadTwo.  */
	if (tx_byte_allocate(byte_pool, (VOID **) &pointer, APP_STACK_SIZE, TX_NO_WAIT) != TX_SUCCESS)
	{
		ret = TX_POOL_ERROR;
	}

	/* Create ThreadTwo.  */
	if (tx_thread_create(&LEDThread2Handle,
		"Thread2",
		LED_Thread2,
		0,
		pointer,
		APP_STACK_SIZE,
		THREAD_TWO_PRIO,
		THREAD_TWO_PREEMPTION_THRESHOLD,
		DEFAULT_TIME_SLICE,
		TX_AUTO_START) != TX_SUCCESS)
	{
		ret = TX_THREAD_ERROR;
	}

	return ret;
}

int g_TickCount;

void TickTest()
{
	g_TickCount++;
}

/**
  * @brief  Main program
  * @param  None
  * @retval None
  */
int main(void)
{
	/* STM32F4xx HAL library initialization:
	     - Configure the Flash prefetch, instruction and Data caches
	     - Configure the Systick to generate an interrupt each 1 msec
	     - Set NVIC Group Priority to 4
	     - Global MSP (MCU Support Package) initialization
	*/
	HAL_Init();  
	
	__$$com.sysprogs.examples.stm32.LEDPORT$$_CLK_ENABLE();
	GPIO_InitTypeDef GPIO_InitStructure;

	GPIO_InitStructure.Pin = GPIO_PIN_$$com.sysprogs.examples.stm32.LED1BIT$$ | GPIO_PIN_$$com.sysprogs.examples.stm32.LED2BIT$$;

	GPIO_InitStructure.Mode = GPIO_MODE_OUTPUT_PP;
	GPIO_InitStructure.Speed = GPIO_SPEED_FREQ_HIGH;
	GPIO_InitStructure.Pull = GPIO_NOPULL;
	HAL_GPIO_Init($$com.sysprogs.examples.stm32.LEDPORT$$, &GPIO_InitStructure);

	/* Start scheduler */
	tx_kernel_enter();

	/* We should never get here as control is now taken by the scheduler */
	for (;;) ;
}

/**
  * @brief  Toggle LED1
  * @param  thread not used
  * @retval None
  */
static void LED_Thread1(ULONG argument)
{
	(void) argument;
  
	for (;;)
	{
		HAL_GPIO_WritePin($$com.sysprogs.examples.stm32.LEDPORT$$, GPIO_PIN_$$com.sysprogs.examples.stm32.LED1BIT$$, GPIO_PIN_SET);
		tx_thread_sleep(2000);
		
		HAL_GPIO_WritePin($$com.sysprogs.examples.stm32.LEDPORT$$, GPIO_PIN_$$com.sysprogs.examples.stm32.LED1BIT$$, GPIO_PIN_RESET);
		tx_thread_suspend(&LEDThread2Handle);
		tx_thread_sleep(2000);
		
		tx_thread_resume(&LEDThread2Handle);
	}
}

/**
  * @brief  Toggle LED2 thread
  * @param  argument not used
  * @retval None
  */
static void LED_Thread2(ULONG argument)
{
	uint32_t count;
	(void) argument;
  
	for (;;)
	{
		HAL_GPIO_TogglePin($$com.sysprogs.examples.stm32.LEDPORT$$, GPIO_PIN_$$com.sysprogs.examples.stm32.LED2BIT$$);
		tx_thread_sleep(200);
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
