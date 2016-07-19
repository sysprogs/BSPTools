#include <errno.h>
#include <sys/types.h>

#ifndef FIXED_STACK_SIZE
#error Please define FIXED_STACK_SIZE via VisualGDB Project Properties
#endif

#ifndef FIXED_HEAP_SIZE
#error Please define FIXED_HEAP_SIZE via VisualGDB Project Properties
#endif

/*
	The variable below only reserves the space for the stack (i.e. ensures a linker error if there is not enough space).
	The actual stack will be still located at the end of SRAM.
	
	VisualGDB 5.2 and later can dynamically check the stack pointer and trigger an exception if it exceeds the size	specified here.
	This needs dynamic analysis to be enabled for the project.
*/
char __attribute__((section(".reserved_for_stack"))) ReservedForStack[FIXED_STACK_SIZE];

/*
	The variable below will be used to store the heap.
*/
char __attribute__((section(".heap"))) FixedSizeHeap[FIXED_HEAP_SIZE];

/*
	This function is used by the malloc() function to gradually extend the heap area.
	Normally it starts right after the "end" symbol and is extended until it collides with the stack pointer.
*/
caddr_t _sbrk(int incr)
{
    static char * heap_end;
    char * prev_heap_end;

    if (heap_end == NULL)
        heap_end = FixedSizeHeap;

    prev_heap_end = heap_end;

    if (heap_end + incr > (FixedSizeHeap + sizeof(FixedSizeHeap)))
    {
        errno = ENOMEM;
        return (caddr_t) - 1;
    }

    heap_end += incr;

    return (caddr_t) prev_heap_end;
}