#include <em_chip.h>
#include <em_cmu.h>

volatile uint32_t TickCount = 0;

#ifdef __cplusplus
extern "C"
#endif
void SysTick_Handler(void)
{
    TickCount++; 
}

static void Delay(uint32_t delayInTicks)
{
    uint32_t curTicks;

    curTicks = TickCount;
    while ((TickCount - curTicks) < delayInTicks)
    {
    }
} 

int main()
{
    CHIP_Init();

    SysTick_Config(CMU_ClockFreqGet(cmuClock_CORE) / 1000);
    CMU_ClockEnable(cmuClock_GPIO, true);
    GPIO_PinModeSet(gpio$$com.sysprogs.examples.ledblink.LEDPORT$$, $$com.sysprogs.examples.ledblink.LEDBIT$$, gpioModePushPull, 1);

    for (;;)
    {
        GPIO_PinOutSet(gpio$$com.sysprogs.examples.ledblink.LEDPORT$$, $$com.sysprogs.examples.ledblink.LEDBIT$$);
        Delay($$com.sysprogs.examples.ledblink.DELAYTICKS$$);
        GPIO_PinOutClear(gpio$$com.sysprogs.examples.ledblink.LEDPORT$$, $$com.sysprogs.examples.ledblink.LEDBIT$$);
        Delay($$com.sysprogs.examples.ledblink.DELAYTICKS$$);
    }
    return 0;
}
