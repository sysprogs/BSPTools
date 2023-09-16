#if defined (STM32F7)
#include <stm32f7xx_hal.h>
#elif defined (STM32U5)
#include <stm32u5xx_hal.h>
#else
#error Unknown device family
#endif

#include <stm32_hal_legacy.h>
#include "../FLASHPatcherAPI.h"

int FLASHPatcher_Init()
{
	int st = HAL_FLASH_Unlock();
	if (st != HAL_OK)
		return st;
	
	FLASH_WaitForLastOperation(HAL_MAX_DELAY);
	return HAL_OK;
}

int FLASHPatcher_EraseSectors(int bank, int firstSector, int count)
{
	FLASH_EraseInitTypeDef erase = { 0, };
	uint32_t error;
#ifdef FLASH_TYPEERASE_PAGES
	erase.TypeErase = FLASH_TYPEERASE_PAGES;
	erase.Page = firstSector;
	erase.NbPages= count;
	erase.Banks = bank;
#else
	erase.TypeErase = FLASH_TYPEERASE_SECTORS;
	erase.Sector = firstSector;
	erase.NbSectors = count;
	erase.VoltageRange = FLASH_VOLTAGE_RANGE_1;
#endif
	return HAL_FLASHEx_Erase(&erase, &error);
}

int FLASHPatcher_ProgramQWord(void *address, uint32_t lo, uint32_t hi)
{
	int st = HAL_FLASH_Program(FLASH_TYPEPROGRAM_WORD, (uint32_t)address, lo);
	if (st)
		return st;
	return HAL_FLASH_Program(FLASH_TYPEPROGRAM_WORD, (uint32_t)address + 4, hi);
	
	//return HAL_FLASH_Program(FLASH_TYPEPROGRAM_DOUBLEWORD, (uint32_t)address, (((uint64_t)hi) << 32) | lo);
}

extern "C" void __attribute__((weak)) FLASH_FlushCaches()
{
}

int FLASHPatcher_Complete()
{
	FLASH_FlushCaches();
	return 0;
}