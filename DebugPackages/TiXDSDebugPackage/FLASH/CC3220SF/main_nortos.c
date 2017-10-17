/*
 * Copyright (c) 2017, Texas Instruments Incorporated
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

/*
 *  ======== main_nortos.c ========
 */
#include <stdint.h>

#include <NoRTOS.h>
#include <ti/drivers/Power.h>
#include "inc/hw_types.h"
#include "inc/hw_flash_ctrl.h"
#include "driverlib/flash.h"

/* Example/Board Header files */
#include "Board.h"

extern void *mainThread(void *arg0);

void *_stack_end;

static volatile unsigned s_ProgramBuffer[65536 / 4];
volatile unsigned g_Result;

struct AddressTable
{
    unsigned Signature;
    unsigned LoadAddress;
    unsigned EndOfStack;
    unsigned ProgramBuffer, ProgramBufferSize;
    
    void *EntryPoint;
    void *Result;
};

enum ProgramCommand
{
    EraseAll,
    ErasePages,
    ProgramPages,
};

static void ProgramEntry(enum ProgramCommand cmd, unsigned arg1, unsigned arg2)
{
    unsigned off;
    g_Result = -1024;
    switch (cmd)
    {
    case EraseAll:
        g_Result = FlashMassErase();
        break;
    case ErasePages:
        for (off = 0; off < arg2; off += FLASH_CTRL_ERASE_SIZE)
        {
            g_Result = FlashErase((arg1 + off) & ~(FLASH_CTRL_ERASE_SIZE - 1));
            if (g_Result)
                break;
        }
        break;
    case ProgramPages:
        g_Result = FlashProgram((unsigned long *)s_ProgramBuffer, arg1, arg2);
    }
    
    asm("bkpt 255");
}

volatile struct AddressTable g_AddressTable = 
{ 
    .Signature = 0x769730AE,
    .EndOfStack = (unsigned)&_stack_end,
    .LoadAddress = 0x20004000,
    .ProgramBuffer = (unsigned)&s_ProgramBuffer,
    .ProgramBufferSize = (unsigned)sizeof(s_ProgramBuffer),
    .EntryPoint = ProgramEntry,
    .Result = (void *)&g_Result,
};


int main(void)
{
    volatile unsigned unused = g_AddressTable.EndOfStack;
    for (;;)
        ;
}
