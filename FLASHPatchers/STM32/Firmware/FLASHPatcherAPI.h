#include <sys/types.h>

extern "C"
{
	int  __attribute__((noinline, noclone)) FLASHPatcher_Init();
	int __attribute__((noinline, noclone)) FLASHPatcher_EraseSectors(int bank, int firstSector, int count);
	int __attribute__((noinline, noclone)) FLASHPatcher_ProgramWords(int bank, void *address, const uint32_t *words, int wordCount);
	int __attribute__((noinline, noclone)) FLASHPatcher_Complete();
}
