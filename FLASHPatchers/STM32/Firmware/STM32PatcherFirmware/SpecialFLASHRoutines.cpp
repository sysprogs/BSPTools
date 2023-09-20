#if defined (STM32H7)
#include <stm32h7xx_hal.h>

HAL_StatusTypeDef HAL_FLASH_ProgramEx(uint32_t bank, uint32_t TypeProgram, uint32_t FlashAddress, uint32_t DataAddress)
{
	HAL_StatusTypeDef status;
	__IO uint32_t *dest_addr = (__IO uint32_t *)FlashAddress;
	__IO uint32_t *src_addr = (__IO uint32_t*)DataAddress;
	uint8_t row_index = FLASH_NB_32BITWORD_IN_FLASHWORD;

	/* Check the parameters */
	assert_param(IS_FLASH_TYPEPROGRAM(TypeProgram));
	assert_param(IS_FLASH_PROGRAM_ADDRESS(FlashAddress));

	/* Process Locked */
	__HAL_LOCK(&pFlash);

	/* Reset error code */
	pFlash.ErrorCode = HAL_FLASH_ERROR_NONE;

	/* Wait for last operation to be completed */
	status = FLASH_WaitForLastOperation((uint32_t)HAL_MAX_DELAY, bank);

	if (status == HAL_OK)
	{
#if defined (DUAL_BANK)
		if (bank == FLASH_BANK_1)
		{
#if defined (FLASH_OPTCR_PG_OTP)
			if (TypeProgram == FLASH_TYPEPROGRAM_OTPWORD)
			{
				/* Set OTP_PG bit */
				SET_BIT(FLASH->OPTCR, FLASH_OPTCR_PG_OTP);
			}
			else
#endif /* FLASH_OPTCR_PG_OTP */
			{
				/* Set PG bit */
				SET_BIT(FLASH->CR1, FLASH_CR_PG);
			}
		}
		else
		{
			/* Set PG bit */
			SET_BIT(FLASH->CR2, FLASH_CR_PG);
		}
#else /* Single Bank */
#error Dual-bank support should be enabled
#if defined (FLASH_OPTCR_PG_OTP)
		if (TypeProgram == FLASH_TYPEPROGRAM_OTPWORD)
		{
			/* Set OTP_PG bit */
			SET_BIT(FLASH->OPTCR, FLASH_OPTCR_PG_OTP);
		}
		else
#endif /* FLASH_OPTCR_PG_OTP */
		{
			/* Set PG bit */
			SET_BIT(FLASH->CR1, FLASH_CR_PG);
		}
#endif /* DUAL_BANK */

		__ISB();
		__DSB();

#if defined (FLASH_OPTCR_PG_OTP)
		if (TypeProgram == FLASH_TYPEPROGRAM_OTPWORD)
		{
			/* Program an OTP word (16 bits) */
			*(__IO uint16_t *)FlashAddress = *(__IO uint16_t*)DataAddress;
		}
		else
#endif /* FLASH_OPTCR_PG_OTP */
		{
			/* Program the flash word */
			do
			{
				*dest_addr = *src_addr;
				dest_addr++;
				src_addr++;
				row_index--;
			} while (row_index != 0U);
		}

		__ISB();
		__DSB();

		/* Wait for last operation to be completed */
		status = FLASH_WaitForLastOperation((uint32_t)HAL_MAX_DELAY, bank);

#if defined (DUAL_BANK)
#if defined (FLASH_OPTCR_PG_OTP)
		if (TypeProgram == FLASH_TYPEPROGRAM_OTPWORD)
		{
			/* If the program operation is completed, disable the OTP_PG */
			CLEAR_BIT(FLASH->OPTCR, FLASH_OPTCR_PG_OTP);
		}
		else
#endif /* FLASH_OPTCR_PG_OTP */
		{
			if (bank == FLASH_BANK_1)
			{
				/* If the program operation is completed, disable the PG */
				CLEAR_BIT(FLASH->CR1, FLASH_CR_PG);
			}
			else
			{
				/* If the program operation is completed, disable the PG */
				CLEAR_BIT(FLASH->CR2, FLASH_CR_PG);
			}
		}
#else /* Single Bank */
#if defined (FLASH_OPTCR_PG_OTP)
		if (TypeProgram == FLASH_TYPEPROGRAM_OTPWORD)
		{
			/* If the program operation is completed, disable the OTP_PG */
			CLEAR_BIT(FLASH->OPTCR, FLASH_OPTCR_PG_OTP);
		}
		else
#endif /* FLASH_OPTCR_PG_OTP */
		{
			/* If the program operation is completed, disable the PG */
			CLEAR_BIT(FLASH->CR1, FLASH_CR_PG);
		}
#endif /* DUAL_BANK */
	}

	/* Process Unlocked */
	__HAL_UNLOCK(&pFlash);

	return status;
}
#endif
