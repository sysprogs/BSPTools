/*
	This file contains the entry point (Reset_HandlerSys) of your firmware project.
	The reset handled initializes the RAM and calls system library initializers as well as
	the platform-specific initializer and the main() function.
*/

#include <stddef.h>
extern void *_estack;

void Reset_HandlerSys();
void Default_HandlerIRQ();

#ifdef DEBUG_DEFAULT_INTERRUPT_HANDLERS
void __attribute__ ((weak)) $$VECTOR$$() $@+7
{
	//If you hit the breakpoint below, one of the interrupts was unhandled in your code. 
	//Define the following function in your code to handle it:
	//	extern "C" void $$VECTOR$$();
	asm("bkpt 255");
}

#else
void $$VECTOR$$() $$ALIGN_SPACE_OFFSET$$ __attribute__ ((weak, alias ("Default_HandlerIRQ")));
#endif

void * g_pfnVectors[$$VECTOR_TABLE_SIZE$$] __attribute__ ((section (".isr_vector"), used)) = 
{
	&_estack,
	&Reset_HandlerSys,
	$$VECTOR_POINTER$$,
};

void SystemInit();
void __libc_init_array();
int main();

extern void *_sidata, *_sdata, *_edata;
extern void *_sbss, *_ebss;

void __attribute__((naked, noreturn)) Reset_HandlerSys()
{
	$$EXTRA_RESET_HANDLER_CODE$$
	//Normally the CPU should will setup the based on the value from the first entry in the vector table.
	//If you encounter problems with accessing stack variables during initialization, ensure the line below is enabled.
	#ifdef sram_layout
	asm ("ldr sp, =_estack");
	#endif

	void **pSource, **pDest;
	for (pSource = &_sidata, pDest = &_sdata; pDest < &_edata; pSource++, pDest++)
		*pDest = *pSource;

	for (pDest = &_sbss; pDest < &_ebss; pDest++)
		*pDest = 0;

	SystemInit();
	__libc_init_array();
	(void)main();
	for (;;) ;
}

void __attribute__((naked, noreturn)) Default_HandlerIRQ()
{
	//If you get stuck here, your code is missing a handler for some interrupt.
	//Define a 'DEBUG_DEFAULT_INTERRUPT_HANDLERS' macro via VisualGDB Project Properties and rebuild your project.
	//This will pinpoint a specific missing vector.
	for (;;) ;
}
