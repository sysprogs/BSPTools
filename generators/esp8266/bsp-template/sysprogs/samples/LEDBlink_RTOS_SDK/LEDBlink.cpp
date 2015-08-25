#ifdef __cplusplus
extern "C"
{
#endif
	
#include <esp_common.h>

#include <freertos/FreeRTOS.h>
#include <freertos/task.h>
#include <pin_mux_register.h>
#include <gpio.h>
#include <uart.h>
	
void user_init(void);
	
#ifdef __cplusplus
}
#endif

#define RAMFUNC __attribute__((section(".entry.text")))
	
static void RAMFUNC LEDBlinkTask(void *pvParameters)
{
	for (int tick = 0;;tick++)
	{
		vTaskDelay($$com.sysprogs.esp8266.ledblink.DELAYMSEC$$ / portTICK_RATE_MS);
		gpio_output_conf(0, BIT1, BIT1, 0);
		vTaskDelay($$com.sysprogs.esp8266.ledblink.DELAYMSEC$$ / portTICK_RATE_MS);
		gpio_output_conf(BIT1, 0, BIT1, 0);
	}
}

//Unless you explicitly define the functions as RAMFUNC, they will be placed in the SPI FLASH and the debugger
//won't be able to set software breakpoints there.
void RAMFUNC user_init(void)  
{
	PIN_FUNC_SELECT(PERIPHS_IO_MUX_U0TXD_U, FUNC_GPIO1);
	gpio_output_conf(0, BIT1, BIT1, 0);

	xTaskCreate(LEDBlinkTask, (signed char *)"LEDBlinkTask", 256, NULL, 2, NULL);
}

