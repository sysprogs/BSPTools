#include <sys/types.h>

extern "C"
{
	int  __attribute__((noinline, noclone)) FLASHPatcher_Init();
	int __attribute__((noinline, noclone)) FLASHPatcher_EraseSectors(int bank, int firstSector, int count);
	int __attribute__((noinline, noclone)) FLASHPatcher_ProgramQWord(void *address, uint32_t lo, uint32_t hi);
	int __attribute__((noinline, noclone)) FLASHPatcher_Complete();
}
