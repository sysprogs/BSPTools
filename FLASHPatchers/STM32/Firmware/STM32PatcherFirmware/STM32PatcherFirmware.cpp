#include <stm32f4xx_hal.h>
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

int FLASHPatcher_EraseSectors(int firstSector, int count)
{
	FLASH_EraseInitTypeDef erase;
	uint32_t error;
	erase.TypeErase = FLASH_TYPEERASE_SECTORS;
	erase.Sector = firstSector;
	erase.NbSectors = count;
	erase.VoltageRange = FLASH_VOLTAGE_RANGE_1;
	return HAL_FLASHEx_Erase(&erase, &error);
}

int FLASHPatcher_ProgramWord(void *address, uint32_t word)
{
	return HAL_FLASH_Program(FLASH_TYPEPROGRAM_WORD, (uint32_t)address, word);
}

int FLASHPatcher_Complete()
{
	FLASH_FlushCaches();
	return 0;
}