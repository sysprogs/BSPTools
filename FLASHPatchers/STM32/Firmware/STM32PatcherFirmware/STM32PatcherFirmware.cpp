#if defined (STM32F4)
#include <stm32f4xx_hal.h>
#elif defined (STM32F7)
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
#elif defined (STM32H5)
#include <stm32h5xx_hal.h>
#elif defined (STM32H7)
#include <stm32h7xx_hal.h>
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
	
#if defined (STM32H7)
	FLASH_WaitForLastOperation(HAL_MAX_DELAY, FLASH_BANK_1);
	FLASH_WaitForLastOperation(HAL_MAX_DELAY, FLASH_BANK_2);
#else
	FLASH_WaitForLastOperation(HAL_MAX_DELAY);
#endif
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
#ifdef FLASH_VOLTAGE_RANGE_1
	erase.VoltageRange = FLASH_VOLTAGE_RANGE_1;
#elif !defined(STM32H5)
	erase.VoltageRange = 0;
#endif
#endif

#ifdef FLASH_BANK_1
	erase.Banks = bank;
#endif
	return HAL_FLASHEx_Erase(&erase, &error);
}

int FLASHPatcher_ProgramWords(int bank, void *address, const uint32_t *words, int wordCount)
{
#if defined (FLASH_TYPEPROGRAM_WORD)
	for (int i = 0; i < wordCount; i++)
	{
		int st = HAL_FLASH_Program(FLASH_TYPEPROGRAM_WORD, (uint32_t)address + i * 4, words[i]);
		if (st)
			return st;
	}
	return 0;
#elif defined (FLASH_TYPEPROGRAM_DOUBLEWORD)
	const int BurstSize = 2;
	if (wordCount % BurstSize)
		return -10;
	
	for (int i = 0; i < wordCount; i += BurstSize)
	{
		int st = HAL_FLASH_Program(FLASH_TYPEPROGRAM_DOUBLEWORD, (uint32_t)address + i * 4, ((uint64_t)words[i + 1] << 32) | words[i]);
		if (st)
			return st;
	}
	return 0;
#elif defined (STM32H7)
	HAL_StatusTypeDef HAL_FLASH_ProgramEx(uint32_t bank, uint32_t TypeProgram, uint32_t FlashAddress, uint32_t DataAddress);
	const int BurstSize = 8;
	if (wordCount % BurstSize)
		return -10;
	
	for (int i = 0; i < wordCount; i += BurstSize)
	{
		int st = HAL_FLASH_ProgramEx(bank, FLASH_TYPEPROGRAM_FLASHWORD, (uint32_t)address + i * 4, (uint32_t)words + i * 4);
		if (st)
			return st;
	}
	return 0;
#else
	const int BurstSize = 4;
	if (wordCount % BurstSize)
		return -10;
	
	for (int i = 0; i < wordCount; i += BurstSize)
	{
		int st = HAL_FLASH_Program(FLASH_TYPEPROGRAM_QUADWORD, (uint32_t)address + i * 4, (uint32_t)words + i * 4);
		if (st)
			return st;
	}
	return 0;
#endif
}

extern "C" void __attribute__((weak)) FLASH_FlushCaches()
{
}

int FLASHPatcher_Complete()
{
#ifdef CORE_CM7
	SCB_InvalidateICache();
	SCB_InvalidateDCache(); 
#endif
	FLASH_FlushCaches();
	return 0;
}

extern "C" uint32_t HAL_GetTick(void)
{
	return 0;
}