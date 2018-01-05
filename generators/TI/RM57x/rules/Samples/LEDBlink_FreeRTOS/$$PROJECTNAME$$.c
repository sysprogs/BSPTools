/** @example example_freeRTOSBlinky.c
*  This is an example which descibes the steps to create an example application which 
*  toggles the High End Timer (HET) pin 17 (LED in USB & HDK) based on the FreeRTOS timer tick of one second.
*
*  @b Step @b 1:
*
*  Create a new project.
*
*  Navigate: -> File -> New -> Project
*
*  @image html example_createProject.jpg "Figure: Create a new Project"
*
*  @b Step @b 2:
*
*  Configure driver code generation: 
*  - Enable GIO driver
*  - Disable others
*
*  Navigate: -> TMS570LCxx /RM5 -> Driver Enable
*
*  @image html example_freeRTOSBlinky_enableDrivers_TMS570LS3x-RMx.jpg "Figure: Driver Configuration"
*
*  @b Step @b 3:
*
*  Configure Interrupt handling: 
*  - Enable SVC
*  - Enter FreeRTOS SVC handler name 'vPortSWI'
*
*  Navigate: -> TMS570LCxx /RM5 -> Interrupts
*
*  @image html example_freeRTOSBlinky_interrupts.jpg "Figure: Interrupt Configuration"
*
*  @b Step @b 4:
*
*  Configure VIM RAM: 
*  - Enter FreeRTOS Timer Tick handler name 'vPortPreemptiveTick' at offset 0x0000000C
*  - Enter SSI handler name 'vPortYeildWithinAPI ' at offset 0x00000058
*
*  Navigate: -> TMS570LCxx /RM5 -> VIM RAM
*
*  @image html example_freeRTOSBlinky_VimRam.jpg "Figure: VIM RAM Configuration"
*
*  @b Step @b 5:
*
*  Configure Vectored Interrupt Module Channels: 
*  - Enable VIM Channel 2
*  - Map VIM Channel 2 to IRQ
*  - Enable VIM Channel 21
*  - Map VIM Channel 21 to IRQ
*
*  Navigate: -> TMS570LCxx /RM5 -> VIM Channel 0-31
*
*  @image html example_freeRTOSBlinky_vimChannelView.jpg "Figure: VIM Channel Configuration"
*
*  @b Step @b 6:
*
*  Configure OS timer tick to 1 ms: 
*  - Enter Tick Rate of 1000
*
*  Navigate: -> OS -> General
*
*  @image html example_freeRTOSBlinky_osGeneral.jpg "Figure: OS General Configuration"
*
*  @b Step @b 7:
*
*  Generate code
*
*  Navigate: -> File -> Generate Code
*
*  @image html example_freeRTOS_generateCode.jpg "Figure: Generate Code"
*
*  @b Step @b 8:
*
*  Copy source code below into your application.
*
*  The example file example_freeRTOSBlinky.c can also be found in the examples folder: ../HALCoGen/examples
*
*  @note HALCoGen generates an enpty main function in sys_main.c, 
*        please make sure that you link in the right main function or copy the source into the user code sections of this file.
*
*  @note Enable GCC extension in the CCS project (Project properties -> Build -> ARM Compiler -> Advanced options -> Language options -> Enable support for GCC extensions)
*
*
*/

/* 
* Copyright (C) 2009-2015 Texas Instruments Incorporated - www.ti.com
* 
* 
*  Redistribution and use in source and binary forms, with or without 
*  modification, are permitted provided that the following conditions 
*  are met:
*
*    Redistributions of source code must retain the above copyright 
*    notice, this list of conditions and the following disclaimer.
*
*    Redistributions in binary form must reproduce the above copyright
*    notice, this list of conditions and the following disclaimer in the 
*    documentation and/or other materials provided with the   
*    distribution.
*
*    Neither the name of Texas Instruments Incorporated nor the names of
*    its contributors may be used to endorse or promote products derived
*    from this software without specific prior written permission.
*
*  THIS SOFTWARE IS PROVIDED BY THE COPYRIGHT HOLDERS AND CONTRIBUTORS 
*  "AS IS" AND ANY EXPRESS OR IMPLIED WARRANTIES, INCLUDING, BUT NOT 
*  LIMITED TO, THE IMPLIED WARRANTIES OF MERCHANTABILITY AND FITNESS FOR
*  A PARTICULAR PURPOSE ARE DISCLAIMED. IN NO EVENT SHALL THE COPYRIGHT 
*  OWNER OR CONTRIBUTORS BE LIABLE FOR ANY DIRECT, INDIRECT, INCIDENTAL, 
*  SPECIAL, EXEMPLARY, OR CONSEQUENTIAL DAMAGES INCLUDING, BUT NOT 
*  LIMITED TO, PROCUREMENT OF SUBSTITUTE GOODS OR SERVICES; LOSS OF USE,
*  DATA, OR PROFITS; OR BUSINESS INTERRUPTION HOWEVER CAUSED AND ON ANY
*  THEORY OF LIABILITY, WHETHER IN CONTRACT, STRICT LIABILITY, OR TORT 
*  INCLUDING NEGLIGENCE OR OTHERWISE ARISING IN ANY WAY OUT OF THE USE 
*  OF THIS SOFTWARE, EVEN IF ADVISED OF THE POSSIBILITY OF SUCH DAMAGE.
*
*/

/* USER CODE BEGIN (0) */
/* USER CODE END */

/* Include Files */

#include "HL_sys_common.h"

/* USER CODE BEGIN (1) */
/* Include FreeRTOS scheduler files */
#include "FreeRTOS.h"
#include "os_task.h"

/* Include HET header file - types, definitions and function declarations for system driver */
#include "HL_het.h"
#include "HL_gio.h"

/* Define Task Handles */
xTaskHandle xTask1Handle;

/* Task1 */
void vTask1(void *pvParameters)
{
    for(;;)
    {
        /* Taggle HET[1] with timer tick */
        gioToggleBit(gioPORTB, 6);
        vTaskDelay(100);
    }   
}
/* USER CODE END */


/** @fn void main(void)
*   @brief Application main function
*
*/

/* USER CODE BEGIN (2) */
/* USER CODE END */


int main(void)
{
/* USER CODE BEGIN (3) */
    
    /* Set high end timer GIO port hetPort pin direction to all output */
    gioInit();
    gioSetDirection(gioPORTB, (1 << 6) | (1 << 7));


    /* Create Task 1 */
    if (xTaskCreate(vTask1,"Task1", configMINIMAL_STACK_SIZE, NULL, 1, &xTask1Handle) != pdTRUE)
    {
        /* Task could not be created */
        while(1);
    }

    /* Start Scheduler */
    vTaskStartScheduler();

    /* Run forever */
    while(1);
/* USER CODE END */
}


/* USER CODE BEGIN (4) */
/* USER CODE END */
