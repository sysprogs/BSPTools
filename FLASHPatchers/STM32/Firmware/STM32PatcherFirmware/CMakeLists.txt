cmake_minimum_required(VERSION 3.15)

project(STM32PatcherFirmware LANGUAGES C CXX ASM)

find_bsp(
	ID com.sysprogs.arm.stm32
	VERSION 2023.07
	MCU STM32C011D6
	CONFIGURATION com.sysprogs.bspoptions.primary_memory=sram com.sysprogs.mcuoptions.ignore_startup_file=1
	FRAMEWORKS com.sysprogs.arm.stm32.hal_systeminit com.sysprogs.arm.stm32.hal com.sysprogs.arm.stm32.ll
	CXX_STANDARD 17
	DISABLE_GNU_EXTENSIONS)


add_bsp_based_executable(
	NAME UniversalFLASHTester
	SOURCES ../UniversalFLASHTester.cpp
	GENERATE_BIN
	GENERATE_MAP
	OUTPUT_RELOCATION_RECORDS
	LINKER_SCRIPT STM32_SRAM_moved.lds)


function(build_patcher_executable TARGET_DEVICE )
    cmake_parse_arguments(_EXE "CUSTOM_SYSTEM_FILE" "" "" ${ARGN})

	string(SUBSTRING ${TARGET_DEVICE} 0 7 TARGET_FAMILY)
	set(_BSP_ID com.sysprogs.arm.stm32)
	set(_BSP_VERSION 2023.07)

	if(_EXE_CUSTOM_SYSTEM_FILE)
		set(_EXE_SYSTEM_FILE "system_${TARGET_FAMILY}xx.c")
	else()
		set(SYSTEMINIT_FRAMEWORK com.sysprogs.arm.stm32.hal_systeminit)
	endif()

	if("${TARGET_FAMILY}" STREQUAL "STM32WL")
		set(_BSP_ID com.sysprogs.arm.stm32wl)
		set(_BSP_VERSION 2021.12)
	endif()


	find_bsp(
		ID ${_BSP_ID}
		VERSION ${_BSP_VERSION}
		MCU ${TARGET_DEVICE}
		CONFIGURATION com.sysprogs.bspoptions.primary_memory=sram com.sysprogs.mcuoptions.ignore_startup_file=1
		FRAMEWORKS ${SYSTEMINIT_FRAMEWORK} com.sysprogs.arm.stm32.hal com.sysprogs.arm.stm32.ll
		CXX_STANDARD 17
		DISABLE_GNU_EXTENSIONS
		ALIAS BSP-${TARGET_FAMILY})

	bsp_compile_definitions(ALIAS BSP-${TARGET_FAMILY} ${TARGET_FAMILY})

	if (NOT ("${CMAKE_BUILD_TYPE}" STREQUAL DEBUG))
	bsp_compile_flags(ALIAS BSP-${TARGET_FAMILY} -flto -Os)
	endif()

	add_bsp_based_executable(
		NAME ${TARGET_FAMILY}Patcher
		SOURCES STM32PatcherFirmware.cpp ../startup.S ../FLASHPatcherEntry.cpp ${_EXE_SYSTEM_FILE}
		BSP_ALIAS BSP-${TARGET_FAMILY}
		GENERATE_BIN
		GENERATE_MAP
		OUTPUT_RELOCATION_RECORDS
		LINKER_SCRIPT STM32_SRAM_minimal.lds)
endfunction()

#build_patcher_executable(STM32F746NG)
#build_patcher_executable(STM32L476RG)
#build_patcher_executable(STM32G0B1RE)
#build_patcher_executable(STM32L552ZE CUSTOM_SYSTEM_FILE)
#build_patcher_executable(STM32U575ZI CUSTOM_SYSTEM_FILE)
#build_patcher_executable(STM32WL55JC)
#build_patcher_executable(STM32C011D6)
#build_patcher_executable(STM32F103RG)
#build_patcher_executable(STM32F030R8)
#build_patcher_executable(STM32L031K6)
build_patcher_executable(STM32L100RC)
