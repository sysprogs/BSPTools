/*
	This file is responsible for generating interrupt veneers for XMC1xxx devices.
	The veneers are required because the Cortex M0 core expects the interrupt vector table to be at address 0x00000000 
	that is not a part of FLASH or RAM on XMC1xxx devices. Instead the device ROM provides a fixed vector table that expects
	the interrupt handlers to be placed each 4 bytes starting at 0x20000000.
	
	Each "veneer" is a set of 2 instructions:
		ldr r0, [pc, #<offset of ISR table slot>]
		mov pc, r0
		
	The actual ISR table immediately follows the veneers. Because the size of each veneer matches the size of a table entry,
	the offset between the veneer and the ISR table slot it access is always the same and is equal to the vector count times vector size.
	
	The code below copies the ISR table from the FLASH into the SRAM and builds the veneers.
*/

void InitializeInteruptVeneers()
{
    extern void *__isr_vector_start__, *__isr_vector_end__;
    extern void *__isr_veneers_start__;
    unsigned vectorCount = &__isr_vector_end__ - &__isr_vector_start__;
    
    for (int i = 0; i < vectorCount; i++)
    {
        (&__isr_veneers_start__)[i] = (void *)(0x46874800 | (vectorCount - 1));    //ldr r0, [pc, #<vector count>]; mov pc, r0
        (&__isr_veneers_start__)[i + vectorCount] = (&__isr_vector_start__)[i];
    }
}
