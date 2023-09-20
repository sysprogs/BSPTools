#if defined (STM32F7)
#include <stm32f7xx_hal.h>
#elif defined (STM32U5)
#include <stm32u5xx_hal.h>
#elif defined (STM32L4)
#include <stm32l4xx_hal.h>
#elif defined (STM32L5)
#include <stm32l5xx_hal.h>
#elif defined (STM32G0)
#include <stm32g0xx_hal.h>
#elif defined (STM32C0)
#include <stm32c0xx_hal.h>
#elif defined (STM32F0)
#include <stm32f0xx_hal.h>
#elif defined (STM32F1)
#include <stm32f1xx_hal.h>
#elif defined (STM32L0)
#include <stm32l0xx_hal.h>
#elif defined (STM32L1)
#include <stm32l1xx_hal.h>
#elif defined (STM32WL)
#include <stm32wlxx_hal.h>
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
#if defined(STM32F0) || defined (STM32F1) || defined(STM32L0) || defined(STM32L1)
	erase.PageAddress = firstSector;
#else
	erase.Page = firstSector;
#endif
	erase.NbPages = count;
#else
	erase.TypeErase = FLASH_TYPEERASE_SECTORS;
	erase.Sector = firstSector;
	erase.NbSectors = count;
	erase.VoltageRange = FLASH_VOLTAGE_RANGE_1;
#endif

#ifdef FLASH_BANK_1
	erase.Banks = bank;
#endif
	return HAL_FLASHEx_Erase(&erase, &error);
}

int FLASHPatcher_ProgramQWord(void *address, const uint32_t *words)
{
#ifdef FLASH_TYPEPROGRAM_WORD
	for (int i = 0; i < 4; i++)
	{
		int st = HAL_FLASH_Program(FLASH_TYPEPROGRAM_WORD, (uint32_t)address + i * 4, words[i]);
		if (st)
			return st;
	}
	return 0;
#elif defined (FLASH_TYPEPROGRAM_DOUBLEWORD)
	int st = HAL_FLASH_Program(FLASH_TYPEPROGRAM_DOUBLEWORD, (uint32_t)address, ((uint64_t)words[1] << 32) | words[0]);
	if (st)
		return st;
	return HAL_FLASH_Program(FLASH_TYPEPROGRAM_DOUBLEWORD, (uint32_t)address + 8, ((uint64_t)words[3] << 32) | words[2]);
#else
	return HAL_FLASH_Program(FLASH_TYPEPROGRAM_QUADWORD, (uint32_t)address, (uint32_t)words);
#endif
}

extern "C" void __attribute__((weak)) FLASH_FlushCaches()
{
}

int FLASHPatcher_Complete()
{
	FLASH_FlushCaches();
	return 0;
}