#include <sys/types.h>

extern "C"
{
	int  __attribute__((noinline, noclone)) FLASHPatcher_Init();
	int __attribute__((noinline, noclone)) FLASHPatcher_EraseSectors(int firstSector, int count);
	int __attribute__((noinline, noclone)) FLASHPatcher_ProgramWord(void *address, uint32_t word);
	int __attribute__((noinline, noclone)) FLASHPatcher_Complete();
}
