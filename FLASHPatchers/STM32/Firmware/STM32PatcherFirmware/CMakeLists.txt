cmake_minimum_required(VERSION 3.15)

project(STM32PatcherFirmware LANGUAGES C CXX ASM)

find_bsp(
	ID com.sysprogs.arm.stm32
	VERSION 2023.07
	MCU STM32F407VG
	CONFIGURATION com.sysprogs.bspoptions.primary_memory=sram com.sysprogs.mcuoptions.ignore_startup_file=1
	FRAMEWORKS com.sysprogs.arm.stm32.hal_systeminit com.sysprogs.arm.stm32.hal com.sysprogs.arm.stm32.ll
	CXX_STANDARD 17
	DISABLE_GNU_EXTENSIONS)

if (NOT ("${CMAKE_BUILD_TYPE}" STREQUAL DEBUG))
bsp_compile_flags(-flto -Os)
endif()

add_bsp_based_executable(
	NAME STM32F4xPatcher
	SOURCES STM32PatcherFirmware.cpp stm32f4xx_hal_conf.h ../startup.S ../FLASHPatcherEntry.cpp
	GENERATE_BIN
	GENERATE_MAP
	OUTPUT_RELOCATION_RECORDS)
