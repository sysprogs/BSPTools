#include <c_types.h>
#include <gpio.h>
#include <portmacro.h>

const int PinNumber = $$com.sysprogs.esp32.ledblink.LEDPIN$$;

void vTaskEnterCritical(void)
{
}

void vTaskExitCritical(void)
{
}

void delay(int cycles)
{
    volatile int i;
    for (i = 0; i < cycles; i++) {}
}

extern "C" int  flashless_entry()
{
    GPIO_ConfigTypeDef cfg;
    
    cfg.GPIO_Pin = PinNumber;
    cfg.GPIO_Pin_high = 0;
    cfg.GPIO_Mode = GPIO_Mode_Output;
    cfg.GPIO_IntrType = GPIO_PIN_INTR_DISABLE;
    cfg.GPIO_Pulldown = GPIO_PullDown_DIS;
    cfg.GPIO_Pullup = GPIO_PullUp_DIS;
    
    gpio_config(&cfg);
    
    for (;;)
    {
        GPIO_OUTPUT_SET(PinNumber, 1);
        delay($$com.sysprogs.esp32.ledblink.DELAYCYCLES$$);
        GPIO_OUTPUT_SET(PinNumber, 0);
        delay($$com.sysprogs.esp32.ledblink.DELAYCYCLES$$);
    }
    
    return 0;
}