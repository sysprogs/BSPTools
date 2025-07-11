/*
 * Copyright (c) 2023, Texas Instruments Incorporated
 * All rights reserved.
 *
 * Redistribution and use in source and binary forms, with or without
 * modification, are permitted provided that the following conditions
 * are met:
 *
 * *  Redistributions of source code must retain the above copyright
 *    notice, this list of conditions and the following disclaimer.
 *
 * *  Redistributions in binary form must reproduce the above copyright
 *    notice, this list of conditions and the following disclaimer in the
 *    documentation and/or other materials provided with the distribution.
 *
 * *  Neither the name of Texas Instruments Incorporated nor the names of
 *    its contributors may be used to endorse or promote products derived
 *    from this software without specific prior written permission.
 *
 * THIS SOFTWARE IS PROVIDED BY THE COPYRIGHT HOLDERS AND CONTRIBUTORS "AS IS"
 * AND ANY EXPRESS OR IMPLIED WARRANTIES, INCLUDING, BUT NOT LIMITED TO,
 * THE IMPLIED WARRANTIES OF MERCHANTABILITY AND FITNESS FOR A PARTICULAR
 * PURPOSE ARE DISCLAIMED. IN NO EVENT SHALL THE COPYRIGHT OWNER OR
 * CONTRIBUTORS BE LIABLE FOR ANY DIRECT, INDIRECT, INCIDENTAL, SPECIAL,
 * EXEMPLARY, OR CONSEQUENTIAL DAMAGES (INCLUDING, BUT NOT LIMITED TO,
 * PROCUREMENT OF SUBSTITUTE GOODS OR SERVICES; LOSS OF USE, DATA, OR PROFITS;
 * OR BUSINESS INTERRUPTION) HOWEVER CAUSED AND ON ANY THEORY OF LIABILITY,
 * WHETHER IN CONTRACT, STRICT LIABILITY, OR TORT (INCLUDING NEGLIGENCE OR
 * OTHERWISE) ARISING IN ANY WAY OUT OF THE USE OF THIS SOFTWARE,
 * EVEN IF ADVISED OF THE POSSIBILITY OF SUCH DAMAGE.
 */

#include <ti/devices/msp/msp.h>
#include <ti/driverlib/driverlib.h>
#include <ti/driverlib/m0p/dl_core.h>

/* This results in approximately 0.5s of delay assuming 32MHz CPU_CLK */
#define DELAY ($$com.sysprogs.examples.ledblink.DELAY$$)

#define POWER_STARTUP_DELAY                                                (16)

#ifdef __cplusplus
extern "C"
{
#endif
void SYSCFG_DL_init(void);
void SYSCFG_DL_initPower(void);
void SYSCFG_DL_GPIO_init(void);
void SYSCFG_DL_SYSCTL_init(void);

#ifdef __cplusplus
}
#endif


int main(void)
{
	/* Power on GPIO, initialize pins as digital outputs */
	SYSCFG_DL_init();

	/* Default: LED1 and LED3 ON, LED2 OFF */
	DL_GPIO_clearPins($$com.sysprogs.examples.ledblink.LEDPORT$$, DL_GPIO_PIN_$$com.sysprogs.examples.ledblink.LEDBIT$$);

	while (1) {
		delay_cycles(DELAY);
		DL_GPIO_togglePins($$com.sysprogs.examples.ledblink.LEDPORT$$, DL_GPIO_PIN_$$com.sysprogs.examples.ledblink.LEDBIT$$);
	}
}


#define SYSCONFIG_WEAK __attribute__((weak))

SYSCONFIG_WEAK void SYSCFG_DL_init(void)
{
	SYSCFG_DL_initPower();
	SYSCFG_DL_GPIO_init();
	/* Module-Specific Initializations*/
	SYSCFG_DL_SYSCTL_init();
}

SYSCONFIG_WEAK void SYSCFG_DL_initPower(void)
{
	DL_GPIO_reset($$com.sysprogs.examples.ledblink.LEDPORT$$);
	DL_GPIO_enablePower($$com.sysprogs.examples.ledblink.LEDPORT$$);
	delay_cycles(POWER_STARTUP_DELAY);
}

SYSCONFIG_WEAK void SYSCFG_DL_GPIO_init(void)
{
	DL_GPIO_initDigitalOutput(IOMUX_PINCM$$com.sysprogs.examples.ledblink.IOMUX$$);
	
	DL_GPIO_clearPins($$com.sysprogs.examples.ledblink.LEDPORT$$, DL_GPIO_PIN_$$com.sysprogs.examples.ledblink.LEDBIT$$);
	DL_GPIO_enableOutput($$com.sysprogs.examples.ledblink.LEDPORT$$, DL_GPIO_PIN_$$com.sysprogs.examples.ledblink.LEDBIT$$);
}


SYSCONFIG_WEAK void SYSCFG_DL_SYSCTL_init(void)
{
	DL_SYSCTL_setSYSOSCFreq(DL_SYSCTL_SYSOSC_FREQ_BASE);
	/* Set default configuration */
	DL_SYSCTL_disableHFXT();
	DL_SYSCTL_disableSYSPLL();
}
